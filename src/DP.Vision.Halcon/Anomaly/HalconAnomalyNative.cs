using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
#if HALCON_SDK
using HalconDotNet;
#endif

namespace DP.Vision.Halcon;

internal static class HalconAnomalyNative
{
    internal static ILoadedAnomalyModel Load(AnomalyModelAsset asset, EHalconAnomalyMethod method, CancellationToken token)
    {
#if HALCON_SDK
        return new Runtime(asset, method, token);
#else
        throw Missing();
#endif
    }
    internal static AnomalyModelAsset Train(HalconAnomalyImplementation implementation, IReadOnlyList<IImageSource> good,
        IReadOnlyList<int> sources, AnomalyTrainingOptions options, CancellationToken token)
    {
#if HALCON_SDK
        var ids = sources.Distinct().ToArray();
        if (ids.Length < 3) throw new ArgumentException("同一张图的重复字符不构成独立来源；HALCON至少需要2个训练来源及1个标定来源。");
        int held = ids[ids.Length - 1];
        var train = good.Where((_, i) => sources[i] != held).ToArray();
        var validate = good.Where((_, i) => sources[i] == held).ToArray();
        int width = good[0].Info.Width, height = good[0].Info.Height;
        if ((long)width * height > 16777216) throw new ArgumentException("HALCON训练输入超过16M像素预算。");
        if (good.Any(g => g.Info.Width != width || g.Info.Height != height)) throw new ArgumentException("HALCON制作裁图须同尺寸。");
        string version = Version();
        var settings = new Dictionary<string, string> { ["halcon_version"] = version, ["preprocessing"] = "gray-byte-fixed-range-v1" };
        byte[] native;
        if (implementation.Method == EHalconAnomalyMethod.Variation)
        {
            using var model = new HVariationModel(); model.CreateVariationModel(width, height, "byte", "standard");
            foreach (var image in train) { token.ThrowIfCancellationRequested(); using var gray = Gray(image, token); model.TrainVariationModel(gray); }
            // 明确记录绝对/相对容差，不伪称为学习得到的深度模型。
            model.PrepareVariationModel(15.0, 3.0);
            settings["absolute_tolerance"] = "15"; settings["relative_tolerance"] = "3";
            using var stream = new MemoryStream(); model.Serialize(stream); native = stream.ToArray();
        }
        else
        {
            string initial = implementation.InitialModel ?? Path.Combine(Environment.GetEnvironmentVariable("HALCONROOT") ?? "", "dl", "initial_dl_anomaly_medium.hdl");
            if (!File.Exists(initial)) throw new FileNotFoundException("HALCON深度训练初始模型不存在。", initial);
            if (new FileInfo(initial).Length > AnomalyModelAsset.MaximumBytes) throw new InvalidDataException("HALCON初始训练模型超过256MiB预算。");
            HOperatorSet.ReadDlModel(initial, out var model);
            try
            {
                HOperatorSet.SetDlModelParam(model, "runtime", "cpu");
                // 小标签保持输入几何，在明确记录的网络预处理阶段放大；64px网络/2个正常来源实测#7882。
                int w = Math.Max(256, (width + 31) / 32 * 32), h = Math.Max(256, (height + 31) / 32 * 32);
                HOperatorSet.SetDlModelParam(model, "image_width", w); HOperatorSet.SetDlModelParam(model, "image_height", h);
                HOperatorSet.GetDlModelParam(model, "type", out var kind); using (kind) if (kind.S != "anomaly_detection") throw new InvalidDataException("初始模型不是HALCON Anomaly Detection。");
                HOperatorSet.GetDlModelParam(model, "image_range_min", out var min); HOperatorSet.GetDlModelParam(model, "image_range_max", out var max);
                using (min) using (max) { settings["range_min"] = min.D.ToString("R", CultureInfo.InvariantCulture); settings["range_max"] = max.D.ToString("R", CultureInfo.InvariantCulture); }
                HOperatorSet.GetDlModelParam(model, "image_num_channels", out var channels);
                using (channels) settings["channels"] = channels.I.ToString(CultureInfo.InvariantCulture);
                settings["network_width"] = w.ToString(CultureInfo.InvariantCulture); settings["network_height"] = h.ToString(CultureInfo.InvariantCulture);
                settings["maximum_epochs"] = implementation.Epochs.ToString(CultureInfo.InvariantCulture); settings["domain_ratio"] = "1";
                using (var file = File.OpenRead(initial)) using (var sha = System.Security.Cryptography.SHA256.Create())
                    settings["initial_model_sha256"] = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                var samples = new List<HTuple>(); HTuple tuple = new HTuple();
                try
                {
                    foreach (var input in train)
                    {
                        token.ThrowIfCancellationRequested(); using var image = Preprocess(input, settings, token);
                        HOperatorSet.CreateDict(out var sample); samples.Add(sample); HOperatorSet.SetDictObject(image, sample, "image");
                        var next = tuple.TupleConcat(sample); tuple.Dispose(); tuple = next;
                    }
                    HOperatorSet.CreateDict(out var parameters);
                    using (parameters)
                    {
                        HOperatorSet.SetDictTuple(parameters, "max_num_epochs", implementation.Epochs);
                        // 小字符少量来源时保持全部训练图域；.2在2张256px良品上复现#7882，不以重复样本填充。
                        HOperatorSet.SetDictTuple(parameters, "domain_ratio", 1.0);
                        try { HOperatorSet.TrainDlModelAnomalyDataset(model, tuple, parameters, out var result); result.Dispose(); }
                        catch (HOperatorException error)
                        {
                            throw new InvalidOperationException("HALCON深度良品训练失败；请检查独立良品变化、图域/网络尺寸及训练环境。未发布新修订、未回退其他算法。原始HALCON错误：" + error.Message, error);
                        }
                    }
                }
                finally { tuple.Dispose(); foreach (var sample in samples) sample.Dispose(); }
                token.ThrowIfCancellationRequested(); native = Save(model);
            }
            finally { HOperatorSet.ClearDlModel(model); model.Dispose(); }
        }
        // 保持标定来源不进入训练。深度阈值根据独立正常图的像素最大值标定；不是训练图自测。
        var probe = new AnomalyModelAsset(implementation.ImplementationId, implementation.ImplementationId + ".v1", width, height, 1, train.Length,
            "独立来源标定中", new Dictionary<string, byte[]> { ["model.bin"] = native }, settings);
        double worst = 0;
        using (var runtime = Load(probe, implementation.Method, token))
            foreach (var image in validate)
            {
                token.ThrowIfCancellationRequested(); using var result = runtime.Inspect(image, new AnomalyDetectionOptions(1, 1), token);
                worst = Math.Max(worst, result.MaximumScore);
            }
        if (implementation.Method == EHalconAnomalyMethod.AnomalyDetection && worst >= 1)
            throw new InvalidOperationException("HALCON独立正常标定已达到异常得分上限1，无法给出有效阈值；请补充正常变化来源/改善训练后再发布。");
        // HALCON AD分数有界0..1，不能照搬Patch距离乘余量得到>1的永远不报NG模型。
        double threshold = implementation.Method == EHalconAnomalyMethod.Variation ? 1
            : Math.Max(.0001, Math.Min(worst * options.Margin, worst + (1 - worst) / 2));
        settings["score_range"] = implementation.Method == EHalconAnomalyMethod.Variation ? "binary-0-2" : "bounded-0-1";
        return new AnomalyModelAsset(implementation.ImplementationId, implementation.ImplementationId + ".v1", width, height, threshold, train.Length,
            $"仅正常图训练，{ids.Length - 1}个独立训练来源/{train.Length}个样本，1个独立标定来源/{validate.Length}个样本，正常最大像素分数{worst:F6}，阈值{threshold:F6}（余量{options.Margin:F2}，有界AD最多使用剩余得分空间一半，饱和标定拒绝发布）；HALCON {version} CPU；不是现场精度验收。",
            new Dictionary<string, byte[]> { ["model.bin"] = native }, settings);
#else
        throw Missing();
#endif
    }
#if HALCON_SDK
    private static string Version()
    { HOperatorSet.GetSystem("version", out var version); using (version) return version.S; }
    private static HImage Gray(IImageSource source, CancellationToken token)
    {
        var info = source.Info;
        if ((long)info.Width * info.Height > 16777216 || info.Layout != EPixelLayout.Gray8 && info.Layout != EPixelLayout.Bgr24)
            throw new ArgumentException("HALCON异常模型支持16M像素内Gray8/Bgr24输入。");
        var pixels = new byte[info.Width * info.Height];
        var row = new byte[info.Width * (info.Layout == EPixelLayout.Bgr24 ? 3 : 1)];
        for (int y = 0; y < info.Height; y++)
        {
            token.ThrowIfCancellationRequested(); source.CopyTo(y * info.Stride, row, 0, row.Length);
            for (int x = 0; x < info.Width; x++) pixels[y * info.Width + x] = info.Layout == EPixelLayout.Gray8 ? row[x]
                : (byte)((29 * row[x * 3] + 150 * row[x * 3 + 1] + 77 * row[x * 3 + 2] + 128) >> 8);
        }
        return HalconTemplateNative.Image(pixels, info.Width, info.Height);
    }
    private static HImage Preprocess(IImageSource source, IReadOnlyDictionary<string, string> settings, CancellationToken token)
    {
        using var gray = Gray(source, token);
        int w = int.Parse(settings["network_width"], CultureInfo.InvariantCulture), h = int.Parse(settings["network_height"], CultureInfo.InvariantCulture);
        using var resize = gray.ZoomImageSize(w, h, "bilinear"); using var real = resize.ConvertImageType("real");
        double min = double.Parse(settings["range_min"], CultureInfo.InvariantCulture), max = double.Parse(settings["range_max"], CultureInfo.InvariantCulture);
        using var scaled = real.ScaleImage((max - min) / 255, min);
        return settings["channels"] == "3" ? scaled.Compose3(scaled, scaled) : scaled.CopyImage();
    }
    private static byte[] Save(HTuple model)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dp-halcon-ad-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try { string path = Path.Combine(dir, "model.hdl"); HOperatorSet.WriteDlModel(model, path);
            if (new FileInfo(path).Length > AnomalyModelAsset.MaximumBytes - 65536) throw new InvalidDataException("HALCON模型超过256MiB预算。");
            return File.ReadAllBytes(path); }
        finally { Directory.Delete(dir, true); }
    }
    private sealed class Runtime : ILoadedAnomalyModel
    {
        private readonly object _gate = new object();
        private readonly HVariationModel? _variation;
        private readonly HTuple? _deep;
        private bool _disposed;
        internal Runtime(AnomalyModelAsset asset, EHalconAnomalyMethod method, CancellationToken token)
        {
            Asset = asset;
            if (!asset.Settings.TryGetValue("halcon_version", out var version) || version != Version()
                || !asset.Settings.TryGetValue("preprocessing", out var preprocessing) || preprocessing != "gray-byte-fixed-range-v1")
                throw new InvalidDataException("HALCON版本或预处理契约不匹配，不自动转换原生模型。");
            if (method == EHalconAnomalyMethod.Variation)
            {
                using var stream = new MemoryStream(asset.Read("model.bin"), false); _variation = HVariationModel.Deserialize(stream);
                try { _variation.PrepareVariationModel(double.Parse(asset.Settings["absolute_tolerance"], CultureInfo.InvariantCulture),
                    double.Parse(asset.Settings["relative_tolerance"], CultureInfo.InvariantCulture)); }
                catch { _variation.Dispose(); throw; }
            }
            else
            {
                string dir = Path.Combine(Path.GetTempPath(), "dp-halcon-ad-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                try { string path = Path.Combine(dir, "model.hdl"); File.WriteAllBytes(path, asset.Read("model.bin"));
                    HOperatorSet.ReadDlModel(path, out var handle); _deep = handle;
                    HOperatorSet.SetDlModelParam(handle, "runtime", "cpu");
                    HOperatorSet.GetDlModelParam(handle, "type", out var type);
                    using (type) if (type.S != "anomaly_detection") throw new InvalidDataException("原生hdl并非HALCON Anomaly Detection。");
                    foreach (var key in new[] { "image_width", "image_height", "image_num_channels", "image_range_min", "image_range_max" })
                    {
                        string setting = key == "image_width" ? "network_width" : key == "image_height" ? "network_height" : key == "image_num_channels" ? "channels" : key == "image_range_min" ? "range_min" : "range_max";
                        HOperatorSet.GetDlModelParam(handle, key, out var value);
                        using (value) if (value.D != double.Parse(asset.Settings[setting], CultureInfo.InvariantCulture)) throw new InvalidDataException("HALCON原生模型与资产输入契约不一致：" + key);
                    }
                    }
                catch { if (_deep != null) { HOperatorSet.ClearDlModel(_deep); _deep.Dispose(); } throw; }
                finally { Directory.Delete(dir, true); }
            }
            if (token.IsCancellationRequested) { Dispose(); token.ThrowIfCancellationRequested(); }
        }
        public AnomalyModelAsset Asset { get; }
        public PatchAnomalyResult Inspect(IImageSource source, AnomalyDetectionOptions options, CancellationToken token = default)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Runtime)); token.ThrowIfCancellationRequested();
                if (source.Info.Width != Asset.Width || source.Info.Height != Asset.Height) throw new ArgumentException("HALCON输入尺寸与制作资产不一致，请重新制作模型或恢复制作ROI。");
                float[] scores;
                if (_variation != null)
                {
                    using var image = Gray(source, token); using var region = _variation.CompareVariationModel(image);
                    using var binary = region.RegionToBin(2, 0, Asset.Width, Asset.Height); using var real = binary.ConvertImageType("real"); scores = Pixels(real);
                }
                else
                {
                    using var image = Preprocess(source, Asset.Settings, token);
                    HOperatorSet.CreateDict(out var sample);
                    using (sample)
                    {
                        HOperatorSet.SetDictObject(image, sample, "image");
                        using var outputs = new HTuple(); HOperatorSet.ApplyDlModel(_deep!, sample, outputs, out var result);
                        using (result)
                        {
                            HOperatorSet.GetDictObject(out var map, result, "anomaly_image");
                            using (map) using (var native = new HImage(map)) using (var resized = native.ZoomImageSize(Asset.Width, Asset.Height, "bilinear")) scores = Pixels(resized);
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                return AnomalyScoreMap.Measure(scores, Asset.Width, Asset.Height, options.Threshold ?? Asset.Threshold, options.MinimumArea,
                    $"{Asset.ImplementationId}，HALCON原生测量；{Asset.Calibration}");
            }
        }
        private static float[] Pixels(HImage image)
        {
            IntPtr pointer = image.GetImagePointer1(out string type, out int width, out int height);
            if (type != "real") throw new InvalidDataException("HALCON异常输出不是实数像素图。");
            var scores = new float[checked(width * height)]; Marshal.Copy(pointer, scores, 0, scores.Length); return scores;
        }
        public void Dispose()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _variation?.Dispose();
                if (_deep != null) { HOperatorSet.ClearDlModel(_deep); _deep.Dispose(); } }
        }
    }
#endif
    private static PlatformNotSupportedException Missing() => new PlatformNotSupportedException("HALCON异常实现未装配SDK，请部署匹配HALCON运行时/许可并以HalconDotNetPath重新生成。");
}
