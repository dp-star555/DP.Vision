namespace DP.Vision.Acquisition;

/// <summary>
/// 已发布逻辑源目录条目；由Composition直接投影，供Workflow侧做运行前检查与候选编辑。
/// 字段来自公共层可解释的机器配置部分与Plugin解析后的验证结果。
/// </summary>
/// <param name="SourceId">逻辑视觉源标识。</param>
/// <param name="ProviderId">服务该源的Provider稳定身份；V2机器组合中为AcquisitionTypeId。</param>
/// <param name="AcquisitionTypeId">AcquisitionType身份；V1组合不解释机器配置时为空。</param>
/// <param name="ResourceKey">规范物理资源键；未安装Type导致Source不可用时为空。</param>
/// <param name="Kind">采集几何形态；未安装Type或V1组合时为空。</param>
/// <param name="SharingPolicy">该源在物理资源上的并发协调策略。</param>
/// <param name="AcquisitionMode">该源的采集时序。</param>
/// <param name="IsAvailable">Type已安装、配置已验证且该源当前可采集。</param>
/// <param name="Diagnostic">不可用原因；可用时为空。</param>
public sealed record VisionAcquisitionSourceInfo(
    string SourceId,
    string ProviderId,
    string? AcquisitionTypeId,
    string ResourceKey,
    EVisionAcquisitionKind? Kind,
    EVisionSourceSharingPolicy SharingPolicy,
    EVisionAcquisitionMode AcquisitionMode,
    bool IsAvailable,
    string? Diagnostic);
