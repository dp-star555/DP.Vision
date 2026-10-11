using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OpenCv;

/// <summary>通过空白和连通域归属进行测量；数量与字符数不一致时按字符数与字宽先验求最优切割（粘连处在最薄处切开、断字整体保留），不按CTC峰值切字；只支持水平单行输入。</summary>
public sealed class OpenCvCharacterSegmenter : ICharacterSegmenter, IGlyphCandidateSegmenter
{
    /// <summary>不为凑齐OCR数量强行分割粘连墨迹；图块保留尚不能归属的内部噪点。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">单行文字原图范围；此处PixelRect是中立PixelBounds的别名。</param>
    /// <param name = "text">真实身份提示，不强制匹配物理数量。</param>
    /// <param name = "token">协作式取消标记。</param>
    public CharacterSegmentation Segment(
        IImageSource frame,
        PixelRect bounds,
        string text,
        CancellationToken token = default
    )
    {
        return SegmentCore(frame, bounds, text, token, false);
    }

    /// <inheritdoc/>
    public CharacterSegmentation SegmentCandidates(
        IImageSource frame,
        PixelRect bounds,
        string text,
        CancellationToken token = default
    )
    {
        return SegmentCore(frame, bounds, text, token, true);
    }

    private static CharacterSegmentation SegmentCore(
        IImageSource frame,
        PixelRect bounds,
        string text,
        CancellationToken token,
        bool candidates
    )
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        if (!bounds.Fits(frame))
        {
            throw new ArgumentException("Line outside image.");
        }

        CharacterSegmentation Stop(string reason, int count = 0, string status = "uncertain")
        {
            return new CharacterSegmentation(status, reason, "none", count, Array.Empty<CharacterPatch>());
        }

        if (!CharacterIdentity.TryTokenizeLine(text, out var tokens))
        {
            return Stop(
                "Only printable Unicode single lines up to 128 characters are supported; spaces are not glyphs.",
                status: "unsupported"
            );
        }

        if (
            bounds.Width < 4
            || bounds.Height < 4
            || bounds.Width > 6000
            || bounds.Height > 512
            || bounds.Height > bounds.Width * 1.5
        )
        {
            return Stop("Invalid horizontal line geometry.");
        }

        token.ThrowIfCancellationRequested();
        using var raw = CvPixels.Mat(frame);
        using var chip = new Mat(raw, CvPixels.Rect(bounds));
        using var mask = CvPixels.Otsu(chip);
        if (Cv2.CountNonZero(mask) == 0)
        {
            return Stop("Insufficient line contrast or empty ink.");
        }

        if (chip.Channels() == 3)
        {
            int colored = 0;
            for (int y = 0; y < chip.Rows; y++)
            {
                for (int x = 0; x < chip.Cols; x++)
                {
                    var p = chip.At<Vec3b>(y, x);
                    if (
                        Math.Max(p.Item0, Math.Max(p.Item1, p.Item2))
                            - Math.Min(p.Item0, Math.Min(p.Item1, p.Item2))
                        > 35
                    )
                    {
                        colored++;
                    }
                }
            }

            if (colored / (double)(chip.Rows * chip.Cols) > .002)
            {
                return Stop("Colored annotation/occlusion in line.");
            }
        }

        using var labels = new Mat();
        using var stats = new Mat();
        using var centers = new Mat();
        int components = Cv2.ConnectedComponentsWithStats(
            mask,
            labels,
            stats,
            centers,
            PixelConnectivity.Connectivity8,
            MatType.CV_32S
        );
        int minimum = Math.Max(2, (int)(bounds.Height * .04));
        if (tokens.Any(c => char.IsPunctuation(c, 0) || char.IsSymbol(c, 0)))
        {
            // 点号/冒号等可能仅占几像素，不能用字母数字的行高噪点门槛删掉；
            // 数量、边界、颜色、切线和完成性检查仍然保留，不会凭身份补造墨迹。
            minimum = 2;
        }

        var keep = new bool[components];
        for (int i = 1; i < components; i++)
        {
            keep[i] = stats.At<int>(i, 4) >= minimum;
        }

        if (keep.Count(v => v) > Math.Max(64, 4 * tokens.Length))
        {
            return Stop("Too many components; noise not forced into characters.");
        }

        var occupied = new bool[bounds.Width];
        bool clipped = false;
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                if (keep[labels.At<int>(y, x)])
                {
                    occupied[x] = true;
                    if (x == 0 || y == 0 || x == bounds.Width - 1 || y == bounds.Height - 1)
                    {
                        clipped = true;
                    }
                }
            }
        }

        var runs = new List<Tuple<int, int>>();
        for (int x = 0; x < occupied.Length; )
        {
            if (!occupied[x])
            {
                x++;
                continue;
            }

            int left = x;
            while (x < occupied.Length && occupied[x])
            {
                x++;
            }

            runs.Add(Tuple.Create(left, x));
        }

        var groups = Enumerable
            .Range(1, components - 1)
            .Where(i => keep[i])
            .Select(i => new List<int> { i })
            .ToList();
        Rect Box(List<int> ids)
        {
            int x = ids.Min(i => stats.At<int>(i, 0)),
                y = ids.Min(i => stats.At<int>(i, 1));
            return new Rect(
                x,
                y,
                ids.Max(i => stats.At<int>(i, 0) + stats.At<int>(i, 2)) - x,
                ids.Max(i => stats.At<int>(i, 1) + stats.At<int>(i, 3)) - y
            );
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < groups.Count && !changed; i++)
            {
                for (int j = i + 1; j < groups.Count; j++)
                {
                    var a = Box(groups[i]);
                    var b = Box(groups[j]);
                    if (
                        (a.Bottom <= b.Top || b.Bottom <= a.Top)
                        && Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)
                            >= .6 * Math.Min(a.Width, b.Width)
                    )
                    {
                        groups[i].AddRange(groups[j]);
                        groups.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            }
        }

        groups = groups.OrderBy(g => Box(g).X + Box(g).Width / 2.0).ToList();
        bool useComponents = runs.Count != tokens.Length && groups.Count == tokens.Length;
        int count = useComponents ? groups.Count : runs.Count;
        int measuredCount = count;
        bool reviewedSplit = false;
        if (clipped || runs.Count == 0)
        {
            return Stop("墨迹触及ROI边界或没有有效字形，请扩大ROI以包含完整上下笔画。", count);
        }

        if (
            candidates
            && count != tokens.Length
            && tokens.All(c => c.Length == 1 && CharacterIdentity.IsAlphanumeric(c[0]))
            && runs.Count < tokens.Length
            && groups.Count < tokens.Length
        )
        {
            var split = ThinBridgeCandidates.Split(mask, runs, tokens.Length, token);
            if (split != null)
            {
                runs = split;
                count = runs.Count;
                useComponents = false;
                reviewedSplit = true;
            }
        }

        CountGuidedCuts.Result? guided = null;
        int projectionGroups = runs.Count;
        if (count != tokens.Length)
        {
            // 数量对不上（粘连、断字、标点、噪点）时，按已知字符数求整行最优切割；
            // 只接受切线不穿过半个字高以上墨迹、各字宽度未严重偏离的结果。
            using var inkMask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.All(0));
            for (int y = 0; y < bounds.Height; y++)
            {
                for (int x = 0; x < bounds.Width; x++)
                {
                    if (keep[labels.At<int>(y, x)])
                    {
                        inkMask.Set(y, x, (byte)255);
                    }
                }
            }

            guided = CountGuidedCuts.Cut(inkMask, tokens, token);
            if (
                guided == null
                || guided.WorstCrossing > MaximumGuidedCrossing
                || guided.WorstWidth > MaximumGuidedWidth
            )
            {
                return Stop(
                    $"投影分组 {runs.Count}，连通域分组 {groups.Count}，文字字符 {tokens.Length}；"
                        + (
                            guided == null
                                ? "按字符数求最优切割也没有得到每字都有墨迹的结果。"
                                : $"按字符数求最优切割的结果不可靠（最大穿墨{guided.WorstCrossing:P0}字高、字宽最大偏离{guided.WorstWidth:P0}）。"
                        )
                        + "可缩小ROI逐字裁取；不会强行等分凑数。",
                    count
                );
            }

            // 转为各字墨迹范围：相邻字共用的切线仍是左右边界，空白处取空白中点。
            runs = guided
                .Pieces.Select(
                    (p, i) =>
                    {
                        int a = p.Item1,
                            b = p.Item2;
                        while (a < b && !ColumnHasInk(inkMask, a))
                        {
                            a++;
                        }

                        while (b > a && !ColumnHasInk(inkMask, b - 1))
                        {
                            b--;
                        }

                        return Tuple.Create(a, b);
                    }
                )
                .ToList();
            count = runs.Count;
            useComponents = false;
        }

        using var patches = new OwnedPatches();
        string basis =
            guided != null ? "count_guided_cuts"
            : reviewedSplit ? "thin_bridge_vertical_candidates"
            : useComponents ? "connected_component_ownership"
            : "vertical_white_gaps";
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        // 整行像素、掩码和连通域标签只取一次；连通域归属用“标签→字符组”查找表，背景中值也只算一次，
        // 每个字符只做一次数组遍历（原先逐像素查集合并对每个字符重排整行背景，长行耗时随字数平方增长）。
        int width = bounds.Width,
            pixels = bounds.Width * bounds.Height,
            channels = chip.Channels();
        var chipData = Bytes(chip);
        var maskData = Bytes(mask);
        var owner = new int[components];
        int[] labelData = Array.Empty<int>();
        var background = new byte[channels];
        if (useComponents)
        {
            for (int id = 0; id < components; id++)
            {
                owner[id] = -1;
            }

            for (int g = 0; g < groups.Count; g++)
            {
                foreach (int id in groups[g])
                {
                    owner[id] = g;
                }
            }

            labels.GetArray(out labelData);
            for (int c = 0; c < channels; c++)
            {
                background[c] = MedianBackground(chipData, maskData, channels, c);
            }
        }

        for (int i = 0; i < tokens.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            int x0,
                x1;
            var cleanedData = (byte[])chipData.Clone();
            int removed = 0;
            var foreign = new byte[pixels];
            if (useComponents)
            {
                var box = Box(groups[i]);
                x0 = Math.Max(0, box.Left - 1);
                x1 = Math.Min(bounds.Width, box.Right + 1);
                var own = new byte[pixels];
                for (int p = 0; p < pixels; p++)
                {
                    int g = owner[labelData[p]];
                    if (g == i)
                    {
                        own[p] = 255;
                    }
                    else if (g >= 0)
                    {
                        foreign[p] = 255;
                    }
                }

                var halo = Dilate(foreign, bounds.Height, width, kernel);
                var ownHalo = Dilate(own, bounds.Height, width, kernel);
                for (int p = 0; p < pixels; p++)
                {
                    if (foreign[p] != 0 || (halo[p] != 0 && ownHalo[p] == 0 && labelData[p] == 0))
                    {
                        Buffer.BlockCopy(background, 0, cleanedData, p * channels, channels);
                    }
                }
            }
            else
            {
                int left = i == 0 ? 0 : (runs[i - 1].Item2 + runs[i].Item1) / 2;
                int right = i == runs.Count - 1 ? bounds.Width : (runs[i].Item2 + runs[i + 1].Item1) / 2;
                x0 = Math.Max(left, runs[i].Item1 - 1);
                x1 = Math.Min(right, runs[i].Item2 + 1);
            }

            int top = bounds.Height,
                bottom = 0;
            for (int y = 0; y < bounds.Height; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (maskData[y * width + x] != 0 && foreign[y * width + x] == 0)
                    {
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y + 1);
                    }
                }
            }

            if (top >= bottom)
            {
                return Stop("Empty ownership group.", count);
            }

            int y0 = Math.Max(0, top - 2),
                y1 = Math.Min(bounds.Height, bottom + 2);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (foreign[y * width + x] != 0)
                    {
                        removed++;
                    }
                }
            }

            using var cleaned = new Mat(bounds.Height, width, chip.Type());
            Marshal.Copy(cleanedData, 0, cleaned.Data, cleanedData.Length);
            using var patch = new Mat(cleaned, new Rect(x0, y0, x1 - x0, y1 - y0));
            patches.Add(
                new CharacterPatch(
                    tokens[i],
                    i,
                    new PixelRect(bounds.X + x0, bounds.Y + y0, x1 - x0, y1 - y0),
                    CvPixels.Buffer(patch),
                    removed
                )
            );
        }

        if (guided != null)
        {
            string detail =
                $"原图投影{projectionGroups}组/连通域{groups.Count}组与{tokens.Length}个字符不一致，已按字符数与字宽先验求最优切割："
                + $"{guided.BridgedCuts}处切过粘连墨迹（最多{guided.WorstCrossing:P0}字高），字宽最大偏离{guided.WorstWidth:P0}。"
                + "断开的字按整体保留；切过粘连处的边缘差异属于切割而非印刷缺陷。";
            return new CharacterSegmentation(
                candidates ? "review_required" : "provisional",
                candidates ? detail + "制作参考时须逐字复核。" : detail + "OCR身份不是业务真值。",
                basis,
                measuredCount,
                patches.Detach()
            );
        }

        return reviewedSplit
            ? new CharacterSegmentation(
                "review_required",
                $"原图仅有{measuredCount}组；根据宽连通字形中的薄墨迹间隙提出{count}字切割候选。未擦除原图墨迹；必须逐个检查粘连处是否切伤笔画，不代表良品。",
                basis,
                measuredCount,
                patches.Detach()
            )
            : new CharacterSegmentation(
                "provisional",
                "Count agreement is provisional; OCR identity is not business truth.",
                basis,
                count,
                patches.Detach()
            );
    }

    /// <summary>按字符数最优切割时，单条切线允许穿过的最大墨迹比例（相对字高）。</summary>
    private const double MaximumGuidedCrossing = .5;

    /// <summary>按字符数最优切割时，单字墨迹宽度允许偏离预期宽度的最大比例。</summary>
    private const double MaximumGuidedWidth = 1.2;

    /// <summary>复制为紧密排列的字节数组（ROI子图先克隆为连续内存）。</summary>
    private static byte[] Bytes(Mat image)
    {
        using var copy = image.IsContinuous() ? null : image.Clone();
        var source = copy ?? image;
        var bytes = new byte[source.Rows * source.Cols * source.ElemSize()];
        Marshal.Copy(source.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>3×3膨胀单通道掩码。</summary>
    private static byte[] Dilate(byte[] data, int rows, int cols, Mat kernel)
    {
        using var source = new Mat(rows, cols, MatType.CV_8UC1);
        Marshal.Copy(data, 0, source.Data, data.Length);
        using var result = new Mat();
        Cv2.Dilate(source, result, kernel);
        return Bytes(result);
    }

    /// <summary>非墨迹像素在指定通道上的中值（偶数个取中间两值的平均），按直方图求顺序统计量；没有背景时为白色。</summary>
    private static byte MedianBackground(byte[] pixels, byte[] mask, int channels, int channel)
    {
        var histogram = new int[256];
        int count = 0;
        for (int p = 0; p < mask.Length; p++)
        {
            if (mask[p] == 0)
            {
                histogram[pixels[p * channels + channel]]++;
                count++;
            }
        }

        if (count == 0)
        {
            return 255;
        }

        int Nth(int index)
        {
            for (int v = 0, seen = 0; v < 256; v++)
            {
                seen += histogram[v];
                if (seen > index)
                {
                    return v;
                }
            }

            return 255;
        }

        return (byte)((Nth((count - 1) / 2) + Nth(count / 2)) / 2);
    }

    private static bool ColumnHasInk(Mat mask, int x)
    {
        for (int y = 0; y < mask.Rows; y++)
        {
            if (mask.At<byte>(y, x) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>只切割明确声明的等格单元，不用于解决有歧义的物理分割。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">显式等格原图范围；此处PixelRect是中立PixelBounds的别名。</param>
    /// <param name = "expected">调用方明确确认的等格标签序列。</param>
    public CharacterSegmentation EqualCells(IImageSource frame, PixelRect bounds, string expected)
    {
        if (
            !CharacterIdentity.TryTokenizeLine(expected, out var identities)
            || string.Concat(identities) != expected
        )
        {
            throw new ArgumentException("Invalid explicit identities.");
        }

        if (!bounds.Fits(frame) || bounds.Width < identities.Length)
        {
            throw new ArgumentException("Invalid equal cells.");
        }

        using var raw = CvPixels.Mat(frame);
        using var chars = new OwnedPatches();
        for (int i = 0; i < identities.Length; i++)
        {
            int left = bounds.X + i * bounds.Width / identities.Length,
                right = bounds.X + (i + 1) * bounds.Width / identities.Length;
            var box = new PixelRect(left, bounds.Y, right - left, bounds.Height);
            using var crop = new Mat(raw, CvPixels.Rect(box));
            chars.Add(new CharacterPatch(identities[i], i, box, CvPixels.Buffer(crop)));
        }

        return new CharacterSegmentation(
            "explicit_cells",
            "Caller asserted equal cells and expected identities.",
            "user_equal_cells",
            chars.Count,
            chars.Detach()
        );
    }
}
