using System;

namespace DP.Vision.Acquisition;

/// <summary>
/// 外部回调缓冲的有界策略；组合发布前必须能确定这三个值，运行期不得再变。
/// <para>
/// <see cref="ByteBudget"/> 计的是**逻辑像素字节**（<see cref="ImageInfo.ByteLength"/> 之和），
/// 不是进程内存占用：图像源可能持有额外副本或保留计数，实际驻留内存会大于该值。
/// 它是一条安全上界，不是内存核算口径。
/// </para>
/// </summary>
public sealed record VisionFrameInboxPolicy
{
    /// <summary>创建有界缓冲策略。</summary>
    /// <param name="capacity">最多同时待领取的帧数；必须为正。</param>
    /// <param name="byteBudget">待领取帧的逻辑像素字节总和上限；必须为正。</param>
    /// <param name="maximumFrameAge">帧从接收到被领取的最大允许间隔；必须为正值。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一值不是正值。</exception>
    public VisionFrameInboxPolicy(int capacity, long byteBudget, TimeSpan maximumFrameAge)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "待领取帧数上限必须为正。");
        if (byteBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteBudget), byteBudget, "字节预算必须为正。");
        if (maximumFrameAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameAge), maximumFrameAge, "最大帧龄必须为正值。");

        Capacity = capacity;
        ByteBudget = byteBudget;
        MaximumFrameAge = maximumFrameAge;
    }

    /// <summary>最多同时待领取的帧数。</summary>
    public int Capacity { get; }

    /// <summary>待领取帧的逻辑像素字节总和上限。</summary>
    public long ByteBudget { get; }

    /// <summary>帧从接收到被领取的最大允许间隔；超龄帧在领取时被释放而不是成功返回。</summary>
    public TimeSpan MaximumFrameAge { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"{Capacity} 帧 / {ByteBudget} 字节 / {MaximumFrameAge.TotalMilliseconds:0} ms";
}
