namespace DP.Vision.Acquisition;

/// <summary>
/// 插件级可用性快照；由Type Catalog在冻结时从各Driver Module的健康报告采集，之后不再变化。
/// </summary>
/// <remarks>
/// 它回答的是"这个插件声明的Type现在能不能真的用"：程序集在、Type也注册了，
/// 但底层运行时（厂商SDK / 原生库）可能没部署。机器配置组合器据此把该插件声明的逻辑源标为不可用，
/// 让缺SDK这类问题在首节点执行前就可见，而不是等到采集时才失败。
/// <para>
/// 可用性不进入 <see cref="VisionAcquisitionTypeCatalog.CatalogId"/>：它是本机部署状态，不是Catalog内容。
/// 同一份部署在两台机器上可以有不同的可用性，但Catalog身份必须一致。
/// </para>
/// </remarks>
/// <param name="PluginId">插件稳定身份，例如 dp.vision.halcon。</param>
/// <param name="IsAvailable">该插件声明的全部Type当前是否可用；同一PluginId由多个Module声明时，全部可用才算可用。</param>
/// <param name="Diagnostic">不可用原因；可用时为空。</param>
public sealed record VisionAcquisitionPluginAvailability(
    string PluginId,
    bool IsAvailable,
    string? Diagnostic);
