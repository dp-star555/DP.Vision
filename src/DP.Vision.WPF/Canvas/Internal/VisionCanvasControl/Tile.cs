using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DP.Vision.UI;

namespace DP.Vision.WPF;

public sealed partial class VisionCanvasControl
{
    private sealed class Tile
    {
        internal BitmapSource Bitmap = null!;
        internal long Revision = -1;
    }
}
