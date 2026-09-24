using System;

namespace DP.Vision;

/// <summary>图像与叠加证据的原子预览包，拥有独立图像源租约；不能把它当成检测任务队列。</summary>
public sealed class CanvasFrame : IDisposable
{
    private readonly object _gate = new object();
    private IImageSource? _source;

    /// <summary>保留图像源的独立租约，并把图像和对应叠加证据作为同一预览包提交。</summary>
    /// <param name = "frameId">原图帧标识，必须与overlay的帧标识一致。</param>
    /// <param name = "sequence">非负预览序号，同一画布会话内应单调递增。</param>
    /// <param name = "image">只读图像源；内部Retain，调用方仍拥有原句柄。</param>
    /// <param name = "overlay">同帧证据快照；null表示不带检测叠加。</param>
    public CanvasFrame(string frameId, long sequence, IImageSource image, GeometryOverlay? overlay = null)
    {
        if (!Identity.IsValid(frameId))
        {
            throw new ArgumentException("帧标识不能为空白，且不得超过256个字符。", nameof(frameId));
        }

        if (sequence < 0)
        {
            throw new ArgumentException("预览序号不能为负数。", nameof(sequence));
        }

        if (overlay != null && overlay.FrameId != frameId)
        {
            throw new ArgumentException("叠加证据的帧标识与图像帧标识不一致。", nameof(overlay));
        }

        if (image == null)
        {
            throw new ArgumentNullException(nameof(image), "预览图像源不能为空。");
        }

        FrameId = frameId;
        Sequence = sequence;
        Overlay = overlay;
        _source = image.Retain();
        Info = image.Info;
    }

    /// <summary>原图帧标识。</summary>
    public string FrameId { get; }

    /// <summary>单调递增的预览序号。</summary>
    public long Sequence { get; }

    /// <summary>原图像素布局。</summary>
    public ImageInfo Info { get; }

    /// <summary>与图像原子绑定的叠加证据，无证据时为null。</summary>
    public GeometryOverlay? Overlay { get; }

    /// <summary>获取生命周期独立的预览包句柄。</summary>
    /// <returns>需要单独Dispose的新句柄；不复制原始像素。</returns>
    public CanvasFrame Retain()
    {
        lock (_gate)
        {
            return new CanvasFrame(
                FrameId,
                Sequence,
                _source ?? throw new ObjectDisposedException(nameof(CanvasFrame)),
                Overlay
            );
        }
    }

    /// <summary>返回独立拥有的图块租约；图像源应避免在渲染线程同步执行缓慢磁盘或网络访问。</summary>
    /// <param name = "level">采样级别，范围0–20，第0级为原像素。</param>
    /// <param name = "x">当前级别的非负图块列索引。</param>
    /// <param name = "y">当前级别的非负图块行索引。</param>
    /// <param name = "size">图块边长，范围16–1024，单位为当前级别像素。</param>
    /// <returns>由调用方释放的图块租约。</returns>
    public IImageSource ReadTile(int level, int x, int y, int size)
    {
        if (level < 0 || level > DisplayLimits.MaxLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "采样级别必须在0～20之间。");
        }

        if (size < DisplayLimits.MinTileEdge || size > DisplayLimits.MaxTileEdge)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "图块边长必须在16～1024之间。");
        }

        long factor = 1L << level,
            levelWidth = (Info.Width + factor - 1) / factor,
            levelHeight = (Info.Height + factor - 1) / factor,
            left = (long)x * size,
            top = (long)y * size;
        if (x < 0 || left >= levelWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "图块列索引超出当前级别的图像范围。");
        }

        if (y < 0 || top >= levelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(y), "图块行索引超出当前级别的图像范围。");
        }

        lock (_gate)
        {
            var tile =
                (_source ?? throw new ObjectDisposedException(nameof(CanvasFrame))).ReadTile(
                    level,
                    x,
                    y,
                    size
                ) ?? throw new InvalidOperationException("图像源没有返回图块。");
            if (
                tile.Info.Width != Math.Min(size, levelWidth - left)
                || tile.Info.Height != Math.Min(size, levelHeight - top)
                || tile.Info.Layout != Info.Layout
            )
            {
                tile.Dispose();
                throw new InvalidOperationException("图像源返回的图块尺寸或像素布局与帧信息不符。");
            }

            return tile;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        IImageSource? old;
        lock (_gate)
        {
            old = _source;
            _source = null;
        }

        old?.Dispose();
    }
}
