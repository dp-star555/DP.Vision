using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// deviceSettings 是设备配置唯一来源的回归。
/// </summary>
/// <remarks>
/// <para>
/// 这里守住的是完整链路：机器配置里的 <c>deviceSettings</c> → 该Type的解析器 → 插件私有绑定对象
/// → 发布到 <c>VisionAcquisitionSourceBinding</c> → Runtime 打开设备时交回同一个插件。
/// </para>
/// <para>
/// 在这条链路接通之前，解析器算出来的设备字段会被丢掉，只剩身份/资源键/摘要三行文本，
/// 于是 Provider 只能从"插件私有配置"这条独立通道再拿一份绑定——
/// 同一台相机要在两处各写一遍，只填 deviceSettings 的部署会在打开设备时报"私有配置中没有绑定"。
/// 所以下面第一条用例刻意**不提供任何私有配置**：它必须仅凭 deviceSettings 走通。
/// </para>
/// </remarks>
[TestClass]
public sealed class DeviceSettingsBindingFlowTests
{
    private const string TypeId = "dp.acquisition.binding.flow";
    private const string ProviderId = "dp.vision.test.binding.flow";

    /// <summary>
    /// 只给 deviceSettings、不给任何私有配置时，解析出的私有绑定必须出现在 Provider 的 OpenAsync 上。
    /// </summary>
    [TestMethod]
    public async Task DeviceSettings_ReachProviderOpen_WithoutAnyPrivateConfiguration()
    {
        var provider = FakeVisionProvider.WithDevices(ProviderId);
        var composition = Compose(provider, StatefulTestDeviceSettingsParser.Parse);

        await using var runtime = new VisionAcquisitionRuntime(composition);
        var state = await runtime.StartAsync(CancellationToken.None);

        Assert.AreEqual(EVisionRuntimeState.Ready, state, "仅凭 deviceSettings 就应该能打开设备。");
        Assert.AreEqual(1, provider.OpenRequests.Count, "Provider 应被请求打开一次。");

        var request = provider.OpenRequests.Single();
        Assert.AreEqual("flow:serial:SN-42", request.ProviderBindingId);

        // 关键断言：Provider 拿到的是解析器造出来的那一个对象，而不是 null、也不是另造一份。
        var parsed = Assert.IsInstanceOfType<TestDeviceBinding>(request.ProviderState);
        Assert.AreEqual("SN-42", parsed.SerialNumber);
    }

    /// <summary>解析出的私有绑定在组合阶段就被发布到公共绑定上，而不是等到打开设备时才现算。</summary>
    [TestMethod]
    public void DeviceSettings_ParsedStateIsPublishedOnSourceBinding()
    {
        var composition = Compose(
            FakeVisionProvider.WithDevices(ProviderId),
            StatefulTestDeviceSettingsParser.Parse);

        var binding = composition.Sources.Single();
        Assert.AreEqual("flow:serial:SN-42", binding.ProviderBindingId);
        Assert.AreEqual("camera:serial:SN-42", binding.ResourceKey);

        var parsed = Assert.IsInstanceOfType<TestDeviceBinding>(binding.ProviderState);
        Assert.AreEqual("SN-42", parsed.SerialNumber);
    }

    /// <summary>解析器不提供私有状态时绑定上为空，打开设备仍然照常发生（可选而非必填）。</summary>
    [TestMethod]
    public async Task DeviceSettings_WithoutParsedState_StillOpensDevice()
    {
        var provider = FakeVisionProvider.WithDevices(ProviderId);
        var composition = Compose(provider, TestDeviceSettingsParser.Parse);

        await using var runtime = new VisionAcquisitionRuntime(composition);
        var state = await runtime.StartAsync(CancellationToken.None);

        Assert.AreEqual(EVisionRuntimeState.Ready, state);
        Assert.IsNull(composition.Sources.Single().ProviderState);
        Assert.IsNull(provider.OpenRequests.Single().ProviderState);
    }

    private static VisionAcquisitionProviderComposition Compose(
        FakeVisionProvider provider,
        Func<string?, VisionDeviceSettingsParseResult> parser)
    {
        var catalog = new VisionAcquisitionTypeCatalogComposer().Compose(new IVisionAcquisitionDriverModule[]
        {
            new FakeAcquisitionDriverModule(
                "dp.vision.test.binding.flow.driver",
                new VisionAcquisitionTypeRegistration(
                    TypeId,
                    ProviderId,
                    "1.0.0",
                    EVisionAcquisitionKind.AreaScan,
                    DeviceSettingsVersion: 1,
                    "绑定流转测试相机",
                    new VisionAcquisitionTypeCapabilities(
                        SupportsFreeRun: true,
                        SupportsSoftwareTrigger: true),
                    () => provider,
                    parser))
        });

        var cameras = VisionAcquisitionMachineConfigurationParser.Parse(
            "{\"sourceId\":\"Camera.Top\",\"acquisitionType\":\"" + TypeId + "\",\"settingsVersion\":1,"
            + "\"deviceSettings\":{\"serialNumber\":\"SN-42\"}}");

        return new VisionAcquisitionMachineConfigurationComposer().Compose(catalog, cameras);
    }
}
