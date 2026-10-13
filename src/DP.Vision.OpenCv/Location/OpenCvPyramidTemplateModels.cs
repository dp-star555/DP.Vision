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

/// <summary>金字塔模板引擎种类：灰度归一化互相关或梯度方向形状。</summary>
public enum EOpenCvPyramidTemplateKind
{
    /// <summary>灰度NCC（TM_CCOEFF_NORMED，掩码内皮尔逊相关）。</summary>
    Ncc = 1,
    /// <summary>梯度方向形状匹配（边缘点方向向量点积平均）。</summary>
    Shape = 2
}

/// <summary>形状匹配的极性方式，与HALCON metric同义。</summary>
internal enum EShapeMetric { UsePolarity = 0, IgnoreGlobalPolarity = 1, IgnoreLocalPolarity = 2 }

/// <summary>金字塔模板的制作参数；读取、校验与规范化文本都在这里。</summary>
internal sealed class OpenCvPyramidSettings
{
    internal int Levels;
    internal double MinimumAngle = -Math.PI, MaximumAngle = Math.PI, MinimumScale = .1, MaximumScale = 10;
    internal int Contrast = 30, MinimumContrast = 10;
    internal EShapeMetric Metric;
    /// <summary>旧版顶层提前终止的贪婪度；顶层改为SIMD逐特征累加后不再使用，只为已制作模型的参数校验保留。</summary>
    internal double Greediness = .9;
    internal bool LeastSquares = true;

    private static readonly string[] NccKeys = { "levels", "minimumAngle", "maximumAngle", "minimumScale", "maximumScale" };
    private static readonly string[] ShapeKeys = NccKeys.Concat(new[] { "contrast", "minimumContrast", "metric", "greediness", "subPixel" }).ToArray();

    internal static OpenCvPyramidSettings Parse(EOpenCvPyramidTemplateKind kind, IReadOnlyDictionary<string, string> settings)
    {
        var keys = kind == EOpenCvPyramidTemplateKind.Shape ? ShapeKeys : NccKeys;
        if (settings.Keys.Any(k => !keys.Contains(k))) throw new ArgumentException("未知的模板制作参数。");
        double Read(string key, double fallback)
        {
            if (!settings.TryGetValue(key, out var text)) return fallback;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("模板制作参数不是有效数字：" + key);
            return value;
        }
        var s = new OpenCvPyramidSettings();
        double levels = Read("levels", 0);
        s.MinimumAngle = Read("minimumAngle", -Math.PI); s.MaximumAngle = Read("maximumAngle", Math.PI);
        s.MinimumScale = Read("minimumScale", .1); s.MaximumScale = Read("maximumScale", 10);
        if (levels != Math.Floor(levels) || levels < 0 || levels > 6) throw new ArgumentException("金字塔层数须为0（自动）或1..6。");
        if (s.MinimumAngle < -Math.PI - 1e-12 || s.MaximumAngle > Math.PI + 1e-12 || s.MinimumAngle > s.MaximumAngle
            || s.MinimumScale < .1 || s.MaximumScale > 10 || s.MinimumScale > s.MaximumScale)
            throw new ArgumentException("制作参数无效：角度为[-π,π]，尺度为[0.1,10]，下限不能大于上限。");
        s.Levels = (int)levels;
        if (kind == EOpenCvPyramidTemplateKind.Shape)
        {
            double contrast = Read("contrast", 30), minimum = Read("minimumContrast", 10), greediness = Read("greediness", .9);
            if (contrast != Math.Floor(contrast) || contrast < 1 || contrast > 255 || minimum != Math.Floor(minimum) || minimum < 1 || minimum > 255)
                throw new ArgumentException("对比度须为1..255的整数。");
            if (minimum > contrast) throw new ArgumentException("搜索最小对比度不能大于制作对比度。");
            if (greediness < 0 || greediness > 1) throw new ArgumentException("贪婪度须为0..1。");
            s.Contrast = (int)contrast; s.MinimumContrast = (int)minimum; s.Greediness = greediness;
            string metric = settings.TryGetValue("metric", out var m) ? m : "use_polarity";
            s.Metric = metric switch
            {
                "use_polarity" => EShapeMetric.UsePolarity,
                "ignore_global_polarity" => EShapeMetric.IgnoreGlobalPolarity,
                "ignore_local_polarity" => EShapeMetric.IgnoreLocalPolarity,
                _ => throw new ArgumentException("极性方式须为use_polarity、ignore_global_polarity或ignore_local_polarity。")
            };
            string sub = settings.TryGetValue("subPixel", out var p) ? p : "least_squares";
            s.LeastSquares = sub switch
            {
                "least_squares" => true,
                "interpolation" => false,
                _ => throw new ArgumentException("亚像素精化须为interpolation或least_squares。")
            };
        }
        return s;
    }

    /// <summary>写入模型文件的规范化参数，读取时与清单中的制作参数逐项比对。</summary>
    internal string Canonical(EOpenCvPyramidTemplateKind kind)
    {
        string text = string.Join("|", new[] { Levels, MinimumAngle, MaximumAngle, MinimumScale, MaximumScale }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        if (kind == EOpenCvPyramidTemplateKind.Shape)
            text += FormattableString.Invariant($"|{Contrast}|{MinimumContrast}|{(int)Metric}|{Greediness:R}|{LeastSquares}");
        return text;
    }
}

/// <summary>金字塔一层的模板数据：裁剪像素、掩码、模板中心以及形状特征。</summary>
internal sealed class OpenCvPyramidLevel
{
    internal int Width, Height;
    /// <summary>模板中心在本层裁剪图中的边界坐标。</summary>
    internal double CenterX, CenterY;
    internal byte[] Pixels = Array.Empty<byte>(), Mask = Array.Empty<byte>();
    /// <summary>形状特征：相对模板中心的边界坐标(Qx,Qy)及单位梯度方向(Ux,Uy)。</summary>
    internal float[] Qx = Array.Empty<float>(), Qy = Array.Empty<float>(), Ux = Array.Empty<float>(), Uy = Array.Empty<float>();
    internal int FeatureCount => Qx.Length;
}

/// <summary>金字塔模板模型：模板宽高、制作参数和逐层数据。</summary>
internal sealed class OpenCvPyramidModel
{
    internal EOpenCvPyramidTemplateKind Kind;
    internal int Width, Height;
    internal OpenCvPyramidSettings Settings = new OpenCvPyramidSettings();
    internal OpenCvPyramidLevel[] Levels = Array.Empty<OpenCvPyramidLevel>();

    internal const int MinimumShapeFeatures = 8;

    internal byte[] Encode()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(1); writer.Write((int)Kind); writer.Write(Width); writer.Write(Height); writer.Write(Settings.Canonical(Kind)); writer.Write(Levels.Length);
            foreach (var level in Levels)
            {
                writer.Write(level.Width); writer.Write(level.Height); writer.Write(level.CenterX); writer.Write(level.CenterY);
                writer.Write(level.Pixels); writer.Write(level.Mask); writer.Write(level.FeatureCount);
                for (int i = 0; i < level.FeatureCount; i++) { writer.Write(level.Qx[i]); writer.Write(level.Qy[i]); writer.Write(level.Ux[i]); writer.Write(level.Uy[i]); }
            }
        }
        return stream.ToArray();
    }

    internal static OpenCvPyramidModel Decode(EOpenCvPyramidTemplateKind kind, VisionTemplateSnapshot snapshot)
    {
        var d = snapshot.Manifest.Definition;
        var settings = OpenCvPyramidSettings.Parse(kind, snapshot.Manifest.BuildSettings);
        using var stream = new MemoryStream(snapshot.Read(OpenCvPyramidTemplateFactory.ModelFile)); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1 || reader.ReadInt32() != (int)kind || reader.ReadInt32() != d.Width || reader.ReadInt32() != d.Height)
            throw new InvalidDataException("OpenCV金字塔模板尺寸、种类或格式无效。");
        if (reader.ReadString() != settings.Canonical(kind)) throw new InvalidDataException("制作参数与模型文件不一致。");
        int count = reader.ReadInt32(); if (count < 1 || count > 6) throw new InvalidDataException("金字塔层数无效。");
        var model = new OpenCvPyramidModel { Kind = kind, Width = d.Width, Height = d.Height, Settings = settings, Levels = new OpenCvPyramidLevel[count] };
        for (int l = 0; l < count; l++)
        {
            var level = new OpenCvPyramidLevel { Width = reader.ReadInt32(), Height = reader.ReadInt32(), CenterX = reader.ReadDouble(), CenterY = reader.ReadDouble() };
            if (level.Width < 1 || level.Height < 1 || level.Width > d.Width + 2 || level.Height > d.Height + 2) throw new InvalidDataException("金字塔层尺寸无效。");
            int area = level.Width * level.Height;
            level.Pixels = reader.ReadBytes(area); level.Mask = reader.ReadBytes(area);
            if (level.Pixels.Length != area || level.Mask.Length != area || level.Mask.Any(b => b != 0 && b != 255) || !level.Mask.Any(b => b != 0)) throw new InvalidDataException("模板像素或掩码无效。");
            int n = reader.ReadInt32(); if (n < 0 || n > 1 << 20 || (kind == EOpenCvPyramidTemplateKind.Shape) != (n > 0)) throw new InvalidDataException("形状特征数量无效。");
            level.Qx = new float[n]; level.Qy = new float[n]; level.Ux = new float[n]; level.Uy = new float[n];
            for (int i = 0; i < n; i++) { level.Qx[i] = reader.ReadSingle(); level.Qy[i] = reader.ReadSingle(); level.Ux[i] = reader.ReadSingle(); level.Uy[i] = reader.ReadSingle(); }
            if (new[] { level.Qx, level.Qy, level.Ux, level.Uy }.Any(a => a.Any(v => float.IsNaN(v) || float.IsInfinity(v)))) throw new InvalidDataException("形状特征无效。");
            model.Levels[l] = level;
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("模型文件末尾有多余数据。");
        return model;
    }

    /// <summary>自动层数的边缘点下限：形状模型顶层至少保留这么多边缘点才继续加层（制作时逐层检查）。</summary>
    internal const int AutoShapeFeatures = 24;

    /// <summary>
    /// 自动层数的几何上限，最多6层。NCC：顶层模板面积不少于256像素、短边不少于8像素（灰度相关需要足够的像素）。
    /// 形状：顶层短边不少于5像素、面积不少于64像素，再由制作时的边缘点数决定是否真的加层——与HALCON按顶层模型点数自动选层同理；
    /// 细长模板按短边限制会过早停止，而它们在更粗的层上仍有大量边缘点。
    /// </summary>
    internal static int AutoLevels(EOpenCvPyramidTemplateKind kind, int width, int height)
    {
        bool shape = kind == EOpenCvPyramidTemplateKind.Shape;
        int levels = 1;
        while (levels < 6)
        {
            double f = Math.Pow(2, levels), w = width / f, h = height / f;
            if (shape ? w * h < 64 || Math.Min(w, h) < 5 : w * h < 256 || Math.Min(w, h) < 8) break;
            levels++;
        }
        return levels;
    }
}

/// <summary>OpenCV金字塔模板制作：在整幅样图的金字塔上截取每层模板，形状模型同时提取边缘特征。</summary>
public sealed class OpenCvPyramidTemplateBuilder : IVisionTemplateBuilder
{
    private readonly string _implementation;
    private readonly EOpenCvPyramidTemplateKind _kind;
    /// <summary>制作与指定匹配实现配套的模型。</summary>
    public OpenCvPyramidTemplateBuilder(string implementation, EOpenCvPyramidTemplateKind kind) { _implementation = implementation; _kind = kind; }

    /// <inheritdoc/>
    public Task<VisionTemplateBuild> BuildAsync(VisionTemplateBuildRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var d = VisionTemplateStore.CopyDefinition(request.Definition); d.Validate();
        if (request.Source.Image.Info.Width != d.SourceWidth || request.Source.Image.Info.Height != d.SourceHeight) throw new ArgumentException("样图尺寸与模板定义不一致。");
        VisionImage.RequireGray8(request.Source.Image, "模板制作");
        if (request.Source.Image.Info.ByteLength > 64 * 1024 * 1024) throw new NotSupportedException("模板样图超过64MiB预算。");
        var settings = OpenCvPyramidSettings.Parse(_kind, request.Settings);
        var fullMask = new byte[d.SourceWidth * d.SourceHeight];
        if (request.Mask == null) { for (int y = d.Y; y < d.Y + d.Height; y++) for (int x = d.X; x < d.X + d.Width; x++) fullMask[y * d.SourceWidth + x] = 255; }
        else
        {
            InspectionMask.Validate(request.Mask, request.Source.Image);
            foreach (var run in request.Mask.Runs)
            {
                token.ThrowIfCancellationRequested(); if (run.Row < d.Y || run.Row >= d.Y + d.Height) continue;
                for (int x = Math.Max(d.X, run.Start); x < Math.Min(d.X + d.Width, run.EndExclusive); x++) fullMask[run.Row * d.SourceWidth + x] = 255;
            }
        }
        if (!fullMask.Any(b => b != 0)) throw new ArgumentException("模板有效区域为空，无法制作模型。");
        var model = Build(request.Source.Image, fullMask, d, settings, token);
        var source = VisionTemplateSource.Encode(request.Source.Image, token);
        using var maskImage = VisionImage.CopyFrom(new ImageInfo(d.SourceWidth, d.SourceHeight, EPixelLayout.Gray8), fullMask);
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new VisionTemplateBuild(_implementation, OpenCvPyramidTemplateFactory.FormatOf(_kind), d,
            request.Settings.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            new[] { new VisionTemplateArtifact("source/image.bin", source), new VisionTemplateArtifact("source/mask.bin", VisionTemplateSource.Encode(maskImage, token)),
                new VisionTemplateArtifact(OpenCvPyramidTemplateFactory.ModelFile, model.Encode()) }));
    }

    private OpenCvPyramidModel Build(IImageSource image, byte[] fullMask, VisionTemplateDefinition d, OpenCvPyramidSettings settings, CancellationToken token)
    {
        int requested = settings.Levels == 0 ? OpenCvPyramidModel.AutoLevels(_kind, d.Width, d.Height) : settings.Levels;
        var levels = new List<OpenCvPyramidLevel>();
        var gray = CvPixels.Gray(image);
        var mask = new Mat(d.SourceHeight, d.SourceWidth, MatType.CV_8UC1);
        try
        {
            Marshal.Copy(fullMask, 0, mask.Data, fullMask.Length);
            for (int l = 0; l < requested; l++)
            {
                token.ThrowIfCancellationRequested();
                if (l > 0)
                {
                    var g = new Mat(); Cv2.PyrDown(gray, g); gray.Dispose(); gray = g;
                    var m = new Mat(); Cv2.PyrDown(mask, m); mask.Dispose(); mask = m;
                    Cv2.Threshold(mask, mask, 127, 255, ThresholdTypes.Binary);
                }
                var level = Level(gray, mask, d, l, settings, out var problem);
                if (level == null)
                {
                    if (l == 0 || settings.Levels != 0) throw new ArgumentException(l == 0 ? problem : $"第{l + 1}层金字塔{problem}请减少金字塔层数。");
                    break;
                }
                levels.Add(level);
            }
        }
        finally { gray.Dispose(); mask.Dispose(); }
        return new OpenCvPyramidModel { Kind = _kind, Width = d.Width, Height = d.Height, Settings = settings, Levels = levels.ToArray() };
    }

    private OpenCvPyramidLevel? Level(Mat gray, Mat mask, VisionTemplateDefinition d, int l, OpenCvPyramidSettings settings, out string problem)
    {
        double f = Math.Pow(2, l);
        int x0 = (int)Math.Floor(d.X / f), y0 = (int)Math.Floor(d.Y / f);
        int x1 = Math.Min(gray.Cols, (int)Math.Ceiling((d.X + d.Width) / f)), y1 = Math.Min(gray.Rows, (int)Math.Ceiling((d.Y + d.Height) / f));
        problem = "模板有效区域过小。";
        if (x1 - x0 < 2 || y1 - y0 < 2) return null;
        var rect = new Rect(x0, y0, x1 - x0, y1 - y0);
        using var crop = new Mat(gray, rect); using var cropMask = new Mat(mask, rect);
        var level = new OpenCvPyramidLevel
        {
            Width = rect.Width, Height = rect.Height, CenterX = (d.X + d.Width / 2d) / f - x0, CenterY = (d.Y + d.Height / 2d) / f - y0,
            Pixels = OpenCvTemplateModelBuilder.Bytes(crop), Mask = OpenCvTemplateModelBuilder.Bytes(cropMask)
        };
        int valid = level.Mask.Count(b => b != 0);
        if (valid < 16) return null;
        if (_kind == EOpenCvPyramidTemplateKind.Ncc)
        {
            Cv2.MeanStdDev(crop, out _, out var std, cropMask);
            problem = "模板灰度几乎没有变化，NCC无法区分位置。";
            return std.Val0 < (l == 0 ? 2 : 1) ? null : level;
        }
        // 形状特征在整幅样图的本层图像上求梯度，避免模板裁剪边界产生虚假边缘。
        var edges = OpenCvShapeFeatures.Extract(gray, rect, cropMask, settings.Contrast);
        var chosen = OpenCvShapeFeatures.Thin(edges, l == 0 ? 4096 : 1024);
        problem = $"边缘特征只有{chosen.Count}个（至少{OpenCvPyramidModel.MinimumShapeFeatures}个），请降低制作对比度或改用更清晰的模板区域。";
        if (chosen.Count < OpenCvPyramidModel.MinimumShapeFeatures) return null;
        if (l > 0 && settings.Levels == 0 && chosen.Count < OpenCvPyramidModel.AutoShapeFeatures) return null;
        int n = chosen.Count;
        level.Qx = new float[n]; level.Qy = new float[n]; level.Ux = new float[n]; level.Uy = new float[n];
        for (int i = 0; i < n; i++)
        {
            var e = chosen[i];
            level.Qx[i] = (float)(e.X + .5 - level.CenterX); level.Qy[i] = (float)(e.Y + .5 - level.CenterY); level.Ux[i] = e.Ux; level.Uy[i] = e.Uy;
        }
        return level;
    }
}

/// <summary>梯度边缘点提取：Sobel梯度、沿梯度方向非极大值抑制、按幅值和间距稀疏化。</summary>
internal static class OpenCvShapeFeatures
{
    internal readonly struct Edge
    {
        internal Edge(int x, int y, float magnitude, float ux, float uy) { X = x; Y = y; Magnitude = magnitude; Ux = ux; Uy = uy; }
        internal int X { get; }
        internal int Y { get; }
        internal float Magnitude { get; }
        internal float Ux { get; }
        internal float Uy { get; }
    }

    /// <summary>
    /// 在image的rect内提取边缘点（坐标相对rect）：幅值为Sobel/4，近似边缘两侧灰度差；只保留掩码内、达到对比度并且沿梯度方向是局部最大的点。
    /// </summary>
    internal static List<Edge> Extract(Mat image, Rect rect, Mat? rectMask, int contrast)
    {
        // 外扩1像素求梯度，rect边缘的点也使用真实邻域。
        var outer = new Rect(Math.Max(0, rect.X - 2), Math.Max(0, rect.Y - 2), 0, 0);
        outer.Width = Math.Min(image.Cols, rect.X + rect.Width + 2) - outer.X; outer.Height = Math.Min(image.Rows, rect.Y + rect.Height + 2) - outer.Y;
        using var roi = new Mat(image, outer);
        using var gx = new Mat(); using var gy = new Mat();
        Cv2.Sobel(roi, gx, MatType.CV_32F, 1, 0, 3, .25, 0, BorderTypes.Replicate);
        Cv2.Sobel(roi, gy, MatType.CV_32F, 0, 1, 3, .25, 0, BorderTypes.Replicate);
        int w = outer.Width, h = outer.Height;
        var fx = new float[w * h]; var fy = new float[w * h];
        for (int y = 0; y < h; y++) { Marshal.Copy(gx.Ptr(y), fx, y * w, w); Marshal.Copy(gy.Ptr(y), fy, y * w, w); }
        var mag = new float[w * h];
        for (int i = 0; i < mag.Length; i++) mag[i] = (float)Math.Sqrt(fx[i] * fx[i] + fy[i] * fy[i]);
        byte[]? mask = rectMask == null ? null : OpenCvTemplateModelBuilder.Bytes(rectMask);
        var result = new List<Edge>();
        int dxo = rect.X - outer.X, dyo = rect.Y - outer.Y;
        for (int y = 0; y < rect.Height; y++)
        for (int x = 0; x < rect.Width; x++)
        {
            if (mask != null && mask[y * rect.Width + x] == 0) continue;
            int ox = x + dxo, oy = y + dyo, i = oy * w + ox;
            float m = mag[i]; if (m < contrast) continue;
            if (ox < 1 || oy < 1 || ox >= w - 1 || oy >= h - 1) continue;
            // 梯度方向量化到0/45/90/135°，与两侧邻点比较。
            double angle = Math.Atan2(fy[i], fx[i]); if (angle < 0) angle += Math.PI;
            int sector = (int)Math.Round(angle / (Math.PI / 4)) % 4;
            int step = sector switch { 0 => 1, 1 => w + 1, 2 => w, _ => w - 1 };
            if (m < mag[i - step] || m <= mag[i + step]) continue;
            result.Add(new Edge(x, y, m, fx[i] / m, fy[i] / m));
        }
        return result;
    }

    /// <summary>超过上限时按幅值优先、保持最小间距地稀疏化，并打乱顺序使任意前缀都均匀覆盖模板（顶层和精化层按前缀取均匀子集）。</summary>
    internal static List<Edge> Thin(List<Edge> edges, int maximum)
    {
        var chosen = edges;
        if (edges.Count > maximum)
        {
            var sorted = edges.OrderByDescending(e => e.Magnitude).ThenBy(e => e.Y).ThenBy(e => e.X).ToList();
            double distance = Math.Sqrt(edges.Count / (double)maximum);
            while (true)
            {
                chosen = Spread(sorted, distance);
                if (chosen.Count <= maximum) break;
                distance *= 1.15;
            }
        }
        // 确定性的均匀打乱：按黄金比例步长遍历。
        int n = chosen.Count; if (n < 3) return chosen.ToList();
        int stride = Math.Max(1, (int)(n * .6180339887)); while (Gcd(stride, n) != 1) stride++;
        var ordered = new List<Edge>(n);
        for (int i = 0, k = 0; i < n; i++, k = (k + stride) % n) ordered.Add(chosen[k]);
        return ordered;
    }

    private static List<Edge> Spread(List<Edge> sorted, double distance)
    {
        double d2 = distance * distance; int cell = Math.Max(1, (int)Math.Ceiling(distance));
        var grid = new Dictionary<long, List<Edge>>(); var result = new List<Edge>();
        foreach (var e in sorted)
        {
            int cx = e.X / cell, cy = e.Y / cell; bool near = false;
            for (int gy = cy - 1; gy <= cy + 1 && !near; gy++)
            for (int gx = cx - 1; gx <= cx + 1 && !near; gx++)
                if (grid.TryGetValue(((long)gy << 32) ^ (uint)gx, out var list))
                    foreach (var o in list) if ((o.X - e.X) * (o.X - e.X) + (o.Y - e.Y) * (o.Y - e.Y) < d2) { near = true; break; }
            if (near) continue;
            long key = ((long)cy << 32) ^ (uint)cx;
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = new List<Edge>();
            bucket.Add(e); result.Add(e);
        }
        return result;
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}

/// <summary>金字塔模板工厂：制作描述、资源检查和匹配器准备；NCC与形状共用。</summary>
public sealed class OpenCvPyramidTemplateFactory : IVisionAlgorithmFactory, IVisionAlgorithmConfigurationValidator, IVisionTemplateFactoryDescription, IVisionTemplatePreviewFactory, IVisionAlgorithmResourceInspector, IVisionTemplateSearchValidator
{
    internal const string ModelFile = "variants/opencv-pyramid/model.bin";
    private readonly EOpenCvPyramidTemplateKind _kind;
    /// <summary>创建指定种类的金字塔模板工厂。</summary>
    public OpenCvPyramidTemplateFactory(EOpenCvPyramidTemplateKind kind) => _kind = kind;
    internal static string FormatOf(EOpenCvPyramidTemplateKind kind) => kind == EOpenCvPyramidTemplateKind.Shape ? "opencv.shape-pyramid.v1" : "opencv.ncc-pyramid.v1";
    /// <summary>模型配套实现身份。</summary>
    public string ImplementationId => _kind == EOpenCvPyramidTemplateKind.Shape ? "opencv.template-shape-model" : "opencv.template-ncc-model";
    /// <inheritdoc/>
    public string BuilderImplementationId => ImplementationId + ".build";
    /// <inheritdoc/>
    public string MethodDisplayName => _kind == EOpenCvPyramidTemplateKind.Shape ? "形状匹配（梯度方向·金字塔）" : "灰度NCC（金字塔）";
    /// <inheritdoc/>
    public Type ContractType => typeof(IPreparedVisionTemplateMatcher);

    /// <inheritdoc/>
    public IReadOnlyList<VisionAlgorithmParameter> BuildParameters
    {
        get
        {
            var list = new List<VisionAlgorithmParameter>
            {
                new VisionAlgorithmParameter("levels", "金字塔层数", typeof(int), "0", "0自动（顶层模板约不小于16×16像素）；1表示不用金字塔，逐像素全分辨率搜索。", 0, 6),
                new VisionAlgorithmParameter("minimumAngle", "模型最小角度", typeof(double), (-Math.PI).ToString("R", CultureInfo.InvariantCulture), "相对制作样图的顺时针旋转下限；匹配节点的搜索区间须包含于该范围。各层角度步长按模板尺寸自动确定。", -Math.PI, Math.PI) { DisplayRadiansAsDegrees = true },
                new VisionAlgorithmParameter("maximumAngle", "模型最大角度", typeof(double), Math.PI.ToString("R", CultureInfo.InvariantCulture), "相对制作样图的顺时针旋转上限。", -Math.PI, Math.PI) { DisplayRadiansAsDegrees = true },
                new VisionAlgorithmParameter("minimumScale", "模型最小尺度", typeof(double), "0.1", minimum: .1, maximum: 10),
                new VisionAlgorithmParameter("maximumScale", "模型最大尺度", typeof(double), "10", minimum: .1, maximum: 10)
            };
            if (_kind == EOpenCvPyramidTemplateKind.Shape) list.AddRange(new[]
            {
                new VisionAlgorithmParameter("contrast", "制作对比度", typeof(int), "30", "模型边缘点的最小灰度差（Sobel幅值/4）。", 1, 255),
                new VisionAlgorithmParameter("minimumContrast", "搜索最小对比度", typeof(int), "10", "搜索图像中低于此灰度差的梯度视为噪声，不参与评分。", 1, 255),
                new VisionAlgorithmParameter("metric", "极性方式", typeof(string), "use_polarity", "use_polarity（明暗方向须一致）、ignore_global_polarity（允许整体反色）或ignore_local_polarity（逐点忽略明暗）。"),
                new VisionAlgorithmParameter("subPixel", "亚像素精化", typeof(string), "least_squares", "interpolation（分数抛物线插值）或least_squares（边缘点到边缘线的最小二乘，默认，更准）。")
            });
            return list;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ValidateSearch(VisionTemplateDefinition definition, IReadOnlyDictionary<string, string> settings, PixelBounds search, TemplatePoseOptions options)
    {
        try
        {
            var parsed = OpenCvPyramidSettings.Parse(_kind, settings);
            int levels = parsed.Levels == 0 ? OpenCvPyramidModel.AutoLevels(_kind, definition.Width, definition.Height) : parsed.Levels;
            OpenCvPyramidPlan.Create(_kind, definition.Width, definition.Height, levels, parsed, search, options);
            return Array.Empty<string>();
        }
        catch (ArgumentException ex) { return new[] { ex.Message }; }
        catch (InvalidOperationException ex) { return new[] { ex.Message }; }
    }

    /// <inheritdoc/>
    public IReadOnlyList<VisionAlgorithmDependency> GetDependencies(VisionAlgorithmConfiguration configuration) => Array.Empty<VisionAlgorithmDependency>();
    /// <inheritdoc/>
    public IReadOnlyList<string> ValidateConfiguration(VisionAlgorithmConfiguration configuration) =>
        VisionAlgorithmConfigurationRules.ValidateVersionOne(configuration, new[] { "templatePath" }, "templatePath");
    /// <inheritdoc/>
    public IReadOnlyList<string> InspectResources(VisionAlgorithmConfiguration configuration)
    {
        if (!configuration.Settings.TryGetValue("templatePath", out var path) || string.IsNullOrWhiteSpace(path)) return new[] { "缺少模板资源引用。" };
        CheckManifest(VisionTemplateStore.Inspect(path)); return VisionTemplateStore.InspectFiles(path);
    }
    private void CheckManifest(VisionTemplateManifest manifest)
    {
        if (manifest.ImplementationId != ImplementationId || manifest.ModelFormat != FormatOf(_kind)) throw new InvalidDataException("模板模型与所选引擎、匹配方式或格式不兼容，请重新制作。");
    }
    private IPreparedVisionTemplateMatcher Create(VisionTemplateSnapshot snapshot, OpenCvPyramidModel model) => _kind == EOpenCvPyramidTemplateKind.Shape
        ? new OpenCvShapeTemplateMatcher(snapshot, model) : new OpenCvNccTemplateMatcher(snapshot, model);
    /// <inheritdoc/>
    public Task<VisionAlgorithmResource> PreparePreviewAsync(VisionTemplateBuild build, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var snapshot = VisionTemplateStore.CaptureBuild(build); CheckManifest(snapshot.Manifest);
        return Task.FromResult(new VisionAlgorithmResource(Create(snapshot, OpenCvPyramidModel.Decode(_kind, snapshot))));
    }
    /// <inheritdoc/>
    public Task<VisionAlgorithmActivation> PrepareAsync(VisionAlgorithmConfiguration configuration, IReadOnlyDictionary<string, object> dependencies, CancellationToken cancellationToken)
    {
        var errors = ValidateConfiguration(configuration); if (errors.Count > 0) throw new ArgumentException(string.Join("；", errors));
        var snapshot = VisionTemplateStore.Capture(configuration.Settings["templatePath"], cancellationToken); CheckManifest(snapshot.Manifest);
        var model = OpenCvPyramidModel.Decode(_kind, snapshot); _ = Cv2.GetVersionString();
        return Task.FromResult(new VisionAlgorithmActivation(snapshot.Identity, EVisionAlgorithmSharing.SharedSerial, token =>
        { token.ThrowIfCancellationRequested(); return Task.FromResult(new VisionAlgorithmResource(Create(snapshot, model))); }));
    }
}
