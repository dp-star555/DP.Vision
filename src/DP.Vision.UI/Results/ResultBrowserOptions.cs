using System;

namespace DP.Vision.UI;

/// <summary>视图浏览器的保留上限，不是进程总内存上限。</summary>
public sealed class ResultBrowserOptions
{
    /// <summary>配置像素、视图、几何和图层偏好的有限容量。</summary>
    /// <param name="previewBytes">保留视图按Info.ByteLength求和的预算；共享源重复计账，不含客户、画布及临时租约。</param>
    /// <param name="maximumViews">视图集合容量，范围1～256。</param>
    /// <param name="maximumGeometryElements">全体视图的累计顶点/游程预算，范围1～2000000；空几何至少计1。</param>
    /// <param name="maximumPreferences">记忆的图层偏好条数，范围128～16384，超限淘汰最早记录。</param>
    public ResultBrowserOptions(
        long previewBytes = 256L * 1024 * 1024,
        int maximumViews = 128,
        long maximumGeometryElements = 2000000,
        int maximumPreferences = 4096
    )
    {
        if (previewBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(previewBytes), "预览像素预算必须为正数。");
        if (maximumViews < 1 || maximumViews > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumViews), "视图容量必须在1～256之间。");
        if (maximumGeometryElements < 1 || maximumGeometryElements > 2000000)
            throw new ArgumentOutOfRangeException(
                nameof(maximumGeometryElements),
                "累计几何预算必须在1～2000000之间。"
            );
        if (maximumPreferences < 128 || maximumPreferences > 16384)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPreferences),
                "显隐偏好容量必须在128～16384之间。"
            );
        PreviewBytes = previewBytes;
        MaximumViews = maximumViews;
        MaximumGeometryElements = maximumGeometryElements;
        MaximumPreferences = maximumPreferences;
    }

    /// <summary>预览像素保守累计字节预算。</summary>
    public long PreviewBytes { get; }

    /// <summary>视图集合容量。</summary>
    public int MaximumViews { get; }

    /// <summary>累计几何元素上限。</summary>
    public long MaximumGeometryElements { get; }

    /// <summary>显隐偏好条目上限。</summary>
    public int MaximumPreferences { get; }
}
