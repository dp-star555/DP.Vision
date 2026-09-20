using System;
using System.Linq;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>公共采集契约的值对象校验、所有权与错误诊断回归。</summary>
[TestClass]
public sealed class ValueObjectContractTests
{
    /// <summary>逻辑源身份去除首尾空白并拒绝空标识。</summary>
    [TestMethod]
    public void SourceReference_TrimsAndRejectsBlank()
    {
        Assert.AreEqual("Camera.Top", new VisionSourceReference("  Camera.Top  ").SourceId);
        Assert.ThrowsExactly<ArgumentException>(() => new VisionSourceReference("   "));
    }

    /// <summary>采集请求拒绝非正超时以及负值、NaN、无穷物理量。</summary>
    [TestMethod]
    public void CaptureRequest_ValidatesTimeoutAndPhysicalQuantities()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VisionCaptureRequest(TimeSpan.Zero));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VisionCaptureRequest(TimeSpan.FromSeconds(1), -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VisionCaptureRequest(TimeSpan.FromSeconds(1), double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new VisionCaptureRequest(TimeSpan.FromSeconds(1), null, double.PositiveInfinity));
    }

    /// <summary>可选物理量默认保持设备当前设置，而不是伪装成0。</summary>
    [TestMethod]
    public void CaptureRequest_KeepsOptionalQuantitiesUnsetByDefault()
    {
        var request = new VisionCaptureRequest(TimeSpan.FromSeconds(2));
        Assert.IsNull(request.ExposureMicroseconds);
        Assert.IsNull(request.GainDecibels);
        Assert.AreEqual(EVisionTriggerMode.KeepCurrent, request.TriggerMode);
        Assert.AreEqual(TimeSpan.FromSeconds(2), request.Timeout);
    }

    /// <summary>发起方身份拒绝空运行或操作身份。</summary>
    [TestMethod]
    public void AcquisitionOwner_RejectsBlankIdentity()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new VisionAcquisitionOwner("  ", "op"));
        Assert.ThrowsExactly<ArgumentException>(() => new VisionAcquisitionOwner("run", " "));
        var owner = new VisionAcquisitionOwner("run-1", "op-1", "   ");
        Assert.IsNull(owner.DisplayName);
    }

    /// <summary>来源事实完整保留采集身份与设备序号。</summary>
    [TestMethod]
    public void CaptureMetadata_PreservesSourceFacts()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var metadata = new VisionCaptureMetadata("cap-1", "Camera.Top", "dp.fake", "camera:serial:DA1", capturedAt, 14582);
        Assert.AreEqual("cap-1", metadata.CaptureId);
        Assert.AreEqual("Camera.Top", metadata.SourceId);
        Assert.AreEqual("dp.fake", metadata.ProviderId);
        Assert.AreEqual("camera:serial:DA1", metadata.ResourceKey);
        Assert.AreEqual(capturedAt, metadata.CapturedAtUtc);
        Assert.AreEqual(14582L, metadata.DeviceSequence);
    }

    /// <summary>Provider帧释放时释放它拥有的图像租约。</summary>
    [TestMethod]
    public void ProviderFrame_DisposesOwnedImage()
    {
        var counter = new Counter();
        var frame = new VisionProviderFrame(new TrackingImageSource(counter), DateTimeOffset.UtcNow, 7);
        Assert.AreEqual(7L, frame.DeviceSequence);
        frame.Dispose();
        Assert.AreEqual(1, counter.Disposes);
    }

    /// <summary>采集结果释放时释放它拥有的帧，进而释放图像租约。</summary>
    [TestMethod]
    public void CapturedImage_DisposesOwnedFrame()
    {
        var counter = new Counter();
        var metadata = new VisionCaptureMetadata("cap-1", "Camera.Top", "dp.fake", "camera:serial:DA1", DateTimeOffset.UtcNow, null);
        var captured = new VisionCapturedImage(new ImageFrame("cap-1", new TrackingImageSource(counter)), metadata);
        Assert.AreEqual("cap-1", captured.Frame.FrameId);
        Assert.AreEqual(1, counter.Retains);
        captured.Dispose();
        Assert.AreEqual(1, counter.Disposes);
    }

    /// <summary>资源冲突诊断必须同时给出请求方与占用方，不能只报"未找到图像"。</summary>
    [TestMethod]
    public void ResourceConflict_MessageCarriesBothParties()
    {
        var diagnostics = new VisionResourceConflictDiagnostics(
            "Camera.Top",
            "camera:serial:DA1",
            "run-1",
            "node-1",
            "run-2",
            "node-9",
            EVisionSourceSharingPolicy.ExclusiveOperation,
            "同一资源键已有持有者。");
        var exception = new VisionResourceConflictException(diagnostics);
        Assert.AreSame(diagnostics, exception.Diagnostics);
        StringAssert.Contains(exception.Message, "Camera.Top");
        StringAssert.Contains(exception.Message, "camera:serial:DA1");
        StringAssert.Contains(exception.Message, "run-2");
        StringAssert.Contains(exception.Message, "node-9");
        StringAssert.Contains(exception.Message, "ExclusiveOperation");
    }

    /// <summary>Provider不可用错误必须带ProviderId，便于定位缺失的SDK或架构。</summary>
    [TestMethod]
    public void ProviderUnavailable_MessageCarriesProviderId()
    {
        var exception = new VisionProviderUnavailableException("dp.vision.halcon", "缺少原生依赖。");
        Assert.AreEqual("dp.vision.halcon", exception.ProviderId);
        StringAssert.Contains(exception.Message, "dp.vision.halcon");
    }

    /// <summary>共享策略与触发模式的整数值是显式契约，不随重构漂移。</summary>
    [TestMethod]
    public void SharingPolicyAndTriggerMode_HaveExplicitValues()
    {
        Assert.AreEqual(
            "ExclusiveOperation=0,Serialized=1,ExclusiveRun=2,Broadcast=3",
            Describe<EVisionSourceSharingPolicy>());
        Assert.AreEqual(
            "KeepCurrent=0,FreeRun=1,Software=2,External=3",
            Describe<EVisionTriggerMode>());
        Assert.AreEqual("Unknown=0,Healthy=1,Degraded=2,Faulted=3", Describe<EVisionDeviceHealthState>());
    }

    /// <summary>设备身份区分"是否报告了可用于互斥协调的规范资源键"。</summary>
    [TestMethod]
    public void DeviceIdentity_ReportsCanonicalKeyAvailability()
    {
        var withoutKey = new VisionDeviceIdentity("dp.fake", "top", null);
        var withKey = new VisionDeviceIdentity("dp.fake", "top", "camera:serial:DA1");
        Assert.IsFalse(withoutKey.HasCanonicalKey);
        Assert.IsTrue(withKey.HasCanonicalKey);
    }

    private static string Describe<T>() where T : struct, Enum =>
        string.Join(",", Enum.GetNames(typeof(T)).Select(name => name + "=" + Convert.ToInt32(Enum.Parse(typeof(T), name))));

    private sealed class Counter
    {
        public int Retains;
        public int Disposes;
    }

    private sealed class TrackingImageSource : IImageSource
    {
        private readonly Counter _counter;

        public TrackingImageSource(Counter counter) => _counter = counter;

        public ImageInfo Info { get; } = new ImageInfo(2, 2, EPixelLayout.Gray8);

        public IImageSource Retain()
        {
            _counter.Retains++;
            return new TrackingImageSource(_counter);
        }

        public IImageSource ReadTile(int level, int tileX, int tileY, int tileSize) => throw new NotSupportedException();

        public void CopyTo(int sourceOffset, byte[] destination, int destinationOffset, int count) =>
            throw new NotSupportedException();

        public void Dispose() => _counter.Disposes++;
    }
}
