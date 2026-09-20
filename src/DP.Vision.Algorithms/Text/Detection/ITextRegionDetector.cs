using System;
using System.Collections.Generic;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>通用的原图坐标文本候选；发现候选不等于印刷质量验收。</summary>
public interface ITextRegionDetector : IDisposable
{
    /// <summary>实际模型快照的标识。</summary>
    string ModelIdentity { get; }

    /// <summary>从借用的不可变图像检测有界候选集合。</summary>
    /// <param name = "image">借用的不可变原图，返回前租约须有效。</param>
    /// <param name = "token">协作式取消标记。</param>
    IReadOnlyList<PixelBounds> Detect(IImageSource image, CancellationToken token = default);
}
