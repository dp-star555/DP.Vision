using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// 金字塔灰度NCC：分数为掩码内皮尔逊相关系数（与TM_CCOEFF_NORMED带掩码同义，负值记0），对线性光照变化不敏感。
/// 顶层一次CCORR加积分图近似分母产生候选；各精化层在小窗口内用精确掩码NCC。顶层旋转模板按姿态缓存，同一节点重复运行时不再旋转。
/// </summary>
internal sealed class OpenCvNccTemplateMatcher : OpenCvPyramidMatcherBase
{
    private readonly Mat[] _images, _masks;
    private readonly object _gate = new object();
    private readonly Dictionary<(int Level, double Angle, double Scale), TopTemplate> _topTemplates = new Dictionary<(int, double, double), TopTemplate>();
    private long _topBytes;
    private const long CacheBytes = 128L * 1024 * 1024;

    internal OpenCvNccTemplateMatcher(VisionTemplateSnapshot snapshot, OpenCvPyramidModel model) : base(snapshot, model)
    {
        _images = new Mat[model.Levels.Length]; _masks = new Mat[model.Levels.Length];
        try
        {
            for (int l = 0; l < model.Levels.Length; l++)
            {
                var level = model.Levels[l];
                _images[l] = ToMat(level.Width, level.Height, level.Pixels); _masks[l] = ToMat(level.Width, level.Height, level.Mask);
            }
            Contours = GrayContours(_images[0], _masks[0], model.Levels[0], model.Width, model.Height);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>显示轮廓：掩码内模板图像的Canny边缘（Otsu定阈值）以及非矩形掩码的边界，模板像素边界坐标。</summary>
    internal static IReadOnlyList<IReadOnlyList<Coordinate2D>> GrayContours(Mat image, Mat mask, OpenCvPyramidLevel level, int width, int height)
    {
        var result = new List<IReadOnlyList<Coordinate2D>>();
        using (var binary = new Mat())
        using (var edges = new Mat())
        {
            double high = Cv2.Threshold(image, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            Cv2.Canny(image, edges, Math.Max(1, high / 2), Math.Max(2, high));
            Cv2.BitwiseAnd(edges, mask, edges);
            Cv2.FindContours(edges, out Point[][] found, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            result.AddRange(found.Where(c => c.Length >= 4).Select(c => (IReadOnlyList<Coordinate2D>)c.Select(p => new Coordinate2D(p.X + .5, p.Y + .5)).ToArray()));
        }
        // 第0层裁剪即模板矩形，掩码可直接用于边界。
        if (level.Width == width && level.Height == height && !TemplateModelContours.IsFull(level.Mask))
            result.AddRange(TemplateModelContours.FromMask(level.Mask, width, height));
        return TemplateModelContours.Limit(result);
    }

    /// <summary>顶层某姿态的零均值掩码模板 T̃=掩码·(T-掩码均值)（CV_32F）及其能量和有效像素数。</summary>
    private sealed class TopTemplate
    {
        internal Mat Tilde = null!;
        internal double Energy;
        internal int Valid;
    }

    private sealed class State : SearchState
    {
        internal Mat[] Pyramid = Array.Empty<Mat>();
        internal Mat TopFloat = null!;
        internal double[] Sum = Array.Empty<double>(), Squares = Array.Empty<double>();
        internal int IntegralWidth;
        internal TopTemplate[] Templates = Array.Empty<TopTemplate>();
        internal bool Lazy;
        public override void Dispose() => TopFloat?.Dispose();
    }

    protected override SearchState Begin(Mat[] pyramid, OpenCvPyramidPlan plan, CancellationToken token)
    {
        var top = pyramid[plan.Top];
        var state = new State { Pyramid = pyramid, Templates = new TopTemplate[plan.TopPoses.Length], TopFloat = new Mat() };
        try
        {
            top.ConvertTo(state.TopFloat, MatType.CV_32F);
            // 顶层图积分图只算一次，所有姿态共用：外接框内的 ΣI、ΣI²。
            using (var sum = new Mat()) using (var squares = new Mat())
            {
                Cv2.Integral(top, sum, squares, MatType.CV_64F);
                state.IntegralWidth = sum.Cols;
                state.Sum = new double[sum.Rows * sum.Cols]; state.Squares = new double[state.Sum.Length];
                for (int y = 0; y < sum.Rows; y++)
                {
                    Marshal.Copy(sum.Ptr(y), state.Sum, y * sum.Cols, sum.Cols);
                    Marshal.Copy(squares.Ptr(y), state.Squares, y * sum.Cols, sum.Cols);
                }
            }
            long total = plan.TopPoses.Sum(p => 4L * p.Width * p.Height);
            // 全部姿态的旋转模板超过缓存上限（如不用金字塔、全分辨率整周搜索）时逐姿态临时生成，用完即释放，不常驻内存。
            if (total > CacheBytes) { state.Lazy = true; return state; }
            lock (_gate)
            {
                long need = 0;
                foreach (var p in plan.TopPoses) if (!_topTemplates.ContainsKey((plan.Top, p.Angle, p.Scale))) need += 4L * p.Width * p.Height;
                if (_topBytes + need > CacheBytes) ClearTop();
            }
            var parallel = new System.Threading.Tasks.ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount };
            System.Threading.Tasks.Parallel.For(0, plan.TopPoses.Length, parallel, i =>
            {
                var pose = plan.TopPoses[i]; var key = (plan.Top, pose.Angle, pose.Scale);
                TopTemplate? cached;
                lock (_gate) _topTemplates.TryGetValue(key, out cached);
                if (cached == null)
                {
                    cached = Tilde(plan.Top, pose);
                    lock (_gate)
                    {
                        if (_topTemplates.TryGetValue(key, out var other)) { cached.Tilde.Dispose(); cached = other; }
                        else { _topTemplates.Add(key, cached); _topBytes += 4L * pose.Width * pose.Height; }
                    }
                }
                state.Templates[i] = cached;
            });
            return state;
        }
        catch { state.Dispose(); throw; }
    }

    private TopTemplate Tilde(int level, PyramidPose pose)
    {
        using var warped = Warp(level, _images[level], pose, InterpolationFlags.Linear, BorderTypes.Replicate);
        using var mask8 = WarpMask(level, _masks[level], pose);
        int valid = Cv2.CountNonZero(mask8);
        var tilde = new Mat();
        if (valid == 0) { warped.ConvertTo(tilde, MatType.CV_32F, 0); return new TopTemplate { Tilde = tilde }; }
        using var mask = new Mat(); mask8.ConvertTo(mask, MatType.CV_32F, 1 / 255d);
        warped.ConvertTo(tilde, MatType.CV_32F, 1, -Cv2.Mean(warped, mask8).Val0);
        Cv2.Multiply(tilde, mask, tilde);
        return new TopTemplate { Tilde = tilde, Energy = tilde.Dot(tilde), Valid = valid };
    }

    /// <summary>
    /// 顶层候选分数：分子 Σ I·T̃ 精确（一次无掩码CCORR）；图像方差取外接框内的方差（积分图），再按掩码像素数归一：
    /// score ≈ ΣI·T̃ / (‖T̃‖·sqrt(n掩码·Var框))。假定框内背景与掩码内方差相近，只用于产生候选，精化层改用精确掩码NCC。
    /// 不用OpenCV的CCOEFF_NORMED：它每次调用都重算整幅积分图，代价约为CCORR的3倍，且分母按外接框会使细长模板旋转后分数被系统性压低。
    /// </summary>
    protected override unsafe void ScanTop(SearchState search, int index, PyramidPose pose, float threshold, float[] values, CancellationToken token)
    {
        var state = (State)search;
        var t = state.Lazy ? Tilde(state.Pyramid.Length - 1, pose) : state.Templates[index];
        using var scores = new Mat();
        try { Cv2.MatchTemplate(state.TopFloat, t.Tilde, scores, TemplateMatchModes.CCorr); }
        finally { if (state.Lazy) t.Tilde.Dispose(); }
        if (t.Valid == 0 || t.Energy < 1e-6) { Array.Clear(values, 0, scores.Rows * scores.Cols); return; }
        for (int y = 0; y < scores.Rows; y++) Marshal.Copy(scores.Ptr(y), values, y * scores.Cols, scores.Cols);
        int mw = scores.Cols, mh = scores.Rows, iw = state.IntegralWidth, bw = pose.Width, bh = pose.Height;
        double area = (double)bw * bh, inverse = 1 / Math.Sqrt(t.Energy * t.Valid), floor = 1e-6 * area * area;
        fixed (double* sum = state.Sum, squares = state.Squares)
        fixed (float* value = values)
        {
            for (int v = 0; v < mh; v++)
            {
                double* s1a = sum + v * iw, s1b = sum + (v + bh) * iw, s2a = squares + v * iw, s2b = squares + (v + bh) * iw;
                float* row = value + v * mw;
                for (int u = 0; u < mw; u++)
                {
                    float numerator = row[u];
                    if (!(numerator > 0)) { row[u] = 0; continue; }
                    double s1 = s1b[u + bw] - s1b[u] - s1a[u + bw] + s1a[u];
                    double s2 = s2b[u + bw] - s2b[u] - s2a[u + bw] + s2a[u];
                    // area²·Var框 = area·ΣI² - (ΣI)²
                    double scaled = area * s2 - s1 * s1;
                    double score = scaled <= floor ? 0 : numerator * inverse * area / Math.Sqrt(scaled);
                    row[u] = score > 1 ? 1 : (float)score;
                }
            }
        }
    }

    /// <summary>
    /// 窗口内精确掩码NCC：T̃=掩码·(T-掩码均值)，分子=Σ I·T̃；分母用 Σ掩码·I 与 Σ掩码·I² 得到掩码内的图像方差。
    /// OpenCV自带的带掩码CCOEFF_NORMED对大模板非常慢，不使用。
    /// </summary>
    protected override float[] Window(SearchState search, int level, PyramidPose pose, int u0, int v0, int r)
    {
        var state = (State)search;
        var image = state.Pyramid[level];
        int side = 2 * r + 1; var result = new float[side * side];
        for (int i = 0; i < result.Length; i++) result[i] = float.NaN;
        int x0 = Math.Max(0, u0 - r), y0 = Math.Max(0, v0 - r);
        int x1 = Math.Min(image.Cols, u0 + r + pose.Width), y1 = Math.Min(image.Rows, v0 + r + pose.Height);
        if (x1 - x0 < pose.Width || y1 - y0 < pose.Height) return result;
        using var warped = Warp(level, _images[level], pose, InterpolationFlags.Linear, BorderTypes.Replicate);
        using var mask8 = WarpMask(level, _masks[level], pose);
        int valid = Cv2.CountNonZero(mask8);
        if (valid < 4) return result;
        using var mask = new Mat(); mask8.ConvertTo(mask, MatType.CV_32F, 1 / 255d);
        using var template = new Mat(); warped.ConvertTo(template, MatType.CV_32F, 1, -Cv2.Mean(warped, mask8).Val0);
        Cv2.Multiply(template, mask, template);
        double energy = template.Dot(template);
        if (energy < 1e-6) return result;
        using var roi8 = new Mat(image, new Rect(x0, y0, x1 - x0, y1 - y0));
        // 减去窗口均值再求平方和，避免单精度下 ΣI² - (ΣI)²/n 的抵消误差；NCC对灰度平移不变。
        using var roi = new Mat(); roi8.ConvertTo(roi, MatType.CV_32F, 1, -Cv2.Mean(roi8).Val0);
        using var square = new Mat(); Cv2.Multiply(roi, roi, square);
        // 窗口只有(2r+1)²个位置：逐位置做三次SIMD点积比对整块ROI做DFT相关快得多。
        for (int gy = 0; gy < side; gy++)
        for (int gx = 0; gx < side; gx++)
        {
            int x = u0 - r + gx - x0, y = v0 - r + gy - y0;
            if (x < 0 || y < 0 || x + pose.Width > roi.Cols || y + pose.Height > roi.Rows) continue;
            var rect = new Rect(x, y, pose.Width, pose.Height);
            using var patch = new Mat(roi, rect); using var patchSquare = new Mat(square, rect);
            double numerator = patch.Dot(template), sum = patch.Dot(mask), sumSquares = patchSquare.Dot(mask);
            double variance = sumSquares - sum * sum / valid;
            double score = variance <= 1e-6 * valid ? 0 : numerator / Math.Sqrt(energy * variance);
            result[gy * side + gx] = (float)(double.IsNaN(score) ? 0 : Math.Max(0, Math.Min(1, score)));
        }
        return result;
    }

    /// <summary>读取分数图；非有限值（平坦区域分母为0）记0，负相关记0。</summary>
    private static float[] Read(Mat scores, bool clamp = true)
    {
        var values = new float[scores.Rows * scores.Cols];
        for (int y = 0; y < scores.Rows; y++) Marshal.Copy(scores.Ptr(y), values, y * scores.Cols, scores.Cols);
        if (clamp) for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            values[i] = float.IsNaN(v) || float.IsInfinity(v) || v < 0 ? 0 : v > 1 ? 1 : v;
        }
        return values;
    }

    private void ClearTop() { foreach (var m in _topTemplates.Values) m.Tilde.Dispose(); _topTemplates.Clear(); _topBytes = 0; }

    public override void Dispose()
    {
        lock (_gate) ClearTop();
        foreach (var m in _images) m?.Dispose();
        foreach (var m in _masks) m?.Dispose();
        base.Dispose();
    }
}
