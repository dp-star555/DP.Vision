using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Halcon.Tests;

/// <summary>
/// V2-8：HALCON线扫Type回归。验收要点（§20 V2-8、§21.21-24）：
/// 机器配置把线扫相机投影为LineScan Source（供线扫节点过滤），
/// 适配器只向上交付SDK完成的整张图，公共层不出现Line/Chunk模型。
/// </summary>
[TestClass]
public sealed class HalconLineScanAcquisitionTests
{
    private const string LineTypeId = HalconAcquisitionDriverModule.LineScanTypeId;
    private const string AreaTypeId = HalconAcquisitionDriverModule.AreaScanTypeId;

    /// <summary>一份含线扫与面阵相机的机器配置按Kind分别投影，线扫Source绑定到规范资源键。</summary>
    [TestMethod]
    public void MachineConfiguration_ProjectsLineScanSourceByKind()
    {
        var definitions = VisionAcquisitionMachineConfigurationParser.Parse(
            "["
            + "{\"sourceId\":\"Camera.Line\",\"acquisitionType\":\"" + LineTypeId + "\",\"settingsVersion\":1,"
            + "\"connection\":{\"openOnApplicationStart\":true,\"transferStart\":\"PerRequest\"},"
            + "\"deviceSettings\":{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"line-1\",\"serialNumber\":\"LINE-001\"}},"
            + "{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + AreaTypeId + "\",\"settingsVersion\":1,"
            + "\"connection\":{\"openOnApplicationStart\":true,\"transferStart\":\"PerRequest\"},"
            + "\"deviceSettings\":{\"interfaceName\":\"GigEVision2\",\"deviceName\":\"cam-top\",\"serialNumber\":\"DEMO0001\"}}"
            + "]");
        var catalog = new VisionAcquisitionTypeCatalogComposer()
            .Compose(new IVisionAcquisitionDriverModule[] { new HalconAcquisitionDriverModule() });

        var composition = new VisionAcquisitionMachineConfigurationComposer().Compose(catalog, definitions);

        var line = composition.SourceCatalog.Single(entry => entry.SourceId == "Camera.Line");
        Assert.AreEqual(LineTypeId, line.ProviderId);
        Assert.AreEqual(LineTypeId, line.AcquisitionTypeId);
        Assert.AreEqual(EVisionAcquisitionKind.LineScan, line.Kind, "线扫节点按Kind过滤Source，投影必须带LineScan。");
        Assert.AreEqual("camera:serial:LINE-001", line.ResourceKey);
        Assert.IsTrue(line.IsAvailable);
        Assert.IsNull(line.Diagnostic);

        var area = composition.SourceCatalog.Single(entry => entry.SourceId == "Camera.Top");
        Assert.AreEqual(EVisionAcquisitionKind.AreaScan, area.Kind, "面阵节点不得看到线扫Source，反之亦然。");
        Assert.AreEqual("camera:serial:DEMO0001", area.ResourceKey);

        // 线扫与面阵各自一个Provider注册（机器配置以AcquisitionTypeId为Provider身份）。
        CollectionAssert.AreEqual(
            new[] { AreaTypeId + "@1.0.0", LineTypeId + "@1.0.0" },
            composition.ProviderManifest.ToArray());
    }

    /// <summary>线扫绑定通过公共接口交付SDK完成的整张图：一次请求一张完整图像，不是逐行/分块缓冲。</summary>
    [TestMethod]
    public async Task LineScanBinding_DeliversSdkAssembledWholeFrame()
    {
        const int width = 4096;
        const int height = 2048;
        var camera = new FakeHalconStreamCamera
        {
            CaptureFrameFactory = () => new FakeHalconGrabFrame(width, height, "byte", new byte[width * height])
        };
        await using var device = Device(camera);

        var frame = await device.CaptureAsync(
            new VisionCaptureRequest(TimeSpan.FromSeconds(1), triggerMode: EVisionTriggerMode.KeepCurrent),
            CancellationToken.None);

        Assert.AreEqual(typeof(VisionProviderFrame), frame.GetType(), "公共层只有一种帧契约，没有Line/Chunk变体。");
        Assert.AreEqual(width, frame.Image.Info.Width);
        Assert.AreEqual(height, frame.Image.Info.Height);
        Assert.AreEqual(EPixelLayout.Gray8, frame.Image.Info.Layout);
        Assert.IsFalse(
            frame.Image.GetType().Assembly.GetName().Name!.Contains("Halcon", StringComparison.OrdinalIgnoreCase),
            "适配器必须交付中立图像，不能把厂商对象越过公共接口。");
        Assert.AreEqual(1, camera.SingleCaptureCount, "一次请求对应一张整图。");

        frame.Dispose();
    }

    /// <summary>公共采集契约不引入Line/Chunk模型，也不暴露任何厂商原生类型。</summary>
    [TestMethod]
    public void PublicContract_IntroducesNoLineChunkModel()
    {
        var exported = typeof(IVisionAcquisitionDevice).Assembly.GetExportedTypes();

        var offenders = exported
            .Where(type => ContainsAny(type.Name, "Chunk", "Line", "Block")
                || ContainsAny(type.Name, "HObject", "HImage", "HRegion", "HXLD", "HTuple", "HFramegrabber"))
            .Select(type => type.Name)
            .ToArray();

        Assert.AreEqual(
            0,
            offenders.Length,
            "公共采集契约里不应出现Line/Chunk/Block模型或厂商原生类型：" + string.Join("、", offenders));
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private static HalconAcquisitionDevice Device(FakeHalconStreamCamera camera) =>
        new HalconAcquisitionDevice(
            new HalconAcquisitionBinding(
                "line-camera",
                "GigEVision2",
                "line-1",
                serialNumber: "LINE-001",
                grabTimeoutMilliseconds: 2500),
            _ => camera);
}
