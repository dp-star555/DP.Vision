using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// OpenCV实现的局部块异常检测（PatchCore式，手工特征）：良品块经贪心k中心核心集压缩为记忆库，
/// 检测块到最近良品块的L2距离即异常得分。阈值按留一法标定（每张良品用不含它的记忆库评分，取最大值乘余量）；
/// 只有一张良品时用平移1像素并轻度模糊的增强图代替。
/// </summary>
public sealed class OpenCvPatchAnomalyDetector : IPatchAnomalyDetector
{
    /// <summary>留一法标定与位置相关训练的查询采样步长；检测步长由参数决定。</summary>
    private const int TrainingStride = 2;

    /// <summary>阈值下限，避免完全相同的良品把阈值标定为0。</summary>
    private const double MinimumThreshold = .05;

    /// <inheritdoc/>
    public PatchAnomalyModel Train(
        IReadOnlyList<IImageSource> good,
        PatchAnomalyOptions options,
        CancellationToken token = default
    )
    {
        if (good == null || good.Count == 0 || good.Any(g => g == null))
        {
            throw new ArgumentException("At least one good image is required.", nameof(good));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        int p = options.PatchSize;
        var planes = new List<(PatchFeatures.Plane Full, PatchFeatures.Plane Half)>();
        foreach (var image in good)
        {
            using var gray = CvPixels.Gray(image);
            planes.Add(PatchFeatures.Prepare(gray));
        }

        if (options.LocalRadius is int radius)
        {
            return TrainLocal(good, planes, options, radius, token);
        }

        // 与位置无关的记忆库按1像素步长采样，覆盖全部平移相位；否则奇数像素平移的良品边缘整体错开1像素，标定阈值被抬高。
        var sets = planes.Select(pl => PatchFeatures.Extract(pl.Full, pl.Half, p, 1, token)).ToList();

        using var memory = Memory(sets, options.MemorySize, p, 17, token);
        double worst = 0;
        string calibration;
        if (sets.Count >= 2)
        {
            for (int i = 0; i < sets.Count; i++)
            {
                using var others = Memory(
                    sets.Where((_, j) => j != i).ToList(),
                    Math.Max(256, options.MemorySize / 2),
                    p,
                    31 + i,
                    token
                );
                worst = Math.Max(worst, InteriorMax(Scores(sets[i], others), sets[i], planes[i].Full, p, 1));
            }

            calibration = $"留一法（{sets.Count}张良品，每张用其余良品的记忆库评分）";
        }
        else
        {
            using var gray = CvPixels.Gray(good[0]);
            using var shifted = Augment(gray);
            var (full, half) = PatchFeatures.Prepare(shifted);
            var augmented = PatchFeatures.Extract(full, half, p, TrainingStride, token);
            worst = InteriorMax(Scores(augmented, memory), augmented, full, p, TrainingStride);
            calibration = "单张良品：平移1像素并轻度模糊的增强图评分";
        }

        memory.GetArray(out float[] values);
        return new PatchAnomalyModel(
            p,
            PatchFeatures.Dimensions(p),
            values,
            Math.Max(MinimumThreshold, worst * options.ThresholdMargin),
            good.Count,
            calibration + $"，最大得分{worst:F3}×余量{options.ThresholdMargin:F2}"
        );
    }

    /// <inheritdoc/>
    public PatchAnomalyResult Detect(
        IImageSource image,
        PatchAnomalyModel model,
        PatchAnomalyOptions options,
        CancellationToken token = default
    )
    {
        if (image == null || model == null || options == null)
        {
            throw new ArgumentNullException(
                image == null ? nameof(image)
                : model == null ? nameof(model)
                : nameof(options)
            );
        }

        double threshold = options.Threshold ?? model.Threshold;
        if (
            model.FeatureSource != PatchAnomalyModel.Handcrafted
            || model.PatchSize != options.PatchSize
            || model.Dimensions != PatchFeatures.Dimensions(model.PatchSize)
        )
        {
            return new PatchAnomalyResult(
                EAlgorithmStatus.UnsupportedInput,
                0,
                threshold,
                new[]
                {
                    new QualityFinding(
                        "patch_anomaly_model_mismatch",
                        $"模型（特征{model.FeatureSource}、块大小{model.PatchSize}）与本实现（手工特征、块大小{options.PatchSize}）不一致，需重新训练。",
                        EQualityFindingKind.Blocker
                    ),
                },
                null
            );
        }

        int p = model.PatchSize;
        using var gray = CvPixels.Gray(image);
        if (model.Radius > 0 && (gray.Cols != model.Width || gray.Rows != model.Height))
        {
            return new PatchAnomalyResult(
                EAlgorithmStatus.UnsupportedInput,
                0,
                threshold,
                new[]
                {
                    new QualityFinding(
                        "patch_anomaly_size_mismatch",
                        $"位置相关模型要求裁图{model.Width}×{model.Height}，实际{gray.Cols}×{gray.Rows}；ROI改变后需重新训练。",
                        EQualityFindingKind.Blocker
                    ),
                },
                null
            );
        }

        var (full, half) = PatchFeatures.Prepare(gray);
        PatchFeatures.Set query;
        float[] scores;
        if (model.Radius > 0)
        {
            // 位置相关：纸白块也评分，良品此处有墨而实际无墨（缺笔画）同样得高分。
            query = PatchFeatures.Extract(full, half, p, options.Stride, token, includeBlank: true);
            scores = LocalScores(query, full, Planes(model), model.Radius, p, token);
        }
        else
        {
            query = PatchFeatures.Extract(full, half, p, options.Stride, token);
            using var memory = new Mat(model.Count, model.Dimensions, MatType.CV_32F);
            memory.SetArray(model.CopyMemory());
            scores = Scores(query, memory);
        }

        // 得分写入块中心区域（边长取块的一半与步长中较大者），避免整块外扩使缺陷框偏大。
        int core = Math.Max(p / 2, options.Stride),
            inset = (p - core) / 2;
        var values = new float[gray.Rows * gray.Cols];
        for (int i = 0; i < query.Count; i++)
        {
            var corner = query.Corners[i];
            for (int y = corner.Y + inset; y < Math.Min(gray.Rows, corner.Y + inset + core); y++)
            {
                for (int x = corner.X + inset; x < Math.Min(gray.Cols, corner.X + inset + core); x++)
                {
                    int k = y * gray.Cols + x;
                    if (values[k] < scores[i])
                    {
                        values[k] = scores[i];
                    }
                }
            }
        }

        using var map = new Mat(gray.Rows, gray.Cols, MatType.CV_32F);
        System.Runtime.InteropServices.Marshal.Copy(values, 0, map.Data, values.Length);

        // 裁图边缘一个块宽的范围只作上下文（1/2尺度上下文块在此范围内会伸出裁图）：
        // 内容被ROI截断时（如字顶贴边）形态与良品不同，但不是印刷缺陷。
        double maximum = scores.DefaultIfEmpty(0).Max();
        return AnomalyMap.Result(
            map,
            threshold,
            p,
            options.MinimumArea,
            maximum,
            $"已评分{query.Count}个局部块（块{p}像素、步长{options.Stride}），记忆库{model.Count}块/{model.TrainingImages}张良品；"
                + $"最大得分{maximum:F3}，阈值{threshold:F3}（{(options.Threshold == null ? model.Calibration : "显式设置")}）。"
                + $"只能发现与良品不相似的局部形态；良品中未出现过的字形也会被视为异常；裁图边缘{p}像素内只作上下文，不单独报异常。"
        );
    }

    /// <summary>
    /// 位置相关训练：保存各良品墨量平面；阈值按留一法（每张良品只与其余良品同位置±半径比较）标定，
    /// 单张良品时用平移1像素并轻度模糊的增强图。
    /// </summary>
    private static PatchAnomalyModel TrainLocal(
        IReadOnlyList<IImageSource> good,
        List<(PatchFeatures.Plane Full, PatchFeatures.Plane Half)> planes,
        PatchAnomalyOptions options,
        int radius,
        CancellationToken token
    )
    {
        int p = options.PatchSize,
            width = planes[0].Full.Width,
            height = planes[0].Full.Height;
        if (planes.Any(pl => pl.Full.Width != width || pl.Full.Height != height))
        {
            throw new ArgumentException(
                "Position-dependent training requires equally sized crops.",
                nameof(good)
            );
        }

        double worst = 0;
        string calibration;
        if (planes.Count >= 2)
        {
            for (int i = 0; i < planes.Count; i++)
            {
                var query = PatchFeatures.Extract(
                    planes[i].Full,
                    planes[i].Half,
                    p,
                    TrainingStride,
                    token,
                    true
                );
                var others = planes.Where((_, j) => j != i).ToList();
                worst = Math.Max(
                    worst,
                    InteriorMax(
                        LocalScores(query, planes[i].Full, others, radius, p, token),
                        query,
                        planes[i].Full,
                        p,
                        TrainingStride
                    )
                );
            }

            calibration = $"位置相关±{radius}像素，留一法（{planes.Count}张良品）";
        }
        else
        {
            using var gray = CvPixels.Gray(good[0]);
            using var augmented = Augment(gray);
            var (full, half) = PatchFeatures.Prepare(augmented);
            var query = PatchFeatures.Extract(full, half, p, TrainingStride, token, true);
            worst = InteriorMax(
                LocalScores(query, full, planes, radius, p, token),
                query,
                full,
                p,
                TrainingStride
            );
            calibration = $"位置相关±{radius}像素，单张良品：平移1像素并轻度模糊的增强图评分";
        }

        var memory = planes.SelectMany(pl => pl.Full.Data.Concat(pl.Half.Data)).ToArray();
        return new PatchAnomalyModel(
            p,
            PatchFeatures.Dimensions(p),
            memory,
            Math.Max(MinimumThreshold, worst * options.ThresholdMargin),
            planes.Count,
            calibration + $"，最大得分{worst:F3}×余量{options.ThresholdMargin:F2}",
            radius,
            width,
            height
        );
    }

    /// <summary>平移1像素并轻度模糊，模拟单张良品时的成像波动。</summary>
    private static Mat Augment(Mat gray)
    {
        var shifted = new Mat();
        using var shift = new Mat(2, 3, MatType.CV_64F, Scalar.All(0));
        shift.Set(0, 0, 1.0);
        shift.Set(0, 2, 1.0);
        shift.Set(1, 1, 1.0);
        shift.Set(1, 2, 1.0);
        Cv2.WarpAffine(gray, shifted, shift, gray.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
        Cv2.GaussianBlur(shifted, shifted, new Size(0, 0), .5);
        return shifted;
    }

    private static List<(PatchFeatures.Plane Full, PatchFeatures.Plane Half)> Planes(PatchAnomalyModel model)
    {
        int w = model.Width,
            h = model.Height,
            hw = Math.Max(1, w / 2),
            hh = Math.Max(1, h / 2),
            length = PatchAnomalyModel.PlaneLength(w, h);
        var memory = model.CopyMemory();
        var planes = new List<(PatchFeatures.Plane, PatchFeatures.Plane)>();
        for (int i = 0; i < model.TrainingImages; i++)
        {
            var full = new float[w * h];
            var half = new float[hw * hh];
            Array.Copy(memory, i * length, full, 0, full.Length);
            Array.Copy(memory, i * length + full.Length, half, 0, half.Length);
            planes.Add((new PatchFeatures.Plane(full, w, h), new PatchFeatures.Plane(half, hw, hh)));
        }

        return planes;
    }

    /// <summary>
    /// 每个查询块到任一良品同位置±半径内块的最近L2距离。按“良品×偏移”整体计算：原尺度块距离是差值平方图在P×P窗口内的和，
    /// 用积分图一次求出所有块。1/2尺度上下文按实际物理位置对应：偏移为奇数像素时，良品的上下文取按同一相位（错开1像素）
    /// 下采样的平面，因此任意整像素平移都能精确对上；否则奇数偏移下上下文相差半个下采样像素，锐利边缘处得分虚高，
    /// 平移1像素的良品也会被判异常。
    /// </summary>
    private static float[] LocalScores(
        PatchFeatures.Set query,
        PatchFeatures.Plane queryFull,
        IReadOnlyList<(PatchFeatures.Plane Full, PatchFeatures.Plane Half)> references,
        int radius,
        int patchSize,
        CancellationToken token
    )
    {
        int n = query.Count,
            w = queryFull.Width,
            h = queryFull.Height,
            hp = patchSize / 2,
            pad = patchSize + radius + 2;
        var best = new double[n];
        var cqx = new int[n];
        var cqy = new int[n];
        for (int i = 0; i < n; i++)
        {
            best[i] = double.MaxValue;
            cqx[i] = (query.Corners[i].X + hp) / 2 - hp;
            cqy[i] = (query.Corners[i].Y + hp) / 2 - hp;
        }

        var queryContext = PatchFeatures.PhaseContext(queryFull, 0, 0, pad);
        var fullIntegral = new double[(w + 1) * (h + 1)];
        var squared = new double[w * h];
        foreach (var (full, _) in references)
        {
            var phases = new PatchFeatures.Plane?[4];
            var contextIntegrals = new Dictionary<(int, int, int), double[]>();
            for (int oy = -radius; oy <= radius; oy++)
            {
                for (int ox = -radius; ox <= radius; ox++)
                {
                    token.ThrowIfCancellationRequested();
                    Array.Clear(squared, 0, squared.Length);
                    for (int y = Math.Max(0, -oy); y < Math.Min(h, h - oy); y++)
                    {
                        int q = y * w,
                            r = (y + oy) * w + ox;
                        for (int x = Math.Max(0, -ox); x < Math.Min(w, w - ox); x++)
                        {
                            double t = queryFull.Data[q + x] - full.Data[r + x];
                            squared[q + x] = t * t;
                        }
                    }

                    Integral(squared, w, h, fullIntegral);

                    // 上下文块覆盖原尺度[2c, 2c+2P)；良品对应区域为[2c+o, …)，落在相位(o mod 2)的下采样平面第c+(o−相位)/2格。
                    int px = ((ox % 2) + 2) % 2,
                        py = ((oy % 2) + 2) % 2,
                        phase = py * 2 + px;
                    var key = (phase, (ox - px) / 2, (oy - py) / 2);
                    if (!contextIntegrals.TryGetValue(key, out var context))
                    {
                        var plane = phases[phase] ??= PatchFeatures.PhaseContext(full, px, py, pad);
                        context = contextIntegrals[key] = ContextIntegral(
                            queryContext,
                            plane,
                            key.Item2,
                            key.Item3
                        );
                    }

                    for (int i = 0; i < n; i++)
                    {
                        int x = query.Corners[i].X,
                            y = query.Corners[i].Y;
                        if (x + ox < 0 || y + oy < 0 || x + ox + patchSize > w || y + oy + patchSize > h)
                        {
                            continue;
                        }

                        double d = Box(fullIntegral, w + 1, x, y, patchSize);
                        if (d >= best[i])
                        {
                            continue;
                        }

                        d += Box(context, queryContext.Width + 1, cqx[i] + pad, cqy[i] + pad, patchSize);
                        if (d < best[i])
                        {
                            best[i] = d;
                        }
                    }
                }
            }
        }

        var scores = new float[n];
        for (int i = 0; i < n; i++)
        {
            scores[i] = best[i] == double.MaxValue ? 0 : (float)Math.Sqrt(Math.Max(0, best[i]));
        }

        return scores;
    }

    /// <summary>
    /// 边缘带以外的块中的最大得分（与<see cref = "AnomalyMap"/>一致：块的得分核心区与边缘带以内区域相交即计入）。
    /// 阈值标定只用这些块：边缘带内的内容可能被裁图截断，检测时也不报异常。裁图太小没有内部区域时用全部块。
    /// </summary>
    private static double InteriorMax(
        float[] scores,
        PatchFeatures.Set set,
        PatchFeatures.Plane full,
        int patchSize,
        int stride
    )
    {
        int core = Math.Max(patchSize / 2, stride),
            inset = (patchSize - core) / 2,
            w = full.Width,
            h = full.Height,
            border = patchSize;
        if (w <= 2 * border || h <= 2 * border)
        {
            return scores.DefaultIfEmpty(0).Max();
        }

        double worst = 0;
        for (int i = 0; i < scores.Length; i++)
        {
            int x = set.Corners[i].X + inset,
                y = set.Corners[i].Y + inset;
            if (x + core > border && x < w - border && y + core > border && y < h - border)
            {
                worst = Math.Max(worst, scores[i]);
            }
        }

        return worst;
    }

    /// <summary>(查询上下文 − 平移后的良品上下文)²的积分图；平移超出填充范围处按纸白（0）计。</summary>
    private static double[] ContextIntegral(
        PatchFeatures.Plane query,
        PatchFeatures.Plane reference,
        int shiftX,
        int shiftY
    )
    {
        int w = query.Width,
            h = query.Height;
        var squared = new double[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double t = query.Data[y * w + x] - reference.At(x + shiftX, y + shiftY);
                squared[y * w + x] = t * t;
            }
        }

        var integral = new double[(w + 1) * (h + 1)];
        Integral(squared, w, h, integral);
        return integral;
    }

    private static void Integral(double[] values, int w, int h, double[] integral)
    {
        int stride = w + 1;
        Array.Clear(integral, 0, stride);
        for (int y = 0; y < h; y++)
        {
            double row = 0;
            integral[(y + 1) * stride] = 0;
            for (int x = 0; x < w; x++)
            {
                row += values[y * w + x];
                integral[(y + 1) * stride + x + 1] = integral[y * stride + x + 1] + row;
            }
        }
    }

    private static double Box(double[] integral, int stride, int x, int y, int size)
    {
        return integral[(y + size) * stride + x + size]
            - integral[y * stride + x + size]
            - integral[(y + size) * stride + x]
            + integral[y * stride + x];
    }

    /// <summary>若干良品块集合并为记忆库并按核心集压缩；全部为纸白时用单个零向量代表纸白。</summary>
    private static Mat Memory(
        IReadOnlyList<PatchFeatures.Set> sets,
        int capacity,
        int patchSize,
        int seed,
        CancellationToken token
    )
    {
        int d = PatchFeatures.Dimensions(patchSize),
            n = sets.Sum(s => s.Count);
        if (n == 0)
        {
            return new Mat(1, d, MatType.CV_32F, Scalar.All(0));
        }

        using var all = new Mat(n, d, MatType.CV_32F);
        all.SetArray(sets.SelectMany(s => s.Values).ToArray());
        var keep = Coreset.Select(all, capacity, seed, token);
        var result = new Mat(keep.Length, d, MatType.CV_32F);
        for (int i = 0; i < keep.Length; i++)
        {
            all.Row(keep[i]).CopyTo(result.Row(i));
        }

        return result;
    }

    /// <summary>每个查询块到记忆库最近块的L2距离。</summary>
    private static float[] Scores(PatchFeatures.Set query, Mat memory)
    {
        if (query.Count == 0)
        {
            return Array.Empty<float>();
        }

        using var q = new Mat(query.Count, query.Dimensions, MatType.CV_32F);
        q.SetArray(query.Values.ToArray());
        using var matcher = new BFMatcher(NormTypes.L2, false);
        var matches = matcher.Match(q, memory);
        var scores = new float[query.Count];
        foreach (var m in matches)
        {
            scores[m.QueryIdx] = m.Distance;
        }

        return scores;
    }
}
