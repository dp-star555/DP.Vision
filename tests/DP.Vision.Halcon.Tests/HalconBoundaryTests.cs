using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Acquisition;
using DP.Vision.Algorithms;
using DP.Vision.Halcon;
using Microsoft.VisualStudio.TestTools.UnitTesting;
#if HALCON_SDK
using HalconDotNet;
#endif

namespace DP.Vision.Halcon.Tests;

/// <summary>新SDK边界的真实像素复制和依赖方向验证，不需要相机设备。</summary>
[TestClass]
public sealed class HalconBoundaryTests
{
    /// <summary>厂商适配不依赖Workflow或旧视觉程序集。</summary>
    [TestMethod]
    public void DependencyDirection_IsIndependent()
    {
        Assert.IsFalse(typeof(HalconAcquisitionDevice).Assembly.GetReferencedAssemblies().Any(a =>
            a.Name!.StartsWith("DP.WorkFlow", StringComparison.Ordinal) || a.Name.StartsWith("MachineVision", StringComparison.Ordinal)));
    }

    /// <summary>未装配SDK的构建不会伪造帧：工厂在造设备时就明确失败，而不是静默降级。</summary>
    [TestMethod]
    public void MissingSdk_IsExplicitlyUnavailable()
    {
#if HALCON_SDK
        Assert.IsTrue(HalconStreamCameras.IsSdkEnabled);
#else
        Assert.IsFalse(HalconStreamCameras.IsSdkEnabled);
        Assert.ThrowsExactly<VisionProviderUnavailableException>(
            () => HalconStreamCameras.Create(new HalconAcquisitionBinding("cam", "GigEVision2", "dev")));
#endif
    }

#if HALCON_SDK
    /// <summary>HALCON原生图像释放后，新版灰度源仍有独立像素。</summary>
    [TestMethod]
    public void GrayImage_CopySurvivesNativeDisposal()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        IImageSource source;
        try
        {
            using var native = new HImage();
            native.GenImage1("byte", 3, 2, pin.AddrOfPinnedObject());
            source = HalconImageSource.CopyFrom(native);
        }
        finally { pin.Free(); }
        using (source)
        {
            var actual = new byte[6]; source.CopyTo(0, actual, 0, 6);
            CollectionAssert.AreEqual(bytes, actual);
        }
    }

    /// <summary>uint2不做隐式降位深；取消、预算与不支持像素明确失败。</summary>
    [TestMethod]
    public void Gray16_PreservesDepthAndRejectsUnsupportedRequests()
    {
        var pin = GCHandle.Alloc(new ushort[] { 0, 256, 65535, 1000 }, GCHandleType.Pinned);
        try
        {
            using var native = new HImage(); native.GenImage1("uint2", 2, 2, pin.AddrOfPinnedObject());
            Assert.ThrowsExactly<OperationCanceledException>(() => HalconImageSource.CopyFrom(native, new CancellationToken(true)));
            Assert.ThrowsExactly<InvalidOperationException>(() => HalconImageSource.CopyFrom(native, maximumBytes: 4));
            using var source = HalconImageSource.CopyFrom(native);
            Assert.AreEqual(EPixelLayout.Gray16, source.Info.Layout);
            var bytes = new byte[8]; source.CopyTo(0, bytes, 0, 8);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 255, 255, 232, 3 }, bytes);
            using var real = new HImage(); real.GenImageConst("real", 2, 2);
            Assert.ThrowsExactly<NotSupportedException>(() => HalconImageSource.CopyFrom(real));
        }
        finally { pin.Free(); }
    }

    /// <summary>RGB平面转换严格保持BGR通道顺序。</summary>
    [TestMethod]
    public void RgbImage_UsesExplicitBgrLayout()
    {
        var r = GCHandle.Alloc(new byte[] { 200, 201 }, GCHandleType.Pinned);
        var g = GCHandle.Alloc(new byte[] { 100, 101 }, GCHandleType.Pinned);
        var b = GCHandle.Alloc(new byte[] { 50, 51 }, GCHandleType.Pinned);
        try
        {
            using var native = new HImage();
            native.GenImage3("byte", 2, 1, r.AddrOfPinnedObject(), g.AddrOfPinnedObject(), b.AddrOfPinnedObject());
            using var source = HalconImageSource.CopyFrom(native);
            Assert.AreEqual(EPixelLayout.Bgr24, source.Info.Layout);
            var bytes = new byte[6]; source.CopyTo(0, bytes, 0, 6);
            CollectionAssert.AreEqual(new byte[] { 50, 100, 200, 51, 101, 201 }, bytes);
        }
        finally { r.Free(); g.Free(); b.Free(); }
    }
#endif
}
