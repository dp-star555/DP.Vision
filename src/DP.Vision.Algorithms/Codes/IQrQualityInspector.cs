namespace DP.Vision.Algorithms;

/// <summary>可独立于一维码算法替换的QR质量接口。</summary>
[VisionCapability("code.qr-quality", "质量检查", "QR印刷检查")]
public interface IQrQualityInspector : IBarcodeQualityInspector { }
