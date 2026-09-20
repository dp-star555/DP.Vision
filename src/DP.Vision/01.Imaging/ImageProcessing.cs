using System;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision;

/// <summary>为后台处理封装图像租约；在排队前保留输入，完成、失败或取消后均释放内部租约。</summary>
public static class ImageProcessing
{
    /// <summary>
    /// 在返回任务前保留输入，再在线程池调用处理函数；返回后调用方可以立即释放自己的源。
    /// 处理函数只借用传入句柄，不得释放它；结果若仍引用图像，须由结果内部另行Retain。
    /// </summary>
    /// <typeparam name="TResult">处理结果类型。</typeparam>
    /// <param name="source">借用的输入源；调用期间不得与其Dispose竞争。</param>
    /// <param name="process">在线程池执行的异步处理函数，返回任务必须涵盖全部图像访问，不得启动脱离任务的后台读取。</param>
    /// <param name="token">协作式取消标记；已开始的工作必须自行响应，入口不会提前释放正在使用的输入。</param>
    /// <returns>处理任务；任务结束时内部输入租约已释放。返回的结果归调用方管理。</returns>
    public static Task<TResult> RunAsync<TResult>(
        IImageSource source,
        Func<IImageSource, CancellationToken, Task<TResult>> process,
        CancellationToken token = default
    )
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (process == null)
            throw new ArgumentNullException(nameof(process));
        if (token.IsCancellationRequested)
            return Task.FromCanceled<TResult>(token);

        // 必须在此同步保留，不能等线程池开始执行后再访问调用方句柄。
        var lease = source.Retain();
        try
        {
            // 不把取消标记传给Task.Run，否则排队期间取消可能跳过委托，导致租约无人释放。
            return Task.Run(async () =>
            {
                using (lease)
                {
                    token.ThrowIfCancellationRequested();
                    return await process(lease, token).ConfigureAwait(false);
                }
            });
        }
        catch
        {
            // 调度自身失败时，委托尚未接管释放责任。
            lease.Dispose();
            throw;
        }
    }
}
