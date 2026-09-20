namespace DP.Vision.UI;

/// <summary>
/// 两种原生画布共用的UI契约。除PostFrame外，所有操作都必须在控件所属UI线程调用。
/// </summary>
public interface IVisionCanvas
{
    /// <summary>从生产者线程提交最新预览帧；不会把预览丢帧策略应用到检测任务。</summary>
    /// <param name = "frame">待显示的图像/证据原子包；控件保留独立租约，调用方仍负责释放自己的句柄。</param>
    /// <returns>是否接受该预览序号。</returns>
    bool PostFrame(CanvasFrame frame);

    /// <summary>在UI线程同步应用图像及对应证据，并保留图像源。</summary>
    /// <param name = "frame">同一帧的图像和叠加快照，调用方继续拥有原句柄。</param>
    void Present(CanvasFrame frame);

    /// <summary>
    /// 在UI线程清空当前图像、缓存及调用时已排队的预览，释放画布自己的租约，不影响检测分支。
    /// 不重置预览序号水位；并发生产者之后提交的新帧仍可显示，停止预览时应先停止生产者。
    /// </summary>
    void ClearImage();

    /// <summary>画布显示策略，不修改原始像素或后台检测参数。</summary>
    CanvasOptions Options { get; set; }

    /// <summary>当前实际显示的帧标识，不是尚在等待的最新帧标识。</summary>
    string? DisplayedFrameId { get; }

    /// <summary>UI拥有的ROI编辑器；不传入后台算法，null表示不启用通用ROI编辑。</summary>
    RoiEditor? Editor { get; set; }

    /// <summary>通过与原生事件相同的坐标转换路径处理宿主输入；鼠标捕获由宿主管理。</summary>
    /// <param name = "action">按下、移动或释放动作。</param>
    /// <param name = "clientPoint">控件客户区坐标；WinForms单位为像素，WPF单位为DIP，不是原图坐标。</param>
    void ProcessRoiPointer(ERoiPointerAction action, PointD clientPoint);

    /// <summary>将当前图像完整适配到客户区；不会重采样或覆盖原始图像。</summary>
    void FitToWindow();

    /// <summary>原图到控件的共享视口。宿主直接修改它前应先取消未提交的编辑手势。</summary>
    CanvasViewport Viewport { get; }
}
