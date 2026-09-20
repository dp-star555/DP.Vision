using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>沿采样方向的边缘极性。</summary>
public enum ECaliperPolarity
{
    /// <summary>两种极性。</summary>
    Any,
    /// <summary>由暗到亮。</summary>
    Rising,
    /// <summary>由亮到暗。</summary>
    Falling
}

/// <summary>一维卡尺采样配置；所有采样中心必须处于图像像素中心范围。</summary>
public sealed class CaliperOptions
{
    /// <summary>构造采样带。</summary>
    /// <param name="start">采样起点，原图边界坐标。</param><param name="end">采样终点。</param>
    /// <param name="halfWidth">垂直方向半宽，按1px采样均值。</param><param name="minimumGradient">最小绝对灰度梯度/像素。</param>
    /// <param name="polarity">极性。</param><param name="minimumSeparation">边缘最小间距，像素。</param>
    /// <param name="bandSampleStep">垂直采样间隔，原图像素，0.1..10；默认1，定位缩放时显式设置。</param>
    public CaliperOptions(PointD start, PointD end, int halfWidth = 2, double minimumGradient = 5, ECaliperPolarity polarity = ECaliperPolarity.Any, double minimumSeparation = 2, double bandSampleStep = 1)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (!Finite(start.X) || !Finite(start.Y) || !Finite(end.X) || !Finite(end.Y) || !Finite(length) || length < 4 || length > 65535
            || halfWidth < 0 || halfWidth > 63 || !Finite(minimumGradient) || minimumGradient <= 0 || !Finite(minimumSeparation) || minimumSeparation <= 0
            || !Finite(bandSampleStep) || bandSampleStep < .1 || bandSampleStep > 10
            || !Enum.IsDefined(typeof(ECaliperPolarity), polarity)) throw new ArgumentException("Invalid caliper sampling configuration.");
        BandSampleStep = bandSampleStep;
        Start = start; End = end; HalfWidth = halfWidth; MinimumGradient = minimumGradient; Polarity = polarity; MinimumSeparation = minimumSeparation;
    }
    private static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    /// <summary>起点。</summary>
    public PointD Start { get; }
    /// <summary>终点。</summary>
    public PointD End { get; }
    /// <summary>垂直方向采样间隔，原图像素。</summary>
    public double BandSampleStep { get; }
    /// <summary>单侧采样步数；物理半宽为HalfWidth×BandSampleStep。</summary>
    public int HalfWidth { get; }
    /// <summary>最小梯度。</summary>
    public double MinimumGradient { get; }
    /// <summary>边缘极性。</summary>
    public ECaliperPolarity Polarity { get; }
    /// <summary>非极大抑制间距。</summary>
    public double MinimumSeparation { get; }
}

/// <summary>梯度峰抛物线插值得到的亚像素边缘事实，不承诺设备测量精度。</summary>
public sealed class CaliperEdge
{
    internal CaliperEdge(PointD position, double distance, double gradient) { Position = position; Distance = distance; Gradient = gradient; }
    /// <summary>原图像素边界坐标。</summary>
    public PointD Position { get; }
    /// <summary>沿采样方向距离。</summary>
    public double Distance { get; }
    /// <summary>有符号梯度，灰度/像素。</summary>
    public double Gradient { get; }
}

/// <summary>卡尺证据，包含完整一维灰度剖面，空边缘集合正常完成。</summary>
public sealed class CaliperResult
{
    internal CaliperResult(string frameId, CaliperOptions options, double step, double[] profile, IEnumerable<CaliperEdge> edges)
    { FrameId = frameId; Start = options.Start; End = options.End; SampleStep = step; Profile = Array.AsReadOnly((double[])profile.Clone()); Edges = Array.AsReadOnly(edges.ToArray()); }
    /// <summary>可选本帧定位来源；原有端点/边缘/距离均保持原图像素。</summary>
    public LocatedCoordinateSystem? CoordinateSystem { get; private set; }
    /// <summary>与Edges同序的双坐标边缘；未绑定时为空。</summary>
    public IReadOnlyList<LocatedPoint>? LocatedEdges { get; private set; }
    /// <summary>采样起点的双坐标。</summary>
    public LocatedPoint? LocatedStart => CoordinateSystem?.Locate(Start);
    /// <summary>采样终点的双坐标。</summary>
    public LocatedPoint? LocatedEnd => CoordinateSystem?.Locate(End);
    /// <summary>附加定位坐标，不改变不可变采样证据。</summary><param name="system">同帧定位。</param><returns>独立结果。</returns>
    public CaliperResult InCoordinates(LocatedCoordinateSystem system)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (system.FrameId != FrameId) throw new InvalidOperationException("Result coordinate frame mismatch.");
        var copy = (CaliperResult)MemberwiseClone(); copy.CoordinateSystem = system;
        copy.LocatedEdges = Array.AsReadOnly(Edges.Select(e => system.Locate(e.Position)).ToArray()); return copy;
    }
    /// <summary>输入帧。</summary>
    public string FrameId { get; }
    /// <summary>采样起点。</summary>
    public PointD Start { get; }
    /// <summary>采样终点。</summary>
    public PointD End { get; }
    /// <summary>均匀采样步长。</summary>
    public double SampleStep { get; }
    /// <summary>一维灰度均值。</summary>
    public IReadOnlyList<double> Profile { get; }
    /// <summary>按采样距离排序的边缘。</summary>
    public IReadOnlyList<CaliperEdge> Edges { get; }
    /// <summary>边缘数。</summary>
    public int Count => Edges.Count;
    /// <summary>完成状态，空边缘不是执行错误。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
}

/// <summary>独立采样带卡尺。</summary>
public interface ICaliperMeasurer
{
    /// <summary>仅接受Gray8，不隐式滤波、裁剪或转换位深。</summary>
    /// <param name="frame">借用原图。</param><param name="options">采样配置。</param><param name="token">取消。</param><returns>同帧采样证据。</returns>
    CaliperResult Measure(ImageFrame frame, CaliperOptions options, CancellationToken token = default);
}

/// <summary>双线性带采样、中心差分、梯度极大值抛物线插值及确定性间距抑制。</summary>
public sealed class CaliperMeasurer : ICaliperMeasurer
{
    /// <inheritdoc/>
    public CaliperResult Measure(ImageFrame frame, CaliperOptions options, CancellationToken token = default)
    {
        if (frame == null || options == null) throw new ArgumentNullException(nameof(frame));
        token.ThrowIfCancellationRequested();
        var info = frame.Image.Info;
        if (info.Layout != EPixelLayout.Gray8) throw new NotSupportedException("Caliper requires explicit Gray8 preprocessing.");
        if ((long)info.Width * info.Height > 16777216) throw new ArgumentException("Caliper image budget exceeded.");
        double dx = options.End.X - options.Start.X, dy = options.End.Y - options.Start.Y, length = Math.Sqrt(dx * dx + dy * dy);
        dx /= length; dy /= length;
        foreach (var p in new[] { options.Start, options.End })
            foreach (int sign in new[] { -1, 1 })
            {
                double x = p.X - dy * options.HalfWidth * options.BandSampleStep * sign, y = p.Y + dx * options.HalfWidth * options.BandSampleStep * sign;
                if (x < .5 || y < .5 || x > info.Width - .5 || y > info.Height - .5) throw new ArgumentException("Caliper band extends outside sampleable pixel centers.");
            }
        var pixels = new byte[info.ByteLength]; frame.Image.CopyTo(0, pixels, 0, pixels.Length);
        int count = (int)Math.Ceiling(length) + 1; double step = length / (count - 1);
        var profile = new double[count]; var gradient = new double[count];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            for (int b = -options.HalfWidth; b <= options.HalfWidth; b++)
                profile[i] += Sample(pixels, info.Width, info.Height, options.Start.X + dx * i * step - dy * b * options.BandSampleStep, options.Start.Y + dy * i * step + dx * b * options.BandSampleStep);
            profile[i] /= options.HalfWidth * 2 + 1;
        }
        for (int i = 1; i < count - 1; i++) gradient[i] = (profile[i + 1] - profile[i - 1]) / (2 * step);
        var candidates = new List<CaliperEdge>();
        for (int i = 2; i < count - 2; i++)
        {
            token.ThrowIfCancellationRequested();
            double g = gradient[i], strength = Math.Abs(g), left = Math.Abs(gradient[i - 1]), right = Math.Abs(gradient[i + 1]);
            if (strength < options.MinimumGradient || strength < left || strength <= right
                || options.Polarity == ECaliperPolarity.Rising && g <= 0 || options.Polarity == ECaliperPolarity.Falling && g >= 0) continue;
            double denominator = left - 2 * strength + right;
            double delta = Math.Abs(denominator) < 1e-12 ? 0 : Math.Max(-.5, Math.Min(.5, .5 * (left - right) / denominator));
            double distance = (i + delta) * step;
            candidates.Add(new CaliperEdge(new PointD(options.Start.X + dx * distance, options.Start.Y + dy * distance), distance, g));
        }
        // 距离索引限制抑制查询，避免对所有已选边缘逐个扫描。
        var selected = new SortedSet<double>(); var edges = new List<CaliperEdge>();
        foreach (var edge in candidates.OrderByDescending(e => Math.Abs(e.Gradient)).ThenBy(e => e.Distance))
        {
            token.ThrowIfCancellationRequested();
            if (selected.GetViewBetween(edge.Distance - options.MinimumSeparation, edge.Distance + options.MinimumSeparation)
                .Any(distance => Math.Abs(distance - edge.Distance) < options.MinimumSeparation)) continue;
            if (edges.Count >= 4096) throw new InvalidOperationException("Caliper evidence budget exceeded.");
            selected.Add(edge.Distance); edges.Add(edge);
        }
        return new CaliperResult(frame.FrameId, options, step, profile, edges.OrderBy(e => e.Distance));
    }

    private static double Sample(byte[] pixels, int width, int height, double x, double y)
    {
        x = Math.Max(0, Math.Min(width - 1, x - .5)); y = Math.Max(0, Math.Min(height - 1, y - .5));
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), nx = Math.Min(width - 1, ix + 1), ny = Math.Min(height - 1, iy + 1);
        double fx = x - ix, fy = y - iy;
        return (pixels[iy * width + ix] * (1 - fx) + pixels[iy * width + nx] * fx) * (1 - fy)
            + (pixels[ny * width + ix] * (1 - fx) + pixels[ny * width + nx] * fx) * fy;
    }
}
