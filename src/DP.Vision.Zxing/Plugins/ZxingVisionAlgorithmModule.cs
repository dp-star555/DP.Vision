using System;
using DP.Vision.Algorithms;

namespace DP.Vision.Zxing;

/// <summary>ZXing 读码模块，掩码是同一读码能力的附加特征。</summary>
public sealed class ZxingVisionAlgorithmModule : IVisionAlgorithmModule
{
    /// <inheritdoc/>
    public string ExtensionId => "dp.vision.zxing";
    /// <inheritdoc/>
    public void Register(IVisionAlgorithmRegistration registrations)
    {
        if (registrations == null) throw new ArgumentNullException(nameof(registrations));
        registrations.Add(new VisionAlgorithmDescriptor("zxing.code", "ZXing", "1", VisionAlgorithmFactory<IBarcodeReader>.Stateless(() => new ZxingBarcodeDecoder()), new[] { "masked" }));
    }
}
