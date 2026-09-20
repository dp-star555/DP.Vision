using System;

namespace DP.Vision.UI;

/// <summary>客户直接提交图像源；预览包的临时租约在内部创建和释放，仍保持图像与证据原子绑定。</summary>
public static class CanvasImageExtensions
{
    /// <summary>在UI线程显示图像；返回前画布保留独立源租约，客户随后可释放自己的源。</summary>
    /// <param name="canvas">目标原生画布。</param>
    /// <param name="source">借用图像源，不转移调用方所有权。</param>
    /// <param name="frameId">原图内容身份，像素改变时必须更换；叠加证据必须属于同一身份。</param>
    /// <param name="sequence">本画布会话中单调递增的非负预览序号，旧序号不会替换当前显示。</param>
    /// <param name="overlay">可选的同帧证据快照。</param>
    public static void SetImage(
        this IVisionCanvas canvas,
        IImageSource source,
        string frameId,
        long sequence,
        GeometryOverlay? overlay = null
    )
    {
        if (canvas == null)
            throw new ArgumentNullException(nameof(canvas));
        using var frame = new CanvasFrame(frameId, sequence, source, overlay);
        canvas.Present(frame);
    }

    /// <summary>从生产者线程提交预览源；返回前完成保留，仅预览允许丢旧保新，不能用作检测队列。</summary>
    /// <param name="canvas">目标原生画布。</param>
    /// <param name="source">借用图像源，返回后客户可释放自己的句柄。</param>
    /// <param name="frameId">与叠加证据一致的原图身份。</param>
    /// <param name="sequence">本画布会话中单调递增的非负序号。</param>
    /// <param name="overlay">可选同帧证据。</param>
    /// <returns>是否接受此序号；拒绝时不会遗留本次提交的内部租约。</returns>
    public static bool PostImage(
        this IVisionCanvas canvas,
        IImageSource source,
        string frameId,
        long sequence,
        GeometryOverlay? overlay = null
    )
    {
        if (canvas == null)
            throw new ArgumentNullException(nameof(canvas));
        using var frame = new CanvasFrame(frameId, sequence, source, overlay);
        return canvas.PostFrame(frame);
    }
}
