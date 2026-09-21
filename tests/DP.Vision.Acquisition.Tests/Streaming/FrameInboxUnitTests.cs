using System;
using DP.Vision;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 有界待领取队列的白盒单测。
/// <para>
/// V2-4 起 Epoch 属于队列内部状态（实施基线 §10.3）：<see cref="VisionFrameInbox.BeginEpoch"/> 开放
/// 本代次的接收与领取，<see cref="VisionFrameInbox.EndEpoch"/> 收口本代次（移出全部未领取帧）；
/// 队列本身不驱动任何设备动作。因此本文件可以直接测代次开合与无代次拒绝这两条路径。
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
        inbox.BeginEpoch(1);

        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 11), Now, out var first, out var reason, out var reject));
        Assert.IsNull(reason, "被接受的帧不应带拒绝原因。");
        Assert.AreEqual(VisionFrameInboxReject.None, reject);
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 12), Now, out var second, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 13), Now, out var third, out _, out _));
        Assert.AreEqual(1, first);
        Assert.AreEqual(2, second);
        Assert.AreEqual(3, third);

        for (var index = 0; index < 3; index++)
        {
            var claim = inbox.TryClaim(Now);
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
        inbox.BeginEpoch(1);

        var claim = inbox.TryClaim(Now);

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
        inbox.BeginEpoch(1);

        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out _, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out _, out _, out _));
        Assert.IsFalse(inbox.TryEnqueue(Frame(counter), Now, out _, out var reason, out var reject));

        Assert.IsNotNull(reason);
        Assert.AreEqual(VisionFrameInboxReject.Overflow, reject);
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
        inbox.BeginEpoch(1);

        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out _, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out _, out _, out _));
        Assert.IsFalse(inbox.TryEnqueue(Frame(counter), Now, out _, out var reason, out var reject));

        Assert.IsNotNull(reason);
        Assert.AreEqual(VisionFrameInboxReject.Overflow, reject);
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
        inbox.BeginEpoch(1);
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out _, out _, out _));

        var claim = inbox.TryClaim(Now.AddMilliseconds(80));

        Assert.IsNull(claim.Entry, "超龄帧不得被返回。");
        Assert.AreEqual(1, claim.ExpiredCount);
        Assert.AreEqual(1, inbox.ExpiredCount);
        Assert.AreEqual(0, inbox.Count);
        Assert.AreEqual(0, inbox.ClaimedCount);
        Assert.AreEqual(1, counter.Disposes);
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>
    /// 代次切换即清退旧代次：BeginEpoch 必须把上一代次的帧全部释放并计数，
    /// 不得留给新一轮领取，也不得让"上一轮未清空"影响新代次。
    /// </summary>
    [TestMethod]
    public void BeginEpoch_DropsPreviousEpochFrames()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(new VisionFrameInboxPolicy(capacity: 8, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1)));
        inbox.BeginEpoch(1);
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 1), Now, out _, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 2), Now, out _, out _, out _));

        var dropped = inbox.BeginEpoch(2);

        Assert.AreEqual(2, dropped);
        Assert.AreEqual(2, inbox.StaleEpochCount);
        Assert.AreEqual(0, inbox.Count, "旧代次帧不得留在队列里等新一轮领取。");
        Assert.AreEqual(2, counter.Disposes);

        // 新代次照常接收与领取，且只有新代次的帧可领取。
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 3), Now, out _, out _, out _));
        var claim = inbox.TryClaim(Now);
        Assert.IsNotNull(claim.Entry);
        Assert.AreEqual(3L, claim.Entry!.DeviceSequence);
        claim.Entry.Frame.Dispose();

        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>Drain 只转移所有权：队列不得释放后还给调用方一个已失效句柄。</summary>
    [TestMethod]
    public void Drain_ReturnsAllEntriesWithoutReleasingThem()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(Policy());
        inbox.BeginEpoch(1);
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 1), Now, out _, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 2), Now, out _, out _, out _));

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
        inbox.BeginEpoch(1);

        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out var first, out _, out _));
        Assert.IsFalse(inbox.TryEnqueue(Frame(counter), Now, out var second, out _, out _));

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
        inbox.BeginEpoch(1);
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out _, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out _, out _, out _));

        var claim = inbox.TryClaim(Now);
        claim.Entry!.Frame.Dispose();
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter), Now, out _, out _, out _));

        Assert.AreEqual(2, inbox.Count);
        Assert.AreEqual(2, inbox.HighWatermark, "峰值不因领取而回落。");
        Assert.AreEqual(8, inbox.Bytes);
        Assert.AreEqual(8, inbox.BytesHighWatermark);
    }

    /// <summary>
    /// 没有活动采集代次时，完整帧必须被拒绝并释放、单独计数，而不是滞留队列或泄漏。
    /// <para>这是 V2-4 的正常间隔隔离，不代表故障，因此不占溢出计数。</para>
    /// </summary>
    [TestMethod]
    public void Enqueue_WithoutActiveEpoch_RejectsAndCounts()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(Policy());

        Assert.IsFalse(inbox.TryEnqueue(Frame(counter), Now, out var sequence, out var reason, out var reject));

        Assert.AreEqual(VisionFrameInboxReject.NoActiveEpoch, reject);
        Assert.IsNotNull(reason);
        StringAssert.Contains(reason!, "活动采集代次");
        Assert.AreEqual(1, sequence, "被拒绝的帧同样分配接收序号，便于定位。");
        Assert.AreEqual(1, inbox.ReceivedCount);
        Assert.AreEqual(1, inbox.RejectedWithoutEpochCount);
        Assert.AreEqual(0, inbox.RejectedCount, "无代次拒绝不占用溢出计数。");
        Assert.AreEqual(0, inbox.Count);
        Assert.AreEqual(1, counter.Disposes, "无代次帧必须由队列释放，不能留给调用方。");
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>
    /// EndEpoch 移出全部未领取条目并清空活动代次；所有权转给调用方，队列不替调用方释放。
    /// <para>队列本身不停流、不关设备；那由会话在 Dispose 路径负责。</para>
    /// </summary>
    [TestMethod]
    public void EndEpoch_DrainsAllEntriesAndCountsUnclaimed()
    {
        var counter = new DisposalCounter();
        var inbox = new VisionFrameInbox(Policy());
        inbox.BeginEpoch(1);
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 1), Now, out _, out _, out _));
        Assert.IsTrue(inbox.TryEnqueue(Frame(counter, 2), Now, out _, out _, out _));

        var drained = inbox.EndEpoch();

        Assert.AreEqual(2, drained.Count);
        Assert.IsNull(inbox.ActiveEpoch, "EndEpoch 必须清空活动代次。");
        Assert.AreEqual(2, inbox.UnclaimedAtEpochEndCount);
        Assert.AreEqual(0, inbox.Count);
        Assert.AreEqual(0, inbox.Bytes);
        Assert.AreEqual(0, counter.Disposes, "EndEpoch 只转移所有权；释放由调用方完成。");

        foreach (var entry in drained)
            entry.Frame.Dispose();
        Assert.AreEqual(2, counter.Disposes);
        Assert.IsTrue(counter.IsBalanced, counter.ToString());
    }

    /// <summary>代次必须严格递增；回退或重复代次确定性拒绝，防止旧帧混入新代次。</summary>
    [TestMethod]
    public void BeginEpoch_RejectsNonIncreasing()
    {
        var inbox = new VisionFrameInbox(Policy());
        inbox.BeginEpoch(1);

        Assert.ThrowsExactly<InvalidOperationException>(() => inbox.BeginEpoch(1));
        Assert.ThrowsExactly<InvalidOperationException>(() => inbox.BeginEpoch(0));
    }

    private static VisionFrameInboxPolicy Policy() =>
        new VisionFrameInboxPolicy(capacity: 8, byteBudget: 4096, maximumFrameAge: TimeSpan.FromMinutes(1));

    private static VisionProviderFrame Frame(DisposalCounter counter, long? deviceSequence = null) =>
        new VisionProviderFrame(new TrackingImageSource(counter), Now, deviceSequence);
}
