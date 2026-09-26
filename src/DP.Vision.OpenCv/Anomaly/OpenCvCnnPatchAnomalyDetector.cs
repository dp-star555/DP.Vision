using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// PatchCore式局部块异常检测，特征取自ONNX卷积骨干网络（由OpenCV DNN运行，不增加运行库依赖）。
/// 与<see cref = "OpenCvPatchAnomalyDetector"/>接口、阈值标定和两种模式相同，只替换特征：
/// 与位置无关模式用核心集记忆库；位置相关模式（<see cref = "PatchAnomalyOptions.LocalRadius"/>）只与良品同位置±半径比较。
/// 模型记录骨干网络哈希与放大倍数，检测时不一致即拒绝。
/// </summary>
public sealed partial class OpenCvCnnPatchAnomalyDetector : IPatchAnomalyDetector, IDisposable
{
    /// <summary>阈值下限。</summary>
    private const double MinimumThreshold = .05;

    /// <summary>裁图边缘只作上下文的宽度（原图像素），与骨干网络的感受野边缘效应对应。</summary>
    private const int Border = 8;

    private readonly CnnFeatures _features;

    /// <summary>位置相关模型的良品特征网格缓存：模型只保存灰度裁图，首次检测时用同一骨干网络重算一次。</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PatchAnomalyModel, List<CellGrid>> _references =
        new System.Runtime.CompilerServices.ConditionalWeakTable<PatchAnomalyModel, List<CellGrid>>();

    /// <summary>加载骨干网络。</summary>
    /// <param name = "backbonePath">
    /// ONNX骨干网络：输入1×3×H×W（H、W为8的倍数），两个输出分别为1/4与1/8分辨率特征。
    /// 可用tools/export_ppocr_backbone.py从PP-OCR检测模型截取。
    /// </param>
    /// <param name = "scale">裁图输入网络前的放大倍数，1–4，默认2（1/4级特征每单元对应原图2像素）。</param>
    public OpenCvCnnPatchAnomalyDetector(string backbonePath, double scale = 2)
    {
        _features = new CnnFeatures(backbonePath, scale);
    }

    /// <summary>写入模型的特征来源标识（骨干网络哈希与放大倍数）。</summary>
    public string FeatureSource => _features.Source;

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

        var grids = good.Select(g => Grid(g, token)).ToList();
        int d = grids[0].Dimensions;
        double worst = 0;
        string calibration;
        float[] memory;
        int radius = options.LocalRadius ?? 0,
            cells = radius > 0 ? Math.Max(1, (int)Math.Ceiling(radius / _features.Cell)) : 0;
        if (radius > 0)
        {
            if (good.Any(g => g.Info.Width != good[0].Info.Width || g.Info.Height != good[0].Info.Height))
            {
                throw new ArgumentException(
                    "Position-dependent training requires equally sized crops.",
                    nameof(good)
                );
            }

            if (grids.Count >= 2)
            {
                for (int i = 0; i < grids.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var others = grids.Where((_, j) => j != i).ToList();
                    worst = Math.Max(worst, Local(grids[i], others, cells).DefaultIfEmpty(0).Max());
                }

                calibration = $"CNN特征，位置相关±{radius}像素，留一法（{grids.Count}张良品）";
            }
            else
            {
                worst = Local(Augmented(good[0], token), grids, cells).DefaultIfEmpty(0).Max();
                calibration = $"CNN特征，位置相关±{radius}像素，单张良品：平移1像素并轻度模糊的增强图评分";
            }

            // 只保存灰度裁图（约每像素4字节），特征在加载后按同一骨干网络重算；比保存特征网格小约40倍。
            memory = good.SelectMany(Pixels).ToArray();
        }
        else
        {
            using var bank = Bank(grids, options.MemorySize, 17, token);
            if (grids.Count >= 2)
            {
                for (int i = 0; i < grids.Count; i++)
                {
                    using var others = Bank(
                        grids.Where((_, j) => j != i).ToList(),
                        Math.Max(256, options.MemorySize / 2),
                        31 + i,
                        token
                    );
                    worst = Math.Max(worst, Nearest(grids[i], others).DefaultIfEmpty(0).Max());
                }

                calibration = $"CNN特征，留一法（{grids.Count}张良品，每张用其余良品的记忆库评分）";
            }
            else
            {
                worst = Nearest(Augmented(good[0], token), bank).DefaultIfEmpty(0).Max();
                calibration = "CNN特征，单张良品：平移1像素并轻度模糊的增强图评分";
            }

            memory = new float[bank.Rows * d];
            System.Runtime.InteropServices.Marshal.Copy(bank.Data, memory, 0, memory.Length);
        }

        return new PatchAnomalyModel(
            4,
            d,
            memory,
            Math.Max(MinimumThreshold, worst * options.ThresholdMargin),
            good.Count,
            calibration + $"，最大得分{worst:F3}×余量{options.ThresholdMargin:F2}",
            radius,
            radius > 0 ? good[0].Info.Width : 0,
            radius > 0 ? good[0].Info.Height : 0,
            _features.Source
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
        if (model.FeatureSource != _features.Source)
        {
            return Blocked(
                "patch_anomaly_model_mismatch",
                $"模型特征来源{model.FeatureSource}与当前骨干网络{_features.Source}不一致，需用当前骨干网络重新训练。",
                threshold
            );
        }

        var grid = Grid(image, token);
        if (grid.Dimensions != model.Dimensions)
        {
            return Blocked("patch_anomaly_model_mismatch", "特征维数与模型不一致。", threshold);
        }

        float[] scores;
        if (model.Radius > 0)
        {
            if (image.Info.Width != model.Width || image.Info.Height != model.Height)
            {
                return Blocked(
                    "patch_anomaly_size_mismatch",
                    $"位置相关模型要求裁图{model.Width}×{model.Height}，实际{image.Info.Width}×{image.Info.Height}；ROI改变后需重新训练。",
                    threshold
                );
            }

            var references = _references.GetValue(model, References);
            scores = Local(grid, references, Math.Max(1, (int)Math.Ceiling(model.Radius / _features.Cell)));
        }
        else
        {
            using var bank = new Mat(model.Count, model.Dimensions, MatType.CV_32F);
            System.Runtime.InteropServices.Marshal.Copy(
                model.CopyMemory(),
                0,
                bank.Data,
                model.Count * model.Dimensions
            );
            scores = Nearest(grid, bank);
        }

        using var gray = CvPixels.Gray(image);
        using var map = new Mat(gray.Rows, gray.Cols, MatType.CV_32F, Scalar.All(0));
        double cell = _features.Cell;
        int side = Math.Max(1, (int)Math.Ceiling(cell));
        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                float s = scores[y * grid.Width + x];
                if (s <= 0)
                {
                    continue;
                }

                int x0 = (int)(x * cell),
                    y0 = (int)(y * cell);
                for (int py = y0; py < Math.Min(gray.Rows, y0 + side); py++)
                {
                    for (int px = x0; px < Math.Min(gray.Cols, x0 + side); px++)
                    {
                        if (map.At<float>(py, px) < s)
                        {
                            map.Set(py, px, s);
                        }
                    }
                }
            }
        }

        double maximum = scores.DefaultIfEmpty(0).Max();
        return AnomalyMap.Result(
            map,
            threshold,
            Border,
            options.MinimumArea,
            maximum,
            $"CNN特征（{_features.Source}）：已评分{scores.Count(v => v > 0)}个网格单元（每单元约{cell:0.#}像素），"
                + $"{(model.Radius > 0 ? $"位置相关±{model.Radius}像素" : $"记忆库{model.Count}块")}/{model.TrainingImages}张良品；"
                + $"最大得分{maximum:F3}，阈值{threshold:F3}（{(options.Threshold == null ? model.Calibration : "显式设置")}）。"
                + $"裁图边缘{Border}像素内只作上下文，不单独报异常。"
        );
    }

    /// <summary>释放骨干网络。</summary>
    public void Dispose()
    {
        _features.Dispose();
    }

    /// <summary>从模型保存的灰度裁图重算良品特征网格（每个模型只算一次，见缓存）。</summary>
    private List<CellGrid> References(PatchAnomalyModel model)
    {
        int per = model.Width * model.Height;
        var memory = model.CopyMemory();
        var grids = new List<CellGrid>();
        for (int i = 0; i < model.TrainingImages; i++)
        {
            using var gray = new Mat(model.Height, model.Width, MatType.CV_32F);
            System.Runtime.InteropServices.Marshal.Copy(memory, i * per, gray.Data, per);
            using var bytes = new Mat();
            gray.ConvertTo(bytes, MatType.CV_8U);
            grids.Add(Grid(bytes));
        }

        return grids;
    }

    /// <summary>裁图灰度逐像素转为浮点，供位置相关模型保存。</summary>
    private static float[] Pixels(IImageSource image)
    {
        using var gray = CvPixels.Gray(image);
        var bytes = new byte[gray.Rows * gray.Cols];
        for (int y = 0; y < gray.Rows; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(gray.Ptr(y), bytes, y * gray.Cols, gray.Cols);
        }

        return bytes.Select(v => (float)v).ToArray();
    }

    private CellGrid Grid(IImageSource image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var gray = CvPixels.Gray(image);
        return Grid(gray);
    }

    private CellGrid Grid(Mat gray)
    {
        var values = _features.Extract(gray, out int w, out int h, out int d, out bool[] blank);
        return new CellGrid(values, blank, w, h, d);
    }

    private CellGrid Augmented(IImageSource image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var gray = CvPixels.Gray(image);
        using var shifted = new Mat();
        using var shift = new Mat(2, 3, MatType.CV_64F, Scalar.All(0));
        shift.Set(0, 0, 1.0);
        shift.Set(0, 2, 1.0);
        shift.Set(1, 1, 1.0);
        shift.Set(1, 2, 1.0);
        Cv2.WarpAffine(gray, shifted, shift, gray.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
        Cv2.GaussianBlur(shifted, shifted, new Size(0, 0), .5);
        return Grid(shifted);
    }

    private static PatchAnomalyResult Blocked(string code, string message, double threshold)
    {
        return new PatchAnomalyResult(
            EAlgorithmStatus.UnsupportedInput,
            0,
            threshold,
            new[] { new QualityFinding(code, message, EQualityFindingKind.Blocker) },
            null
        );
    }

    /// <summary>位置相关：每单元到任一良品同位置±cells单元内的最近L2距离（纸白单元也评分）。</summary>
    private static float[] Local(CellGrid query, IReadOnlyList<CellGrid> references, int cells)
    {
        int d = query.Dimensions;
        var scores = new float[query.Width * query.Height];
        for (int y = 0; y < query.Height; y++)
        {
            for (int x = 0; x < query.Width; x++)
            {
                int q = (y * query.Width + x) * d;
                float best = float.MaxValue;
                foreach (var r in references)
                {
                    for (int ry = Math.Max(0, y - cells); ry <= Math.Min(r.Height - 1, y + cells); ry++)
                    {
                        for (int rx = Math.Max(0, x - cells); rx <= Math.Min(r.Width - 1, x + cells); rx++)
                        {
                            int o = (ry * r.Width + rx) * d;
                            float sum = 0;
                            for (int k = 0; k < d && sum < best; k++)
                            {
                                float t = query.Values[q + k] - r.Values[o + k];
                                sum += t * t;
                            }

                            if (sum < best)
                            {
                                best = sum;
                            }
                        }
                    }
                }

                scores[y * query.Width + x] = best == float.MaxValue ? 0 : (float)Math.Sqrt(best);
            }
        }

        return scores;
    }

    /// <summary>与位置无关：非纸白单元组成记忆库并按核心集压缩；全为纸白时用单个零向量。</summary>
    private static Mat Bank(IReadOnlyList<CellGrid> grids, int capacity, int seed, CancellationToken token)
    {
        int d = grids[0].Dimensions;
        var rows = new List<float>();
        foreach (var g in grids)
        {
            for (int i = 0; i < g.Blank.Length; i++)
            {
                if (!g.Blank[i])
                {
                    rows.AddRange(new ArraySegment<float>(g.Values, i * d, d));
                }
            }
        }

        if (rows.Count == 0)
        {
            return new Mat(1, d, MatType.CV_32F, Scalar.All(0));
        }

        int n = rows.Count / d;
        using var all = new Mat(n, d, MatType.CV_32F);
        System.Runtime.InteropServices.Marshal.Copy(rows.ToArray(), 0, all.Data, rows.Count);
        var keep = Coreset.Select(all, capacity, seed, token);
        var result = new Mat(keep.Length, d, MatType.CV_32F);
        for (int i = 0; i < keep.Length; i++)
        {
            all.Row(keep[i]).CopyTo(result.Row(i));
        }

        return result;
    }

    /// <summary>非纸白单元到记忆库最近块的L2距离；纸白单元得分0。</summary>
    private static float[] Nearest(CellGrid query, Mat bank)
    {
        int d = query.Dimensions;
        var index = Enumerable.Range(0, query.Blank.Length).Where(i => !query.Blank[i]).ToArray();
        var scores = new float[query.Blank.Length];
        if (index.Length == 0)
        {
            return scores;
        }

        var values = new float[index.Length * d];
        for (int i = 0; i < index.Length; i++)
        {
            Array.Copy(query.Values, index[i] * d, values, i * d, d);
        }

        using var q = new Mat(index.Length, d, MatType.CV_32F);
        System.Runtime.InteropServices.Marshal.Copy(values, 0, q.Data, values.Length);
        using var matcher = new BFMatcher(NormTypes.L2, false);
        foreach (var m in matcher.Match(q, bank))
        {
            scores[index[m.QueryIdx]] = m.Distance;
        }

        return scores;
    }
}
