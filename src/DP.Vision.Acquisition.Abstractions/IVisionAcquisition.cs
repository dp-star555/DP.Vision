using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition;

/// <summary>Workflow与其他业务唯一需要学习的采集入口；内部隐藏Provider选择、连接复用、互斥、超时和来源元数据。</summary>
public interface IVisionAcquisition
{
    /// <summary>按逻辑源采集一帧。</summary>
    /// <param name="source">已发布的逻辑视觉源。</param>
    /// <param name="request">采集请求。</param>
    /// <param name="owner">发起方身份，用于资源冲突诊断。</param>
    /// <param name="cancellationToken">协作取消；取消只表示请求取消，不证明原生SDK调用已退出。</param>
    /// <returns>调用方必须释放的采集结果。</returns>
    ValueTask<VisionCapturedImage> CaptureAsync(
        VisionSourceReference source,
        VisionCaptureRequest request,
        VisionAcquisitionOwner owner,
        CancellationToken cancellationToken);
}
