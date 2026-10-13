using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>一个角度/尺度组合在某层的外接框尺寸。</summary>
internal readonly struct PyramidPose
{
    internal PyramidPose(double angle, double scale, double lo, double hi, int width, int height)
    { Angle = angle; Scale = scale; Lo = lo; Hi = hi; Width = width; Height = height; }
    internal double Angle { get; }
    internal double Scale { get; }
    /// <summary>该角度所属搜索区间，局部精化不越出区间。</summary>
    internal double Lo { get; }
    internal double Hi { get; }
    internal int Width { get; }
    internal int Height { get; }
}

/// <summary>粗到精过程中的候选：本层中心边界坐标、角度、尺度和分数。</summary>
internal struct PyramidCandidate
{
    internal double X, Y, Angle, Scale, Lo, Hi;
    internal float Score;
    /// <summary>最终层整数左上角与外接框，用于区域检查。</summary>
    internal int U, V, Width, Height;
}

/// <summary>
/// 金字塔搜索计划：使用的层数、各层自适应步长、顶层全部角度/尺度组合以及按位姿假设计的工作量。
/// 步长使模板最远点在本层移动约1像素（atan(2/直径)），最终层取自适应步长与节点采样步长中较小者。
/// </summary>
internal sealed class OpenCvPyramidPlan
{
    internal int Top;
    internal int[] SearchWidth = Array.Empty<int>(), SearchHeight = Array.Empty<int>();
    internal double[] AngleStep = Array.Empty<double>(), ScaleStep = Array.Empty<double>();
    internal PyramidPose[] TopPoses = Array.Empty<PyramidPose>();
    internal int Candidates;
    internal long Work;
    internal int TemplateWidth, TemplateHeight;
    internal double MinimumScale, MaximumScale;
    internal bool FixedAngle, FixedScale;
    /// <summary>形状引擎：顶层步长加倍、中间层2倍步长取样。</summary>
    internal bool Coarse;

    /// <summary>精化窗口半径：从顶层下来的第一层用3，其余2。</summary>
    internal int Radius(int level) => level == Top - 1 ? 3 : 2;

    /// <summary>
    /// 第level层的角度（或尺度）取样间距：顶层为该层步长；中间层为2倍步长（只需把上一层±半个间距的不确定度再减半）；第0层为最终步长。
    /// 每层只扫3个值（第0层5个），而不是按本层步长铺满上一层的整个间距。NCC相关峰窄，中间层保持本层步长。
    /// </summary>
    internal double Spacing(bool angle, int level)
    {
        var steps = angle ? AngleStep : ScaleStep;
        return level == Top || level == 0 || !Coarse ? steps[level] : 2 * steps[level];
    }

    /// <summary>精化第level层时围绕候选的扫描半径：形状为上一层半个间距，NCC为上一层一个步长（相关峰窄，留足余量）。</summary>
    internal double Range(bool angle, int level)
    {
        double previous = level == Top ? Spacing(angle, level) : Spacing(angle, level + 1);
        return Coarse ? previous / 2 : previous;
    }

    /// <summary>精化第level层时的取样数。</summary>
    internal int Samples(bool angle, int level) => 2 * (int)Math.Ceiling(Range(angle, level) / Spacing(angle, level) - 1e-9) + 1;

    internal static OpenCvPyramidPlan Create(EOpenCvPyramidTemplateKind kind, int width, int height, int modelLevels, OpenCvPyramidSettings settings, PixelBounds search, TemplatePoseOptions options)
    {
        foreach (var interval in options.AngleIntervals())
            if (interval.Minimum < settings.MinimumAngle - 1e-10 || interval.Maximum > settings.MaximumAngle + 1e-10)
                throw new ArgumentException("搜索角度超出模型制作范围。");
        if (options.MinimumScale < settings.MinimumScale - 1e-10 || options.MaximumScale > settings.MaximumScale + 1e-10)
            throw new ArgumentException("搜索尺度超出模型制作范围。");
        var plan = new OpenCvPyramidPlan
        {
            TemplateWidth = width, TemplateHeight = height, MinimumScale = options.MinimumScale, MaximumScale = options.MaximumScale,
            FixedAngle = options.MaximumAngleRadians - options.MinimumAngleRadians < 1e-12, FixedScale = options.MaximumScale - options.MinimumScale < 1e-12,
            Candidates = options.MaximumCandidates, Coarse = kind == EOpenCvPyramidTemplateKind.Shape
        };
        int levels = Math.Max(1, modelLevels);
        plan.SearchWidth = new int[levels]; plan.SearchHeight = new int[levels]; plan.AngleStep = new double[levels]; plan.ScaleStep = new double[levels];
        for (int l = 0; l < levels; l++)
        {
            plan.SearchWidth[l] = l == 0 ? search.Width : (plan.SearchWidth[l - 1] + 1) / 2;
            plan.SearchHeight[l] = l == 0 ? search.Height : (plan.SearchHeight[l - 1] + 1) / 2;
            double f = Math.Pow(2, l), diameter = Math.Max(2, options.MaximumScale * Math.Sqrt(width * width + height * height) / f);
            plan.AngleStep[l] = Math.Min(.2, 2 / diameter); plan.ScaleStep[l] = Math.Min(.1, 2 / diameter);
        }
        plan.AngleStep[0] = Math.Min(plan.AngleStep[0], options.AngleStepRadians); plan.ScaleStep[0] = Math.Min(plan.ScaleStep[0], options.ScaleStep);
        // 顶层不能小到模板放不下：level0能放下的组合在更高层一定也放得下（逐层向上取整），只需检查level0至少有一个组合可放。
        int top = levels - 1;
        var poses = Poses(plan, top, options);
        if (poses.All(p => plan.Positions(0, Bounds(plan, 0, p.Angle, p.Scale)) == 0)) { plan.Top = top; plan.TopPoses = Array.Empty<PyramidPose>(); return plan; }
        // 顶层搜索图至少要比模板大一些，否则粗搜索没有意义；逐层降低。
        while (top > 0 && poses.All(p => plan.Positions(top, p) == 0)) { top--; poses = Poses(plan, top, options); }
        // 形状顶层容许模板最远点偏离约1像素（步长加倍）：边缘方向场经多次高斯降采样后随姿态变化平缓，下一层±上层步长的局部扫描补回。
        // NCC的相关峰更窄，保持原步长。
        if (top > 0 && kind == EOpenCvPyramidTemplateKind.Shape)
        {
            plan.AngleStep[top] = Math.Min(.4, 2 * plan.AngleStep[top]); plan.ScaleStep[top] = Math.Min(.2, 2 * plan.ScaleStep[top]);
            poses = Poses(plan, top, options);
        }
        plan.Top = top; plan.TopPoses = poses;
        long work = 0;
        foreach (var p in poses) work = checked(work + plan.Positions(top, p));
        for (int l = top - (top == 0 ? 0 : 1); l >= 0; l--)
        {
            long local = (plan.FixedAngle ? 1 : plan.Samples(true, l)) + (plan.FixedScale ? 0 : plan.Samples(false, l) - 1);
            int r = l == top ? 1 : plan.Radius(l);
            work = checked(work + plan.Candidates * local * (2 * r + 1) * (2 * r + 1));
        }
        plan.Work = work;
        if (work > options.MaximumWork)
            throw new InvalidOperationException($"金字塔搜索位姿假设超出预算：预计 {work:N0}，上限 {options.MaximumWork:N0}（顶层第{top + 1}层 {plan.SearchWidth[top]}×{plan.SearchHeight[top]}，{poses.Length}组角度/尺度）。请缩小搜索ROI或角度/尺度范围，或增加金字塔层数。");
        return plan;
    }

    private static PyramidPose[] Poses(OpenCvPyramidPlan plan, int level, TemplatePoseOptions options)
    {
        var result = new List<PyramidPose>();
        var scales = Axis(options.MinimumScale, options.MaximumScale, plan.ScaleStep[level], false);
        foreach (var interval in options.AngleIntervals())
        {
            bool full = interval.Maximum - interval.Minimum >= 2 * Math.PI - 1e-9;
            foreach (double angle in Axis(interval.Minimum, interval.Maximum, plan.AngleStep[level], full))
                foreach (double scale in scales) result.Add(Bounds(plan, level, angle, scale, interval.Minimum, interval.Maximum));
        }
        if (result.Count > 200000) throw new ArgumentException("顶层角度/尺度组合超过200000组，请缩小搜索范围。");
        return result.ToArray();
    }

    internal static double[] Axis(double minimum, double maximum, double step, bool periodic)
    {
        if (maximum - minimum < 1e-12) return new[] { minimum };
        int segments = Math.Max(1, (int)Math.Ceiling((maximum - minimum) / step - 1e-9));
        int count = periodic ? segments : segments + 1;
        var values = new double[count];
        for (int i = 0; i < count; i++) values[i] = minimum + (maximum - minimum) * i / segments;
        return values;
    }

    /// <summary>某层某姿态的外接框，模板连续尺寸为宽高/2^层。</summary>
    internal static PyramidPose Bounds(OpenCvPyramidPlan plan, int level, double angle, double scale, double lo = 0, double hi = 0)
    {
        double f = Math.Pow(2, level), w = plan.TemplateWidth / f, h = plan.TemplateHeight / f, c = Math.Abs(Math.Cos(angle)), s = Math.Abs(Math.Sin(angle));
        return new PyramidPose(angle, scale, lo, hi, Math.Max(1, (int)Math.Ceiling(scale * (c * w + s * h) - 1e-9)), Math.Max(1, (int)Math.Ceiling(scale * (s * w + c * h) - 1e-9)));
    }

    internal long Positions(int level, PyramidPose pose)
    {
        long w = SearchWidth[level] - pose.Width + 1, h = SearchHeight[level] - pose.Height + 1;
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>
/// 金字塔粗到精匹配的公共流程：顶层全范围（并行于角度/尺度组合）→ 每组合取若干峰 → 全局NMS取前K → 逐层在±步长、±半径窗口内精化
/// → 最终层抛物线亚像素（位置、角度、尺度）→ 引擎可选的进一步精化 → 区域约束检查。具体打分由NCC或形状引擎实现。
/// </summary>
internal abstract class OpenCvPyramidMatcherBase : IPreparedVisionTemplateMatcher, IDisposable
{
    protected readonly OpenCvPyramidModel Model;
    private readonly VisionTemplateDefinition _definition;
    private readonly TemplateReference _reference;
    private readonly Mat _mask0;
    protected IReadOnlyList<IReadOnlyList<Coordinate2D>> Contours = Array.Empty<IReadOnlyList<Coordinate2D>>();

    protected OpenCvPyramidMatcherBase(VisionTemplateSnapshot snapshot, OpenCvPyramidModel model)
    {
        Model = model; _definition = VisionTemplateStore.CopyDefinition(snapshot.Manifest.Definition); ModelIdentity = snapshot.Identity; _reference = _definition.Reference();
        _mask0 = ToMat(model.Levels[0].Width, model.Levels[0].Height, model.Levels[0].Mask);
    }

    public VisionTemplateDefinition Definition => VisionTemplateStore.CopyDefinition(_definition);
    public string ModelIdentity { get; }

    internal static Mat ToMat(int width, int height, byte[] data)
    {
        var mat = new Mat(height, width, MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(data, 0, mat.Data, data.Length);
        return mat;
    }

    /// <summary>把第level层模板（像素或掩码）按姿态旋转缩放到外接框：模板中心对齐外接框中心。</summary>
    internal Mat Warp(int level, Mat source, PyramidPose pose, InterpolationFlags interpolation, BorderTypes border)
    {
        var data = Model.Levels[level];
        double c = pose.Scale * Math.Cos(pose.Angle), s = pose.Scale * Math.Sin(pose.Angle), px = data.CenterX - .5, py = data.CenterY - .5;
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, c); matrix.Set(0, 1, -s); matrix.Set(0, 2, (pose.Width - 1) / 2d - (c * px - s * py));
        matrix.Set(1, 0, s); matrix.Set(1, 1, c); matrix.Set(1, 2, (pose.Height - 1) / 2d - (s * px + c * py));
        var result = new Mat();
        try { Cv2.WarpAffine(source, result, matrix, new Size(pose.Width, pose.Height), interpolation, border, Scalar.All(0)); return result; }
        catch { result.Dispose(); throw; }
    }

    /// <summary>旋转后的掩码：线性插值后取半覆盖，保持有效区域与连续模板一致。</summary>
    internal Mat WarpMask(int level, Mat mask, PyramidPose pose)
    {
        var warped = Warp(level, mask, pose, InterpolationFlags.Linear, BorderTypes.Constant);
        Cv2.Threshold(warped, warped, 127, 255, ThresholdTypes.Binary);
        return warped;
    }

    /// <summary>每次匹配的引擎状态（搜索金字塔及其派生数据）。</summary>
    protected abstract class SearchState : IDisposable
    {
        public abstract void Dispose();
    }

    protected abstract SearchState Begin(Mat[] pyramid, OpenCvPyramidPlan plan, CancellationToken token);
    /// <summary>
    /// 顶层全图打分，写入map的前 (搜索宽-框宽+1)×(搜索高-框高+1) 项（行优先）；低于阈值的位置可以写成任意更小值。
    /// map是每个工作线程复用的缓冲区：逐姿态新建的分数图常超过85KB进入大对象堆，频繁触发完整GC。
    /// </summary>
    protected abstract void ScanTop(SearchState state, int index, PyramidPose pose, float threshold, float[] map, CancellationToken token);
    /// <summary>以(u0,v0)为左上角预测，在±r窗口内打分；返回(2r+1)²个分数，不可放置的位置为NaN。</summary>
    protected abstract float[] Window(SearchState state, int level, PyramidPose pose, int u0, int v0, int r);
    /// <summary>最终层引擎特有的精化（形状最小二乘）；默认不变。</summary>
    protected virtual PyramidCandidate Polish(SearchState state, PyramidCandidate candidate, OpenCvPyramidPlan plan) => candidate;

    public TemplatePoseResult Match(ImageFrame frame, PixelBounds searchBounds, TemplatePoseOptions options, RegionGeometry? region = null, CancellationToken token = default)
    {
        VisionImage.RequireGray8(frame.Image, "模板匹配");
        if (!searchBounds.Fits(frame.Image)) throw new ArgumentException("匹配范围越界。");
        if ((long)frame.Image.Info.Width * frame.Image.Info.Height > 16777216) throw new ArgumentException("匹配图像超过像素预算。");
        InspectionMask.Validate(region, frame.Image);
        var narrowed = RegionMatchMinimum.Narrow(searchBounds, region);
        var plan = OpenCvPyramidPlan.Create(Model.Kind, Model.Width, Model.Height, Model.Levels.Length, Model.Settings, narrowed ?? searchBounds, options);
        if (narrowed is not { } bounds || plan.TopPoses.Length == 0) return new TemplatePoseResult(frame.FrameId, ModelIdentity, 0, null, _reference, Contours);
        using var window = RegionWindow.Create(region, frame, bounds, token);
        var pyramid = new Mat[plan.Top + 1];
        Mat? gray = null;
        try
        {
            gray = CvPixels.Gray(frame.Image);
            pyramid[0] = new Mat(gray, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            for (int l = 1; l <= plan.Top; l++) { pyramid[l] = new Mat(); Cv2.PyrDown(pyramid[l - 1], pyramid[l]); }
            using var state = Begin(pyramid, plan, token);
            var finals = Search(state, plan, options, token);
            float best = 0; PyramidCandidate? chosen = null;
            foreach (var candidate in finals.OrderByDescending(c => c.Score))
            {
                token.ThrowIfCancellationRequested();
                if (window != null && window.HasOutside && !Inside(window, candidate)) continue;
                best = candidate.Score; chosen = candidate; break;
            }
            TemplatePoseTransform? transform = null;
            if (chosen is { } c && c.Score >= options.MinimumScore)
                transform = new TemplatePoseTransform(Model.Width, Model.Height, new PointD(bounds.X + c.X, bounds.Y + c.Y), Math.Atan2(Math.Sin(c.Angle), Math.Cos(c.Angle)), c.Scale);
            return new TemplatePoseResult(frame.FrameId, ModelIdentity, Math.Max(0, Math.Min(1, best)), transform, _reference, Contours);
        }
        finally { foreach (var m in pyramid) m?.Dispose(); gray?.Dispose(); }
    }

    private List<PyramidCandidate> Search(SearchState state, OpenCvPyramidPlan plan, TemplatePoseOptions options, CancellationToken token)
    {
        int top = plan.Top, k = plan.Candidates;
        float Threshold(int level) => (float)(options.MinimumScore * Math.Pow(.9, level));
        // 顶层：并行于组合，每个组合取最多4个峰（峰间距为模板短边的一半）。
        double f = Math.Pow(2, top);
        int peakRadius = Math.Max(2, (int)(.5 * Math.Min(Model.Width, Model.Height) / f * plan.MinimumScale));
        float threshold = Threshold(top);
        var found = new List<PyramidCandidate>[plan.TopPoses.Length];
        var parallel = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount };
        int mapSize = plan.SearchWidth[top] * plan.SearchHeight[top];
        Parallel.For(0, plan.TopPoses.Length, parallel, () => new float[mapSize], (i, loop, map) =>
        {
            var pose = plan.TopPoses[i];
            if (plan.Positions(top, pose) == 0) return map;
            ScanTop(state, i, pose, threshold, map, token);
            int mw = plan.SearchWidth[top] - pose.Width + 1, mh = plan.SearchHeight[top] - pose.Height + 1;
            var list = new List<PyramidCandidate>();
            foreach (var (u, v, score) in Peaks(map, mw, mh, threshold, Math.Min(4, k), peakRadius))
                list.Add(new PyramidCandidate { X = u + pose.Width / 2d, Y = v + pose.Height / 2d, Angle = pose.Angle, Scale = pose.Scale, Lo = pose.Lo, Hi = pose.Hi, Score = score });
            found[i] = list;
            return map;
        }, _ => { });
        var candidates = Suppress(found.Where(l => l != null).SelectMany(l => l), peakRadius, k);
        // 顶层分数只用于排序：明显弱于最佳（低于其75%）的候选几乎不可能在精化后反超，丢弃以免逐个精化；多个相同目标分数相近，不受影响。
        if (candidates.Count > 1) { float cut = .75f * candidates[0].Score; candidates = candidates.Where(c => c.Score >= cut).ToList(); }
        if (candidates.Count == 0) return candidates;
        // 逐层精化；只有一层时在第0层做一次±1步长、±1像素的局部精化以得到亚像素邻域。
        for (int l = top == 0 ? 0 : top - 1; l >= 0; l--)
        {
            token.ThrowIfCancellationRequested();
            bool same = l == top;
            int r = same ? 1 : plan.Radius(l), level = l;
            double rangeA = plan.Range(true, l), rangeS = plan.Range(false, l);
            var refined = new PyramidCandidate?[candidates.Count];
            var current = candidates;
            Parallel.For(0, current.Count, parallel, i => refined[i] = Refine(state, plan, level, current[i], same ? 1 : 2, r, rangeA, rangeS, level == 0));
            float t = Threshold(l);
            var alive = refined.Where(c => c.HasValue).Select(c => c!.Value).ToList();
            var kept = alive.Where(c => c.Score >= t).ToList();
            if (kept.Count == 0 && alive.Count > 0) kept.Add(alive.OrderByDescending(c => c.Score).First());
            double fl = Math.Pow(2, l);
            candidates = Suppress(kept, Math.Max(2, .5 * Math.Min(Model.Width, Model.Height) / fl * plan.MinimumScale), k);
        }
        for (int i = 0; i < candidates.Count; i++) candidates[i] = Polish(state, candidates[i], plan);
        return candidates;
    }

    /// <summary>
    /// 本层局部精化：先在原尺度下扫描±上层半个取样间距，再在最佳角度下扫描尺度（可分离搜索，组合数为两轴之和而非乘积），
    /// 每个组合在预测位置±r窗口内取最大；最终层用角度、尺度剖面和位置邻域做抛物线亚像素。
    /// </summary>
    private PyramidCandidate? Refine(SearchState state, OpenCvPyramidPlan plan, int level, PyramidCandidate c, int factor, int r, double rangeA, double rangeS, bool final)
    {
        var angles = Local(c.Angle, rangeA, plan.Spacing(true, level), c.Lo, c.Hi, plan.FixedAngle);
        var scales = Local(c.Scale, rangeS, plan.Spacing(false, level), plan.MinimumScale, plan.MaximumScale, plan.FixedScale);
        int side = 2 * r + 1;
        var cache = new Dictionary<(int, int), (float Score, int Index, float[] Map, PyramidPose Pose, int U0, int V0)>();
        (float Score, int Index, float[] Map, PyramidPose Pose, int U0, int V0) Evaluate(int i, int j)
        {
            if (cache.TryGetValue((i, j), out var hit)) return hit;
            var pose = OpenCvPyramidPlan.Bounds(plan, level, angles[i], scales[j], c.Lo, c.Hi);
            int u0 = (int)Math.Round(c.X * factor - pose.Width / 2d), v0 = (int)Math.Round(c.Y * factor - pose.Height / 2d);
            var map = Window(state, level, pose, u0, v0, r);
            float m = float.NegativeInfinity; int mi = -1;
            for (int q = 0; q < map.Length; q++) if (!float.IsNaN(map[q]) && map[q] > m) { m = map[q]; mi = q; }
            return cache[(i, j)] = (m, mi, map, pose, u0, v0);
        }
        int Nearest(double[] values, double v) { int best = 0; for (int q = 1; q < values.Length; q++) if (Math.Abs(values[q] - v) < Math.Abs(values[best] - v)) best = q; return best; }
        int j0 = Nearest(scales, c.Scale), bi = 0;
        for (int i = 1; i < angles.Length; i++) if (Evaluate(i, j0).Score > Evaluate(bi, j0).Score) bi = i;
        int bj = j0;
        for (int j = 0; j < scales.Length; j++) if (Evaluate(bi, j).Score > Evaluate(bi, bj).Score) bj = j;
        var best = Evaluate(bi, bj);
        if (best.Index < 0) return null;
        int bu = best.U0 - r + best.Index % side, bv = best.V0 - r + best.Index / side;
        double dx = 0, dy = 0, angle = angles[bi], scale = scales[bj];
        if (final)
        {
            int mx = best.Index % side, my = best.Index / side;
            float At(int x, int y) => x < 0 || y < 0 || x >= side || y >= side ? float.NaN : best.Map[y * side + x];
            dx = Vertex(At(mx - 1, my), best.Score, At(mx + 1, my)); dy = Vertex(At(mx, my - 1), best.Score, At(mx, my + 1));
            if (bi > 0 && bi < angles.Length - 1) angle += Vertex(Evaluate(bi - 1, j0).Score, Evaluate(bi, j0).Score, Evaluate(bi + 1, j0).Score) * (angles[bi + 1] - angles[bi - 1]) / 2;
            if (bj > 0 && bj < scales.Length - 1) scale += Vertex(Evaluate(bi, bj - 1).Score, best.Score, Evaluate(bi, bj + 1).Score) * (scales[bj + 1] - scales[bj - 1]) / 2;
        }
        return new PyramidCandidate
        {
            X = bu + best.Pose.Width / 2d + dx, Y = bv + best.Pose.Height / 2d + dy, Angle = angle, Scale = scale, Lo = c.Lo, Hi = c.Hi, Score = best.Score,
            U = bu, V = bv, Width = best.Pose.Width, Height = best.Pose.Height
        };
    }

    /// <summary>以center为中心、±range内按step取样，限制在[lo,hi]区间内。</summary>
    private static double[] Local(double center, double range, double step, double lo, double hi, bool fixedValue)
    {
        if (fixedValue) return new[] { center };
        int m = Math.Max(1, (int)Math.Ceiling(range / step - 1e-9));
        var values = new List<double>(2 * m + 1);
        for (int i = -m; i <= m; i++)
        {
            double v = center + i * step;
            if (v < lo - 1e-12 || v > hi + 1e-12) continue;
            values.Add(v);
        }
        if (values.Count == 0) values.Add(Math.Max(lo, Math.Min(hi, center)));
        return values.ToArray();
    }

    /// <summary>三点抛物线顶点相对中点的偏移，限制在±0.5。</summary>
    internal static double Vertex(float left, float center, float right)
    {
        if (float.IsNaN(left) || float.IsNaN(right) || float.IsInfinity(left) || float.IsInfinity(right)) return 0;
        double denominator = left - 2d * center + right;
        if (denominator >= -1e-9) return 0;
        return Math.Max(-.5, Math.Min(.5, (left - right) / (2 * denominator)));
    }

    /// <summary>分数图上逐次取最大值并抑制邻域，最多count个且不低于阈值。</summary>
    internal static IEnumerable<(int U, int V, float Score)> Peaks(float[] map, int width, int height, float threshold, int count, int radius)
    {
        var result = new List<(int, int, float)>();
        for (int n = 0; n < count; n++)
        {
            float best = threshold; int index = -1;
            for (int i = 0, length = width * height; i < length; i++) if (map[i] >= best) { best = map[i]; index = i; }
            if (index < 0 || float.IsNaN(best)) break;
            int u = index % width, v = index / width;
            result.Add((u, v, best));
            for (int y = Math.Max(0, v - radius); y <= Math.Min(height - 1, v + radius); y++)
                for (int x = Math.Max(0, u - radius); x <= Math.Min(width - 1, u + radius); x++) map[y * width + x] = float.NegativeInfinity;
        }
        return result;
    }

    /// <summary>按分数降序做中心距离NMS，最多保留count个。</summary>
    internal static List<PyramidCandidate> Suppress(IEnumerable<PyramidCandidate> candidates, double radius, int count)
    {
        var kept = new List<PyramidCandidate>(); double r2 = radius * radius;
        foreach (var c in candidates.OrderByDescending(c => c.Score))
        {
            if (kept.Count >= count) break;
            if (kept.Any(o => (o.X - c.X) * (o.X - c.X) + (o.Y - c.Y) * (o.Y - c.Y) < r2)) continue;
            kept.Add(c);
        }
        return kept;
    }

    /// <summary>模板有效区域在最终姿态下必须整体落在搜索区域内。</summary>
    private bool Inside(RegionWindow window, PyramidCandidate c)
    {
        var pose = new PyramidPose(c.Angle, c.Scale, c.Lo, c.Hi, c.Width, c.Height);
        if (c.U < 0 || c.V < 0 || c.U + c.Width > window.Outside.Cols || c.V + c.Height > window.Outside.Rows) return false;
        using var mask = WarpMask(0, _mask0, pose);
        using var roi = new Mat(window.Outside, new Rect(c.U, c.V, c.Width, c.Height));
        return Cv2.Mean(roi, mask).Val0 < 1e-6;
    }

    public virtual void Dispose() => _mask0.Dispose();
}
