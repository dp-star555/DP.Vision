using System;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>采集模式与外部回调缓冲公共模型的契约回归（实施基线 ACQUISITION_RUNTIME_V1 §V1-A）。</summary>
[TestClass]
public sealed class AcquisitionModeContractTests
{
    /// <summary>两种采集模式的整数值是显式契约，不随重构漂移。</summary>
    [TestMethod]
    public void AcquisitionMode_IntegerValuesAreStable()
    {
        // 逐项对照而不是直接比较两个常量：分析器会把编译期可判定的常量比较判为恒真（MSTEST0032），
        // 而这里要锁的恰恰是"值没有漂移"，不能因为分析器不理解就删掉。
        var expected = new[]
        {
            (Mode: EVisionAcquisitionMode.OnDemand, Value: 0),
            (Mode: EVisionAcquisitionMode.BufferedExternal, Value: 1),
        };

        foreach (var (mode, value) in expected)
            Assert.AreEqual(value, (int)mode, mode.ToString());
    }

    /// <summary>有界策略拒绝非正的容量、字节预算与帧龄。</summary>
    [TestMethod]
    public void InboxPolicy_RejectsNonPositiveBounds()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new VisionFrameInboxPolicy(0, 1024, TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new VisionFrameInboxPolicy(-1, 1024, TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new VisionFrameInboxPolicy(4, 0, TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new VisionFrameInboxPolicy(4, -8, TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new VisionFrameInboxPolicy(4, 1024, TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new VisionFrameInboxPolicy(4, 1024, TimeSpan.FromMilliseconds(-1)));
    }

    /// <summary>有界策略完整保留三个上界。</summary>
    [TestMethod]
    public void InboxPolicy_PreservesBounds()
    {
        var policy = new VisionFrameInboxPolicy(6, 4096, TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(6, policy.Capacity);
        Assert.AreEqual(4096L, policy.ByteBudget);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), policy.MaximumFrameAge);
    }

    /// <summary>外部回调缓冲Source必须声明有界策略；缺失时无法建立队列，拒绝发布。</summary>
    [TestMethod]
    public void SourceBinding_BufferedExternalRequiresInboxPolicy()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new VisionAcquisitionSourceBinding(
            "Camera.Top",
            "dp.fake",
            "top",
            "camera:serial:A",
            EVisionSourceSharingPolicy.ExclusiveRun,
            EVisionAcquisitionMode.BufferedExternal));
    }

    /// <summary>主动单次采集Source不接受待领取队列策略——多半是漏配了模式。</summary>
    [TestMethod]
    public void SourceBinding_OnDemandRejectsInboxPolicy()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new VisionAcquisitionSourceBinding(
            "Camera.Top",
            "dp.fake",
            "top",
            "camera:serial:A",
            EVisionSourceSharingPolicy.ExclusiveOperation,
            EVisionAcquisitionMode.OnDemand,
            new VisionFrameInboxPolicy(4, 1024, TimeSpan.FromSeconds(1))));
    }

    /// <summary>既有5参数调用点行为不变：默认仍是主动单次采集且没有队列策略。</summary>
    [TestMethod]
    public void SourceBinding_FiveArgumentFormRemainsOnDemand()
    {
        var binding = new VisionAcquisitionSourceBinding(
            "Camera.Top", "dp.fake", "top", "camera:serial:A");

        Assert.AreEqual(EVisionAcquisitionMode.OnDemand, binding.AcquisitionMode);
        Assert.IsNull(binding.InboxPolicy);
        Assert.AreEqual(EVisionSourceSharingPolicy.ExclusiveOperation, binding.SharingPolicy);
    }

    /// <summary>外部回调缓冲Source保留模式与有界策略。</summary>
    [TestMethod]
    public void SourceBinding_BufferedExternalPreservesPolicy()
    {
        var policy = new VisionFrameInboxPolicy(3, 2048, TimeSpan.FromSeconds(2));
        var binding = new VisionAcquisitionSourceBinding(
            "Camera.Top",
            "dp.fake",
            "top",
            "camera:serial:A",
            EVisionSourceSharingPolicy.ExclusiveRun,
            EVisionAcquisitionMode.BufferedExternal,
            policy);

        Assert.AreEqual(EVisionAcquisitionMode.BufferedExternal, binding.AcquisitionMode);
        Assert.AreSame(policy, binding.InboxPolicy);
    }

    /// <summary>
    /// 外部回调缓冲来源必须带接收序号与接收时刻；缺失说明该帧不是从有界队列领取的。
    /// <para>
    /// 必须断言失败<em>原因</em>：只断言"抛 ArgumentException"是不够的——本类型还有另一条
    /// "主动采集不得带接收字段"的分支，它会用同样的异常类型兜住缺失场景，让用例假绿。
    /// </para>
    /// </summary>
    [TestMethod]
    public void CaptureMetadata_BufferedExternalRequiresReceivedFacts()
    {
        var capturedAt = DateTimeOffset.UtcNow;

        var missingSequence = Assert.ThrowsExactly<ArgumentException>(() => new VisionCaptureMetadata(
            "cap-1", "Camera.Top", "dp.fake", "camera:serial:A", capturedAt, 7,
            EVisionAcquisitionMode.BufferedExternal, null, capturedAt));
        StringAssert.Contains(missingSequence.Message, "接收序号");
        Assert.AreEqual("receivedSequence", missingSequence.ParamName);

        var missingReceivedAt = Assert.ThrowsExactly<ArgumentException>(() => new VisionCaptureMetadata(
            "cap-1", "Camera.Top", "dp.fake", "camera:serial:A", capturedAt, 7,
            EVisionAcquisitionMode.BufferedExternal, 42, null));
        StringAssert.Contains(missingReceivedAt.Message, "接收时刻");
        Assert.AreEqual("receivedAtUtc", missingReceivedAt.ParamName);

        // 两个字段齐备时必须成功，否则上面的"缺失即失败"无法区分"校验"与"一律拒绝"。
        var complete = new VisionCaptureMetadata(
            "cap-1", "Camera.Top", "dp.fake", "camera:serial:A", capturedAt, 7,
            EVisionAcquisitionMode.BufferedExternal, 42, capturedAt);
        Assert.AreEqual(42L, complete.ReceivedSequence);
    }

    /// <summary>主动单次采集不应带接收序号或接收时刻；该序号只属于回调队列。</summary>
    [TestMethod]
    public void CaptureMetadata_OnDemandRejectsReceivedFacts()
    {
        var capturedAt = DateTimeOffset.UtcNow;

        Assert.ThrowsExactly<ArgumentException>(() => new VisionCaptureMetadata(
            "cap-1", "Camera.Top", "dp.fake", "camera:serial:A", capturedAt, 7,
            EVisionAcquisitionMode.OnDemand, 42, capturedAt));
    }

    /// <summary>既有6参数调用点行为不变：默认是主动单次采集，接收字段为空。</summary>
    [TestMethod]
    public void CaptureMetadata_SixArgumentFormRemainsOnDemand()
    {
        var metadata = new VisionCaptureMetadata(
            "cap-1", "Camera.Top", "dp.fake", "camera:serial:A", DateTimeOffset.UtcNow, 14582);

        Assert.AreEqual(EVisionAcquisitionMode.OnDemand, metadata.AcquisitionMode);
        Assert.IsNull(metadata.ReceivedSequence);
        Assert.IsNull(metadata.ReceivedAtUtc);
    }

    /// <summary>外部回调缓冲来源完整保留接收序号与接收时刻，且不冒充设备序号。</summary>
    [TestMethod]
    public void CaptureMetadata_BufferedExternalPreservesReceivedFacts()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var receivedAt = capturedAt.AddMilliseconds(3);
        var metadata = new VisionCaptureMetadata(
            "cap-1", "Camera.Top", "dp.fake", "camera:serial:A", capturedAt, 7,
            EVisionAcquisitionMode.BufferedExternal, 42, receivedAt);

        Assert.AreEqual(EVisionAcquisitionMode.BufferedExternal, metadata.AcquisitionMode);
        Assert.AreEqual(42L, metadata.ReceivedSequence);
        Assert.AreEqual(receivedAt, metadata.ReceivedAtUtc);
        // 接收序号是Runtime内部序号，不能顶替设备自己的帧序号。
        Assert.AreEqual(7L, metadata.DeviceSequence);
    }
}
