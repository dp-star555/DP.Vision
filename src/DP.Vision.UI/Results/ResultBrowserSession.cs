using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.UI;

/// <summary>
/// 线程安全的视图集合浏览器：管理选择、图层偏好及有限图像租约，不理解视图来自何种业务。
/// UI按Version合并刷新；异步结果是否仍适合显示由外层决定。
/// </summary>
public sealed partial class ResultBrowserSession : IViewDisplaySink, IDisposable
{
    private readonly object _gate = new object();
    private readonly ResultBrowserOptions _options;
    private List<ViewSlot> _views = new List<ViewSlot>();
    private readonly Dictionary<Tuple<string, string>, bool> _visibility =
        new Dictionary<Tuple<string, string>, bool>();
    private readonly Queue<Tuple<string, string>> _preferenceOrder = new Queue<Tuple<string, string>>();
    private string? _selectedView;
    private long _version,
        _sequence,
        _clock,
        _contentVersion,
        _bytes;

    // 仅用于拒绝旧界面事件，不是生产者版本或流程运行身份。
    private object _collection = new object();
    private bool _disposed;

    /// <summary>创建独立浏览会话，由宿主最终释放会话拥有的预览租约。</summary>
    /// <param name="options">可选保留上限。</param>
    public ResultBrowserSession(ResultBrowserOptions? options = null) =>
        _options = options ?? new ResultBrowserOptions();

    /// <summary>单调递增的界面状态版本，供UI合并刷新。</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                Alive();
                return _version;
            }
        }
    }

    /// <summary>不含源租约的只读界面快照。</summary>
    public ResultBrowserSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                Alive();
                return SnapshotLocked();
            }
        }
    }

    /// <inheritdoc/>
    public bool SetViews(IEnumerable<VisionView> views)
    {
        if (views == null)
            throw new ArgumentNullException(nameof(views), "视图集合不能为空；清空请传入空集合。");
        // 在锁外枚举外部集合，并限制枚举长度。提交的先后以进入接受锁的顺序为准。
        lock (_gate)
        {
            if (_disposed)
                return false;
        }
        var inputs = views.Take(_options.MaximumViews + 1).ToArray();
        if (inputs.Length > _options.MaximumViews)
            return false;
        if (
            inputs.Any(v => v == null)
            || inputs.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() != inputs.Length
        )
            throw new ArgumentException("视图不得为空且视图键必须唯一。", nameof(views));
        long geometry = inputs.Sum(v => v.Overlay.Layers.Sum(l => l.Visuals.Sum(GeometryCost)));
        if (geometry > _options.MaximumGeometryElements)
            return false;
        var release = new List<IImageSource>();
        try
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;
                var next = new List<ViewSlot>();
                try
                {
                    foreach (var input in inputs)
                    {
                        var info = input.Image.Info;
                        var previous = _views.FirstOrDefault(v =>
                            v.Id == input.Id && v.FrameId == input.FrameId
                        );
                        bool samePixels = previous?.Source?.Info == info;
                        var view = new ViewSlot
                        {
                            Id = input.Id,
                            Name = input.Name,
                            FrameId = input.FrameId,
                            Overlay = input.Overlay,
                            Bytes = info.ByteLength,
                            LastUse = previous?.LastUse ?? ++_clock,
                            ContentVersion = samePixels ? previous!.ContentVersion : ++_contentVersion,
                        };
                        next.Add(view);
                        if (view.Bytes <= _options.PreviewBytes)
                            view.Source = input.Image.Retain();
                    }
                }
                catch
                {
                    foreach (var view in next)
                        if (view.Source != null)
                            release.Add(view.Source);
                    throw;
                }
                foreach (var view in _views)
                    ReleaseView(view, release);
                _views = next;
                if (!_views.Any(v => v.Id == _selectedView))
                    _selectedView = _views.FirstOrDefault()?.Id;
                _bytes = _views.Where(v => v.Source != null).Sum(v => v.Bytes);
                TrimLocked(release);
                _collection = new object();
                _version++;
                return true;
            }
        }
        finally
        {
            ReleaseSources(release);
        }
    }

    /// <summary>清空全部视图和保留图像，保留图层偏好；UI在下次刷新释放画布租约。</summary>
    public void Clear() => SetViews(Array.Empty<VisionView>());

    /// <summary>选择已提交的视图，不执行算法或重新生成已淘汰的像素。</summary>
    /// <param name="viewId">集合内的稳定视图键。</param>
    public void SelectView(string viewId)
    {
        lock (_gate)
        {
            Alive();
            var view =
                _views.FirstOrDefault(v => v.Id == viewId)
                ?? throw new ArgumentException("视图集合中不存在此视图。", nameof(viewId));
            if (_selectedView == viewId)
                return;
            _selectedView = viewId;
            view.LastUse = ++_clock;
            _version++;
        }
    }

    /// <summary>修改当前视图的单层显示偏好，不修改原始CanvasLayer。</summary>
    /// <param name="layerId">当前视图中的稳定图层键。</param>
    /// <param name="visible">是否显示。</param>
    public void SetLayerVisible(string layerId, bool visible)
    {
        lock (_gate)
        {
            Alive();
            var view = SelectedView() ?? throw new InvalidOperationException("请先选择视图。");
            if (!view.Overlay.Layers.Any(l => l.Id == layerId))
                throw new ArgumentException("当前视图不存在此图层。", nameof(layerId));
            Remember(view, layerId, visible);
            _version++;
        }
    }

    /// <summary>对当前视图全选或全不选图层。</summary>
    /// <param name="visible">所有图层的目标可见性。</param>
    public void SetAllLayersVisible(bool visible)
    {
        lock (_gate)
        {
            Alive();
            var view = SelectedView();
            if (view == null)
                return;
            ForgetVisibility(view);
            foreach (var layer in view.Overlay.Layers)
                Remember(view, layer.Id, visible);
            _version++;
        }
    }

    /// <summary>恢复当前视图各层的默认显示状态，不影响其他视图偏好。</summary>
    public void ResetLayerVisibility()
    {
        lock (_gate)
        {
            Alive();
            var view = SelectedView();
            if (view == null)
                return;
            ForgetVisibility(view);
            _version++;
        }
    }

    internal ResultBrowserPresentation Capture()
    {
        lock (_gate)
        {
            Alive();
            var snapshot = SnapshotLocked();
            var view = SelectedView();
            if (view?.Source == null)
                return new ResultBrowserPresentation(snapshot, null, 0);
            var layers = view
                .Overlay.Layers.Select(l => new CanvasLayer(
                    l.Id,
                    l.Kind,
                    l.Visuals,
                    l.Order,
                    IsVisible(view, l),
                    l.Name
                ))
                .ToArray();
            var overlay = new GeometryOverlay(view.FrameId, layers);
            return new ResultBrowserPresentation(
                snapshot,
                new CanvasFrame(view.FrameId, checked(++_sequence), view.Source, overlay),
                view.ContentVersion
            );
        }
    }

    private ResultBrowserSnapshot SnapshotLocked()
    {
        var view = SelectedView();
        var views = _views
            .Select(v => new BrowserChoice(v.Id, v.Name + (v.Source == null ? "（预览未保留）" : "")))
            .ToArray();
        var layers =
            view?.Overlay.Layers.Select(l => new BrowserLayerChoice(l.Id, l.Name, IsVisible(view, l)))
                .ToArray()
            ?? Array.Empty<BrowserLayerChoice>();
        string status =
            view == null ? "暂无视图"
            : view.Source == null ? "预览未保留（像素预算限制）"
            : view.Name;
        return new ResultBrowserSnapshot(
            _version,
            _collection,
            view?.Id,
            Array.AsReadOnly(views),
            Array.AsReadOnly(layers),
            status,
            view?.Source != null,
            _bytes
        );
    }

    private static long GeometryCost(Visual visual) => Math.Max(1, visual.Geometry.ElementCount);

    private ViewSlot? SelectedView() => _views.FirstOrDefault(v => v.Id == _selectedView);

    private static Tuple<string, string> Key(ViewSlot view, string layer) => Tuple.Create(view.Id, layer);

    private bool IsVisible(ViewSlot view, CanvasLayer layer) =>
        _visibility.TryGetValue(Key(view, layer.Id), out bool value) ? value : layer.Visible;

    private void ForgetVisibility(ViewSlot view)
    {
        foreach (var layer in view.Overlay.Layers)
            _visibility.Remove(Key(view, layer.Id));
        var remaining = _preferenceOrder.Where(k => _visibility.ContainsKey(k)).ToArray();
        _preferenceOrder.Clear();
        foreach (var key in remaining)
            _preferenceOrder.Enqueue(key);
    }

    private void Remember(ViewSlot view, string layer, bool visible)
    {
        var key = Key(view, layer);
        if (!_visibility.ContainsKey(key))
        {
            while (_visibility.Count >= _options.MaximumPreferences)
                _visibility.Remove(_preferenceOrder.Dequeue());
            _preferenceOrder.Enqueue(key);
        }
        _visibility[key] = visible;
    }

    private void TrimLocked(List<IImageSource> release)
    {
        var selected = SelectedView();
        foreach (
            var view in _views
                .Where(v => v.Source != null)
                .OrderBy(v => ReferenceEquals(v, selected) ? 1 : 0)
                .ThenBy(v => v.LastUse)
        )
        {
            if (_bytes <= _options.PreviewBytes)
                break;
            ReleaseView(view, release);
        }
    }

    private void ReleaseView(ViewSlot view, List<IImageSource> release)
    {
        if (view.Source == null)
            return;
        release.Add(view.Source);
        view.Source = null;
        _bytes -= view.Bytes;
    }

    private static void ReleaseSources(IEnumerable<IImageSource> sources)
    {
        List<Exception>? errors = null;
        foreach (var source in sources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception error)
            {
                (errors ??= new List<Exception>()).Add(error);
            }
        }
        if (errors != null)
            throw new AggregateException("部分预览源释放失败。", errors);
    }

    private void Alive()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ResultBrowserSession), "结果浏览会话已释放。");
    }

    /// <summary>释放全部预览及偏好，不影响客户独立持有的源。</summary>
    public void Dispose()
    {
        var release = new List<IImageSource>();
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var view in _views)
                ReleaseView(view, release);
            _views.Clear();
            _visibility.Clear();
            _preferenceOrder.Clear();
        }
        ReleaseSources(release);
    }
}
