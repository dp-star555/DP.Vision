namespace DP.Vision.Algorithms;

/// <summary>实际码内容与几何；解码成功不代表外观合格。</summary>
public sealed class BarcodeObservation
{
    /// <summary>创建不可变的解码证据。</summary>
    /// <param name = "text">原始解码内容，不用预期值修正。</param>
    /// <param name = "format">解码得到的码制名称。</param>
    /// <param name = "bounds">选定的原图像素范围。</param>
    /// <param name = "moduleGrid">可选的实测QR网格，不能作为独立标准真值。</param>
    public BarcodeObservation(
        string text,
        string format,
        PixelBounds bounds,
        BarcodeModuleGrid? moduleGrid = null
    )
    {
        Text = text;
        Format = format;
        Bounds = bounds;
        ModuleGrid = moduleGrid;
    }

    /// <summary>实际解码内容。</summary>
    public string Text { get; }

    /// <summary>解码得到的码制。</summary>
    public string Format { get; }

    /// <summary>原图中的选定范围。</summary>
    public PixelBounds Bounds { get; }

    /// <summary>实测而非纠错后的QR结构。</summary>
    public BarcodeModuleGrid? ModuleGrid { get; }
}
