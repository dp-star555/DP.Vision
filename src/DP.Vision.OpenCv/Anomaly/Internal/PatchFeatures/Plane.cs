namespace DP.Vision.OpenCv;

internal static partial class PatchFeatures
{
    /// <summary>按行排列的单通道浮点平面；越界读取返回0（纸白）。</summary>
    internal sealed class Plane
    {
        internal Plane(float[] data, int width, int height)
        {
            Data = data;
            Width = width;
            Height = height;
        }

        internal float[] Data { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal float At(int x, int y)
        {
            return x < 0 || y < 0 || x >= Width || y >= Height ? 0f : Data[y * Width + x];
        }
    }
}
