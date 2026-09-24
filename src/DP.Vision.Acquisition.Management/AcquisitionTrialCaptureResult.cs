using System;

namespace DP.Vision.Acquisition.Management;

/// <summary>
/// 一次试拍的结果；成功与失败都用同一个结果类型表达。
/// <para>
/// 试拍是诊断动作而不是业务流程：配置错误或设备不可用应当变成界面上看得见的原因，
/// 因此除取消外的任何失败都返回本结果，不向界面抛采集异常。
/// </para>
/// </summary>
public sealed class AcquisitionTrialCaptureResult
{
    /// <summary>创建试拍结果。</summary>
    /// <param name="sourceId">试拍的目标逻辑源标识。</param>
    /// <param name="succeeded">是否成功取到一帧。</param>
    /// <param name="duration">从进入试拍到返回结果（含等待设备与像素落地）的耗时。</param>
    /// <param name="captureId">成功时的采集身份；失败时为空。</param>
    /// <param name="providerId">成功时实际完成采集的Provider身份；失败时为空。</param>
    /// <param name="resourceKey">成功时占用的物理资源键；失败时为空。</param>
    /// <param name="capturedAtUtc">成功时设备报告或运行时观测的采集时刻；失败时为空。</param>
    /// <param name="deviceSequence">成功时设备可选提供的帧序号；不支持时为空。</param>
    /// <param name="imageWidth">成功时的图像宽度（像素）；失败时为空。</param>
    /// <param name="imageHeight">成功时的图像高度（像素）；失败时为空。</param>
    /// <param name="pixelLayout">成功时的像素布局；失败时为空。</param>
    /// <param name="imageByteLength">成功时的图像字节数；失败时为空。</param>
    /// <param name="failureKind">失败类别；成功时为空。</param>
    /// <param name="failureMessage">失败说明；成功时为空。</param>
    /// <exception cref="ArgumentException">逻辑源标识为空。</exception>
    public AcquisitionTrialCaptureResult(
        string sourceId,
        bool succeeded,
        TimeSpan duration,
        string? captureId = null,
        string? providerId = null,
        string? resourceKey = null,
        DateTimeOffset? capturedAtUtc = null,
        long? deviceSequence = null,
        int? imageWidth = null,
        int? imageHeight = null,
        EPixelLayout? pixelLayout = null,
        int? imageByteLength = null,
        string? failureKind = null,
        string? failureMessage = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("试拍目标逻辑源标识不能为空。", nameof(sourceId));

        SourceId = sourceId.Trim();
        Succeeded = succeeded;
        Duration = duration;
        CaptureId = captureId;
        ProviderId = providerId;
        ResourceKey = resourceKey;
        CapturedAtUtc = capturedAtUtc;
        DeviceSequence = deviceSequence;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        PixelLayout = pixelLayout;
        ImageByteLength = imageByteLength;
        FailureKind = failureKind;
        FailureMessage = failureMessage;
    }

    /// <summary>试拍的目标逻辑源标识。</summary>
    public string SourceId { get; }

    /// <summary>是否成功取到一帧。</summary>
    public bool Succeeded { get; }

    /// <summary>试拍耗时；失败时同样给出，用于区分"立刻失败"与"等到超时"。</summary>
    public TimeSpan Duration { get; }

    /// <summary>成功时的采集身份；失败时为空。</summary>
    public string? CaptureId { get; }

    /// <summary>成功时实际完成采集的Provider身份；失败时为空。</summary>
    public string? ProviderId { get; }

    /// <summary>成功时占用的物理资源键；失败时为空。</summary>
    public string? ResourceKey { get; }

    /// <summary>成功时设备报告或运行时观测的采集时刻；失败时为空。</summary>
    public DateTimeOffset? CapturedAtUtc { get; }

    /// <summary>成功时设备可选提供的帧序号；不支持时为空。</summary>
    public long? DeviceSequence { get; }

    /// <summary>成功时的图像宽度（像素）；失败时为空。</summary>
    public int? ImageWidth { get; }

    /// <summary>成功时的图像高度（像素）；失败时为空。</summary>
    public int? ImageHeight { get; }

    /// <summary>成功时的像素布局；失败时为空。</summary>
    public EPixelLayout? PixelLayout { get; }

    /// <summary>成功时的图像字节数；失败时为空。</summary>
    public int? ImageByteLength { get; }

    /// <summary>
    /// 失败类别：SourceConfiguration/ResourceConflict/DeviceOffline/ParameterNotSupported/CaptureTimeout/Data/
    /// ProviderUnavailable/Acquisition/Unexpected。
    /// <para>
    /// 取消不产生类别而是原样抛 <see cref="OperationCanceledException"/>；
    /// 类别是界面分组显示的键，消息才是给人看的原因，两者都必须保留。
    /// </para>
    /// </summary>
    public string? FailureKind { get; }

    /// <summary>失败说明；成功时为空。</summary>
    public string? FailureMessage { get; }
}