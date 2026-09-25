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
                worst = Math.Max(worst, Scores(sets[i], others).DefaultIfEmpty(0).Max());
            }

            calibration = $"留一法（{sets.Count}张良品，每张用其余良品的记忆库评分）";
        }
        else
        {
            using var gray = CvPixels.Gray(good[0]);
            using var shifted = Augment(gray);
            var (full, half) = PatchFeatures.Prepare(shifted);
            worst = Scores(PatchFeatures.Extract(full, half, p, TrainingStride, token), memory)
                .DefaultIfEmpty(0)
                .Max();
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
            scores = LocalScores(query, Planes(model), model.Radius, p, token);
        }
        else
        {
            query = PatchFeatures.Extract(full, half, p, options.Stride, token);
            using var memory = new Mat(model.Count, model.Dimensions, MatType.CV_32F);
            memory.SetArray(model.CopyMemory());
            scores = Scores(query, memory);
        }

        // 得分写入块中心区域（边长取块的一半与步长中较大者），避免整块外扩使缺陷框偏大。
        using var map = new Mat(gray.Rows, gray.Cols, MatType.CV_32F, Scalar.All(0));
        int core = Math.Max(p / 2, options.Stride),
            inset = (p - core) / 2;
        for (int i = 0; i < query.Count; i++)
        {
            var corner = query.Corners[i];
            for (int y = corner.Y + inset; y < Math.Min(gray.Rows, corner.Y + inset + core); y++)
            {
                for (int x = corner.X + inset; x < Math.Min(gray.Cols, corner.X + inset + core); x++)
                {
                    if (map.At<float>(y, x) < scores[i])
                    {
                        map.Set(y, x, scores[i]);
                    }
                }
            }
        }

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
                worst = Math.Max(worst, LocalScores(query, others, radius, p, token).DefaultIfEmpty(0).Max());
            }

            calibration = $"位置相关±{radius}像素，留一法（{planes.Count}张良品）";
        }
        else
        {
            using var gray = CvPixels.Gray(good[0]);
            using var augmented = Augment(gray);
            var (full, half) = PatchFeatures.Prepare(augmented);
            var query = PatchFeatures.Extract(full, half, p, TrainingStride, token, true);
            worst = LocalScores(query, planes, radius, p, token).DefaultIfEmpty(0).Max();
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

    /// <summary>每个查询块到任一良品同位置±半径内块的最近L2距离。</summary>
    private static float[] LocalScores(
        PatchFeatures.Set query,
        IReadOnlyList<(PatchFeatures.Plane Full, PatchFeatures.Plane Half)> references,
        int radius,
        int patchSize,
        CancellationToken token
    )
    {
        var scores = new float[query.Count];
        var q = new float[query.Dimensions];
        for (int i = 0; i < query.Count; i++)
        {
            if ((i & 1023) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            query.Values.CopyTo(i * query.Dimensions, q, 0, query.Dimensions);
            var corner = query.Corners[i];
            float best = float.MaxValue;
            foreach (var (full, half) in references)
            {
                for (
                    int y = Math.Max(0, corner.Y - radius);
                    y <= Math.Min(full.Height - patchSize, corner.Y + radius);
                    y++
                )
                {
                    for (
                        int x = Math.Max(0, corner.X - radius);
                        x <= Math.Min(full.Width - patchSize, corner.X + radius);
                        x++
                    )
                    {
                        float d = PatchFeatures.SquaredDistance(q, full, half, x, y, patchSize, best);
                        if (d < best)
                        {
                            best = d;
                        }
                    }
                }
            }

            scores[i] = best == float.MaxValue ? 0 : (float)Math.Sqrt(best);
        }

        return scores;
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
