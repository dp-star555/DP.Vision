using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>OpenCV灰度SqDiff模型制作；生成独立像素和掩码，不保存Mat句柄。</summary>
public sealed class OpenCvTemplateModelBuilder : IVisionTemplateBuilder
{
    private readonly string _implementation;
    /// <summary>制作与指定匹配实现配套的模型。</summary>
    public OpenCvTemplateModelBuilder(string implementation) => _implementation = implementation;
    /// <inheritdoc/>
    public Task<VisionTemplateBuild> BuildAsync(VisionTemplateBuildRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var d = VisionTemplateStore.CopyDefinition(request.Definition); d.Validate();
        if (request.Source.Image.Info.Width != d.SourceWidth || request.Source.Image.Info.Height != d.SourceHeight) throw new ArgumentException("样图尺寸与模板定义不一致。");
        if (!CvPixels.Supports(request.Source.Image) || request.Source.Image.Info.ByteLength > 64 * 1024 * 1024) throw new NotSupportedException("模板仅支持有界8位图像；16位需显式转换。");
        var settings = Parse(request.Settings);
        using var gray = CvPixels.Gray(request.Source.Image);
        if (settings.Kernel > 1) Cv2.GaussianBlur(gray, gray, new Size(settings.Kernel, settings.Kernel), 0);
        using var crop = new Mat(gray, new Rect(d.X, d.Y, d.Width, d.Height));
        var pixels = Bytes(crop); var mask = Enumerable.Repeat((byte)255, d.Width * d.Height).ToArray();
        if (request.Mask != null)
        {
            InspectionMask.Validate(request.Mask, request.Source.Image); Array.Clear(mask, 0, mask.Length);
            foreach (var run in request.Mask.Runs)
            {
                token.ThrowIfCancellationRequested(); if (run.Row < d.Y || run.Row >= d.Y + d.Height) continue;
                for (int x = Math.Max(d.X, run.Start); x < Math.Min(d.X + d.Width, run.EndExclusive); x++) mask[(run.Row - d.Y) * d.Width + x - d.X] = 255;
            }
        }
        if (!mask.Any(b => b != 0)) throw new ArgumentException("模板有效区域为空，无法制作模型。");
        byte[] model;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            { writer.Write(1); writer.Write(d.Width); writer.Write(d.Height); writer.Write(settings.Kernel);
              writer.Write(settings.MinimumAngle); writer.Write(settings.MaximumAngle); writer.Write(settings.MinimumScale); writer.Write(settings.MaximumScale);
              writer.Write(pixels); writer.Write(mask); }
            model = stream.ToArray();
        }
        var source = VisionTemplateSource.Encode(request.Source.Image, token);
        var fullMask = new byte[d.SourceWidth * d.SourceHeight];
        for (int y = 0; y < d.Height; y++) Array.Copy(mask, y * d.Width, fullMask, (y + d.Y) * d.SourceWidth + d.X, d.Width);
        using var maskImage = VisionImage.CopyFrom(new ImageInfo(d.SourceWidth, d.SourceHeight, EPixelLayout.Gray8), fullMask);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new VisionTemplateBuild(_implementation, OpenCvTemplateModelFactory.Format, d,
            request.Settings.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            new[] { new VisionTemplateArtifact("source/image.bin", source), new VisionTemplateArtifact("source/mask.bin", VisionTemplateSource.Encode(maskImage, token)),
                new VisionTemplateArtifact(OpenCvTemplateModelFactory.ModelFile, model) }));
    }
    internal static byte[] Bytes(Mat mat)
    {
        var bytes = new byte[mat.Rows * mat.Cols];
        for (int y = 0; y < mat.Rows; y++) Marshal.Copy(mat.Ptr(y), bytes, y * mat.Cols, mat.Cols);
        return bytes;
    }
    internal static (int Kernel, double MinimumAngle, double MaximumAngle, double MinimumScale, double MaximumScale) Parse(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.Keys.Any(k => !new[] { "blurKernel", "minimumAngle", "maximumAngle", "minimumScale", "maximumScale" }.Contains(k))) throw new ArgumentException("未知的模板制作参数。");
        double Read(string key, double fallback) => settings.TryGetValue(key, out var text) ? double.Parse(text, CultureInfo.InvariantCulture) : fallback;
        double kernel = Read("blurKernel", 1), lo = Read("minimumAngle", -Math.PI), hi = Read("maximumAngle", Math.PI), s0 = Read("minimumScale", .1), s1 = Read("maximumScale", 10);
        if (new[] { kernel, lo, hi, s0, s1 }.Any(v => double.IsNaN(v) || double.IsInfinity(v)) || kernel != (int)kernel || kernel < 1 || kernel > 9 || (int)kernel % 2 != 1
            || lo < -Math.PI || hi > Math.PI || lo > hi || s0 < .1 || s1 > 10 || s0 > s1) throw new ArgumentException("制作参数无效：模糊核须为1..9奇数，角度为[-π,π]，尺度为[0.1,10]。");
        return ((int)kernel, lo, hi, s0, s1);
    }
}

/// <summary>配套制作描述和资源准备；读取时校验实现与格式。</summary>
public sealed class OpenCvTemplateModelFactory : IVisionAlgorithmFactory, IVisionAlgorithmConfigurationValidator, IVisionTemplateFactoryDescription, IVisionTemplatePreviewFactory, IVisionAlgorithmResourceInspector
{
    internal const string Format = "opencv.gray-sqdiff.v1";
    internal const string ModelFile = "variants/opencv-gray/model.bin";
    private readonly bool _pose;
    /// <summary>创建平移或旋转尺度模型工厂。</summary>
    public OpenCvTemplateModelFactory(bool pose) => _pose = pose;
    /// <summary>模型配套实现身份。</summary>
    public string ImplementationId => _pose ? "opencv.template-pose-model" : "opencv.template-model";
    /// <inheritdoc/>
    public string BuilderImplementationId => ImplementationId + ".build";
    /// <inheritdoc/>
    public string MethodDisplayName => _pose ? "灰度平方差（离散旋转尺度）" : "灰度平方差（固定姿态平移）";
    /// <inheritdoc/>
    public Type ContractType => typeof(IPreparedVisionTemplateMatcher);
    /// <inheritdoc/>
    public IReadOnlyList<VisionAlgorithmParameter> BuildParameters => new[]
    {
        new VisionAlgorithmParameter("blurKernel", "灰度平滑核", typeof(int), "1", "1表示不平滑；仅接受1..9奇数。匹配图像使用同样平滑。", 1, 9),
        new VisionAlgorithmParameter("minimumAngle", "模型最小角度", typeof(double), (-Math.PI).ToString("R", CultureInfo.InvariantCulture), "顺时针弧度。", -Math.PI, Math.PI),
        new VisionAlgorithmParameter("maximumAngle", "模型最大角度", typeof(double), Math.PI.ToString("R", CultureInfo.InvariantCulture), "顺时针弧度。", -Math.PI, Math.PI),
        new VisionAlgorithmParameter("minimumScale", "模型最小尺度", typeof(double), "0.1", minimum: .1, maximum: 10),
        new VisionAlgorithmParameter("maximumScale", "模型最大尺度", typeof(double), "10", minimum: .1, maximum: 10)
    };
    /// <inheritdoc/>
    public IReadOnlyList<VisionAlgorithmDependency> GetDependencies(VisionAlgorithmConfiguration configuration) => Array.Empty<VisionAlgorithmDependency>();
    /// <inheritdoc/>
    public IReadOnlyList<string> ValidateConfiguration(VisionAlgorithmConfiguration configuration)
    {
        return VisionAlgorithmConfigurationRules.ValidateVersionOne(configuration, new[] { "templatePath" }, "templatePath");
    }
    /// <inheritdoc/>
    public IReadOnlyList<string> InspectResources(VisionAlgorithmConfiguration configuration)
    {
        if (!configuration.Settings.TryGetValue("templatePath", out var path) || string.IsNullOrWhiteSpace(path)) return new[] { "缺少模板资源引用。" };
        CheckManifest(VisionTemplateStore.Inspect(path)); return VisionTemplateStore.InspectFiles(path);
    }
    private void CheckManifest(VisionTemplateManifest manifest)
    {
        if (manifest.ImplementationId != ImplementationId || manifest.ModelFormat != Format) throw new InvalidDataException("模板模型与所选引擎、匹配方式或格式不兼容，请重新制作。");
    }
    /// <inheritdoc/>
    public Task<VisionAlgorithmResource> PreparePreviewAsync(VisionTemplateBuild build, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var snapshot = VisionTemplateStore.CaptureBuild(build); CheckManifest(snapshot.Manifest);
        return Task.FromResult(new VisionAlgorithmResource(new OpenCvPreparedTemplateMatcher(snapshot, OpenCvPreparedTemplateMatcher.Decode(snapshot), _pose)));
    }
    /// <inheritdoc/>
    public Task<VisionAlgorithmActivation> PrepareAsync(VisionAlgorithmConfiguration configuration, IReadOnlyDictionary<string, object> dependencies, CancellationToken cancellationToken)
    {
        var errors = ValidateConfiguration(configuration); if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
        var snapshot = VisionTemplateStore.Capture(configuration.Settings["templatePath"], cancellationToken); CheckManifest(snapshot.Manifest);
        var data = OpenCvPreparedTemplateMatcher.Decode(snapshot); _ = Cv2.GetVersionString();
        return Task.FromResult(new VisionAlgorithmActivation(snapshot.Identity, EVisionAlgorithmSharing.SharedSerial, token =>
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new VisionAlgorithmResource(new OpenCvPreparedTemplateMatcher(snapshot, data, _pose))); }));
    }
}

/// <summary>共享串行模型：灰度/掩码一次加载，姿态候选按参数缓存。</summary>
internal sealed class OpenCvPreparedTemplateMatcher : IPreparedVisionTemplateMatcher, IDisposable
{
    private readonly Mat _gray, _mask;
    private readonly bool _pose;
    private readonly VisionTemplateDefinition _definition;
    private readonly (int Kernel, double MinimumAngle, double MaximumAngle, double MinimumScale, double MaximumScale) _settings;
    private readonly Dictionary<(double Angle, double Scale), (Mat Image, Mat Mask)> _candidates = new Dictionary<(double, double), (Mat, Mat)>();
    private long _candidateBytes;
    internal OpenCvPreparedTemplateMatcher(VisionTemplateSnapshot snapshot, ModelData data, bool pose)
    {
        _definition = VisionTemplateStore.CopyDefinition(snapshot.Manifest.Definition); ModelIdentity = snapshot.Identity; _pose = pose;
        _settings = data.Settings;
        _gray = new Mat(_definition.Height, _definition.Width, MatType.CV_8UC1);
        try { _mask = new Mat(_definition.Height, _definition.Width, MatType.CV_8UC1); }
        catch { _gray.Dispose(); throw; }
        try { Marshal.Copy(data.Pixels, 0, _gray.Data, data.Pixels.Length); Marshal.Copy(data.Mask, 0, _mask.Data, data.Mask.Length); }
        catch { _gray.Dispose(); _mask.Dispose(); throw; }
    }
    public VisionTemplateDefinition Definition => VisionTemplateStore.CopyDefinition(_definition);
    public string ModelIdentity { get; }
    internal sealed class ModelData
    {
        internal byte[] Pixels = Array.Empty<byte>(), Mask = Array.Empty<byte>();
        internal (int Kernel, double MinimumAngle, double MaximumAngle, double MinimumScale, double MaximumScale) Settings;
    }
    internal static ModelData Decode(VisionTemplateSnapshot snapshot)
    {
        using var stream = new MemoryStream(snapshot.Read(OpenCvTemplateModelFactory.ModelFile)); using var reader = new BinaryReader(stream);
        var d = snapshot.Manifest.Definition;
        if (reader.ReadInt32() != 1 || reader.ReadInt32() != d.Width || reader.ReadInt32() != d.Height) throw new InvalidDataException("OpenCV模板尺寸或格式无效。");
        int kernel = reader.ReadInt32(); double lo = reader.ReadDouble(), hi = reader.ReadDouble(), s0 = reader.ReadDouble(), s1 = reader.ReadDouble();
        var settings = OpenCvTemplateModelBuilder.Parse(snapshot.Manifest.BuildSettings);
        if ((kernel, lo, hi, s0, s1) != settings) throw new InvalidDataException("制作参数与模型文件不一致。");
        int area = checked(d.Width * d.Height); var pixels = reader.ReadBytes(area); var mask = reader.ReadBytes(area);
        if (pixels.Length != area || mask.Length != area || stream.Position != stream.Length || !mask.Any(b => b != 0) || mask.Any(b => b != 0 && b != 255)) throw new InvalidDataException("模板像素或掩码无效。");
        return new ModelData { Pixels = pixels, Mask = mask, Settings = settings };
    }
    public TemplatePoseResult Match(ImageFrame frame, PixelBounds searchBounds, TemplatePoseOptions options, RegionGeometry? region = null, CancellationToken token = default)
    {
        if (!searchBounds.Fits(frame.Image) || !CvPixels.Supports(frame.Image)) throw new ArgumentException("匹配范围越界或图像格式不支持。");
        if ((long)frame.Image.Info.Width * frame.Image.Info.Height > 16777216) throw new ArgumentException("匹配图像超过像素预算。");
        InspectionMask.Validate(region, frame.Image);
        if (!_pose && (options.AnglesRadians.Count != 1 || options.Scales.Count != 1)) throw new ArgumentException("平移模型只能使用一个固定姿态。");
        var angles = options.AnglesRadians.Select(a => Math.Atan2(Math.Sin(a), Math.Cos(a))).Distinct().ToArray();
        long work = 0;
        foreach (double angle in angles) foreach (double scale in options.Scales)
        {
            if (angle < _settings.MinimumAngle - 1e-10 || angle > _settings.MaximumAngle + 1e-10 || scale < _settings.MinimumScale - 1e-10 || scale > _settings.MaximumScale + 1e-10) throw new ArgumentException("搜索角度或尺度超出模型制作范围。");
            var size = SizeFor(angle, scale);
            if (size.Width > searchBounds.Width || size.Height > searchBounds.Height) continue;
            long cost = (long)(searchBounds.Width - size.Width + 1) * (searchBounds.Height - size.Height + 1) * size.Width * size.Height;
            if (cost > options.MaximumWork - work) throw new InvalidOperationException("模板比较预算超限，请缩小搜索区域或候选数量。"); work += cost;
        }
        token.ThrowIfCancellationRequested();
        if (_candidates.Count + options.AnglesRadians.Count * options.Scales.Count > 512) ClearCandidates();
        using var gray = CvPixels.Gray(frame.Image);
        if (_settings.Kernel > 1) Cv2.GaussianBlur(gray, gray, new Size(_settings.Kernel, _settings.Kernel), 0);
        using var search = new Mat(gray, new Rect(searchBounds.X, searchBounds.Y, searchBounds.Width, searchBounds.Height));
        double best = -1; TemplatePoseTransform? transform = null;
        foreach (double angle in angles) foreach (double scale in options.Scales)
        {
            token.ThrowIfCancellationRequested(); var size = SizeFor(angle, scale);
            if (size.Width > searchBounds.Width || size.Height > searchBounds.Height) continue;
            if (!_candidates.TryGetValue((angle, scale), out var candidate))
            {
                if (_candidateBytes + 2L * size.Width * size.Height > 64L * 1024 * 1024) ClearCandidates();
                using var matrix = new Mat(2, 3, MatType.CV_64FC1);
                double a = scale * Math.Cos(angle), b = -scale * Math.Sin(angle);
                matrix.Set(0, 0, a); matrix.Set(0, 1, b); matrix.Set(0, 2, (size.Width - 1) / 2d - a * (_gray.Cols - 1) / 2d - b * (_gray.Rows - 1) / 2d);
                matrix.Set(1, 0, -b); matrix.Set(1, 1, a); matrix.Set(1, 2, (size.Height - 1) / 2d + b * (_gray.Cols - 1) / 2d - a * (_gray.Rows - 1) / 2d);
                var image = new Mat(); var mask = new Mat();
                try { Cv2.WarpAffine(_gray, image, matrix, size, InterpolationFlags.Linear, BorderTypes.Replicate);
                    Cv2.WarpAffine(_mask, mask, matrix, size, InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
                    candidate = (image, mask); _candidates.Add((angle, scale), candidate); _candidateBytes += 2L * size.Width * size.Height; }
                catch { image.Dispose(); mask.Dispose(); throw; }
            }
            int area = Cv2.CountNonZero(candidate.Mask); if (area == 0) continue;
            using var scores = new Mat(); Cv2.MatchTemplate(search, candidate.Image, scores, TemplateMatchModes.SqDiff, candidate.Mask);
            if (!RegionMatchMinimum.Find(scores, region, frame, searchBounds, size.Width, size.Height, candidate.Mask, token, out var minimum, out var location)) continue;
            if (double.IsNaN(minimum) || double.IsInfinity(minimum)) throw new InvalidOperationException("模板匹配分数非有限值。");
            double score = Math.Max(0, Math.Min(1, 1 - minimum / (65025d * area)));
            if (score > best) { best = score; transform = new TemplatePoseTransform(_gray.Cols, _gray.Rows,
                new PointD(searchBounds.X + location.X + size.Width / 2d, searchBounds.Y + location.Y + size.Height / 2d), angle, scale); }
        }
        token.ThrowIfCancellationRequested();
        return new TemplatePoseResult(frame.FrameId, ModelIdentity, Math.Max(0, best), best >= options.MinimumScore ? transform : null);
    }
    private Size SizeFor(double angle, double scale) => new Size(
        Math.Max(1, (int)Math.Ceiling(scale * (Math.Abs(Math.Cos(angle)) * _gray.Cols + Math.Abs(Math.Sin(angle)) * _gray.Rows) - 1e-10)),
        Math.Max(1, (int)Math.Ceiling(scale * (Math.Abs(Math.Sin(angle)) * _gray.Cols + Math.Abs(Math.Cos(angle)) * _gray.Rows) - 1e-10)));
    private void ClearCandidates() { foreach (var c in _candidates.Values) { c.Image.Dispose(); c.Mask.Dispose(); } _candidates.Clear(); _candidateBytes = 0; }
    public void Dispose() { ClearCandidates(); _gray.Dispose(); _mask.Dispose(); }
}
