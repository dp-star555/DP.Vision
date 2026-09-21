using System;
using System.Threading;
using DP.Vision;

namespace DP.Vision.Acquisition.Tests;

/// <summary>释放计数；用于观察真实资源是否被释放，而不是读被测对象自报的计数。</summary>
internal sealed class DisposalCounter
{
    private int _created;
    private int _retains;
    private int _disposes;

    /// <summary>由设备（或生产者）直接创建的图像句柄数；<see cref="IImageSource.Retain"/> 产生的新句柄不计入。</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary><see cref="IImageSource.Retain"/> 的调用次数。</summary>
    public int Retains => Volatile.Read(ref _retains);

    /// <summary><see cref="IDisposable.Dispose"/> 的调用次数。</summary>
    public int Disposes => Volatile.Read(ref _disposes);

    /// <summary>
    /// 每个句柄恰好释放一次的等式：创建数 + 保留数 == 释放数。
    /// <para>
    /// 只断言某一个计数值无法区分"漏释放"与"重复释放"，等式才能同时锁住两个方向。
    /// </para>
    /// <para>
    /// <b>前提：断言时不得还有任何未释放的句柄。</b>队列里待领取的帧、调用方尚未 Dispose 的
    /// <c>VisionCapturedImage</c>、<c>using var</c> 声明的局部变量（方法退出才释放）都会让等式不成立，
    /// 而那是正常的持有而不是泄漏。断言前先排空队列并显式释放被测对象。
    /// </para>
    /// </summary>
    public bool IsBalanced => Disposes == Created + Retains;

    /// <summary>登记一次创建。</summary>
    public void CountCreated() => Interlocked.Increment(ref _created);

    /// <summary>登记一次 Retain。</summary>
    public void CountRetain() => Interlocked.Increment(ref _retains);

    /// <summary>登记一次 Dispose。</summary>
    public void CountDispose() => Interlocked.Increment(ref _disposes);

    /// <inheritdoc/>
    public override string ToString() => $"创建 {Created} / 保留 {Retains} / 释放 {Disposes}";
}

/// <summary>
/// 会记录释放次数的中立图像源。
/// <para>
/// 断言"帧被释放"必须观察真实资源：被测对象自己维护的计数器即使不释放也会自增，
/// 用它做断言等于自证。
/// </para>
/// </summary>
internal sealed class TrackingImageSource : IImageSource
{
    private readonly DisposalCounter _counter;
    private readonly ImageInfo _info;

    /// <summary>创建追踪图像源；本构造函数代表"生产者新建了一帧"，计入创建数。</summary>
    /// <param name="counter">释放计数。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    public TrackingImageSource(DisposalCounter counter, int width = 2, int height = 2)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _info = new ImageInfo(width, height, EPixelLayout.Gray8);
        _counter.CountCreated();
    }

    /// <summary>由 <see cref="Retain"/> 产生的新句柄；计入保留数而不是创建数。</summary>
    private TrackingImageSource(DisposalCounter counter, ImageInfo info)
    {
        _counter = counter;
        _info = info;
        _counter.CountRetain();
    }

    /// <inheritdoc/>
    public ImageInfo Info => _info;

    /// <inheritdoc/>
    public IImageSource Retain() => new TrackingImageSource(_counter, _info);

    /// <inheritdoc/>
    public IImageSource ReadTile(int level, int tileX, int tileY, int tileSize) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public void Dispose() => _counter.CountDispose();
}
