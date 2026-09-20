using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>确定性内存设备；可注入采集行为，默认每次返回内容不同的中立灰度帧。</summary>
internal sealed class FakeVisionDevice : IVisionAcquisitionDevice
{
    private readonly Func<VisionCaptureRequest, CancellationToken, ValueTask<VisionProviderFrame>> _capture;
    private int _captureCount;
    private int _disposeCount;

    /// <summary>创建设备。</summary>
    /// <param name="identity">设备报告身份。</param>
    /// <param name="capture">可选采集行为；为空时使用默认确定性行为。</param>
    public FakeVisionDevice(
        VisionDeviceIdentity identity,
        Func<VisionCaptureRequest, CancellationToken, ValueTask<VisionProviderFrame>>? capture = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _capture = capture ?? DefaultCapture;
    }

    /// <inheritdoc/>
    public VisionDeviceIdentity Identity { get; }

    /// <summary>已执行的采集次数。</summary>
    public int CaptureCount => Volatile.Read(ref _captureCount);

    /// <summary>已执行的释放次数。</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <inheritdoc/>
    public ValueTask<VisionProviderFrame> CaptureAsync(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        Interlocked.Increment(ref _captureCount);
        return _capture(request, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return default;
    }

    private static ValueTask<VisionProviderFrame> DefaultCapture(
        VisionCaptureRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var image = TestImages.Gray8(seed: unchecked((byte)(request.GetHashCode() & 0xFF)));
        return new ValueTask<VisionProviderFrame>(
            new VisionProviderFrame(image, DateTimeOffset.UtcNow, null));
    }
}
