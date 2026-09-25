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
        : this(text, format, bounds, moduleGrid, "") { }

    /// <summary>创建不可变的解码证据，并记录读取前是否做过修复预处理。</summary>
    /// <param name = "text">原始解码内容，不用预期值修正。</param>
    /// <param name = "format">解码得到的码制名称。</param>
    /// <param name = "bounds">选定的原图像素范围。</param>
    /// <param name = "moduleGrid">可选的实测QR网格，不能作为独立标准真值；已按原图坐标给出。</param>
    /// <param name = "preprocessing">空字符串表示原图直接读出；否则为读出前使用的预处理名称，说明原图可读性不足。</param>
    public BarcodeObservation(
        string text,
        string format,
        PixelBounds bounds,
        BarcodeModuleGrid? moduleGrid,
        string preprocessing
    )
    {
        Text = text;
        Format = format;
        Bounds = bounds;
        ModuleGrid = moduleGrid;
        Preprocessing = preprocessing ?? throw new System.ArgumentNullException(nameof(preprocessing));
    }

    /// <summary>实际解码内容。</summary>
    public string Text { get; }

    /// <summary>解码得到的码制。</summary>
    public string Format { get; }

    /// <summary>原图中的选定范围。</summary>
    public PixelBounds Bounds { get; }

    /// <summary>实测而非纠错后的QR结构。</summary>
    public BarcodeModuleGrid? ModuleGrid { get; }

    /// <summary>读出前使用的修复预处理；空字符串表示原图直接可读。非空时内容可信，但原图可读性余量不足。</summary>
    public string Preprocessing { get; }
}
