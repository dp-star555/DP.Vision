using System;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition;

/// <summary>
/// 站点级采集运行所有权；用于让外部回调缓冲源把"哪一根运行的帧"界定清楚。
/// <para>
/// <b>只有根运行宿主解析本接口。</b> 嵌套运行（恢复子流程、联合参与者、告警协调）在类型上拿不到它，
/// 因此无法重新布防、清空队列或释放父运行资源——这是结构性保证，不依赖调用方传对枚举。
/// </para>
/// <para>
/// 不使用"传入 Root/Nested 标记由实现自行判断"的替代方案：那种写法把安全性交给调用方，传错即退回原状。
/// </para>
/// </summary>
public interface IVisionAcquisitionRunOwner
{
    /// <summary>
    /// 开始一根根运行：为外部回调缓冲源建立新的采集代次并布防。
    /// <para>必须在工作流引擎执行首节点之前调用，使"回调早于采集节点"成为正常时序。</para>
    /// </summary>
    /// <param name="runId">根运行身份，用于诊断与冲突报告。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>本根运行的采集所有权租约；释放它即退役本轮。</returns>
    ValueTask<IVisionAcquisitionRunLease> BeginRunAsync(string runId, CancellationToken cancellationToken);
}

/// <summary>一根根运行的采集所有权租约。</summary>
/// <remarks>
/// 释放顺序固定：先停止接收流（等待已进入的回调退出），再释放未领取帧，最后归还所有权。
/// 释放后不得再有本代次的帧进入任何队列。
/// </remarks>
public interface IVisionAcquisitionRunLease : IAsyncDisposable
{
    /// <summary>本租约对应的根运行身份。</summary>
    string RunId { get; }

    /// <summary>本租约建立的采集代次。</summary>
    int Epoch { get; }

    /// <summary>本轮已布防的逻辑源标识，按序数排序；用于运行制品与诊断。</summary>
    System.Collections.Generic.IReadOnlyList<string> ArmedSourceIds { get; }
}
