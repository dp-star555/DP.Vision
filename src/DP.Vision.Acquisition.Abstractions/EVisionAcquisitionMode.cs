namespace DP.Vision.Acquisition;

/// <summary>逻辑源采用主动单次采集还是外部回调缓冲；由机器配置决定，工作流文档不解释它。</summary>
public enum EVisionAcquisitionMode
{
    /// <summary>
    /// 节点到达后才发起一次采集并等待结果：自由运行取一帧、软件触发，或节点布防后等待下一次外部触发。
    /// 只保证取得"节点开始等待之后"的帧，不解决节点之前已经发生的回调。
    /// </summary>
    OnDemand = 0,

    /// <summary>
    /// 相机由站点级会话持续接收外部触发帧并进入有界待领取队列，节点稍后原子领取最早未消费帧。
    /// 允许帧早于采集节点到达，因此必须配合根运行接收边界，旧运行的帧不得进入新运行。
    /// </summary>
    BufferedExternal = 1
}
