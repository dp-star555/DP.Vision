namespace DP.Vision.Acquisition;

/// <summary>取图流启动策略；由机器配置的connection.transferStart决定，工作流文档不解释它。</summary>
public enum EVisionAcquisitionTransferStart
{
    /// <summary>节点到达后按请求采集；适合主动获取，设备保持Connected。</summary>
    PerRequest = 0,

    /// <summary>设备连接成功后立即注册完整帧回调并StartGrab；适合外部触发和降低首帧延迟。</summary>
    OnConnect = 1
}
