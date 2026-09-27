namespace DP.Vision.OpenCv;

public sealed partial class OpenCvPatchAnomalyDetector
{
    /// <summary>
    /// 位置相关比较用的一张良品：原尺度墨量平面与四个下采样相位的1/2尺度上下文平面（已乘权重并补纸白边）。
    /// 与查询无关，模型载入后只计算一次、各次检测复用（原先每次检测都重算）。
    /// </summary>
    private sealed class LocalReference
    {
        internal LocalReference(PatchFeatures.Plane full, int patchSize, int radius)
        {
            Full = full;
            int pad = Pad(patchSize, radius);
            Phases = new PatchFeatures.Plane[4];
            for (int phase = 0; phase < 4; phase++)
            {
                Phases[phase] = PatchFeatures.PhaseContext(full, phase % 2, phase / 2, pad);
            }
        }

        internal PatchFeatures.Plane Full { get; }

        /// <summary>按相位 py·2+px 索引。</summary>
        internal PatchFeatures.Plane[] Phases { get; }

        internal static int Pad(int patchSize, int radius)
        {
            return patchSize + radius + 2;
        }
    }
}
