using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>可选的人工复核参考制作接口，不用于正式验收分割。</summary>
public interface IGlyphCandidateSegmenter
{
    /// <summary>提供有界的候选切分，同时保留物理证据的不确定状态。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">用于参考制作的单行原图范围。</param>
    /// <param name = "text">候选标签提示，不构成强制切分依据。</param>
    /// <param name = "token">协作式取消标记。</param>
    CharacterSegmentation SegmentCandidates(
        IImageSource frame,
        PixelBounds bounds,
        string text,
        CancellationToken token = default
    );
}
