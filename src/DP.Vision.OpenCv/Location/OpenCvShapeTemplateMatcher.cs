using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// 梯度方向形状匹配（Steger/HALCON同类思路）：分数为模型边缘点单位梯度方向与图像对应点单位梯度方向点积的平均值，
/// 低于搜索最小对比度的图像梯度记零向量。对光照线性变化、局部遮挡和背景杂乱稳健。
/// 顶层按贪婪度提前终止；旋转/缩放直接作用于特征点和方向，不旋转图像；最终层可选点到边缘线的最小二乘精化。
/// </summary>
internal sealed class OpenCvShapeTemplateMatcher : OpenCvPyramidMatcherBase
{
    private readonly OpenCvPyramidSettings _settings;

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
    private (float[] X, float[] Y) Directions(Mat image, Rect rect)
    {
        var (gx, gy) = Gradients(image, rect);
        float minimum = _settings.MinimumContrast;
        for (int i = 0; i < gx.Length; i++)
        {
            float m = (float)Math.Sqrt(gx[i] * gx[i] + gy[i] * gy[i]);
            if (m < minimum) { gx[i] = 0; gy[i] = 0; }
            else { gx[i] /= m; gy[i] /= m; }
        }
        return (gx, gy);
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
    private (int[] X, int[] Y, float[] Ux, float[] Uy) Rotate(int level, PyramidPose pose)
    {
        var data = Model.Levels[level]; int n = data.FeatureCount;
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

    protected override unsafe float[] ScanTop(SearchState search, int index, PyramidPose pose, float threshold, CancellationToken token)
    {
        var state = (State)search;
        int width = state.Pyramid[state.Pyramid.Length - 1].Cols, height = state.Pyramid[state.Pyramid.Length - 1].Rows;
        int mw = width - pose.Width + 1, mh = height - pose.Height + 1;
        var (fx, fy, ux, uy) = Rotate(state.Pyramid.Length - 1, pose);
        int n = fx.Length;
        var offsets = new int[n];
        for (int i = 0; i < n; i++) offsets[i] = fy[i] * width + fx[i];
        // 提前终止界：安全界（剩余点全部完美也达不到阈值）与贪婪界（部分和应不低于阈值比例）按贪婪度插值。
        var bound = new float[n]; double g = _settings.Greediness, smin = threshold;
        for (int j = 0; j < n; j++) { int count = j + 1; bound[j] = (float)(g * smin * count + (1 - g) * (smin * n - (n - count)) - 1e-4); }
        var map = new float[mw * mh];
        // 顶层图已经过多次高斯降采样，方向场在1像素内变化很小：大图隔点扫描，下一层±3像素的精化窗口补回偏差。
        int top = state.Pyramid.Length - 1, stride = top > 0 && (long)mw * mh > 4096 ? 2 : 1;
        if (stride > 1) for (int i = 0; i < map.Length; i++) map[i] = -1;
        int metric = (int)_settings.Metric; float inverse = 1f / n;
        fixed (float* dx = state.TopX, dy = state.TopY, px = ux, py = uy, pb = bound, pm = map)
        fixed (int* po = offsets)
        {
            for (int v = 0; v < mh; v += stride)
            {
                if ((v & 15) == 0) token.ThrowIfCancellationRequested();
                for (int u = 0; u < mw; u += stride)
                {
                    int origin = v * width + u; float sum = 0; int j = 0;
                    if (metric == 0)
                        for (; j < n; j++) { int p = origin + po[j]; sum += px[j] * dx[p] + py[j] * dy[p]; if (sum < pb[j]) break; }
                    else if (metric == 1)
                        for (; j < n; j++) { int p = origin + po[j]; sum += px[j] * dx[p] + py[j] * dy[p]; if (Math.Abs(sum) < pb[j]) break; }
                    else
                        for (; j < n; j++) { int p = origin + po[j]; sum += Math.Abs(px[j] * dx[p] + py[j] * dy[p]); if (sum < pb[j]) break; }
                    pm[v * mw + u] = j == n ? (metric == 1 ? Math.Abs(sum) : sum) * inverse : -1;
                }
            }
        }
        return map;
    }

    protected override unsafe float[] Window(SearchState search, int level, PyramidPose pose, int u0, int v0, int r)
    {
        var state = (State)search;
        var image = state.Pyramid[level];
        int side = 2 * r + 1; var result = new float[side * side];
        for (int i = 0; i < result.Length; i++) result[i] = float.NaN;
        int x0 = Math.Max(0, u0 - r), y0 = Math.Max(0, v0 - r);
        int x1 = Math.Min(image.Cols, u0 + r + pose.Width), y1 = Math.Min(image.Rows, v0 + r + pose.Height);
        if (x1 - x0 < pose.Width || y1 - y0 < pose.Height) return result;
        var rect = new Rect(x0, y0, x1 - x0, y1 - y0);
        var (dxs, dys) = Directions(image, rect);
        var (fx, fy, ux, uy) = Rotate(level, pose);
        int n = fx.Length, metric = (int)_settings.Metric, width = rect.Width;
        fixed (float* dx = dxs, dy = dys, px = ux, py = uy)
        {
            for (int gy = 0; gy < side; gy++)
            for (int gx = 0; gx < side; gx++)
            {
                int u = u0 - r + gx - x0, v = v0 - r + gy - y0;
                if (u < 0 || v < 0 || u + pose.Width > rect.Width || v + pose.Height > rect.Height) continue;
                float sum = 0; int origin = v * width + u;
                for (int j = 0; j < n; j++)
                {
                    int p = origin + fy[j] * width + fx[j];
                    float d = px[j] * dx[p] + py[j] * dy[p];
                    sum += metric == 2 ? Math.Abs(d) : d;
                }
                result[gy * side + gx] = Math.Max(0, metric == 1 ? Math.Abs(sum) / n : sum / n);
            }
        }
        return result;
    }

    /// <summary>
    /// 点到边缘线最小二乘：每个模型边缘点沿其法向在±2像素内找图像梯度投影的极大（抛物线亚像素），
    /// 以点到该边缘切线的距离为残差，线性化求解平移、旋转和尺度增量，迭代至收敛。精化量异常时保留插值结果。
    /// </summary>
    protected override PyramidCandidate Polish(SearchState search, PyramidCandidate candidate, OpenCvPyramidPlan plan)
    {
        if (!_settings.LeastSquares) return candidate;
        var state = (State)search; var image = state.Pyramid[0]; var data = Model.Levels[0];
        var pose = OpenCvPyramidPlan.Bounds(plan, 0, candidate.Angle, candidate.Scale);
        int margin = 5;
        int x0 = Math.Max(0, (int)Math.Floor(candidate.X - pose.Width / 2d) - margin), y0 = Math.Max(0, (int)Math.Floor(candidate.Y - pose.Height / 2d) - margin);
        int x1 = Math.Min(image.Cols, (int)Math.Ceiling(candidate.X + pose.Width / 2d) + margin), y1 = Math.Min(image.Rows, (int)Math.Ceiling(candidate.Y + pose.Height / 2d) + margin);
        if (x1 - x0 < 3 || y1 - y0 < 3) return candidate;
        var rect = new Rect(x0, y0, x1 - x0, y1 - y0);
        var (gx, gy) = Gradients(image, rect);
        int w = rect.Width, h = rect.Height;
        float Sample(float[] field, double x, double y)
        {
            // 边界坐标转像素索引坐标后双线性插值。
            double fx = x - .5 - x0, fy = y - .5 - y0;
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            if (ix < 0 || iy < 0 || ix + 1 >= w || iy + 1 >= h) return float.NaN;
            double ax = fx - ix, ay = fy - iy; int i = iy * w + ix;
            return (float)((1 - ay) * ((1 - ax) * field[i] + ax * field[i + 1]) + ay * ((1 - ax) * field[i + w] + ax * field[i + w + 1]));
        }
        double cx = candidate.X, cy = candidate.Y, angle = candidate.Angle, scale = candidate.Scale;
        int unknowns = 2 + (plan.FixedAngle ? 0 : 1) + (plan.FixedScale ? 0 : 1);
        double polarity = 1;
        if (_settings.Metric == EShapeMetric.IgnoreGlobalPolarity)
        {
            double c0 = Math.Cos(angle), s0 = Math.Sin(angle), total = 0;
            for (int i = 0; i < data.FeatureCount; i++)
            {
                double rx = scale * (c0 * data.Qx[i] - s0 * data.Qy[i]), ry = scale * (s0 * data.Qx[i] + c0 * data.Qy[i]);
                double nx = c0 * data.Ux[i] - s0 * data.Uy[i], ny = s0 * data.Ux[i] + c0 * data.Uy[i];
                float ax = Sample(gx, cx + rx, cy + ry), ay = Sample(gy, cx + rx, cy + ry);
                if (!float.IsNaN(ax)) total += nx * ax + ny * ay;
            }
            polarity = total < 0 ? -1 : 1;
        }
        var samples = new float[9];
        for (int iteration = 0; iteration < 6; iteration++)
        {
            var normal = new double[unknowns, unknowns]; var rhs = new double[unknowns]; int used = 0;
            double c = Math.Cos(angle), s = Math.Sin(angle);
            var row = new double[unknowns];
            for (int i = 0; i < data.FeatureCount; i++)
            {
                double rx = scale * (c * data.Qx[i] - s * data.Qy[i]), ry = scale * (s * data.Qx[i] + c * data.Qy[i]);
                double nx = c * data.Ux[i] - s * data.Uy[i], ny = s * data.Ux[i] + c * data.Uy[i];
                double px = cx + rx, py = cy + ry;
                int bestK = -1; float bestV = float.NegativeInfinity; bool ok = true;
                for (int q = 0; q < samples.Length; q++)
                {
                    double t = -2 + .5 * q;
                    float ax = Sample(gx, px + t * nx, py + t * ny), ay = Sample(gy, px + t * nx, py + t * ny);
                    if (float.IsNaN(ax)) { ok = false; break; }
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
