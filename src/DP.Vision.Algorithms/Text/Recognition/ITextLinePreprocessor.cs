using System.Threading;

namespace DP.Vision.Algorithms;

using PixelRect = DP.Vision.Algorithms.PixelBounds;

/// <summary>任务级预处理接口，与具体原生视觉库无关。</summary>
public interface ITextLinePreprocessor
{
    /// <summary>预处理明确选定的水平单行，不执行文本检测，也不注入预期文本。</summary>
    /// <param name = "frame">不可变图像。</param>
    /// <param name = "bounds">原图坐标范围。</param>
    /// <param name = "token">取消标记。</param>
    /// <returns>归一化输入及填充几何。</returns>
    TextLineInput Prepare(IImageSource frame, PixelRect bounds, CancellationToken token);
}
