using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OpenCv;

public sealed partial class OpenCvBarcodePrintInspector
{
    private sealed class Band(int top, int bottom, bool[] profile)
    {
        internal int Top = top,
            Bottom = bottom;
        internal bool[] Profile = profile;
    }
}
