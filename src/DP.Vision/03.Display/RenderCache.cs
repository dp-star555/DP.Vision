using System;
using System.Collections.Generic;

namespace DP.Vision;

/// <summary>原生画布适配器使用的显式字节预算LRU缓存，逐出条目时释放资源。</summary>
public sealed class RenderCache<T> : IDisposable
    where T : class
{
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries =
        new Dictionary<string, LinkedListNode<Entry>>();
    private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
    private readonly Action<T> _dispose;
    private readonly long _budget;

    /// <summary>创建仅在渲染线程操作的缓存，不负责跨线程同步。</summary>
    /// <param name = "budget">可计入缓存的载荷字节上限，必须大于0。</param>
    /// <param name = "dispose">逐出或清空条目时调用的资源释放函数。</param>
    public RenderCache(long budget, Action<T> dispose)
    {
        if (budget < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "缓存预算必须大于0。");
        }

        _budget = budget;
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose), "资源释放函数不能为空。");
    }

    /// <summary>当前计入缓存的载荷字节数。</summary>
    public long Bytes { get; private set; }

    /// <summary>查找缓存值，命中后标记为最近使用。</summary>
    /// <param name = "key">当前图像版本内的缓存键。</param>
    /// <param name = "value">命中时返回借用值；未命中为null，不移交资源所有权。</param>
    /// <returns>缓存是否命中。</returns>
    public bool TryGet(string key, out T? value)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>预算允许时接管资源，必要时逐出旧项；失败时调用方仍拥有资源。</summary>
    /// <param name = "key">非空且未存在的缓存键。</param>
    /// <param name = "value">准备移交的非空资源。</param>
    /// <param name = "bytes">本条目计入缓存的非负载荷字节数。</param>
    /// <returns>是否成功接管；单条目超过预算时返回false。</returns>
    public bool Add(string key, T value, long bytes)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("缓存键不能为空。", nameof(key));
        }

        if (value == null)
        {
            throw new ArgumentException("缓存资源不能为空。", nameof(value));
        }

        if (bytes < 0)
        {
            throw new ArgumentException("缓存字节数不能为负数。", nameof(bytes));
        }

        if (bytes > _budget)
        {
            return false;
        }

        if (_entries.ContainsKey(key))
        {
            throw new ArgumentException("缓存键已存在。", nameof(key));
        }

        while (Bytes + bytes > _budget && _lru.Last != null)
        {
            RemoveLast();
        }

        var node = _lru.AddFirst(
            new Entry
            {
                Key = key,
                Value = value,
                Bytes = bytes,
            }
        );
        _entries.Add(key, node);
        Bytes += bytes;
        return true;
    }

    private void RemoveLast()
    {
        var node = _lru.Last!;
        _lru.RemoveLast();
        _entries.Remove(node.Value.Key);
        Bytes -= node.Value.Bytes;
        _dispose(node.Value.Value);
    }

    /// <summary>释放全部缓存资源；清空后仍可继续使用此缓存。</summary>
    public void Dispose()
    {
        while (_lru.Count > 0)
        {
            RemoveLast();
        }
    }

    private sealed class Entry
    {
        internal string Key = "";
        internal T Value = null!;
        internal long Bytes;
    }
}
