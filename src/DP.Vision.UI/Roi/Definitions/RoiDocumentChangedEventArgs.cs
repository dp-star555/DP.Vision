using System;

namespace DP.Vision.UI;

/// <summary>已提交的编辑事务，包括撤销、重做或加载；事件参数保存不可变快照。</summary>
public sealed class RoiDocumentChangedEventArgs : EventArgs
{
    /// <summary>创建文档变更事件参数。</summary>
    /// <param name = "before">变更前的已提交文档。</param>
    /// <param name = "after">变更后的已提交文档。</param>
    /// <param name = "operation">操作标识，例如create、edit、delete、metadata、load、undo或redo。</param>
    public RoiDocumentChangedEventArgs(RoiDocument before, RoiDocument after, string operation)
    {
        Before = before;
        After = after;
        Operation = operation;
    }

    /// <summary>变更前的已提交状态。</summary>
    public RoiDocument Before { get; }

    /// <summary>变更后的已提交状态。</summary>
    public RoiDocument After { get; }

    /// <summary>本次提交的操作标识。</summary>
    public string Operation { get; }
}
