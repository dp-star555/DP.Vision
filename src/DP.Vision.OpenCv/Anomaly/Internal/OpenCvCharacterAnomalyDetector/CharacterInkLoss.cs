using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

public sealed partial class OpenCvCharacterAnomalyDetector
{
    /// <summary>
    /// 逐字符缺墨检查（单向）：笔画内的墨量比任何良品在±1像素内的最低墨量还低多少。
    /// 墨量与局部块模型相同（灰度按本字符单元的纸色归一化为0纸白–1满墨并轻度平滑），因此整体亮度/增益变化相互抵消，
    /// 而斑驳、褪色、断笔使笔画内墨量下降。参考为各良品先做3×3最小值（容许±1像素位置差），再逐像素取最小；
    /// 只检查良品平均墨量达到峰值一半的笔画区域，差值做3×3平均后取最大。良品数据取自位置相关手工特征模型中保存的墨量平面。
    /// </summary>
    private sealed class CharacterInkLoss
    {
        /// <summary>缺墨阈值下限（墨量比例），相当于纸墨反差约200灰度级时的8个灰度级。</summary>
        internal const double MinimumThreshold = .04;

        private readonly float[] _lower;
        private readonly byte[] _mask;

        internal CharacterInkLoss(IReadOnlyList<float[]> planes, int width, int height)
        {
            Width = width;
            Height = height;
            Samples = planes.Count;
            _lower = new float[width * height];
            var mean = new float[width * height];
            for (int i = 0; i < _lower.Length; i++)
            {
                _lower[i] = float.MaxValue;
            }

            using var kernel = new Mat(3, 3, MatType.CV_8UC1, Scalar.All(1));
            foreach (var plane in planes)
            {
                using var source = Mat.FromPixelData(height, width, MatType.CV_32FC1, plane);
                using var eroded = new Mat();
                Cv2.Erode(source, eroded, kernel);
                var values = new float[width * height];
                Marshal.Copy(eroded.Data, values, 0, values.Length);
                for (int i = 0; i < values.Length; i++)
                {
                    _lower[i] = Math.Min(_lower[i], values[i]);
                    mean[i] += plane[i] / planes.Count;
                }
            }

            var sorted = (float[])mean.Clone();
            Array.Sort(sorted);
            float peak = sorted[(int)(sorted.Length * .95)];
            _mask = mean.Select(m => (byte)(peak > .05f && m >= peak / 2 ? 1 : 0)).ToArray();
        }

        internal int Width { get; }

        internal int Height { get; }

        internal int Samples { get; }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            PatchAnomalyModel,
            CharacterInkLoss
        > References = new System.Runtime.CompilerServices.ConditionalWeakTable<
            PatchAnomalyModel,
            CharacterInkLoss
        >();

        /// <summary>模型的缺墨参考：由模型中各良品的墨量平面计算一次，与模型同生命周期复用。</summary>
        internal static CharacterInkLoss For(PatchAnomalyModel model)
        {
            return References.GetValue(model, m => new CharacterInkLoss(Planes(m), m.Width, m.Height));
        }

        /// <summary>模型是否带逐图墨量平面（位置相关手工特征模型）。</summary>
        internal static bool Supports(PatchAnomalyModel model)
        {
            return model.FeatureSource == PatchAnomalyModel.Handcrafted && model.Radius > 0;
        }

        /// <summary>模型中保存的各良品原尺度墨量平面（训练顺序）。</summary>
        internal static List<float[]> Planes(PatchAnomalyModel model)
        {
            int length = PatchAnomalyModel.PlaneLength(model.Width, model.Height);
            var memory = model.CopyMemory();
            var planes = new List<float[]>();
            for (int i = 0; i < model.TrainingImages; i++)
            {
                var plane = new float[model.Width * model.Height];
                Array.Copy(memory, i * length, plane, 0, plane.Length);
                planes.Add(plane);
            }

            return planes;
        }

        /// <summary>灰度单元换算为墨量平面：与局部块特征相同（PatchFeatures.Prepare的原尺度平面）。</summary>
        internal static float[] Ink(Mat gray)
        {
            return PatchFeatures.Prepare(gray).Full.Data;
        }

        /// <summary>缺墨图（CV_32F，已做3×3平均，调用方释放）与其最大值。</summary>
        internal (double Score, Mat Excess) Measure(float[] ink)
        {
            if (ink.Length != _lower.Length)
            {
                throw new ArgumentException("Ink plane size mismatch.", nameof(ink));
            }

            var excess = new float[ink.Length];
            for (int i = 0; i < ink.Length; i++)
            {
                excess[i] = _mask[i] == 0 ? 0 : Math.Max(0, _lower[i] - ink[i]);
            }

            using var raw = Mat.FromPixelData(Height, Width, MatType.CV_32FC1, excess);
            var smoothed = new Mat();
            Cv2.Blur(raw, smoothed, new Size(3, 3));
            Cv2.MinMaxLoc(smoothed, out _, out double max);
            return (max, smoothed);
        }

        /// <summary>
        /// 按来源图留一标定阈值：每张来源图的全部样本不参与参考，再评分，取最大值×余量，不低于<see cref = "MinimumThreshold"/>。
        /// 同一张图（同一次印刷）的重复字符几乎一样，按单个样本留一会把阈值压得过紧。来源图少于2张时无法标定，返回null。
        /// </summary>
        /// <param name = "planes">各样本墨量平面。</param>
        /// <param name = "sources">各样本的来源图编号。</param>
        /// <param name = "width">平面宽度。</param>
        /// <param name = "height">平面高度。</param>
        /// <param name = "margin">阈值余量（与局部块比较相同）。</param>
        internal static double? Calibrate(
            IReadOnlyList<float[]> planes,
            IReadOnlyList<int> sources,
            int width,
            int height,
            double margin
        )
        {
            var distinct = sources.Distinct().ToList();
            if (distinct.Count < 2)
            {
                return null;
            }

            double worst = 0;
            foreach (int held in distinct)
            {
                var reference = new CharacterInkLoss(
                    planes.Where((_, i) => sources[i] != held).ToList(),
                    width,
                    height
                );
                for (int i = 0; i < planes.Count; i++)
                {
                    if (sources[i] == held)
                    {
                        var (score, excess) = reference.Measure(planes[i]);
                        excess.Dispose();
                        worst = Math.Max(worst, score);
                    }
                }
            }

            return Math.Min(1, Math.Max(MinimumThreshold, worst * margin));
        }
    }
}
