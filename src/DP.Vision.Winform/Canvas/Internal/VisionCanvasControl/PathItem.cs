using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DP.Vision.UI;

namespace DP.Vision.Winform;

public sealed partial class VisionCanvasControl
{
    private sealed class PathItem : IDisposable
    {
        internal GraphicsPath Path = null!;
        internal double Tolerance;

        public void Dispose()
        {
            Path.Dispose();
        }
    }
}
