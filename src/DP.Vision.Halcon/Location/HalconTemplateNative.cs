using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
#if HALCON_SDK
using HalconDotNet;
#endif

namespace DP.Vision.Halcon;

internal interface IHalconPreparedTemplateMatcher : IPreparedVisionTemplateMatcher, IDisposable { }

internal static class HalconTemplateNative
{
    internal static void CheckEnvironment()
    {
#if HALCON_SDK
        using var image = new HImage(); image.GenImageConst("byte", 1, 1);
#else
        throw MissingSdk();
#endif
    }
    internal static VisionTemplateBuild Build(VisionTemplateBuildRequest request, bool shape, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if HALCON_SDK
        var definition = VisionTemplateStore.CopyDefinition(request.Definition); definition.Validate();
        var image = request.Source.Image;
        if (image.Info.Width != definition.SourceWidth || image.Info.Height != definition.SourceHeight) throw new ArgumentException("样图尺寸与模板定义不一致。");
        var settings = HalconTemplateSettings.Parse(request.Settings, shape);
        InspectionMask.Validate(request.Mask, image);
        var pixels = GrayBytes(image, token);
        var mask = new byte[definition.SourceWidth * definition.SourceHeight];
        for (int y = definition.Y; y < definition.Y + definition.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = definition.X; x < definition.X + definition.Width; x++)
                if (request.Mask == null || request.Mask.Contains(new PointD(x + .5, y + .5))) mask[y * definition.SourceWidth + x] = 255;
        }
        var local = CropMask(mask, definition);
        using var full = Image(pixels, definition.SourceWidth, definition.SourceHeight);
        using var crop = full.CropPart(definition.Y, definition.X, definition.Width, definition.Height);
        using var region = Region(local, definition.Width, definition.Height);
        using var domain = crop.ReduceDomain(region);
        if (region.AreaCenter(out double row, out double column) <= 0) throw new ArgumentException("模板有效区域为空。");
        byte[] native;
        using (var stream = new MemoryStream())
        {
            if (shape)
            {
                using var model = new HShapeModel();
                model.CreateScaledShapeModel(domain, settings.Levels, -settings.MaximumAngle, settings.MaximumAngle - settings.MinimumAngle,
                    settings.AngleStep, settings.MinimumScale, settings.MaximumScale, settings.ScaleStep, "auto", settings.Metric, settings.Contrast, settings.MinimumContrast);
                model.SetShapeModelOrigin((definition.Height - 1) / 2d - row, (definition.Width - 1) / 2d - column);
                model.Serialize(stream);
            }
            else
            {
                using var model = new HNCCModel();
                model.CreateNccModel(domain, settings.Levels, -settings.MaximumAngle, settings.MaximumAngle - settings.MinimumAngle, settings.AngleStep, settings.Metric);
                model.SetNccModelOrigin((definition.Height - 1) / 2d - row, (definition.Width - 1) / 2d - column);
                model.Serialize(stream);
            }
            native = stream.ToArray();
        }
        token.ThrowIfCancellationRequested();
        var factory = new HalconTemplateModelFactory(shape);
        using var maskImage = VisionImage.CopyFrom(new ImageInfo(definition.SourceWidth, definition.SourceHeight, EPixelLayout.Gray8), mask);
        return new VisionTemplateBuild(factory.ImplementationId, factory.Format, definition, request.Settings,
            new[] { new VisionTemplateArtifact("source/image.bin", VisionTemplateSource.Encode(image, token)),
                new VisionTemplateArtifact("source/mask.bin", VisionTemplateSource.Encode(maskImage, token)),
                new VisionTemplateArtifact(HalconTemplateModelFactory.ModelFile, native) });
#else
        throw MissingSdk();
#endif
    }
    internal static IHalconPreparedTemplateMatcher Load(VisionTemplateSnapshot snapshot, bool shape, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if HALCON_SDK
        return new HalconPreparedTemplateMatcher(snapshot, shape, token);
#else
        throw MissingSdk();
#endif
    }
#if HALCON_SDK
    internal static byte[] GrayBytes(IImageSource source, CancellationToken token)
        => GrayBytes(source, new PixelBounds(0, 0, source.Info.Width, source.Info.Height), token);

    /// <summary>把原图中指定矩形转换为连续灰度字节；灰度图按行整块复制，彩色图逐像素加权。</summary>
    internal static byte[] GrayBytes(IImageSource source, PixelBounds crop, CancellationToken token)
    {
        var info = source.Info;
        if (info.Layout != EPixelLayout.Gray8 && info.Layout != EPixelLayout.Bgr24 && info.Layout != EPixelLayout.Bgra32
            && info.Layout != EPixelLayout.Rgb24 && info.Layout != EPixelLayout.Rgba32)
            throw new NotSupportedException("HALCON模板仅支持8位灰度或RGB/BGR图像；16位图像需要显式转换。");
        if ((long)info.Width * info.Height > 16777216 || info.ByteLength > 64 * 1024 * 1024) throw new ArgumentException("HALCON模板图像超过16M像素或64MiB预算。");
        if (!crop.Fits(info.Width, info.Height)) throw new ArgumentOutOfRangeException(nameof(crop));
        int channels = info.Layout == EPixelLayout.Gray8 ? 1 : info.Layout == EPixelLayout.Bgr24 || info.Layout == EPixelLayout.Rgb24 ? 3 : 4;
        bool rgb = info.Layout == EPixelLayout.Rgb24 || info.Layout == EPixelLayout.Rgba32;
        var result = new byte[crop.Width * crop.Height]; var row = channels == 1 ? null : new byte[crop.Width * channels];
        for (int y = 0; y < crop.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            int offset = checked((crop.Y + y) * info.Stride + crop.X * channels);
            if (row == null) { source.CopyTo(offset, result, y * crop.Width, crop.Width); continue; }
            source.CopyTo(offset, row, 0, row.Length);
            for (int x = 0; x < crop.Width; x++) result[y * crop.Width + x] =
                (byte)(((rgb ? 77 : 29) * row[x * channels] + 150 * row[x * channels + 1] + (rgb ? 29 : 77) * row[x * channels + 2] + 128) >> 8);
        }
        return result;
    }
    internal static HImage Image(byte[] pixels, int width, int height)
    {
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned); var image = new HImage();
        try { image.GenImage1("byte", width, height, pin.AddrOfPinnedObject()); return image; }
        catch { image.Dispose(); throw; }
        finally { pin.Free(); }
    }
    internal static byte[] CropMask(byte[] mask, VisionTemplateDefinition d)
    {
        var result = new byte[d.Width * d.Height];
        for (int y = 0; y < d.Height; y++) Array.Copy(mask, (y + d.Y) * d.SourceWidth + d.X, result, y * d.Width, d.Width);
        if (!result.Any(v => v == 255) || result.Any(v => v != 0 && v != 255)) throw new InvalidDataException("HALCON模板掩码无效或为空。");
        return result;
    }
    internal static HRegion Region(byte[] mask, int width, int height)
    {
        var runs = new List<RegionRun>();
        for (int y = 0; y < height; y++) for (int x = 0; x < width;)
        {
            if (mask[y * width + x] == 0) { x++; continue; }
            int start = x++; while (x < width && mask[y * width + x] != 0) x++;
            runs.Add(new RegionRun(y, start, x));
        }
        return Region(runs);
    }
    internal static HRegion Region(IEnumerable<RegionRun> runs)
    {
        var list = runs.ToArray(); var region = new HRegion();
        try
        {
            if (list.Length == 0) { region.GenEmptyRegion(); return region; }
            using var rows = new HTuple(list.Select(r => r.Row).ToArray());
            using var starts = new HTuple(list.Select(r => r.Start).ToArray());
            using var ends = new HTuple(list.Select(r => r.EndExclusive - 1).ToArray());
            region.GenRegionRuns(rows, starts, ends); return region;
        }
        catch { region.Dispose(); throw; }
    }
#else
    private static PlatformNotSupportedException MissingSdk() => new PlatformNotSupportedException("HALCON算法未装配SDK。请以HalconDotNetPath构建引擎，并部署匹配版本的HALCON运行环境和许可。");
#endif
}

#if HALCON_SDK
internal sealed class HalconPreparedTemplateMatcher : IHalconPreparedTemplateMatcher
{
    private readonly VisionTemplateDefinition _definition;
    private readonly HalconTemplateSettings _settings;
    private readonly byte[] _mask;
    private readonly HShapeModel? _shape;
    private readonly HNCCModel? _ncc;
    private bool _disposed;
    internal HalconPreparedTemplateMatcher(VisionTemplateSnapshot snapshot, bool shape, CancellationToken token)
    {
        _definition = VisionTemplateStore.CopyDefinition(snapshot.Manifest.Definition); _definition.Validate();
        _settings = HalconTemplateSettings.Parse(snapshot.Manifest.BuildSettings, shape); ModelIdentity = snapshot.Identity;
        using var mask = VisionTemplateSource.Decode(snapshot.Read("source/mask.bin"));
        if (mask.Info.Layout != EPixelLayout.Gray8 || mask.Info.Width != _definition.SourceWidth || mask.Info.Height != _definition.SourceHeight) throw new InvalidDataException("HALCON模板掩码尺寸不一致。");
        var pixels = new byte[mask.Info.ByteLength]; mask.CopyTo(0, pixels, 0, pixels.Length); _mask = HalconTemplateNative.CropMask(pixels, _definition);
        using var stream = new MemoryStream(snapshot.Read(HalconTemplateModelFactory.ModelFile), false);
        try
        {
            if (shape) _shape = HShapeModel.Deserialize(stream);
            else _ncc = HNCCModel.Deserialize(stream);
            token.ThrowIfCancellationRequested();
        }
        catch { _shape?.Dispose(); _ncc?.Dispose(); throw; }
    }
    public VisionTemplateDefinition Definition => VisionTemplateStore.CopyDefinition(_definition);
    public string ModelIdentity { get; }
    public TemplatePoseResult Match(ImageFrame frame, PixelBounds search, TemplatePoseOptions options, RegionGeometry? region = null, CancellationToken token = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HalconPreparedTemplateMatcher));
        token.ThrowIfCancellationRequested();
        if (!search.Fits(frame.Image)) throw new ArgumentException("HALCON搜索范围越界。");
        _settings.ValidateSearch(options, _shape != null); InspectionMask.Validate(region, frame.Image);
        var allowed = new RegionGeometry(Enumerable.Range(search.Y, search.Height).Select(y => new RegionRun(y, search.X, search.X + search.Width)));
        if (region != null) allowed = allowed.Intersect(region, token);
        if (allowed.AreaPixels == 0) return new TemplatePoseResult(frame.FrameId, ModelIdentity, 0, null);
        // 只把允许区域外接框（外扩一圈边距并按64像素对齐，保持金字塔网格与原图一致）交给HALCON，
        // 小ROI不再转换和建立整幅图金字塔；结果坐标加回裁剪原点。
        var crop = Crop(allowed.Bounds, frame.Image.Info.Width, frame.Image.Info.Height);
        var fit = new FitArea(allowed, search, region == null);
        using var image = HalconTemplateNative.Image(HalconTemplateNative.GrayBytes(frame.Image, crop, token), crop.Width, crop.Height);
        using var nativeRegion = HalconTemplateNative.Region(allowed.Runs.Select(r => new RegionRun(r.Row - crop.Y, r.Start - crop.X, r.EndExclusive - crop.X)));
        using var domain = image.ReduceDomain(nativeRegion);
        TemplatePoseTransform? best = null; double score = 0; long validationWork = 0;
        foreach (var interval in options.AngleIntervals())
        {
            token.ThrowIfCancellationRequested();
            HTuple rows, columns, angles, scores; HTuple? scales = null;
            if (_shape != null) domain.FindScaledShapeModel(_shape, -interval.Maximum, interval.Maximum - interval.Minimum,
                options.MinimumScale, options.MaximumScale, options.MinimumScore, 1025, 1, "interpolation", 0, .9,
                out rows, out columns, out angles, out scales, out scores);
            else domain.FindNccModel(_ncc!, -interval.Maximum, interval.Maximum - interval.Minimum, options.MinimumScore, 1025, 1, "true", 0, out rows, out columns, out angles, out scores);
            using (rows) using (columns) using (angles) using (scales) using (scores)
            {
                token.ThrowIfCancellationRequested();
                if (scores.Length >= 1025) throw new InvalidOperationException("HALCON匹配候选超过1024，请缩小搜索ROI或提高最小分数。");
                for (int i = 0; i < scores.Length; i++)
                {
                    // 返回的Row/Column是HALCON仿射矩阵平移量。先按affine_trans_pixel转换原点，
                    // 再进入共同像素边界坐标；旋转/尺度下不能直接给Row/Column加0.5。
                    double actualAngle = -angles[i].D, actualScale = scales?[i].D ?? 1;
                    double c = Math.Cos(actualAngle), s = Math.Sin(actualAngle);
                    var pose = new TemplatePoseTransform(_definition.Width, _definition.Height,
                        new PointD(crop.X + columns[i].D + .5 * actualScale * (c - s), crop.Y + rows[i].D + .5 * actualScale * (s + c)), actualAngle, actualScale);
                    double value = Math.Max(0, Math.Min(1, scores[i].D));
                    if (value < score || !FitsMask(pose, fit, options.MaximumWork, ref validationWork, token)) continue;
                    best = pose; score = value;
                }
            }
        }
        return new TemplatePoseResult(frame.FrameId, ModelIdentity, score, best);
    }
    // 外接框外扩32像素、原点按64对齐（最多6层金字塔），裁剪到原图内。
    private static PixelBounds Crop(RectD bounds, int width, int height)
    {
        int x0 = Math.Max(0, ((int)bounds.X - 32) / 64 * 64), y0 = Math.Max(0, ((int)bounds.Y - 32) / 64 * 64);
        int x1 = Math.Min(width, (int)(bounds.X + bounds.Width) + 32), y1 = Math.Min(height, (int)(bounds.Y + bounds.Height) + 32);
        return new PixelBounds(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>候选验证用的允许区域：纯矩形时只比较角点，否则在外接框内建一次位图供所有候选查表。</summary>
    private sealed class FitArea
    {
        internal FitArea(RegionGeometry allowed, PixelBounds search, bool rectangle)
        {
            Rectangle = rectangle; Search = search;
            if (rectangle) return;
            var b = allowed.Bounds; X = (int)b.X; Y = (int)b.Y; Width = (int)b.Width; Height = (int)b.Height;
            Bits = new byte[Width * Height];
            foreach (var run in allowed.Runs) for (int x = run.Start; x < run.EndExclusive; x++) Bits[(run.Row - Y) * Width + x - X] = 1;
        }
        internal readonly bool Rectangle; internal readonly PixelBounds Search;
        internal readonly int X, Y, Width, Height; internal readonly byte[]? Bits;
        internal bool Contains(int x, int y) => Rectangle
            ? x >= Search.X && x < Search.X + Search.Width && y >= Search.Y && y < Search.Y + Search.Height
            : x >= X && x < X + Width && y >= Y && y < Y + Height && Bits![(y - Y) * Width + x - X] != 0;
    }

    // HALCON搜索domain只约束模型原点；共同契约要求整个有效模板落在搜索区域内。
    private bool FitsMask(TemplatePoseTransform pose, FitArea allowed, long budget, ref long work, CancellationToken token)
    {
        var corners = new[] { pose.ToImage(new Coordinate2D(0, 0)), pose.ToImage(new Coordinate2D(_definition.Width, 0)),
            pose.ToImage(new Coordinate2D(0, _definition.Height)), pose.ToImage(new Coordinate2D(_definition.Width, _definition.Height)) };
        int x0 = (int)Math.Floor(corners.Min(p => p.X) + 1e-6), y0 = (int)Math.Floor(corners.Min(p => p.Y) + 1e-6),
            x1 = (int)Math.Ceiling(corners.Max(p => p.X) - 1e-6), y1 = (int)Math.Ceiling(corners.Max(p => p.Y) - 1e-6);
        // 纯矩形区域：模板外接框完全在矩形内即合法，不需要逐像素。
        if (allowed.Rectangle && allowed.Contains(x0, y0) && allowed.Contains(x1 - 1, y1 - 1)) return true;
        work = checked(work + (long)(x1 - x0) * (y1 - y0));
        if (work > budget) throw new InvalidOperationException("HALCON候选ROI验证预算超限，请缩小搜索ROI或减少候选。");
        for (int y = y0; y < y1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = x0; x < x1; x++)
            {
                var point = pose.ToTemplate(new Coordinate2D(x + .5, y + .5));
                int col = (int)Math.Floor(point.X + 1e-6), row = (int)Math.Floor(point.Y + 1e-6);
                if (col >= 0 && col < _definition.Width && row >= 0 && row < _definition.Height && _mask[row * _definition.Width + col] != 0
                    && !allowed.Contains(x, y)) return false;
            }
        }
        return true;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _shape?.Dispose(); _ncc?.Dispose(); }
}
#endif
