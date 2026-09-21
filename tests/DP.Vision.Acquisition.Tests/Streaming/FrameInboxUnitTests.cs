using System;
using DP.Vision;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 有界待领取队列的白盒单测。
/// <para>
/// Epoch 过滤与超龄释放无法只从公共 API 到达：会话退役时会清空队列，
/// 因此"旧代次帧被丢弃"这条路径只能直接测队列本身。
/// </para>
/// <para>
/// 队列是内部实现，本文件依赖 <c>InternalsVisibleTo</c>；它只对采集测试程序集开放。
/// </para>
/// </summary>
[TestClass]
public sealed class FrameInboxUnitTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>帧按到达顺序领取，且领取即转移所有权。</summary>
    [TestMethod]
    public void Claim_ReturnsFramesInArrivalOrder()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(Policy());

        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 11), 1, Now, out var first, out var accepted));
        Assert.IsNull(accepted, "被接受的帧不应带溢出原因。");
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 12), 1, Now, out var second, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 13), 1, Now, out var third, out _));
        Assert.AreEqual(1, first);
        Assert.AreEqual(2, second);
        Assert.AreEqual(3, third);

        for (var index = 0; index < 3; index++)
        {
            var claim = inbox.TryClaim(Now, 1);
            Assert.IsNotNull(claim.Entry);
            Assert.AreEqual(11L + index, claim.Entry!.DeviceSequence);

            // 领取后所有权归调用方；队列不得再持有或释放它。
            claim.Entry.Frame.Dispose();
        }

        Assert.AreEqual(3, inbox.ClaimedCount);
        Assert.AreEqual(0, inbox.Count);
        Assert.AreEqual(0, inbox.Bytes);
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>空队列领取返回"无可用帧"，不抛异常、不消耗计数。</summary>
    [TestMethod]
    public void Claim_OnEmptyInboxReturnsNoEntry()
    {
        var inbox = new VisionFrameInbox(Policy());

        var claim = inbox.TryClaim(Now, 1);

        Assert.IsNull(claim.Entry);
        Assert.AreEqual(0, claim.ExpiredCount);
        Assert.AreEqual(0, claim.StaleEpochCount);
        Assert.AreEqual(0, inbox.ClaimedCount);
        Assert.AreEqual(0, inbox.ReceivedCount);
    }

    /// <summary>达到容量上限时拒绝新帧，并在入队路径上立即释放被拒绝的帧。</summary>
    [TestMethod]
    public void Enqueue_RejectsWhenCapacityReached()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(new VisionFrameInboxPolicy(capacity: 2, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1)));

        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out _));
        Assert.IsFalse(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out var reason));

        Assert.IsNotNull(reason);
        StringAssert.Contains(reason!, "帧数 2/2", "溢出诊断必须给出当前占用与上限，否则现场无法判断该调容量还是调消费端。");
        Assert.AreEqual(2, inbox.Count);
        Assert.AreEqual(1, inbox.RejectedCount);
        Assert.AreEqual(1, counter.Disposes, "被拒绝的帧必须在入队路径上释放，不能留给调用方。");

        // 均衡等式的前提是没有任何未释放句柄：队列里那两帧仍被正常持有，先排空再断言。
        foreach (var entry in inbox.Drain())
            entry.Frame.Dispose();
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>字节预算按逻辑像素字节计，超预算同样拒绝。</summary>
    [TestMethod]
    public void Enqueue_RejectsWhenByteBudgetExceeded()
    {
        var counter = new DisposalCounter();
        // 每帧 Gray8 2x2 = 4 字节；预算 8 字节只够两帧，容量故意给足以隔离字节维度。
        var inbox = new VisionFrameInbox(new VisionFrameInboxPolicy(capacity: 16, byteBudget: 8, maximumFrameAge: TimeSpan.FromMinutes(1)));

        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out _));
        Assert.IsFalse(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out var reason));

        Assert.IsNotNull(reason);
        StringAssert.Contains(reason!, "字节");
        Assert.AreEqual(8, inbox.Bytes);
        Assert.AreEqual(2, inbox.Count);
        Assert.AreEqual(1, inbox.RejectedCount);
        Assert.AreEqual(1, counter.Disposes);

        // 同容量用例：队列仍正常持有两帧，排空后才满足均衡等式。
        foreach (var entry in inbox.Drain())
            entry.Frame.Dispose();
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>超龄帧在领取尝试中被释放并计数，而不是成功返回。</summary>
    [TestMethod]
    public void Claim_DropsExpiredFramesInsteadOfReturningThem()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(new VisionFrameInboxPolicy(capacity: 4, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMilliseconds(50)));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out _));

        var claim = inbox.TryClaim(Now.AddMilliseconds(80), 1);

        Assert.IsNull(claim.Entry, "超龄帧不得被返回。");
        Assert.AreEqual(1, claim.ExpiredCount);
        Assert.AreEqual(1, inbox.ExpiredCount);
        Assert.AreEqual(0, inbox.Count);
        Assert.AreEqual(0, inbox.ClaimedCount);
        Assert.AreEqual(1, counter.Disposes);
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>其他代次的帧一律不领取，并在扫描中释放。</summary>
    [TestMethod]
    public void Claim_DoesNotReturnFramesFromAnotherEpoch()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(Policy());
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 1), 1, Now, out _, out _));

        var claim = inbox.TryClaim(Now, 2);

        Assert.IsNull(claim.Entry, "上一轮运行的帧不得被新一轮领取。");
        Assert.AreEqual(1, claim.StaleEpochCount);
        Assert.AreEqual(1, inbox.StaleEpochCount);
        Assert.AreEqual(0, inbox.Count);
        Assert.AreEqual(1, counter.Disposes);
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>
    /// 兜底路径：即使退役没来得及清空队列，旧代次的帧也不得被新一轮领取。
    /// <para>
    /// 端到端用例走的是"退役即清空"这条主路径，因此本用例不调用清退，
    /// 直接构造"队列里同时留着旧代次与新代次帧"来锁住代次过滤这道独立防线。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Claim_RejectsStaleEpochEvenWhenInboxWasNotDrained()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(Policy());
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 1), 1, Now, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 2), 2, Now, out _, out _));

        var claim = inbox.TryClaim(Now, 2);

        Assert.IsNotNull(claim.Entry, "当前代次的帧必须可领取。");
        Assert.AreEqual(2L, claim.Entry!.DeviceSequence, "旧代次帧必须被跳过，而不是按到达顺序先返回。");
        Assert.AreEqual(1, claim.StaleEpochCount, "被跳过的旧代次帧必须被释放并计数。");
        Assert.AreEqual(0, inbox.Count, "旧代次帧不得留在队列里等着下一轮再被跳过。");
        Assert.AreEqual(1, counter.Disposes);

        claim.Entry.Frame.Dispose();
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>清退非当前代次时保留当前代次，且不改变相对顺序。</summary>
    [TestMethod]
    public void DropStaleEpochs_KeepsOnlyCurrentEpoch()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(new VisionFrameInboxPolicy(capacity: 8, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1)));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 1), 1, Now, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 2), 1, Now, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 3), 2, Now, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 4), 2, Now, out _, out _));

        var dropped = inbox.DropStaleEpochs(2);

        Assert.AreEqual(2, dropped);
        Assert.AreEqual(2, inbox.Count);
        Assert.AreEqual(2, inbox.StaleEpochCount);
        Assert.AreEqual(2, counter.Disposes);

        var first = inbox.TryClaim(Now, 2);
        var second = inbox.TryClaim(Now, 2);
        Assert.AreEqual(3L, first.Entry!.DeviceSequence, "清退后必须保持当前代次的相对顺序。");
        Assert.AreEqual(4L, second.Entry!.DeviceSequence);
        first.Entry.Frame.Dispose();
        second.Entry.Frame.Dispose();

        // 队列与调用方都不再持有句柄后，等式才成立。
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>Drain 只转移所有权：队列不得释放后还给调用方一个已失效句柄。</summary>
    [TestMethod]
    public void Drain_ReturnsAllEntriesWithoutReleasingThem()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(Policy());
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 1), 1, Now, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 2), 1, Now, out _, out _));

        var drained = inbox.Drain();

        Assert.AreEqual(2, drained.Count);
        Assert.AreEqual(0, inbox.Count);
        Assert.AreEqual(0, inbox.Bytes);
        Assert.AreEqual(2, inbox.DrainedCount);
        Assert.AreEqual(0, counter.Disposes, "Drain 返回的句柄仍归调用方，队列不能替调用方释放。");

        foreach (var entry in drained)
            entry.Frame.Dispose();
        Assert.AreEqual(2, counter.Disposes);
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>被拒绝的帧同样分配接收序号，否则现场无法定位丢弃发生在哪一帧。</summary>
    [TestMethod]
    public void Enqueue_AssignsMonotonicSequenceEvenWhenRejected()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(new VisionFrameInboxPolicy(capacity: 1, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1)));

        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out var first, out _));
        Assert.IsFalse(inbox.TryEnqueue(Frame(counter), 1, Now, out var second, out _));

        Assert.AreEqual(1, first);
        Assert.AreEqual(2, second);
        Assert.AreEqual(2, inbox.ReceivedCount);
        Assert.AreEqual(1, inbox.RejectedCount);
    }

    /// <summary>高水位记录峰值占用，供容量调优判断，而不是当前值。</summary>
    [TestMethod]
    public void HighWatermarks_TrackPeakOccupancy()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(Policy());
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out _));

        var claim = inbox.TryClaim(Now, 1);
        claim.Entry!.Frame.Dispose();
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), 1, Now, out _, out _));

        Assert.AreEqual(2, inbox.Count);
        Assert.AreEqual(2, inbox.HighWatermark, "峰值不因领取而回落。");
        Assert.AreEqual(8, inbox.Bytes);
        Assert.AreEqual(8, inbox.BytesHighWatermark);
    }

    private static VisionFrameInboxPolicy Policy() =>
        new VisionFrameInboxPolicy(capacity: 8, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1));

    private static VisionProviderFrame Frame(DisposalCounter counter, long? deviceSequence = null) =>
        new VisionProviderFrame(new TrackingImageSource(counter), Now, deviceSequence);
}
