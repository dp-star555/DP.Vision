using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>一次采集帧的像素落地方式：中立布局、交给pylon转换的目标格式，以及是否需要转换。</summary>
/// <param name="Layout">中立像素布局。</param>
/// <param name="TargetPixelFormat">转换目标格式名，取值来自 <c>Basler.Pylon.PixelType</c> 的枚举成员名。</param>
/// <param name="RequiresConversion">为真表示需要经过pylon像素转换，而不是直接复制。</param>
public readonly struct BaslerPixelConversion(
    EPixelLayout Layout,
    string TargetPixelFormat,
    bool RequiresConversion)
{
    /// <summary>中立像素布局。</summary>
    public EPixelLayout Layout { get; } = Layout;

    /// <summary>转换目标格式名。</summary>
    public string TargetPixelFormat { get; } = TargetPixelFormat;

    /// <summary>是否需要经过pylon像素转换。</summary>
    public bool RequiresConversion { get; } = RequiresConversion;
}

/// <summary>
/// 把pylon像素格式映射到中立布局的显式规则表。
///
/// 这里刻意按格式名（而不是厂商枚举）做判断，使该映射成为不依赖SDK的纯逻辑，可以在没有相机、
/// 甚至没有pylon运行时的机器上被完整验证。映射之外的格式一律拒绝，不做"猜一个最接近的格式"，
/// 因为静默的位深或通道语义改变会让下游算法结果无法解释。
/// </summary>
public static class BaslerPixelFormats
{
    /// <summary>pylon 的 Mono8 格式名。</summary>
    public const string Mono8 = "Mono8";

    /// <summary>pylon 的 Mono16 格式名。</summary>
    public const string Mono16 = "Mono16";

    /// <summary>pylon 的 BGR8packed 格式名。</summary>
    public const string Bgr8Packed = "BGR8packed";

    /// <summary>pylon 的 RGB8packed 格式名。</summary>
    public const string Rgb8Packed = "RGB8packed";

    /// <summary>解析一次采集帧的像素落地方式。</summary>
    /// <param name="sourcePixelFormat">设备报告的像素格式名，通常取 <c>IGrabResult.PixelTypeValue.ToString()</c>。</param>
    /// <returns>像素落地方式。</returns>
    /// <exception cref="VisionDataException">格式为空或不在显式支持范围内。</exception>
    public static BaslerPixelConversion Resolve(string sourcePixelFormat)
    {
        if (string.IsNullOrWhiteSpace(sourcePixelFormat))
            throw new VisionDataException("Basler 采集帧没有报告像素格式，无法确定中立布局。");

        var name = sourcePixelFormat.Trim();

        // 四种可以直接复制的格式：不做任何转换，字节语义与设备完全一致。
        switch (name)
        {
            case Mono8:
                return new BaslerPixelConversion(EPixelLayout.Gray8, Mono8, RequiresConversion: false);
            case Mono16:
                return new BaslerPixelConversion(EPixelLayout.Gray16, Mono16, RequiresConversion: false);
            case Bgr8Packed:
                return new BaslerPixelConversion(EPixelLayout.Bgr24, Bgr8Packed, RequiresConversion: false);
            case Rgb8Packed:
                return new BaslerPixelConversion(EPixelLayout.Rgb24, Rgb8Packed, RequiresConversion: false);
        }

        // 单色族：一律转成8位或16位灰度，不把单色通道复制成三通道。
        if (name.StartsWith("Mono", StringComparison.Ordinal))
        {
            var wide = name.Contains("10") || name.Contains("12") || name.Contains("16");
            return wide
                ? new BaslerPixelConversion(EPixelLayout.Gray16, Mono16, RequiresConversion: true)
                : new BaslerPixelConversion(EPixelLayout.Gray8, Mono8, RequiresConversion: true);
        }

        // 彩色族：只接受8位深度的输入，转成BGR8packed。更高位深会丢动态范围，明确拒绝。
        if (IsEightBitColor(name))
            return new BaslerPixelConversion(EPixelLayout.Bgr24, Bgr8Packed, RequiresConversion: true);

        throw new VisionDataException(
            $"Basler 采集帧的像素格式 {name} 不在本Provider的显式支持范围内"
            + $"（直接支持 {Mono8}/{Mono16}/{Bgr8Packed}/{Rgb8Packed}；"
            + "转换支持 Mono 族与8位深度彩色族）。"
            + "请把相机的 PixelFormat 设为上述格式之一，而不是依赖隐式转换。");
    }

    /// <summary>判断是否为8位深度的彩色格式名。</summary>
    /// <param name="name">像素格式名。</param>
    /// <returns>属于8位彩色族时返回 <see langword="true"/>。</returns>
    private static bool IsEightBitColor(string name)
    {
        var colorFamily = name.StartsWith("Bayer", StringComparison.Ordinal)
            || name.StartsWith("RGB", StringComparison.Ordinal)
            || name.StartsWith("BGR", StringComparison.Ordinal)
            || name.StartsWith("YCbCr", StringComparison.Ordinal)
            || name.StartsWith("BiColor", StringComparison.Ordinal);
        if (!colorFamily)
            return false;
        // 位深按名字里的数字判断：8位可以无损落到Bgr24，10/12/16位会丢动态范围。
        return !name.Contains("10") && !name.Contains("12") && !name.Contains("16");
    }
}
