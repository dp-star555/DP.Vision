using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// 一次中立像素落地的观测：把设备帧复制为中立图像所花的时间与产出的逻辑像素字节数。
/// <para>
/// 它存在的理由是后续的"表示缓存"决策：中立像素到 HObject/Mat 的转换要不要缓存，
/// 取决于一次转换究竟花了多少时间、搬了多少字节。没有这两个数字，缓存只能靠猜。
/// </para>
/// </summary>
/// <param name="Duration">像素复制与布局转换的耗时。</param>
/// <param name="Bytes">本次落地产出的中立图像逻辑像素字节数。</param>
public sealed record VisionPixelTransferObservation(TimeSpan Duration, long Bytes);

/// <summary>
/// 一个逻辑源上的像素落地观测累计值；供运行监视判断转换开销是否值得缓存。
/// <para>
/// 同时保留累计值与最近一次值：累计值回答"这台相机长期搬了多少"，
/// 最近一次回答"当前这一帧多重"，两者缺一都无法判断缓存收益。
/// </para>
/// </summary>
/// <param name="Count">已完成的像素落地次数。</param>
/// <param name="BytesTotal">累计产出的逻辑像素字节数。</param>
/// <param name="DurationTotalMilliseconds">累计耗时（毫秒）。</param>
/// <param name="LastBytes">最近一次落地产出的字节数。</param>
/// <param name="LastDurationMilliseconds">最近一次落地的耗时（毫秒）。</param>
public sealed record VisionPixelTransferSummary(
    long Count = 0,
    long BytesTotal = 0,
    double DurationTotalMilliseconds = 0,
    long LastBytes = 0,
    double LastDurationMilliseconds = 0);