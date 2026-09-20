using System;

namespace DP.Vision.UI;

/// <summary>两种原生浏览器共用的原子呈现逻辑；只在画布所属UI线程使用，不拥有输入会话或画布。</summary>
public sealed class ResultBrowserPresenter
{
    private readonly ResultBrowserSession _session;
    private readonly IVisionCanvas _canvas;
    private long _version = -1;
    private long _contentVersion = -1;
    private string? _view;

    /// <summary>将共享会话连接到一个专用画布，宿主不要再向该画布直接提交其他帧。</summary>
    /// <param name="session">由外层浏览器拥有的结果会话。</param>
    /// <param name="canvas">仅供本浏览器呈现使用的画布。</param>
    public ResultBrowserPresenter(ResultBrowserSession session, IVisionCanvas canvas)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session), "浏览会话不能为空。");
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas), "专用画布不能为空。");
    }

    /// <summary>将一次同版本图像/图层快照提交到画布；没有状态变化时不复制、不重新创建预览包。</summary>
    /// <returns>已应用的选择栏快照；没有变化时为null。</returns>
    public ResultBrowserSnapshot? Refresh()
    {
        if (_session.Version == _version)
            return null;
        using var presentation = _session.Capture();
        var snapshot = presentation.Snapshot;
        bool changedSelection = snapshot.ViewId != _view;
        bool newContent = presentation.ContentVersion != _contentVersion;
        if (newContent || changedSelection || presentation.Frame == null)
            _canvas.ClearImage();
        if (presentation.Frame != null)
        {
            _canvas.Present(presentation.Frame);
            if (changedSelection)
                _canvas.FitToWindow();
        }
        _version = snapshot.Version;
        _contentVersion = presentation.ContentVersion;
        _view = snapshot.ViewId;
        return snapshot;
    }
}
