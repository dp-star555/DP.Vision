using System;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 插件目录加载测试用的插件入口。本类型是测试程序集里唯一公开实现
/// <see cref="IVisionAcquisitionProviderPlugin"/> 的类型——加载器按"程序集里所有公开实现者"发现入口，
/// 因此再增加一个公开实现者会让同一个插件包产生多个插件，破坏这些测试的断言。
/// 行为由插件私有配置文本驱动，所以一个入口就能覆盖可用、不可用和配置透传三类场景。
/// </summary>
public sealed class ConfigurableAcquisitionProviderPlugin : IVisionAcquisitionProviderPlugin, IVisionAcquisitionProviderHealth
{
    /// <summary>插件稳定身份；测试Manifest必须声明同一身份，否则加载被拒绝。</summary>
    public const string PluginIdentity = "dp.vision.test";

    /// <summary>本插件贡献的Provider身份。</summary>
    public const string ProviderIdentity = "dp.vision.test.provider";

    /// <summary>本插件贡献的Module身份前缀。</summary>
    public const string ModuleIdentity = "dp.vision.test.module";

    /// <summary>把收到的私有配置编码进Module身份的分隔符，便于从加载结果观察转交结果。</summary>
    public const char ConfigurationSeparator = ':';

    private const string UnavailableMarker = "unavailable";

    private string? _configuration;

    /// <inheritdoc/>
    public string PluginId => PluginIdentity;

    /// <inheritdoc/>
    /// <remarks>
    /// 把收到的配置编码进Module身份：<c>Assembly.LoadFrom</c> 在 .NET Framework 下会再加载一份副本，
    /// 静态字段不跨副本共享，因此测试必须从加载结果观察"公共层原样转交"，不能依赖静态观测点。
    /// </remarks>
    public IVisionAcquisitionProviderModule CreateModule(string? configuration)
    {
        _configuration = configuration;
        return new FakeVisionProviderModule(
            ModuleIdentity + ConfigurationSeparator + (configuration ?? "<null>"),
            new VisionAcquisitionProviderRegistration(
                ProviderIdentity,
                "1.0.0",
                () => FakeVisionProvider.WithDevices(ProviderIdentity)));
    }

    /// <inheritdoc/>
    public bool TryGetHealth(out string? diagnostic)
    {
        if (_configuration is not null
            && _configuration.IndexOf(UnavailableMarker, StringComparison.Ordinal) >= 0)
        {
            diagnostic = $"Provider {ProviderIdentity} 已安装，但测试配置把它标记为不可用。";
            return false;
        }

        diagnostic = null;
        return true;
    }
}
