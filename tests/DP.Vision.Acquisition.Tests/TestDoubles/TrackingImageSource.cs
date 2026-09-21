using System;
using System.Threading;
using DP.Vision;

namespace DP.Vision.Acquisition.Tests;

/// <summary>释放计数；用于观察真实资源是否被释放，而不是读被测对象自报的计数。</summary>
internal sealed class DisposalCounter
{
    private int _retains;
    private int _disposes;

    /// <summary><see cref="IImageSource.Retain"/> 的调用次数。</summary>
    public int Retains => Volatile.Read(ref _retains);

    /// <summary><see cref="IDisposable.Dispose"/> 的调用次数。</summary>
    public int Disposes => Volatile.Read(ref _disposes);

    /// <summary>登记一次 Retain。</summary>
    public void CountRetain() => Interlocked.Increment(ref _retains);

    /// <summary>登记一次 Dispose。</summary>
    public void CountDispose() => Interlocked.Increment(ref _disposes);
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

    /// <summary>创建追踪图像源。</summary>
    /// <param name="counter">释放计数。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    public TrackingImageSource(DisposalCounter counter, int width = 2, int height = 2)
    {
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _info = new ImageInfo(width, height, EPixelLayout.Gray8);
    }

    /// <inheritdoc/>
    public ImageInfo Info => _info;

    /// <inheritdoc/>
    public IImageSource Retain()
    {
        _counter.CountRetain();
        return new TrackingImageSource(_counter, _info.Width, _info.Height);
    }

    /// <inheritdoc/>
    public IImageSource ReadTile(int level, int tileX, int tileY, int tileSize) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count) =>
        throw new NotSupportedException();

    /// <inheritdoc/>
    public void Dispose() => _counter.CountDispose();
}
