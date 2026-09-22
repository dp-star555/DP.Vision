using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;

namespace DP.Vision.Acquisition.Tests;

/// <summary>
/// 打开行为可控的Provider：可按设备绑定身份决定"这次打开要不要卡住"，
/// 并在打开开始与打开完成时分别通知测试。
/// <para>
/// 存在的理由：验证"不同 ResourceKey 互不串行"只能靠**事件顺序**——
/// 一个慢的打开还卡在原地时，另一个快的打开是否已经完成。
/// 用计时阈值断言会变成抖动测试，用日志字符串又观察不到"谁在等谁"。
/// </para>
/// </summary>
internal sealed class BlockingVisionProvider : IVisionAcquisitionProvider
{
    private readonly Func<string, IVisionAcquisitionDevice> _deviceFactory;
    private readonly Func<string, Task?>? _blockOn;
    private readonly Action<string>? _onOpenStarted;
    private readonly Action<string>? _onOpened;
    private readonly List<string> _opened = new List<string>();
    private readonly object _gate = new object();
    private int _disposeCount;

    /// <summary>创建Provider。</summary>
    /// <param name="providerId">Provider稳定身份。</param>
    /// <param name="deviceFactory">按设备绑定身份创建设备。</param>
    /// <param name="blockOn">返回该绑定打开时要等待的任务；返回空表示这次打开不阻塞。</param>
    /// <param name="onOpenStarted">打开动作开始（进入等待闸门之前）时回调。</param>
    /// <param name="onOpened">打开真正完成时回调。</param>
    public BlockingVisionProvider(
        string providerId,
        Func<string, IVisionAcquisitionDevice> deviceFactory,
        Func<string, Task?>? blockOn = null,
        Action<string>? onOpenStarted = null,
        Action<string>? onOpened = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider身份不能为空。", nameof(providerId));
        ProviderId = providerId;
        _deviceFactory = deviceFactory ?? throw new ArgumentNullException(nameof(deviceFactory));
        _blockOn = blockOn;
        _onOpenStarted = onOpenStarted;
        _onOpened = onOpened;
    }

    /// <inheritdoc/>
    public string ProviderId { get; }

    /// <summary>已完成打开的绑定身份，按完成顺序。</summary>
    public IReadOnlyList<string> OpenedBindings
    {
        get { lock (_gate) return _opened.ToArray(); }
    }

    /// <summary>已执行的释放次数。</summary>
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    /// <inheritdoc/>
    public async ValueTask<IVisionAcquisitionDevice> OpenAsync(
        VisionAcquisitionProviderBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));
        cancellationToken.ThrowIfCancellationRequested();

        var bindingId = binding.ProviderBindingId;
        _onOpenStarted?.Invoke(bindingId);

        var gate = _blockOn?.Invoke(bindingId);
        if (gate is not null)
            await gate.ConfigureAwait(false);

        var device = _deviceFactory(bindingId);
        lock (_gate)
            _opened.Add(bindingId);
        _onOpened?.Invoke(bindingId);
        return device;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        return default;
    }
}
