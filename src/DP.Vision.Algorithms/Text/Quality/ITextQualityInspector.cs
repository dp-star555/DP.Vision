using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>整段文字质量的替换接口；执行前必须声明所需识别和参考条件。</summary>
public interface ITextQualityInspector
{
    /// <summary>该实现是否需要独立参考图像。</summary>
    bool RequiresReferences { get; }

    /// <summary>非显式等格布局时，是否需要实际OCR身份。</summary>
    bool RequiresRecognition { get; }

    /// <summary>借用输入执行质量测量，返回由调用方拥有的证据。</summary>
    /// <param name = "request">原始图像、字符身份、独立参考及测量参数。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>调用方必须Dispose的质量结果，完成状态与缺陷数量分开表达。</returns>
    TextQualityResult Inspect(TextQualityRequest request, CancellationToken token = default);
}
