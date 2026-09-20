using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Tests;

/// <summary>只通过统一源验证图块读取、独立租约和后台任务的释放责任，不访问内部缓冲区。</summary>
[TestClass]
public sealed class ImageSourceTests
{
    /// <summary>分级边缘图块返回统一源；原图释放后图块仍有效。</summary>
    [TestMethod]
    public void TilesAreIndependentSources()
    {
        var source = VisionImage.CopyFrom(
            new ImageInfo(33, 33, EPixelLayout.Gray8),
            Enumerable.Range(0, 33 * 33).Select(i => (byte)(i % 251)).ToArray()
        );
        using var tile = source.ReadTile(1, 1, 1, 16);
        source.Dispose();
        var bytes = new byte[1];
        tile.CopyTo(0, bytes, 0, 1);
        Assert.AreEqual((byte)((32 * 33 + 32) % 251), bytes[0]);
        Assert.AreEqual(1, tile.Info.Width);
        Assert.AreEqual(1, tile.Info.Height);
        Assert.ThrowsExactly<ObjectDisposedException>(() => source.ReadTile(0, 0, 0, 16));
    }

    /// <summary>后台尚未完成时，客户可释放自己的源；显示和处理租约全部结束后才允许回池。</summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task DisplayAndProcessingOwnIndependentLeases()
    {
        using var pool = new FrameBufferPool(new ImageInfo(1, 1, EPixelLayout.Gray8), 1, 1);
        Assert.IsTrue(pool.TryRent(out var rented));
        using var writer = rented!;
        writer.Write(0, new byte[] { 42 }, 0, 1);
        var source = writer.Publish();
        using var display = new CanvasFrame("shared", 1, source);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = ImageProcessing.RunAsync(
            source,
            async (lease, token) =>
            {
                await gate.Task.ConfigureAwait(false);
                var bytes = new byte[1];
                lease.CopyTo(0, bytes, 0, 1);
                return bytes[0];
            }
        );
        try
        {
            source.Dispose();
            Assert.IsFalse(pool.TryRent(out _));
            display.Dispose();
            Assert.IsFalse(pool.TryRent(out _));
        }
        finally
        {
            source.Dispose();
            gate.TrySetResult(true);
        }
        Assert.AreEqual((byte)42, await work);
        Assert.IsTrue(pool.TryRent(out var again));
        again!.Dispose();
    }

    /// <summary>处理失败或处理中取消都释放内部源，但不会提前归还仍在回调中读取的槽位。</summary>
    /// <param name="cancel">是否以协作取消结束，否则模拟算法异常。</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(10000)]
    public async Task FailureAndCancellationReturnPoolSlot(bool cancel)
    {
        using var pool = new FrameBufferPool(new ImageInfo(1, 1, EPixelLayout.Gray8), 1, 1);
        pool.TryRent(out var rented);
        using var writer = rented!;
        var source = writer.Publish();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = ImageProcessing.RunAsync<int>(
            source,
            async (lease, token) =>
            {
                entered.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("算法失败测试");
            },
            cancellation.Token
        );
        try
        {
            source.Dispose();
            await entered.Task;
            if (cancel)
                cancellation.Cancel();
            Assert.IsFalse(pool.TryRent(out _));
        }
        finally
        {
            source.Dispose();
            gate.TrySetResult(true);
        }
        if (cancel)
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await work);
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await work);
        Assert.IsTrue(pool.TryRent(out var again));
        again!.Dispose();
    }

    /// <summary>任务排队后立即取消也必须执行内部清理，不能因为Task.Run跳过委托而泄漏租约。</summary>
    [TestMethod]
    [Timeout(10000)]
    public async Task ImmediateCancellationNeverLeaks()
    {
        using var pool = new FrameBufferPool(new ImageInfo(1, 1, EPixelLayout.Gray8), 1, 1);
        for (int i = 0; i < 100; i++)
        {
            Assert.IsTrue(pool.TryRent(out var rented));
            using var writer = rented!;
            var source = writer.Publish();
            using var cancellation = new CancellationTokenSource();
            var work = ImageProcessing.RunAsync(
                source,
                async (lease, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return 0;
                },
                cancellation.Token
            );
            source.Dispose();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await work);
        }
        Assert.IsTrue(pool.TryRent(out var last));
        last!.Dispose();
    }

    /// <summary>客户不再能直接创建或使用内部缓冲区实现。</summary>
    [TestMethod]
    public void StorageImplementationsAreNotExported()
    {
        var names = typeof(VisionImage).Assembly.GetExportedTypes().Select(t => t.Name).ToArray();
        CollectionAssert.DoesNotContain(names, "ImageBuffer");
        CollectionAssert.DoesNotContain(names, "MemoryImageSource");
    }
}
