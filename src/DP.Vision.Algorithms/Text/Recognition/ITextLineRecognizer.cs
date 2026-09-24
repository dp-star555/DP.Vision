using System;
using System.Threading;

namespace DP.Vision.Algorithms;

using PixelRect = DP.Vision.Algorithms.PixelBounds;

/// <summary>可替换的单行识别器；拥有者必须协调释放与正在执行的调用。</summary>
public interface ITextLineRecognizer : IDisposable
{
    /// <summary>识别明确选择的水平单行。</summary>
    /// <param name = "frame">输入图像。</param>
    /// <param name = "bounds">原图ROI。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>OCR证据，不是外观判定。</returns>
    TextLineRecognition Recognize(IImageSource frame, PixelRect bounds, CancellationToken token);
}
