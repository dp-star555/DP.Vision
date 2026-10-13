using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// 梯度方向形状匹配（Steger/HALCON同类思路）：分数为模型边缘点单位梯度方向与图像对应点单位梯度方向点积的平均值，
/// 低于搜索最小对比度的图像梯度记零向量。对光照线性变化、局部遮挡和背景杂乱稳健。
/// 顶层按特征SIMD累加（点数缩减到均匀子集）；精化层只在读到的像素上现算梯度；旋转/缩放直接作用于特征点和方向，不旋转图像；最终层可选点到边缘线的最小二乘精化。
/// </summary>
internal sealed class OpenCvShapeTemplateMatcher : OpenCvPyramidMatcherBase
{
    private readonly OpenCvPyramidSettings _settings;
    private const int TopFeatures = 64;

    internal OpenCvShapeTemplateMatcher(VisionTemplateSnapshot snapshot, OpenCvPyramidModel model) : base(snapshot, model)
    {
        _settings = model.Settings;
        Contours = ShapeContours(model);
    }

    /// <summary>显示轮廓：第0层模板上达到制作对比度的细化边缘，模板像素边界坐标。</summary>
    private static IReadOnlyList<IReadOnlyList<Coordinate2D>> ShapeContours(OpenCvPyramidModel model)
    {
        var level = model.Levels[0];
        using var image = ToMat(level.Width, level.Height, level.Pixels);
        using var mask = ToMat(level.Width, level.Height, level.Mask);
        var edges = OpenCvShapeFeatures.Extract(image, new Rect(0, 0, level.Width, level.Height), mask, model.Settings.Contrast);
        using var binary = new Mat(level.Height, level.Width, MatType.CV_8UC1, Scalar.All(0));
        foreach (var e in edges) binary.Set(e.Y, e.X, (byte)255);
        Cv2.FindContours(binary, out Point[][] found, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        return TemplateModelContours.Limit(found.Where(c => c.Length >= 3)
            .Select(c => (IReadOnlyList<Coordinate2D>)c.Select(p => new Coordinate2D(p.X + .5, p.Y + .5)).ToArray()).ToList());
    }

    private sealed class State : SearchState
    {
        internal Mat[] Pyramid = Array.Empty<Mat>();
        internal float[] TopX = Array.Empty<float>(), TopY = Array.Empty<float>();
        public override void Dispose() { }
    }

    protected override SearchState Begin(Mat[] pyramid, OpenCvPyramidPlan plan, CancellationToken token)
    {
        var top = pyramid[plan.Top];
        var (x, y) = Directions(top, new Rect(0, 0, top.Cols, top.Rows));
        return new State { Pyramid = pyramid, TopX = x, TopY = y };
    }

    /// <summary>rect内的单位梯度方向（外扩1像素求Sobel）；幅值低于搜索最小对比度记零。</summary>
    /// <summary>rect内的单位梯度方向（外扩1像素求Sobel）；幅值低于搜索最小对比度记零。</summary>
    private (float[] X, float[] Y) Directions(Mat image, Rect rect)
    {
        var (x, y) = Gradients(image, rect);
        float minimum = _settings.MinimumContrast;
        for (int i = 0; i < x.Length; i++)
        {
            float m = (float)Math.Sqrt(x[i] * x[i] + y[i] * y[i]);
            if (m < minimum) { x[i] = 0; y[i] = 0; }
            else { x[i] /= m; y[i] /= m; }
        }
        return (x, y);
    }

    /// <summary>rect内的Sobel梯度/4（近似灰度差）。</summary>
    private static (float[] X, float[] Y) Gradients(Mat image, Rect rect)
    {
        int ox = Math.Max(0, rect.X - 1), oy = Math.Max(0, rect.Y - 1);
        var outer = new Rect(ox, oy, Math.Min(image.Cols, rect.X + rect.Width + 1) - ox, Math.Min(image.Rows, rect.Y + rect.Height + 1) - oy);
        using var roi = new Mat(image, outer);
        using var sx = new Mat(); using var sy = new Mat();
        Cv2.Sobel(roi, sx, MatType.CV_32F, 1, 0, 3, .25, 0, BorderTypes.Replicate);
        Cv2.Sobel(roi, sy, MatType.CV_32F, 0, 1, 3, .25, 0, BorderTypes.Replicate);
        var x = new float[rect.Width * rect.Height]; var y = new float[x.Length];
        for (int row = 0; row < rect.Height; row++)
        {
            Marshal.Copy(sx.Ptr(row + rect.Y - oy) + (rect.X - ox) * sizeof(float), x, row * rect.Width, rect.Width);
            Marshal.Copy(sy.Ptr(row + rect.Y - oy) + (rect.X - ox) * sizeof(float), y, row * rect.Width, rect.Width);
        }
        return (x, y);
    }

    /// <summary>按姿态变换本层特征：外接框内的像素偏移和旋转后的单位方向。</summary>
    private (int[] X, int[] Y, float[] Ux, float[] Uy) Rotate(int level, PyramidPose pose, int limit = int.MaxValue)
    {
        var data = Model.Levels[level]; int n = Math.Min(limit, data.FeatureCount);
        double c = Math.Cos(pose.Angle), s = Math.Sin(pose.Angle), k = pose.Scale, hw = pose.Width / 2d, hh = pose.Height / 2d;
        var x = new int[n]; var y = new int[n]; var ux = new float[n]; var uy = new float[n];
        for (int i = 0; i < n; i++)
        {
            double qx = data.Qx[i], qy = data.Qy[i];
            x[i] = Math.Max(0, Math.Min(pose.Width - 1, (int)Math.Floor(hw + k * (c * qx - s * qy))));
            y[i] = Math.Max(0, Math.Min(pose.Height - 1, (int)Math.Floor(hh + k * (s * qx + c * qy))));
            ux[i] = (float)(c * data.Ux[i] - s * data.Uy[i]); uy[i] = (float)(s * data.Ux[i] + c * data.Uy[i]);
        }
        return (x, y, ux, uy);
    }

    /// <summary>
    /// 顶层全图打分，按特征累加（LINE-MOD式线性内存访问）：对每个特征，把该特征在所有位置上的贡献沿图像行连续地累加到分数行，
    /// 读写都是连续内存，用Vector&lt;float&gt;做SIMD；不再逐位置做带分支的提前终止（分支预测失败和随机访存才是瓶颈）。
    /// 顶层只用前TopFeatures个特征（特征顺序已均匀打乱，前缀在模板上均匀分布），相当于HALCON的点数缩减；大图隔行扫描，下一层±3像素窗口补回。
    /// </summary>
    protected override unsafe void ScanTop(SearchState search, int index, PyramidPose pose, float threshold, float[] map, CancellationToken token)
    {
        var state = (State)search;
        int top = state.Pyramid.Length - 1, width = state.Pyramid[top].Cols, height = state.Pyramid[top].Rows;
        int mw = width - pose.Width + 1, mh = height - pose.Height + 1;
        var (fx, fy, ux, uy) = Rotate(top, pose, TopFeatures);
        int n = fx.Length, metric = (int)_settings.Metric, lanes = Vector<float>.Count;
        int stride = top > 0 && (long)mw * mh > 4096 ? 2 : 1;
        fixed (float* dx = state.TopX, dy = state.TopY, pm = map)
        {
            for (int v = 0; v < mh; v++)
            {
                float* row = pm + v * mw;
                float initial = v % stride == 0 ? 0 : -1;
                for (int u = 0; u < mw; u++) row[u] = initial;
                if (initial < 0) continue;
                if ((v & 15) == 0) token.ThrowIfCancellationRequested();
                for (int j = 0; j < n; j++)
                {
                    int origin = (v + fy[j]) * width + fx[j];
                    float* sx = dx + origin, sy = dy + origin;
                    float cx = ux[j], cy = uy[j];
                    var vx = new Vector<float>(cx); var vy = new Vector<float>(cy);
                    int u = 0;
                    if (metric == 2)
                    {
                        for (; u <= mw - lanes; u += lanes)
                            *(Vector<float>*)(row + u) += Vector.Abs(vx * *(Vector<float>*)(sx + u) + vy * *(Vector<float>*)(sy + u));
                        for (; u < mw; u++) row[u] += Math.Abs(cx * sx[u] + cy * sy[u]);
                    }
                    else
                    {
                        for (; u <= mw - lanes; u += lanes)
                            *(Vector<float>*)(row + u) += vx * *(Vector<float>*)(sx + u) + vy * *(Vector<float>*)(sy + u);
                        for (; u < mw; u++) row[u] += cx * sx[u] + cy * sy[u];
                    }
                }
                float inverse = 1f / n;
                if (metric == 1) for (int u = 0; u < mw; u++) row[u] = Math.Abs(row[u]) * inverse;
                else for (int u = 0; u < mw; u++) row[u] *= inverse;
            }
        }
    }

    /// <summary>
    /// 精化窗口打分：只在实际读到的像素上从8位图像现算Sobel梯度（边界复制，与Directions一致），不为整个窗口生成方向图——
    /// 第0层窗口可达数十万像素，而(2r+1)²个位置×特征子集只读其中几万个。
    /// </summary>
    protected override unsafe float[] Window(SearchState search, int level, PyramidPose pose, int u0, int v0, int r)
    {
        var state = (State)search;
        var image = state.Pyramid[level];
        int side = 2 * r + 1; var result = new float[side * side];
        for (int i = 0; i < result.Length; i++) result[i] = float.NaN;
        int width = image.Cols, height = image.Rows;
        int x0 = Math.Max(0, u0 - r), y0 = Math.Max(0, v0 - r);
        int x1 = Math.Min(width, u0 + r + pose.Width), y1 = Math.Min(height, v0 + r + pose.Height);
        if (x1 - x0 < pose.Width || y1 - y0 < pose.Height) return result;
        // 打分用均匀特征子集：中间层128个、第0层1024个。
        var (fx, fy, ux, uy) = Rotate(level, pose, level == 0 ? 1024 : 128);
        int n = fx.Length, metric = (int)_settings.Metric;
        long step = image.Step(); byte* data = (byte*)image.Data;
        float minimum2 = (float)_settings.MinimumContrast * _settings.MinimumContrast;
        for (int gy = 0; gy < side; gy++)
        for (int gx = 0; gx < side; gx++)
        {
            int iu = u0 - r + gx, iv = v0 - r + gy;
            if (iu < x0 || iv < y0 || iu + pose.Width > x1 || iv + pose.Height > y1) continue;
            float sum = 0;
            for (int j = 0; j < n; j++)
            {
                int x = iu + fx[j], y = iv + fy[j];
                int xm = x > 0 ? x - 1 : 0, xp = x < width - 1 ? x + 1 : width - 1;
                byte* r0 = data + (y > 0 ? y - 1 : 0) * step, r1 = data + y * step, r2 = data + (y < height - 1 ? y + 1 : height - 1) * step;
                float sx = (r0[xp] + 2 * r1[xp] + r2[xp] - r0[xm] - 2 * r1[xm] - r2[xm]) * .25f;
                float sy = (r2[xm] + 2 * r2[x] + r2[xp] - r0[xm] - 2 * r0[x] - r0[xp]) * .25f;
                float m2 = sx * sx + sy * sy;
                if (m2 < minimum2) continue;
                float d = (ux[j] * sx + uy[j] * sy) / (float)Math.Sqrt(m2);
                sum += metric == 2 ? Math.Abs(d) : d;
            }
            result[gy * side + gx] = Math.Max(0, metric == 1 ? Math.Abs(sum) / n : sum / n);
        }
        return result;
    }

    /// <summary>
    /// 点到边缘线最小二乘：每个模型边缘点沿其法向在±2像素内找图像梯度投影的极大（抛物线亚像素），
    /// 以点到该边缘切线的距离为残差，线性化求解平移、旋转和尺度增量，迭代至收敛。精化量异常时保留插值结果。
    /// </summary>
    protected override PyramidCandidate Polish(SearchState search, PyramidCandidate candidate, OpenCvPyramidPlan plan)
    {
        var image = ((State)search).Pyramid[0];
        var refined = _settings.LeastSquares ? LeastSquares(image, candidate, plan) : candidate;
        // 分数在最终亚像素位姿上用全部特征和双线性插值梯度重算（与HALCON一致）；整数网格上的分数因为最近像素取样而偏低，
        // 颗粒重、边缘抖动的实拍图上更明显。重算值只会替换更低的网格分数。
        float rescored = Rescore(image, refined);
        if (rescored > refined.Score) refined.Score = rescored;
        return refined;
    }

    /// <summary>在连续位姿上计算形状分数：第0层全部特征，梯度在8位图像上稀疏计算并双线性插值。</summary>
    private float Rescore(Mat image, PyramidCandidate pose)
    {
        var data = Model.Levels[0]; var gradients = new SparseGradients(image);
        double c = Math.Cos(pose.Angle), s = Math.Sin(pose.Angle), minimum2 = (double)_settings.MinimumContrast * _settings.MinimumContrast, sum = 0;
        int n = data.FeatureCount;
        for (int i = 0; i < n; i++)
        {
            double x = pose.X + pose.Scale * (c * data.Qx[i] - s * data.Qy[i]), y = pose.Y + pose.Scale * (s * data.Qx[i] + c * data.Qy[i]);
            if (!gradients.At(x, y, out float gx, out float gy)) continue;
            double m2 = (double)gx * gx + (double)gy * gy;
            if (m2 < minimum2) continue;
            double d = ((c * data.Ux[i] - s * data.Uy[i]) * gx + (s * data.Ux[i] + c * data.Uy[i]) * gy) / Math.Sqrt(m2);
            sum += _settings.Metric == EShapeMetric.IgnoreLocalPolarity ? Math.Abs(d) : d;
        }
        double score = _settings.Metric == EShapeMetric.IgnoreGlobalPolarity ? Math.Abs(sum) / n : sum / n;
        return (float)Math.Max(0, Math.Min(1, score));
    }

    private PyramidCandidate LeastSquares(Mat image, PyramidCandidate candidate, OpenCvPyramidPlan plan)
    {
        var data = Model.Levels[0];
        var gradients = new SparseGradients(image);
        // 最小二乘用前1024个特征（均匀子集），足以约束4个参数。
        int count = Math.Min(data.FeatureCount, 1024);
        double cx = candidate.X, cy = candidate.Y, angle = candidate.Angle, scale = candidate.Scale;
        int unknowns = 2 + (plan.FixedAngle ? 0 : 1) + (plan.FixedScale ? 0 : 1);
        double polarity = 1;
        if (_settings.Metric == EShapeMetric.IgnoreGlobalPolarity)
        {
            double c0 = Math.Cos(angle), s0 = Math.Sin(angle), total = 0;
            for (int i = 0; i < count; i++)
            {
                double rx = scale * (c0 * data.Qx[i] - s0 * data.Qy[i]), ry = scale * (s0 * data.Qx[i] + c0 * data.Qy[i]);
                double nx = c0 * data.Ux[i] - s0 * data.Uy[i], ny = s0 * data.Ux[i] + c0 * data.Uy[i];
                if (gradients.At(cx + rx, cy + ry, out float ax, out float ay)) total += nx * ax + ny * ay;
            }
            polarity = total < 0 ? -1 : 1;
        }
        var samples = new float[9];
        for (int iteration = 0; iteration < 6; iteration++)
        {
            var normal = new double[unknowns, unknowns]; var rhs = new double[unknowns]; int used = 0;
            double c = Math.Cos(angle), s = Math.Sin(angle);
            var row = new double[unknowns];
            for (int i = 0; i < count; i++)
            {
                double rx = scale * (c * data.Qx[i] - s * data.Qy[i]), ry = scale * (s * data.Qx[i] + c * data.Qy[i]);
                double nx = c * data.Ux[i] - s * data.Uy[i], ny = s * data.Ux[i] + c * data.Uy[i];
                double px = cx + rx, py = cy + ry;
                int bestK = -1; float bestV = float.NegativeInfinity; bool ok = true;
                for (int q = 0; q < samples.Length; q++)
                {
                    double t = -2 + .5 * q;
                    if (!gradients.At(px + t * nx, py + t * ny, out float ax, out float ay)) { ok = false; break; }
                    float projected = (float)(nx * ax + ny * ay);
                    samples[q] = _settings.Metric == EShapeMetric.IgnoreLocalPolarity ? Math.Abs(projected) : (float)(polarity * projected);
                    if (samples[q] > bestV) { bestV = samples[q]; bestK = q; }
                }
                if (!ok || bestK <= 0 || bestK >= samples.Length - 1 || bestV < _settings.MinimumContrast) continue;
                double offset = -2 + .5 * (bestK + Vertex(samples[bestK - 1], samples[bestK], samples[bestK + 1]));
                int k = 0; row[k++] = nx; row[k++] = ny;
                if (!plan.FixedAngle) row[k++] = nx * -ry + ny * rx;
                if (!plan.FixedScale) row[k++] = nx * rx + ny * ry;
                for (int a = 0; a < unknowns; a++) { rhs[a] += row[a] * offset; for (int b = 0; b < unknowns; b++) normal[a, b] += row[a] * row[b]; }
                used++;
            }
            if (used < Math.Max(OpenCvPyramidModel.MinimumShapeFeatures, 2 * unknowns)) return candidate;
            var delta = Solve(normal, rhs);
            if (delta == null) return candidate;
            int z = 0; double ddx = delta[z++], ddy = delta[z++], dθ = plan.FixedAngle ? 0 : delta[z++], dσ = plan.FixedScale ? 0 : delta[z++];
            cx += ddx; cy += ddy; angle = Math.Max(candidate.Lo, Math.Min(candidate.Hi, angle + dθ)); scale = Math.Max(plan.MinimumScale, Math.Min(plan.MaximumScale, scale * (1 + dσ)));
            if (Math.Abs(ddx) + Math.Abs(ddy) < 1e-3 && Math.Abs(dθ) < 1e-6 && Math.Abs(dσ) < 1e-6) break;
        }
        // 最小二乘只做亚像素修正：超过1.5像素或两个角度/尺度步长视为发散，保留插值结果。
        if (Math.Abs(cx - candidate.X) > 1.5 || Math.Abs(cy - candidate.Y) > 1.5
            || Math.Abs(angle - candidate.Angle) > 2 * plan.AngleStep[0] + 1e-12 || Math.Abs(scale - candidate.Scale) > 2 * plan.ScaleStep[0] + 1e-12) return candidate;
        candidate.X = cx; candidate.Y = cy; candidate.Angle = angle; candidate.Scale = scale;
        return candidate;
    }

    /// <summary>按需在8位图像上计算Sobel/4梯度并双线性插值（边界坐标）；不为整块ROI生成梯度图。</summary>
    private readonly unsafe struct SparseGradients
    {
        private readonly byte* _data;
        private readonly long _step;
        private readonly int _width, _height;
        internal SparseGradients(Mat image) { _data = (byte*)image.Data; _step = image.Step(); _width = image.Cols; _height = image.Rows; }

        private void Sobel(int x, int y, out float gx, out float gy)
        {
            int xm = x > 0 ? x - 1 : 0, xp = x < _width - 1 ? x + 1 : _width - 1;
            byte* r0 = _data + (y > 0 ? y - 1 : 0) * _step, r1 = _data + y * _step, r2 = _data + (y < _height - 1 ? y + 1 : _height - 1) * _step;
            gx = (r0[xp] + 2 * r1[xp] + r2[xp] - r0[xm] - 2 * r1[xm] - r2[xm]) * .25f;
            gy = (r2[xm] + 2 * r2[x] + r2[xp] - r0[xm] - 2 * r0[x] - r0[xp]) * .25f;
        }

        /// <summary>边界坐标(x,y)处的梯度；越出图像返回false。</summary>
        internal bool At(double x, double y, out float gx, out float gy)
        {
            double fx = x - .5, fy = y - .5;
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            gx = gy = 0;
            if (ix < 0 || iy < 0 || ix + 1 >= _width || iy + 1 >= _height) return false;
            float ax = (float)(fx - ix), ay = (float)(fy - iy);
            Sobel(ix, iy, out var x00, out var y00); Sobel(ix + 1, iy, out var x10, out var y10);
            Sobel(ix, iy + 1, out var x01, out var y01); Sobel(ix + 1, iy + 1, out var x11, out var y11);
            gx = (1 - ay) * ((1 - ax) * x00 + ax * x10) + ay * ((1 - ax) * x01 + ax * x11);
            gy = (1 - ay) * ((1 - ax) * y00 + ax * y10) + ay * ((1 - ax) * y01 + ax * y11);
            return true;
        }
    }

    /// <summary>小型对称正定方程组的高斯消元（部分主元）；病态时返回null。</summary>
    private static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length; var m = (double[,])a.Clone(); var x = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < n; row++) if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (Math.Abs(m[pivot, col]) < 1e-9) return null;
            if (pivot != col) { for (int k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]); (x[col], x[pivot]) = (x[pivot], x[col]); }
            for (int row = col + 1; row < n; row++)
            {
                double f = m[row, col] / m[col, col];
                for (int k = col; k < n; k++) m[row, k] -= f * m[col, k];
                x[row] -= f * x[col];
            }
        }
        for (int row = n - 1; row >= 0; row--)
        {
            double sum = x[row];
            for (int k = row + 1; k < n; k++) sum -= m[row, k] * x[k];
            x[row] = sum / m[row, row];
        }
        return x;
    }
}
