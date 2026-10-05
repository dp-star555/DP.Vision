using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>
/// 模板参考：参考原点和X轴方向在模板像素边界坐标中的位置，以及标识参考内容的签名。
/// 匹配结果用它算出本帧的参考点和参考方向；坐标系构建用签名识别模板是否变化。
/// </summary>
public sealed class TemplateReference
{
    /// <summary>创建参考。</summary>
    /// <param name="originX">参考原点X，模板像素边界坐标。</param>
    /// <param name="originY">参考原点Y，模板像素边界坐标。</param>
    /// <param name="axisAngleRadians">参考X轴相对模板X轴的顺时针弧度。</param>
    /// <param name="signature">参考内容签名；原点、方向或模板内容变化时必须不同。</param>
    public TemplateReference(double originX, double originY, double axisAngleRadians, string signature)
    {
        if (!Finite(originX) || !Finite(originY) || !Finite(axisAngleRadians) || string.IsNullOrWhiteSpace(signature))
            throw new ArgumentException("模板参考的原点、方向必须有限，签名不能为空。");
        OriginX = originX; OriginY = originY; AxisAngleRadians = axisAngleRadians; Signature = signature;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>参考原点X，模板像素边界坐标。</summary>
    public double OriginX { get; }
    /// <summary>参考原点Y，模板像素边界坐标。</summary>
    public double OriginY { get; }
    /// <summary>参考X轴相对模板X轴的顺时针弧度。</summary>
    public double AxisAngleRadians { get; }
    /// <summary>参考内容签名。</summary>
    public string Signature { get; }

    /// <summary>把业务坐标定义和本参考绑定：签名随参考变化，下游按旧参考制作的ROI会被拒绝。</summary>
    /// <param name="definition">业务坐标定义。</param>
    /// <returns>原点约定附加了参考签名的定义。</returns>
    public VisionCoordinateDefinition Bind(VisionCoordinateDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        return new VisionCoordinateDefinition(definition.Id, definition.Name, definition.Version, definition.Unit,
            definition.OriginDescription + "｜模板参考 " + Signature, definition.AxisDescription);
    }

    /// <summary>以图像模板的一块矩形为模板时的参考：原点在矩形中心，X轴沿模板X轴，签名来自矩形内像素。</summary>
    /// <param name="image">模板图像。</param>
    /// <param name="bounds">模板矩形。</param>
    /// <param name="token">取消。</param>
    /// <returns>参考。</returns>
    public static TemplateReference FromImage(IImageSource image, PixelBounds bounds, CancellationToken token = default)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        if (!bounds.Fits(image)) throw new ArgumentOutOfRangeException(nameof(bounds));
        if ((long)bounds.Width * bounds.Height * image.Info.BytesPerPixel > 64L * 1024 * 1024) throw new ArgumentException("模板签名超出64MiB预算。");
        using var hash = SHA256.Create();
        using var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, Encoding.UTF8, true))
        { writer.Write(bounds.Width); writer.Write(bounds.Height); writer.Write((int)image.Info.Layout); }
        var bytes = header.ToArray(); hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
        var row = new byte[bounds.Width * image.Info.BytesPerPixel];
        for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            image.CopyTo(y * image.Info.Stride + bounds.X * image.Info.BytesPerPixel, row, 0, row.Length);
            hash.TransformBlock(row, 0, row.Length, row, 0);
        }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return new TemplateReference(bounds.Width / 2d, bounds.Height / 2d, 0, "image:" + BitConverter.ToString(hash.Hash!).Replace("-", ""));
    }
}
