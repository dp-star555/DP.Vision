namespace DP.Vision.OpenCv;

public sealed partial class OpenCvCnnPatchAnomalyDetector
{
    /// <summary>一张裁图的CNN特征网格（行优先，每单元Dimensions维）及每单元是否纸白。</summary>
    private sealed class CellGrid
    {
        internal CellGrid(float[] values, bool[] blank, int width, int height, int dimensions)
        {
            Values = values;
            Blank = blank;
            Width = width;
            Height = height;
            Dimensions = dimensions;
        }

        internal float[] Values { get; }

        internal bool[] Blank { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal int Dimensions { get; }
    }
}
