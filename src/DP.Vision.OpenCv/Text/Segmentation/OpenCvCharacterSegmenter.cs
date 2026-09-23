using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OpenCv;

/// <summary>通过空白和连通域归属进行测量，不按CTC峰值切字；只支持水平单行输入。</summary>
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

        if (string.IsNullOrEmpty(text) || text.Length > 128 || text.Any(c => c < 32 || c > 126))
        {
            return Stop("Only printable ASCII lines up to 128 tokens are supported.", status: "unsupported");
        }

        var tokens = text.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (!tokens.Any(CharacterIdentity.IsAlphanumeric))
        {
            return Stop("No alphanumeric tokens.", status: "unsupported");
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
            && tokens.All(CharacterIdentity.IsAlphanumeric)
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

        if (count != tokens.Length)
        {
            return Stop(
                $"投影分组 {runs.Count}，连通域分组 {groups.Count}，文字字符 {tokens.Length}；未找到可靠的完整切割。可缩小ROI逐字裁取；不会强行等分凑数。",
                count
            );
        }

        using var patches = new OwnedPatches();
        string basis =
            reviewedSplit ? "thin_bridge_vertical_candidates"
            : useComponents ? "connected_component_ownership"
            : "vertical_white_gaps";
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        for (int i = 0; i < tokens.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!CharacterIdentity.IsAlphanumeric(tokens[i]))
            {
                continue;
            }

            int x0,
                x1;
            using var cleaned = chip.Clone();
            int removed = 0;
            using var foreign = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.All(0));
            if (useComponents)
            {
                var box = Box(groups[i]);
                x0 = Math.Max(0, box.Left - 1);
                x1 = Math.Min(bounds.Width, box.Right + 1);
                var ownIds = new HashSet<int>(groups[i]);
                var otherIds = new HashSet<int>(groups.Where((g, j) => i != j).SelectMany(g => g));
                using var own = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.All(0));
                for (int y = 0; y < bounds.Height; y++)
                {
                    for (int x = 0; x < bounds.Width; x++)
                    {
                        int id = labels.At<int>(y, x);
                        if (otherIds.Contains(id))
                        {
                            foreign.Set(y, x, (byte)255);
                        }

                        if (ownIds.Contains(id))
                        {
                            own.Set(y, x, (byte)255);
                        }
                    }
                }

                using var halo = new Mat();
                using var ownHalo = new Mat();
                Cv2.Dilate(foreign, halo, kernel);
                Cv2.Dilate(own, ownHalo, kernel);
                var background = new byte[chip.Channels()];
                for (int c = 0; c < background.Length; c++)
                {
                    var values = new List<byte>();
                    for (int y = 0; y < bounds.Height; y++)
                    {
                        for (int x = 0; x < bounds.Width; x++)
                        {
                            if (mask.At<byte>(y, x) == 0)
                            {
                                values.Add(
                                    chip.Channels() == 1 ? chip.At<byte>(y, x) : chip.At<Vec3b>(y, x)[c]
                                );
                            }
                        }
                    }

                    values.Sort();
                    background[c] =
                        values.Count == 0
                            ? (byte)255
                            : (byte)((values[(values.Count - 1) / 2] + values[values.Count / 2]) / 2);
                }

                for (int y = 0; y < bounds.Height; y++)
                {
                    for (int x = 0; x < bounds.Width; x++)
                    {
                        if (
                            foreign.At<byte>(y, x) != 0
                            || (
                                halo.At<byte>(y, x) != 0
                                && ownHalo.At<byte>(y, x) == 0
                                && labels.At<int>(y, x) == 0
                            )
                        )
                        {
                            if (chip.Channels() == 1)
                            {
                                cleaned.Set(y, x, background[0]);
                            }
                            else
                            {
                                cleaned.Set(y, x, new Vec3b(background[0], background[1], background[2]));
                            }
                        }
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
                    if (mask.At<byte>(y, x) != 0 && foreign.At<byte>(y, x) == 0)
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
                    if (foreign.At<byte>(y, x) != 0)
                    {
                        removed++;
                    }
                }
            }

            using var patch = new Mat(cleaned, new Rect(x0, y0, x1 - x0, y1 - y0));
            patches.Add(
                new CharacterPatch(
                    tokens[i].ToString(),
                    i,
                    new PixelRect(bounds.X + x0, bounds.Y + y0, x1 - x0, y1 - y0),
                    CvPixels.Buffer(patch),
                    removed
                )
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

    /// <summary>只切割明确声明的等格单元，不用于解决有歧义的物理分割。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">显式等格原图范围；此处PixelRect是中立PixelBounds的别名。</param>
    /// <param name = "expected">调用方明确确认的等格标签序列。</param>
    public CharacterSegmentation EqualCells(IImageSource frame, PixelRect bounds, string expected)
    {
        if (
            string.IsNullOrEmpty(expected)
            || expected.Length > 128
            || expected.Any(c => !CharacterIdentity.IsAlphanumeric(c))
        )
        {
            throw new ArgumentException("Invalid explicit identities.");
        }

        if (!bounds.Fits(frame) || bounds.Width < expected.Length)
        {
            throw new ArgumentException("Invalid equal cells.");
        }

        using var raw = CvPixels.Mat(frame);
        using var chars = new OwnedPatches();
        for (int i = 0; i < expected.Length; i++)
        {
            int left = bounds.X + i * bounds.Width / expected.Length,
                right = bounds.X + (i + 1) * bounds.Width / expected.Length;
            var box = new PixelRect(left, bounds.Y, right - left, bounds.Height);
            using var crop = new Mat(raw, CvPixels.Rect(box));
            chars.Add(new CharacterPatch(expected[i].ToString(), i, box, CvPixels.Buffer(crop)));
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
