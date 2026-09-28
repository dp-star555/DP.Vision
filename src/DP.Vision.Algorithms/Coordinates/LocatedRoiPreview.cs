using System;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>为展示生成摆正的ROI副本；读取和质量测量仍可在原图精确掩膜上运行。</summary>
public sealed class LocatedRoiPreview : IDisposable
{
    private LocatedRoiPreview(IImageSource image, CoordinateMatrix2D previewToImage, string sourceFrameId)
    {
        Image = image;
        PreviewToImage = previewToImage;
        SourceFrameId = sourceFrameId;
    }

    /// <summary>预览对象拥有的摆正图像租约；调用方不要单独释放，需独立生命周期时调用Retain。</summary>
    public IImageSource Image { get; }
    /// <summary>预览像素边界坐标到原图像素边界坐标的映射，不能用预览坐标直接标注原图。</summary>
    public CoordinateMatrix2D PreviewToImage { get; }
    /// <summary>本次预览所对应的原图身份。</summary>
    public string SourceFrameId { get; }

    /// <summary>在局部坐标中裁取轴对齐矩形，用最近邻采样生成展示副本；不替代原图检测证据。</summary>
    /// <param name="frame">原图及其帧身份。</param>
    /// <param name="coordinates">同帧的局部到原图定位坐标系。</param>
    /// <param name="localBounds">局部整数像素边界矩形。</param>
    /// <param name="token">协作式取消。</param>
    /// <returns>调用方负责释放的摆正图像。</returns>
    public static LocatedRoiPreview Create(ImageFrame frame, LocatedCoordinateSystem coordinates,
        PixelBounds localBounds, CancellationToken token = default)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (coordinates == null) throw new ArgumentNullException(nameof(coordinates));
        coordinates.Validate(frame, coordinates.CoordinateSystemId, coordinates.TemplateSignature);
        if (localBounds.X < 0 || localBounds.Y < 0 || localBounds.Width < 1 || localBounds.Height < 1
            || (long)localBounds.X + localBounds.Width > coordinates.Pose.TemplateWidth
            || (long)localBounds.Y + localBounds.Height > coordinates.Pose.TemplateHeight)
            throw new ArgumentOutOfRangeException(nameof(localBounds));
        var matrix = coordinates.LocalToImage;
        var origin = matrix.Map(new Coordinate2D(localBounds.X, localBounds.Y));
        var previewToImage = CoordinateMatrix2D.FromAffine(matrix.M11, matrix.M12, origin.X,
            matrix.M21, matrix.M22, origin.Y);
        return new LocatedRoiPreview(AffineImageResampler.Nearest(frame.Image, previewToImage,
            localBounds.Width, localBounds.Height, token), previewToImage, frame.FrameId);
    }

    /// <summary>释放摆正预览副本。</summary>
    public void Dispose() => Image.Dispose();
}
