using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>
/// 逐字符局部块异常检测的OpenCV实现：字符按整行几何归一化（大写高度缩放到32像素，单元外涂纸色），
/// 在单元内按位置相关模式与同键良品比较；手工特征模型另带缺墨检查（按来源图留一标定阈值）。
/// 局部块模型的训练由构造时提供的实现完成，检测使用各参考自带的实现。
/// </summary>
public sealed partial class OpenCvCharacterAnomalyDetector : ICharacterAnomalyDetector
{
    private readonly IPatchAnomalyDetector? _legacyTrainer;
    private readonly IAnomalyImplementation? _implementation;

    /// <summary>使用厂商中立实现训练字符，归一化与模型推理分别负责。</summary>
    /// <param name="implementation">明确实现；必须具有训练能力。</param>
    public OpenCvCharacterAnomalyDetector(IAnomalyImplementation implementation)
    { _implementation = implementation ?? throw new ArgumentNullException(nameof(implementation)); }

    /// <summary>使用手工特征实现训练。</summary>
    public OpenCvCharacterAnomalyDetector()
        : this(new OpenCvPatchAnomalyDetector()) { }

    /// <summary>使用宿主提供的训练实现（例如CNN骨干网络特征），宿主拥有。</summary>
    /// <param name = "trainer">局部块异常检测实现。</param>
    public OpenCvCharacterAnomalyDetector(IPatchAnomalyDetector trainer)
    {
        _legacyTrainer = trainer ?? throw new ArgumentNullException(nameof(trainer));
    }

    /// <summary>归一化单元高度（像素）；模型单元高度与此不同时不检测。</summary>
    public static int CellHeight => CharacterCells.CellHeight;

    /// <inheritdoc/>
    public IReadOnlyList<CharacterAnomalyTraining> Train(
        IReadOnlyList<CharacterAnomalyLine> lines,
        CharacterAnomalyOptions options,
        CancellationToken token = default
    )
    {
        if (lines == null || lines.Count == 0)
        {
            throw new ArgumentException("Good lines are required.", nameof(lines));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var grays = new Dictionary<IImageSource, Mat>(ReferenceComparer.Instance);
        try
        {
            var samples = Collect(lines, grays, token);
            if (samples.Count == 0)
            {
                throw new ArgumentException("No measurable characters with a model key in the good lines.");
            }

            var sources = grays.Values.ToList();
            var trained = new List<CharacterAnomalyTraining>();
            foreach (var group in samples.GroupBy(s => s.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var all = group.ToArray();
                if (all.Select(s => s.Line.Normalization).Distinct().Count() != 1)
                    throw new ArgumentException(
                        "同一字符模型不能混用ROI行几何和历史墨迹行几何，请统一提供制作行ROI。"
                    );
                int width = all.Max(s => CharacterCells.Width(s.Cell.Width, s.Line));
                var chosen =
                    all.Length <= options.MaximumSamples ? all : Diverse(all, width, options.MaximumSamples);
                var crops = new List<IImageSource>();
                try
                {
                    foreach (var s in chosen)
                    {
                        using var cell = CharacterCells.Normalize(s.Gray, s.Line, s.Cell, width);
                        crops.Add(CvPixels.Buffer(cell.Image));
                    }

                    // 来源：同一原图对象的灰度图相同，即同一来源。缺墨阈值总按来源图留一标定。
                    var origins = chosen
                        .Select(c => sources.FindIndex(g => ReferenceEquals(g, c.Gray)))
                        .ToList();
                    var model = _legacyTrainer == null ? null :
                        options.SourceLeaveOneOut && _legacyTrainer is IGroupedPatchAnomalyTrainer grouped
                            ? grouped.Train(crops, origins, options.Patch, token)
                            : _legacyTrainer.Train(crops, options.Patch, token);
                    var asset = model != null ? PatchAnomalyImplementation.Capture(model, width, CharacterCells.CellHeight)
                        : (_implementation as IAnomalyTrainer ?? throw new NotSupportedException("所选异常实现只有推理能力，请导入模型。"))
                            .Train(crops, origins, new AnomalyTrainingOptions(options.Patch.ThresholdMargin), token);
                    double? ink = model != null && CharacterInkLoss.Supports(model)
                        ? CharacterInkLoss.Calibrate(
                            CharacterInkLoss.Planes(model),
                            origins,
                            model.Width,
                            model.Height,
                            options.Patch.ThresholdMargin
                        )
                        : null;
                    trained.Add(
                        new CharacterAnomalyTraining(
                            group.Key,
                            asset,
                            options.Patch,
                            width,
                            CharacterCells.CellHeight,
                            chosen.Length,
                            ink,
                            asset.Calibration
                                + (
                                    ink is double t
                                        ? $"；缺墨阈值{t:F3}（按来源图留一）"
                                        : "；缺墨检查未标定（良品来源图少于2张）"
                                ),
                            all[0].Line.Normalization
                        )
                    );
                }
                finally
                {
                    foreach (var c in crops)
                    {
                        c.Dispose();
                    }
                }
            }

            return trained.AsReadOnly();
        }
        finally
        {
            foreach (var g in grays.Values)
            {
                g.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<NormalizedCharacterCell> NormalizeCells(
        IReadOnlyList<CharacterAnomalyLine> training,
        IReadOnlyList<CharacterAnomalyLine> others,
        CancellationToken token = default
    )
    {
        if (training == null || others == null)
        {
            throw new ArgumentNullException(training == null ? nameof(training) : nameof(others));
        }

        var grays = new Dictionary<IImageSource, Mat>(ReferenceComparer.Instance);
        var cells = new List<NormalizedCharacterCell>();
        try
        {
            var all = Collect(training.Concat(others).ToArray(), grays, token);
            var widths = all.Where(s => s.SampleIndex < training.Count)
                .GroupBy(s => s.Key)
                .ToDictionary(g => g.Key, g => g.Max(s => CharacterCells.Width(s.Cell.Width, s.Line)));
            var modes = all.Where(s => s.SampleIndex < training.Count)
                .GroupBy(s => s.Key)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Line.Normalization).Distinct().Single());
            foreach (var s in all)
            {
                token.ThrowIfCancellationRequested();
                if (widths.TryGetValue(s.Key, out int width))
                {
                    if (s.Line.Normalization != modes[s.Key])
                        throw new ArgumentException(
                            "训练与对照字符必须采用同一行归一化方式，请一致提供制作行ROI。"
                        );
                    using var cell = CharacterCells.Normalize(s.Gray, s.Line, s.Cell, width);
                    cells.Add(
                        new NormalizedCharacterCell(
                            s.SampleIndex,
                            s.Index,
                            s.Key,
                            s.SampleIndex < training.Count,
                            CvPixels.Buffer(cell.Image)
                        )
                    );
                }
            }

            return cells.AsReadOnly();
        }
        catch
        {
            foreach (var c in cells)
            {
                c.Dispose();
            }

            throw;
        }
        finally
        {
            foreach (var g in grays.Values)
            {
                g.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public CharacterAnomalyInspection Inspect(
        IImageSource image,
        IReadOnlyList<CharacterAnomalyCharacter> characters,
        PixelBounds crop,
        Func<string, CharacterAnomalyReference?> references,
        bool inkLoss = true,
        CancellationToken token = default
    )
    {
        if (image == null || characters == null || references == null)
        {
            throw new ArgumentNullException(image == null ? nameof(image) : nameof(characters));
        }

        using var gray = CvPixels.Gray(image);
        var line = CharacterCells.Measure(gray, characters);
        var roiLine = CharacterCells.MeasureRegion(gray, crop);
        var region = CvPixels.Rect(crop) & new Rect(0, 0, gray.Cols, gray.Rows);
        var work = characters
            .Where(c => c.Key != null)
            .Select(c => (character: c, reference: references(c.Key!)))
            .ToList();
        var outcomes = new (CharacterAnomalyOutcome Outcome, Mat? Heat)[work.Count];
        void Run(int i)
        {
            outcomes[i] = One(
                gray,
                work[i].reference?.Normalization == ECharacterNormalization.LineRegion ? roiLine : line,
                region,
                work[i].character,
                work[i].reference,
                inkLoss,
                token
            );
        }

        try
        {
            if (
                work.Count > 1
                && work.All(w => w.reference == null || w.reference.Runtime is PatchAnomalyRuntime)
            )
            {
                // 手工特征实现线程安全，各字符相互独立。
                Parallel.For(0, work.Count, new ParallelOptions { CancellationToken = token }, Run);
            }
            else
            {
                for (int i = 0; i < work.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Run(i);
                }
            }

            using var heat = new Mat(
                Math.Max(1, region.Height),
                Math.Max(1, region.Width),
                MatType.CV_8UC1,
                Scalar.All(0)
            );
            bool anyHeat = false;
            foreach (var (_, mapped) in outcomes)
            {
                if (mapped != null)
                {
                    Cv2.Max(heat, mapped, heat);
                    anyHeat = true;
                }
            }

            return new CharacterAnomalyInspection(
                new PixelBounds(region.X, region.Y, region.Width, region.Height),
                outcomes.Select(o => o.Outcome),
                anyHeat ? CvPixels.Buffer(heat) : null
            );
        }
        finally
        {
            foreach (var (_, mapped) in outcomes)
            {
                mapped?.Dispose();
            }
        }
    }

    /// <summary>检测一个字符；返回结果与映射回热力图范围坐标的热力图（没有时为null，调用方释放）。只读访问共享的灰度图。</summary>
    private static (CharacterAnomalyOutcome Outcome, Mat? Heat) One(
        Mat gray,
        CharacterLine? line,
        Rect region,
        CharacterAnomalyCharacter c,
        CharacterAnomalyReference? reference,
        bool inkLoss,
        CancellationToken token
    )
    {
        (CharacterAnomalyOutcome, Mat?) Stop(ECharacterAnomalyStatus status, string code, string message)
        {
            return (
                new CharacterAnomalyOutcome(
                    c,
                    status,
                    0,
                    0,
                    new[] { new QualityFinding(code, message, EQualityFindingKind.Defect, c.Bounds) }
                ),
                null
            );
        }

        if (reference == null)
        {
            return Stop(
                ECharacterAnomalyStatus.MissingModel,
                "anomaly_character_model_missing",
                "异常模型库中没有该字符的模型，无法判断；请用含该字符的良品补充训练。"
            );
        }

        if (line == null)
        {
            return Stop(
                ECharacterAnomalyStatus.Unmeasurable,
                "anomaly_line_unmeasurable",
                "无法测量该行的字高与基线。"
            );
        }

        if (reference.CellHeight != CharacterCells.CellHeight)
        {
            return Stop(
                ECharacterAnomalyStatus.Blocked,
                "patch_anomaly_model_mismatch",
                "字符模型的归一化尺寸与当前实现不一致，需重新训练。"
            );
        }

        using var cell = CharacterCells.Normalize(gray, line, c.Bounds, reference.CellWidth);
        using var source = CvPixels.Buffer(cell.Image);
        using var result = reference.Runtime.Inspect(source, reference.Detection, token);
        var findings = result
            .Findings.Select(f =>
                f.Bounds is { } b
                    ? new QualityFinding(
                        f.Code,
                        f.Message,
                        f.Kind,
                        cell.ToImage(b.X, b.Y, b.Width, b.Height),
                        f.AreaPixels is { } area ? cell.ToImageArea(area) : (int?)null
                    )
                    : f
            )
            .ToList();
        bool completed = result.Status == EAlgorithmStatus.Completed;
        using var local = result.HeatMap != null ? CvPixels.Mat(result.HeatMap) : new Mat();
        double? inkScore = null,
            inkThreshold = null;
        var legacy = (reference.Runtime as PatchAnomalyRuntime)?.Model;
        var ink =
            inkLoss
            && completed
            && reference.InkThreshold != null
            && legacy != null && CharacterInkLoss.Supports(legacy)
                ? CharacterInkLoss.For(legacy)
                : null;
        if (ink != null && reference.InkThreshold is double threshold)
        {
            var (score, excess) = ink.Measure(CharacterInkLoss.Ink(cell.Image));
            using (excess)
            {
                inkScore = score;
                inkThreshold = threshold;
                findings.AddRange(InkFindings(excess, threshold, reference.Detection.MinimumArea, cell));
                findings.Add(
                    new QualityFinding(
                        "ink_loss_scope",
                        $"缺墨检查：笔画内墨量最多比良品最低值低{score:F3}（阈值{threshold:F3}，{score / threshold:F2}倍），"
                            + $"参考{ink.Samples}个良品样本。",
                        EQualityFindingKind.Information
                    )
                );
                if (!local.Empty())
                {
                    // 缺墨也画入热力图（128对应阈值），与局部块比较取较大者。
                    using var inkHeat = new Mat();
                    excess.ConvertTo(inkHeat, MatType.CV_8U, 128.0 / threshold);
                    Cv2.Max(local, inkHeat, local);
                }
            }
        }

        var outcome = new CharacterAnomalyOutcome(
            c,
            completed ? ECharacterAnomalyStatus.Compared : ECharacterAnomalyStatus.Blocked,
            result.MaximumScore,
            result.Threshold,
            findings,
            inkScore,
            inkThreshold
        );
        if (local.Empty() || region.Width <= 0 || region.Height <= 0)
        {
            return (outcome, null);
        }

        using var back = new Mat(2, 3, MatType.CV_64FC1);
        back.Set(0, 0, 1 / cell.Scale);
        back.Set(0, 1, 0.0);
        back.Set(0, 2, cell.X0 - region.X);
        back.Set(1, 0, 0.0);
        back.Set(1, 1, 1 / cell.Scale);
        back.Set(1, 2, cell.Y0 - region.Y);
        var mapped = new Mat();
        Cv2.WarpAffine(
            local,
            mapped,
            back,
            new Size(region.Width, region.Height),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.All(0)
        );
        return (outcome, mapped);
    }

    /// <summary>缺墨图中超过阈值、面积不小于下限的连通区域（原图坐标）。</summary>
    private static IEnumerable<QualityFinding> InkFindings(
        Mat excess,
        double threshold,
        int minimumArea,
        CharacterCell cell
    )
    {
        using var mask = new Mat();
        Cv2.Threshold(excess, mask, threshold, 255, ThresholdTypes.Binary);
        mask.ConvertTo(mask, MatType.CV_8U);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(
            mask,
            labels,
            stats,
            centroids,
            PixelConnectivity.Connectivity8
        );
        var findings = new List<QualityFinding>();
        for (int k = 1; k < count; k++)
        {
            int area = stats.At<int>(k, (int)ConnectedComponentsTypes.Area);
            if (area < minimumArea)
            {
                continue;
            }

            int x = stats.At<int>(k, (int)ConnectedComponentsTypes.Left),
                y = stats.At<int>(k, (int)ConnectedComponentsTypes.Top),
                w = stats.At<int>(k, (int)ConnectedComponentsTypes.Width),
                h = stats.At<int>(k, (int)ConnectedComponentsTypes.Height);
            using var part = new Mat(excess, new Rect(x, y, w, h));
            Cv2.MinMaxLoc(part, out _, out double peak);
            findings.Add(
                new QualityFinding(
                    "ink_loss",
                    $"缺墨：最多比良品最低墨量低{peak:F3}（阈值{threshold:F3}，{peak / threshold:F2}倍），面积{area}像素²；笔画内比所有良品同位置都浅（斑驳、褪色或断笔）。",
                    EQualityFindingKind.Defect,
                    cell.ToImage(x, y, w, h),
                    cell.ToImageArea(area)
                )
            );
        }

        return findings;
    }

    /// <summary>收集各行中带模型键且行几何可测量的字符（灰度图按原图对象缓存到<paramref name = "grays"/>，由调用方释放）。</summary>
    private static List<Sample> Collect(
        IReadOnlyList<CharacterAnomalyLine> lines,
        Dictionary<IImageSource, Mat> grays,
        CancellationToken token
    )
    {
        var samples = new List<Sample>();
        for (int n = 0; n < lines.Count; n++)
        {
            var sample = lines[n];
            token.ThrowIfCancellationRequested();
            if (!grays.TryGetValue(sample.Image, out var gray))
            {
                gray = grays[sample.Image] = CvPixels.Gray(sample.Image);
            }

            var line = sample.Bounds is PixelBounds bounds
                ? CharacterCells.MeasureRegion(gray, bounds)
                : CharacterCells.Measure(gray, sample.Characters);
            if (
                line == null
                && sample.Characters.Any(c =>
                    c.Key != null
                    && CharacterIdentity.IsGlyph(c.Character)
                    && !(c.Character.Length == 1 && CharacterCells.IsAlphanumeric(c.Character[0]))
                )
            )
                throw new ArgumentException(
                    "纯标点/符号行或不可测量Unicode行需要显式稳定单行ROI，不能按单字墨迹缩放。"
                );
            if (line == null)
            {
                continue;
            }

            for (int i = 0; i < sample.Characters.Count; i++)
            {
                var c = sample.Characters[i];
                if (c.Key != null)
                {
                    samples.Add(new Sample(n, i, c.Key, gray, line, c.Bounds));
                }
            }
        }

        return samples;
    }

    /// <summary>
    /// 样本多于上限时按形态多样性选取（贪心最远点）：先取最接近其余样本的一个，再依次取与已选样本最不相似的。
    /// 按顺序等间隔抽取会漏掉少数形态不同的良品（例如另一行字号略不同的同一字符），检测时这些良品会被误报。
    /// 超过400个样本时先等间隔抽到400个再选。
    /// </summary>
    private static Sample[] Diverse(Sample[] all, int width, int maximum)
    {
        var pool =
            all.Length <= 400
                ? all
                : Enumerable.Range(0, 400).Select(i => all[(int)((long)i * all.Length / 400)]).ToArray();
        var vectors = pool.Select(s =>
            {
                using var cell = CharacterCells.Normalize(s.Gray, s.Line, s.Cell, width);
                using var small = new Mat();
                // 缩到1/2比较整体形态，对细微噪声不敏感。
                Cv2.Resize(
                    cell.Image,
                    small,
                    new Size(width / 2, CharacterCells.CellHeight / 2),
                    0,
                    0,
                    InterpolationFlags.Area
                );
                var bytes = new byte[small.Rows * small.Cols];
                System.Runtime.InteropServices.Marshal.Copy(small.Data, bytes, 0, bytes.Length);
                return bytes.Select(b => (float)b).ToArray();
            })
            .ToArray();
        int n = vectors.Length;
        double Distance(int i, int j)
        {
            double sum = 0;
            var a = vectors[i];
            var b = vectors[j];
            for (int k = 0; k < a.Length; k++)
            {
                double t = a[k] - b[k];
                sum += t * t;
            }

            return sum;
        }

        int first = Enumerable
            .Range(0, n)
            .OrderBy(i => Enumerable.Range(0, n).Sum(j => j == i ? 0 : Math.Sqrt(Distance(i, j))))
            .First();
        var selected = new List<int> { first };
        var nearest = Enumerable.Range(0, n).Select(i => Distance(i, first)).ToArray();
        while (selected.Count < maximum)
        {
            int next = Enumerable.Range(0, n).OrderByDescending(i => nearest[i]).First();
            if (nearest[next] <= 0)
            {
                break;
            }

            selected.Add(next);
            for (int i = 0; i < n; i++)
            {
                nearest[i] = Math.Min(nearest[i], Distance(i, next));
            }
        }

        return selected.OrderBy(i => i).Select(i => pool[i]).ToArray();
    }
}
