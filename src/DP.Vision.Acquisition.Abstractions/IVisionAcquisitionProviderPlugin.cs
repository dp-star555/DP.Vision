using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// Provider插件入口。插件目录只认识本契约，不解释任何Provider私有字段；
/// 宿主因此不需要在编译期引用具体厂商程序集。
/// </summary>
public interface IVisionAcquisitionProviderPlugin
{
    /// <summary>插件稳定身份；必须与Manifest声明的pluginId一致，否则拒绝加载。</summary>
    string PluginId { get; }

    /// <summary>
    /// 用插件私有配置创建Module。配置内容只有本Provider自己解释，
    /// 公共层既不解析也不校验其字段；配置为空表示使用插件默认配置。
    /// </summary>
    /// <param name="configuration">插件私有配置JSON文本；为空时使用默认配置。</param>
    /// <returns>可参与候选组合的Module。</returns>
    /// <exception cref="VisionSourceConfigurationException">私有配置无法被本Provider解释。</exception>
    IVisionAcquisitionProviderModule CreateModule(string? configuration);
}

/// <summary>
/// Provider插件可选健康报告；由插件入口实现。
/// 宿主用它把"Provider已安装但当前不可用"（例如缺SDK、许可证无效、CPU架构不匹配）
/// 写进逻辑源目录，使采集节点在首节点执行前就能给出Provider级诊断。
/// </summary>
public interface IVisionAcquisitionProviderHealth
{
    /// <summary>报告Provider当前是否可用。</summary>
    /// <param name="diagnostic">不可用原因；可用时为空。</param>
    /// <returns>可用时返回 <see langword="true"/>。</returns>
    bool TryGetHealth(out string? diagnostic);
}
