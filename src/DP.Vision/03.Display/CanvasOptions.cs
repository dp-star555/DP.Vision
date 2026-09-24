using System;

namespace DP.Vision;

/// <summary>可移植的画布显示策略；LOD需显式启用，且只影响开放轮廓。</summary>
public sealed class CanvasOptions
{
    /// <summary>创建有界的显示策略；Gray16窗口映射仅影响显示，不改原始图像。</summary>
    /// <param name = "contourLod">是否启用开放轮廓的显示简化，默认关闭。</param>
    /// <param name = "maximumScreenError">允许的几何显示误差，大于0且不超过2，单位为控件像素或DIP。</param>
    /// <param name = "tileCacheBytes">图块像素载荷缓存预算，范围4–512MiB；不是进程总内存上限。</param>
    /// <param name = "tileSize">图块边长，必须是64–512之间的2次幂。</param>
    /// <param name = "gray16Low">Gray16显示黑点；该值映射为0。</param>
    /// <param name = "gray16High">Gray16显示白点；该值映射为255，必须大于黑点。</param>
    /// <param name = "showImage">是否显示图像层；不影响原图和叠加几何。</param>
    public CanvasOptions(
        bool contourLod = false,
        double maximumScreenError = .5,
        long tileCacheBytes = 64 * 1024 * 1024,
        int tileSize = 256,
        ushort gray16Low = 0,
        ushort gray16High = 65535,
        bool showImage = true
    )
    {
        if (!PointD.Valid(maximumScreenError) || maximumScreenError <= 0 || maximumScreenError > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumScreenError), "显示误差必须大于0且不超过2。");
        }

        if (tileCacheBytes < 4 * 1024 * 1024 || tileCacheBytes > 512L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(tileCacheBytes), "图块缓存预算必须在4～512MiB之间。");
        }

        if (tileSize < 64 || tileSize > 512 || (tileSize & (tileSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tileSize), "图块边长必须是64～512之间的2次幂。");
        }

        if (gray16High <= gray16Low)
        {
            throw new ArgumentOutOfRangeException(nameof(gray16High), "Gray16显示白点必须大于黑点。");
        }

        ContourLod = contourLod;
        MaximumScreenError = maximumScreenError;
        TileCacheBytes = tileCacheBytes;
        TileSize = tileSize;
        Gray16Low = gray16Low;
        Gray16High = gray16High;
        ShowImage = showImage;
    }

    /// <summary>是否启用仅用于显示的轮廓简化。</summary>
    public bool ContourLod { get; }

    /// <summary>屏幕坐标中的最大几何偏差，不是抗锯齿或灰度误差上限。</summary>
    public double MaximumScreenError { get; }

    /// <summary>计入缓存的像素载荷字节预算，不是整个进程的内存预算。</summary>
    public long TileCacheBytes { get; }

    /// <summary>以2次幂表示的图块边长。</summary>
    public int TileSize { get; }

    /// <summary>Gray16显示黑点。</summary>
    public ushort Gray16Low { get; }

    /// <summary>Gray16显示白点。</summary>
    public ushort Gray16High { get; }

    /// <summary>图像层是否可见。</summary>
    public bool ShowImage { get; }
}
