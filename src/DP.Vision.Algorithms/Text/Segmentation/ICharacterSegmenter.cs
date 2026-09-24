using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>可替换的物理字符提取接口；借用输入，返回结果的所有权交给调用方。</summary>
public interface ICharacterSegmenter
{
    /// <summary>使用字符身份提示，但不强制把图像切成提示数量；支持协作式取消。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">单行文字的原图整数范围。</param>
    /// <param name = "text">真实读取的身份提示，不能强制图像凑齐其数量。</param>
    /// <param name = "token">协作式取消标记。</param>
    CharacterSegmentation Segment(
        IImageSource frame,
        PixelBounds bounds,
        string text,
        CancellationToken token = default
    );

    /// <summary>只切割调用方明确声明的等宽单元，不作为粘连墨迹的自动回退。</summary>
    /// <param name = "frame">借用的原始只读图像。</param>
    /// <param name = "bounds">明确声明等宽布局的原图范围。</param>
    /// <param name = "expected">调用方明确确认的等格标签序列，不是推断的OCR结果。</param>
    CharacterSegmentation EqualCells(IImageSource frame, PixelBounds bounds, string expected);
}
