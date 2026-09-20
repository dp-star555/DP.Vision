using System;

namespace DP.Vision.UI;

/// <summary>一次渲染所需的同版本界面状态和帧租约，UI应用完毕即释放。</summary>
internal sealed class ResultBrowserPresentation : IDisposable
{
    internal ResultBrowserPresentation(
        ResultBrowserSnapshot snapshot,
        CanvasFrame? frame,
        long contentVersion
    )
    {
        Snapshot = snapshot;
        Frame = frame;
        ContentVersion = contentVersion;
    }

    internal ResultBrowserSnapshot Snapshot { get; }
    internal CanvasFrame? Frame { get; }
    internal long ContentVersion { get; }

    public void Dispose() => Frame?.Dispose();
}
