using System.Collections.Generic;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

internal static partial class PatchFeatures
{
    /// <summary>一组块特征及其在原尺度上的左上角坐标。</summary>
    internal sealed class Set
    {
        internal Set(int dimensions)
        {
            Dimensions = dimensions;
        }

        internal int Dimensions { get; }

        internal List<float> Values { get; } = new List<float>();

        internal List<Point> Corners { get; } = new List<Point>();

        internal int Count => Corners.Count;
    }
}
