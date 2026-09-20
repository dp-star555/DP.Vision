using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>借用的独立参考快照；字符身份和存储策略由调用方管理。</summary>
public sealed class GlyphTemplate
{
    /// <summary>引用调用方拥有的不可变参考图及二值化模式，不接管租约。</summary>
    /// <param name = "image">调用期间必须保持有效的独立参考图像租约。</param>
    /// <param name = "binarization">该参考类别采用的二值化模式。</param>
    public GlyphTemplate(IImageSource image, EGlyphBinarization binarization)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        Binarization = binarization;
    }

    /// <summary>借用的参考像素，不由此对象释放。</summary>
    public IImageSource Image { get; }

    /// <summary>参考配置指定的二值化模式。</summary>
    public EGlyphBinarization Binarization { get; }
}
