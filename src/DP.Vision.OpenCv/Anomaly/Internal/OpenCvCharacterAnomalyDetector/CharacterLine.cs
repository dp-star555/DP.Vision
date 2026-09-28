namespace DP.Vision.OpenCv;

public sealed partial class OpenCvCharacterAnomalyDetector
{
    /// <summary>一行文字的几何：大写字母/数字顶线、基线（原图纵坐标）及纸色灰度。</summary>
    private sealed class CharacterLine
    {
        internal CharacterLine(double capTop, double baseline, byte paper)
        {
            CapTop = capTop;
            Baseline = baseline;
            Paper = paper;
        }

        /// <summary>大写字母/数字顶线（原图纵坐标）。</summary>
        internal double CapTop { get; }

        /// <summary>基线（原图纵坐标）。</summary>
        internal double Baseline { get; }

        /// <summary>行高（大写高度，原图像素）。</summary>
        internal double Height => Baseline - CapTop;

        /// <summary>纸色灰度，用于单元外填充。</summary>
        internal byte Paper { get; }
    }
}
