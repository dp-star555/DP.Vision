using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

public sealed partial class OpenCvCharacterAnomalyDetector
{
    /// <summary>
    /// 逐字符异常检测的字符归一化：按整行几何（大写/数字高度与基线的中位数）缩放，而不是按每个字自己的墨迹框缩放——
    /// 缺了顶部或底部笔画的字不会被放大“补齐”，缺陷保持在原来的位置。水平方向以分割单元中心为中心，
    /// 单元外的列涂成纸色，相邻字符不进入模型。
    /// </summary>
    private static class CharacterCells
    {
        /// <summary>归一化后的大写字母/数字高度（像素）。</summary>
        internal const int CapHeight = 32;

        /// <summary>上下各留出的行高比例，容纳下伸/上伸部分及检测块上下文。</summary>
        internal const double VerticalPad = .45;

        /// <summary>左右各留出的行高比例。</summary>
        internal const double HorizontalPad = .35;

        /// <summary>归一化单元高度。</summary>
        internal static int CellHeight => (int)Math.Round(CapHeight * (1 + 2 * VerticalPad));

        private static int Pad => (int)Math.Round(HorizontalPad * CapHeight);

        private static bool Tall(string label)
        {
            return label.Length != 1
                || label[0] > 127
                || label[0] >= '0' && label[0] <= '9'
                || label[0] >= 'A' && label[0] <= 'Z'
                || "bdfhklt".IndexOf(label[0]) >= 0;
        }

        private static bool Anchor(string label)
        {
            if (!CharacterIdentity.IsGlyph(label))
                return false;
            var category = CharUnicodeInfo.GetUnicodeCategory(label, 0);
            return category >= UnicodeCategory.UppercaseLetter && category <= UnicodeCategory.OtherLetter
                || category >= UnicodeCategory.DecimalDigitNumber && category <= UnicodeCategory.OtherNumber;
        }

        /// <summary>稳定单行ROI几何，不从当前字符墨迹推导比例；缺笔、纯标点或空墨不会被缩放补齐。</summary>
        internal static CharacterLine? MeasureRegion(Mat gray, PixelBounds bounds)
        {
            if (
                bounds.X < 0
                || bounds.Y < 0
                || bounds.Width < 4
                || bounds.Height < 4
                || (long)bounds.X + bounds.Width > gray.Cols
                || (long)bounds.Y + bounds.Height > gray.Rows
            )
                return null;
            using var crop = new Mat(gray, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            double height = bounds.Height / (1 + 2 * VerticalPad);
            double top = bounds.Y + VerticalPad * height;
            return new CharacterLine(
                top,
                top + height,
                Percentile(crop, .98),
                ECharacterNormalization.LineRegion
            );
        }

        /// <summary>ASCII字母或数字：参与行几何测量。</summary>
        internal static bool IsAlphanumeric(char c)
        {
            return c >= '0' && c <= '9' || c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z';
        }

        private static bool Descender(string label) => label.Length == 1 && "gjpqy".IndexOf(label[0]) >= 0;

        private static double Median(IEnumerable<double> values)
        {
            var a = values.OrderBy(v => v).ToArray();
            return a.Length == 0 ? double.NaN : a[a.Length / 2];
        }

        /// <summary>测量一行的大写高度顶线、基线和纸色；墨迹按整行Otsu阈值判定。无法测量时返回null。</summary>
        /// <param name = "gray">整图灰度。</param>
        /// <param name = "characters">该行字符（原图坐标），身份用于区分高字符和下伸字符。</param>
        internal static CharacterLine? Measure(Mat gray, IReadOnlyList<CharacterAnomalyCharacter> characters)
        {
            var alnum = characters.Where(c => Anchor(c.Character)).ToArray();
            if (alnum.Length == 0)
            {
                return null;
            }

            var image = new Rect(0, 0, gray.Cols, gray.Rows);
            var union = alnum
                .Select(c => new Rect(c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height))
                .Aggregate((a, b) => a | b);
            union = new Rect(union.X, union.Y - union.Height / 2, union.Width, union.Height * 2) & image;
            if (union.Width < 2 || union.Height < 2)
            {
                return null;
            }

            using var crop = new Mat(gray, union);
            using var ink = new Mat();
            Cv2.Threshold(crop, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
            byte paper = Percentile(crop, .98);
            var tops = new List<(string c, double top, double bottom)>();
            foreach (var c in alnum)
            {
                var cell = new Rect(c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height) & union;
                if (cell.Width < 1 || cell.Height < 1)
                {
                    continue;
                }

                using var part = new Mat(
                    ink,
                    new Rect(cell.X - union.X, cell.Y - union.Y, cell.Width, cell.Height)
                );
                using var rows = new Mat();
                Cv2.Reduce(part, rows, ReduceDimension.Column, ReduceTypes.Max, -1);
                int top = -1,
                    bottom = -1;
                for (int y = 0; y < rows.Rows; y++)
                {
                    if (rows.At<byte>(y, 0) != 0)
                    {
                        top = top < 0 ? y : top;
                        bottom = y + 1;
                    }
                }

                if (top >= 0)
                {
                    tops.Add((c.Character, cell.Y + top, cell.Y + bottom));
                }
            }

            double baseline = Median(tops.Where(t => !Descender(t.c)).Select(t => t.bottom));
            double capTop = Median(tops.Where(t => Tall(t.c)).Select(t => t.top));
            if (double.IsNaN(capTop) && !double.IsNaN(baseline))
            {
                // 只有x高度的小写字母时按常见字体x高度约为大写高度的0.7倍估计。
                capTop = baseline - (baseline - Median(tops.Select(t => t.top))) / .7;
            }

            return double.IsNaN(baseline) || double.IsNaN(capTop) || baseline - capTop < 6
                ? null
                : new CharacterLine(capTop, baseline, paper);
        }

        /// <summary>某字符模型的归一化单元宽度：训练样本中最宽的分割单元加左右留白。</summary>
        /// <param name = "cellWidth">分割单元的原图宽度。</param>
        /// <param name = "line">所在行的几何。</param>
        internal static int Width(double cellWidth, CharacterLine line)
        {
            return (int)Math.Ceiling(cellWidth * CapHeight / line.Height) + 2 * Pad;
        }

        /// <summary>把一个字符归一化为指定宽度的单元（高度<see cref = "CellHeight"/>），单元外的列涂成纸色。</summary>
        /// <param name = "gray">整图灰度。</param>
        /// <param name = "line">所在行的几何。</param>
        /// <param name = "cell">字符分割单元（原图坐标）。</param>
        /// <param name = "width">该字符模型的单元宽度。</param>
        internal static CharacterCell Normalize(Mat gray, CharacterLine line, PixelBounds cell, int width)
        {
            double scale = CapHeight / line.Height,
                x0 = cell.X + cell.Width / 2.0 - width / scale / 2,
                y0 = line.CapTop - VerticalPad * line.Height;
            using var transform = new Mat(2, 3, MatType.CV_64FC1);
            transform.Set(0, 0, scale);
            transform.Set(0, 1, 0.0);
            transform.Set(0, 2, -x0 * scale);
            transform.Set(1, 0, 0.0);
            transform.Set(1, 1, scale);
            transform.Set(1, 2, -y0 * scale);
            var output = new Mat();
            Cv2.WarpAffine(
                gray,
                output,
                transform,
                new Size(width, CellHeight),
                scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear,
                BorderTypes.Constant,
                new Scalar(line.Paper)
            );
            int left = (int)Math.Floor((cell.X - x0) * scale),
                right = (int)Math.Ceiling((cell.X + cell.Width - x0) * scale);
            if (left > 0)
            {
                output[new Rect(0, 0, Math.Min(left, width), CellHeight)].SetTo(new Scalar(line.Paper));
            }

            if (right < width)
            {
                int from = Math.Max(0, right);
                output[new Rect(from, 0, width - from, CellHeight)].SetTo(new Scalar(line.Paper));
            }

            return new CharacterCell(output, x0, y0, scale);
        }

        private static byte Percentile(Mat gray, double fraction)
        {
            using var hist = new Mat();
            Cv2.CalcHist(
                new[] { gray },
                new[] { 0 },
                null,
                hist,
                1,
                new[] { 256 },
                new[] { new Rangef(0, 256) }
            );
            double total = gray.Rows * gray.Cols,
                sum = 0;
            for (int v = 0; v < 256; v++)
            {
                sum += hist.At<float>(v);
                if (sum >= total * fraction)
                {
                    return (byte)v;
                }
            }

            return 255;
        }
    }
}
