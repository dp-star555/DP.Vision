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
