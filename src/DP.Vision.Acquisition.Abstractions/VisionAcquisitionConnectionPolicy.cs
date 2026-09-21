namespace DP.Vision.Acquisition;

/// <summary>相机连接与取流策略；属于机器配置，不在Workflow文档中出现。</summary>
/// <param name="OpenOnApplicationStart">软件启动、Plugin和机器配置加载完成后是否立即打开物理设备。</param>
/// <param name="TransferStart">取图流启动策略：节点请求时启动，或连接成功后立即启动。</param>
public sealed record VisionAcquisitionConnectionPolicy(
    bool OpenOnApplicationStart = true,
    EVisionAcquisitionTransferStart TransferStart = EVisionAcquisitionTransferStart.PerRequest);
