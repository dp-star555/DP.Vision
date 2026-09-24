namespace DP.Vision.Acquisition;

/// <summary>同一物理资源键上并发采集的协调策略。</summary>
public enum EVisionSourceSharingPolicy
{
    /// <summary>同一ResourceKey同时只允许一个采集；冲突立即确定性失败并报告占用者。</summary>
    ExclusiveOperation = 0,

    /// <summary>按到达顺序串行；等待支持取消和超时，不能无限排队。</summary>
    Serialized = 1,

    /// <summary>根运行开始取得租约，结束或退役时释放；依赖运行作用域所有权，尚未实现。</summary>
    ExclusiveRun = 2,

    /// <summary>单一设备流向多个订阅者发布同一不可变像素内容；需要有真实连续流需求后设计，尚未实现。</summary>
    Broadcast = 3
}
