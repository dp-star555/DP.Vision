using System;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition;

/// <summary>
/// Provider设备可选的持续接收能力；实现它表示设备能在没有采集请求的情况下持续产生帧。
/// <para>
/// 本接口不向Workflow暴露：工作流只认识 <see cref="IVisionAcquisition"/>。
/// </para>
/// </summary>
public interface IVisionStreamingAcquisitionDevice
{
    /// <summary>布防设备并把后续帧交给接收方。</summary>
    /// <param name="sink">帧接收方；所有权与停止语义见 <see cref="IVisionProviderFrameSink"/>。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>停止本次接收的句柄；调用方负责释放。</returns>
    ValueTask<IVisionAcquisitionStream> StartStreamAsync(
        IVisionProviderFrameSink sink,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider向Acquisition Runtime交付回调帧的接收口。
/// <para>所有权：<see cref="Publish"/> 一进入，帧所有权就无条件转给接收方；接收方即使拒绝也必须释放它。</para>
/// <para>线程：<see cref="Publish"/> 会被厂商SDK回调线程直接调用，必须快速、非阻塞，且不得把异常抛回该线程。</para>
/// <para>顺序：<see cref="Complete"/> 之后不得再调用 <see cref="Publish"/>。</para>
/// </summary>
public interface IVisionProviderFrameSink
{
    /// <summary>交付一帧；所有权随之转移。</summary>
    /// <param name="frame">Provider拥有的中立帧；进入本方法后由接收方负责释放。</param>
    void Publish(VisionProviderFrame frame);

    /// <summary>声明本次接收已结束，不再有后续帧。</summary>
    /// <param name="failure">意外结束的原因；正常停止时为空。</param>
    void Complete(Exception? failure);
}

/// <summary>一次持续接收的句柄。</summary>
/// <remarks>
/// <see cref="IAsyncDisposable.DisposeAsync"/> 完成后：不得再调用 <see cref="IVisionProviderFrameSink"/> 的任何方法，
/// 且必须已经等待所有"已经进入 <see cref="IVisionProviderFrameSink.Publish"/> 的回调"退出。
/// 实现不得与活动回调并发释放设备。
/// </remarks>
public interface IVisionAcquisitionStream : IAsyncDisposable
{
}
