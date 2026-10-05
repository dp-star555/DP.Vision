namespace DP.Vision.Algorithms;

/// <summary>可独立于QR质量和码读取替换的一维码质量接口。</summary>
[VisionCapability("code.linear-quality", "质量检查", "一维码印刷检查")]
public interface ILinearBarcodeQualityInspector : IBarcodeQualityInspector { }
