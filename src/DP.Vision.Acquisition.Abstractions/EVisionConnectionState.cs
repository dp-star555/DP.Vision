namespace DP.Vision.Acquisition;

/// <summary>
/// 单个物理设备的连接状态（计划§10.1）；与取流状态分开维护，例如 Connected+Stopped 与 Connected+Running。
/// 设备连接属于软件生命周期：软件启动时打开，软件退出、配置停用或受控重配置时关闭。
/// </summary>
public enum EVisionConnectionState
{
    /// <summary>已创建会话但尚未开始连接。</summary>
    Created = 0,

    /// <summary>正在打开设备。</summary>
    Connecting = 1,

    /// <summary>设备已连接，SDK设备对象已创建。</summary>
    Connected = 2,

    /// <summary>连接失败或运行期故障；拒绝新的采集直到显式恢复。</summary>
    Faulted = 3,

    /// <summary>正在关闭设备。</summary>
    Disconnecting = 4,

    /// <summary>设备已关闭，会话已释放。</summary>
    Disposed = 5
}
