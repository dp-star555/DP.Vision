using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>在参考图的一块固定内容上估计待检图的小平移（整图同一平移），用于固定相机下的ROI随动。</summary>
public interface ITranslationRegistrar
{
    /// <summary>在<paramref name = "bounds"/>内配准；掩码外（例如忽略区）的像素不参与。</summary>
    /// <param name = "image">借用的待检图。</param>
    /// <param name = "reference">借用的参考图。</param>
    /// <param name = "bounds">参与配准的原图范围，须同时位于两图内。</param>
    /// <param name = "mask">原图坐标的有效像素掩码（通常由<see cref = "InspectionMask.Compose"/>生成）；null时范围内全部有效。</param>
    /// <param name = "options">可信条件；null时使用默认值。</param>
    /// <param name = "token">协作式取消标记。</param>
    TranslationRegistrationResult Register(
        IImageSource image,
        IImageSource reference,
        PixelBounds bounds,
        RegionGeometry? mask = null,
        TranslationRegistrationOptions? options = null,
        CancellationToken token = default
    );
}
