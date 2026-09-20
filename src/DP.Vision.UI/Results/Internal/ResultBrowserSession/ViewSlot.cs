namespace DP.Vision.UI;

public sealed partial class ResultBrowserSession
{
    /// <summary>预览像素可淘汰，名称及图层元数据保留，以明确显示未保留状态。</summary>
    private sealed class ViewSlot
    {
        internal string Id = "";
        internal string Name = "";
        internal string FrameId = "";
        internal GeometryOverlay Overlay = null!;
        internal IImageSource? Source;
        internal long Bytes;
        internal long LastUse;
        internal long ContentVersion;
    }
}
