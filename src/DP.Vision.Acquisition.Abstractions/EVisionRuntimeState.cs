namespace DP.Vision.Acquisition;

/// <summary>
/// 采集运行时整体的生命周期状态（计划§9）：Construct → StartAsync → Ready → StopAsync。
/// Required源全部打开成功才能进入Ready；Optional源失败只降级为Degraded，对应Source不可用但不阻断整体就绪；
/// Required源失败则进入NotReady，不能伪装成继续可用。
/// </summary>
public enum EVisionRuntimeState
{
    /// <summary>已构造但尚未启动。</summary>
    Created = 0,

    /// <summary>正在按ResourceKey打开设备。</summary>
    Starting = 1,

    /// <summary>全部Required源已连接，Runtime可接受采集与根运行。</summary>
    Ready = 2,

    /// <summary>Required源全部连接，但至少一个Optional源打开失败；对应Source不可用并保留完整诊断。</summary>
    Degraded = 3,

    /// <summary>至少一个Required源打开失败；Runtime未就绪，不得进入可运行状态。</summary>
    NotReady = 4,

    /// <summary>已停止：设备已按关闭顺序释放，不再接受采集与根运行。</summary>
    Stopped = 5
}
