using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace TCPChat10.Glass;

/// <summary>注册到玻璃层的一个界面元素。</summary>
public sealed class GlassEntry
{
    public required FrameworkElement Element { get; init; }
    public double Radius { get; init; } = 12;

    /// <summary>重画时现取色调(主题会变, 所以用委托而不是固定值)。</summary>
    public required Func<(Color Tint, double Opacity)> Style { get; init; }

    /// <summary>11.7: 可选裁剪宿主(例如 ListHost: 气泡滚出消息列表时在此裁剪, 不会画到顶栏或输入栏上)。</summary>
    public FrameworkElement? ClipHost { get; init; }

    /// <summary>11.7: 是否为固定镀铬层(顶栏、输入栏等置顶画)。</summary>
    public bool IsChrome { get; init; }
}

/// <summary>
/// 玻璃层的宿主: 管一张整窗的 Win2D 画布 —— 底下铺背景图, 上面按注册的矩形画玻璃。
/// 界面元素只要 Register 一下(圆角 + 色调), 位置变化由 Refresh() 重新量。
/// </summary>
public sealed class GlassHost
{
    private readonly CanvasControl _canvas;
    private readonly LiquidGlassRenderer _renderer = new();
    private readonly List<GlassEntry> _entries = new();
    private readonly List<Surface> _current = new();
    private readonly List<Surface> _pending = new();

    private readonly record struct Surface(Rect Rect, Color Tint, double Opacity, double Radius, bool IsChrome);

    public GlassHost(CanvasControl canvas)
    {
        _canvas = canvas;
        canvas.CreateResources += OnCreateResources;
        canvas.Draw += OnDraw;
    }

    /// <summary>开关(关掉时整个画布不显示, 界面回到普通主题色)。</summary>
    public bool Enabled { get; private set; }

    public int Quality { get; private set; } = 60;
    public GlassParams Params { get; private set; } = GlassParams.For(60);

    /// <summary>给自检/底栏看的一句话。</summary>
    public string Status { get; private set; } = "初始化…";

    public double LastDrawMs => _renderer.LastDrawMs;
    public int SurfaceCount => _entries.Count;
    public bool Ready => _renderer.Ready;

    public void Register(GlassEntry entry) => Guard("注册玻璃面", () =>
    {
        // 11.5.2: 幂等 —— 列表回收气泡时会重新绑定 DataContext, 同一个 Border 可能被注册多次,
        // 重复注册只会让它在每次 Refresh 里被量好几遍(还会互相盖掉)。
        if (_entries.Any(e => ReferenceEquals(e.Element, entry.Element))) { Invalidate(); return; }
        _entries.Add(entry);
        Invalidate();
    });

    public void Unregister(FrameworkElement element) => Guard("移除玻璃面", () =>
    {
        if (_entries.RemoveAll(e => e.Element == element) > 0) Invalidate();
    });

    public void Clear() => Guard("清空玻璃面", () =>
    {
        _entries.Clear();
        Invalidate();
    });

    /// <summary>开/关 + 质量。两个都立即生效, 不用重启。</summary>
    public void Configure(bool enabled, int quality)
    {
        Enabled = enabled;
        Quality = Math.Clamp(quality, 0, 100);
        Params = GlassParams.For(Quality);
        ApplyDpi();
        Invalidate();
    }

    public void Invalidate()
    {
        // 画布已经隐藏/还没挂到可视树上时不要去催它重画 —— 这时候 Invalidate 可能抛
        // COMException, 而它常常是在 XAML 的回调(气泡 Loaded/Unloaded)里被调到的,
        // 抛出去就会变成"程序自己退出"(11.0 修)。
        try
        {
            if (!Enabled || _canvas.XamlRoot == null) return;
            if (_canvas.Visibility != Visibility.Visible) return;
            _canvas.Invalidate();
        }
        catch { }
    }

    /// <summary>画/量玻璃时出任何错都不能把程序带下去(设备丢失、驱动重置、显存不够…)。</summary>
    public string LastError { get; private set; } = "";

    private void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            LastError = what + ": " + ex.GetType().Name + " " + ex.Message;
            try { Status = "玻璃出错(已忽略): " + LastError; } catch { }
        }
    }

    /// <summary>
    /// 重新量一遍每个面的位置。滚动、换行、窗口缩放、气泡新增都会调到这里;
    /// 位置没变就不重画(否则每帧都在跑效果链)。
    /// </summary>
    public void Refresh() => Guard("量位置", RefreshCore);

    private void RefreshCore()
    {
        if (!Enabled || !_renderer.Ready)
        {
            if (_current.Count > 0) { _current.Clear(); Invalidate(); }
            return;
        }

        _pending.Clear();
        var snapshot = _entries.ToArray();
        foreach (var entry in snapshot)
        {
            var el = entry.Element;
            if (!el.IsLoaded || el.Visibility != Visibility.Visible) continue;
            if (el.ActualWidth < 8 || el.ActualHeight < 8) continue;

            Rect rect;
            try
            {
                var transform = el.TransformToVisual(_canvas);
                rect = transform.TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            }
            catch { continue; }

            if (rect.Width < 8 || rect.Height < 8) continue;

            // 视口裁剪: 滚出画布的面不画
            if (rect.Bottom <= 0 || rect.Right <= 0 ||
                rect.X >= _canvas.ActualWidth || rect.Y >= _canvas.ActualHeight) continue;

            // 11.7: 裁剪宿主(如 ListHost) —— 气泡滚出消息列表区域时被精准截断, 绝不侵入顶栏/输入栏
            if (entry.ClipHost != null && entry.ClipHost.IsLoaded && entry.ClipHost.Visibility == Visibility.Visible)
            {
                try
                {
                    var hostTransform = entry.ClipHost.TransformToVisual(_canvas);
                    var hostBounds = hostTransform.TransformBounds(new Rect(0, 0, entry.ClipHost.ActualWidth, entry.ClipHost.ActualHeight));

                    double x1 = Math.Max(rect.X, hostBounds.X);
                    double y1 = Math.Max(rect.Y, hostBounds.Y);
                    double x2 = Math.Min(rect.X + rect.Width, hostBounds.X + hostBounds.Width);
                    double y2 = Math.Min(rect.Y + rect.Height, hostBounds.Y + hostBounds.Height);

                    if (x2 <= x1 || y2 <= y1) continue;
                    rect = new Rect(x1, y1, x2 - x1, y2 - y1);
                }
                catch { }
            }

            if (rect.Width < 8 || rect.Height < 8) continue;

            var (tint, opacity) = entry.Style();
            _pending.Add(new Surface(rect, tint, opacity, entry.Radius, entry.IsChrome));
        }

        if (Same(_pending, _current)) return;
        _current.Clear();
        _current.AddRange(_pending);
        Invalidate();
    }

    /// <summary>自检用: 列出当前每个玻璃面的位置与色调。</summary>
    public IReadOnlyList<string> DescribeSurfaces()
    {
        var list = new List<string>();
        foreach (var s in _current)
            list.Add($"({s.Rect.X:F0},{s.Rect.Y:F0},{s.Rect.Width:F0}x{s.Rect.Height:F0}) #{s.Tint.R:X2}{s.Tint.G:X2}{s.Tint.B:X2}@{s.Opacity:F2}");
        return list;
    }

    /// <summary>
    /// 自检用: 每个注册过的玻璃面现在什么状态(画了 / 为什么没画)。
    /// 排查"某些消息没有玻璃"这类问题时, 一眼就能看出是被裁掉了还是压根没注册。
    /// </summary>
    public IReadOnlyList<string> DescribeEntries()
    {
        var list = new List<string>();
        foreach (var entry in _entries)
        {
            var el = entry.Element;
            double w = 0, h = 0;
            try { w = el.ActualWidth; h = el.ActualHeight; } catch { }

            string state;
            if (el.Visibility != Visibility.Visible) state = "没画:隐藏";
            else if (w < 8 || h < 8) state = $"没画:太小({w:F0}x{h:F0})";
            else
            {
                try
                {
                    var rect = el.TransformToVisual(_canvas).TransformBounds(new Rect(0, 0, w, h));
                    if (rect.Width < 8 || rect.Height < 8) state = "没画:矩形太小";
                    else if (rect.Bottom <= 0 || rect.Right <= 0 || rect.X >= _canvas.ActualWidth || rect.Y >= _canvas.ActualHeight)
                        state = $"没画:视口外({rect.X:F0},{rect.Y:F0},{rect.Width:F0}x{rect.Height:F0})";
                    else
                    {
                        var (tint, opacity) = entry.Style();
                        state = $"画 ({rect.X:F0},{rect.Y:F0},{rect.Width:F0}x{rect.Height:F0}) #{tint.R:X2}{tint.G:X2}{tint.B:X2}@{opacity:F2}";
                    }
                }
                catch (Exception ex) { state = "没画:定位失败 " + ex.GetType().Name; }
            }

            var vm = (el as FrameworkElement)?.DataContext as ViewModels.MessageVm;
            var who = vm == null ? "(无DataContext)" : (vm.IsSelf ? "自己" : "别人");
            list.Add($"{who} {state}  [{el.GetType().Name}]");
        }
        return list;
    }

    private static bool Same(List<Surface> a, List<Surface> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.IsChrome != y.IsChrome ||
                Math.Abs(x.Rect.X - y.Rect.X) > 0.5 || Math.Abs(x.Rect.Y - y.Rect.Y) > 0.5 ||
                Math.Abs(x.Rect.Width - y.Rect.Width) > 0.5 || Math.Abs(x.Rect.Height - y.Rect.Height) > 0.5 ||
                Math.Abs(x.Opacity - y.Opacity) > 0.01 || x.Tint != y.Tint || Math.Abs(x.Radius - y.Radius) > 0.5)
                return false;
        }
        return true;
    }

    private void ApplyDpi()
    {
        try
        {
            var scale = _canvas.XamlRoot?.RasterizationScale ?? 1.0;
            // 质量低的时候按更低的分辨率渲染: 效果链的中间纹理也随之变小, GPU 省一大截
            _canvas.DpiScale = (float)Math.Max(1.0, Math.Min(scale, Params.DpiCap));
        }
        catch { }
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        ApplyDpi();
        args.TrackAsyncAction(LoadAsync(sender).AsAsyncAction());
    }

    private async Task LoadAsync(CanvasControl sender)
    {
        try
        {
            var bitmap = await GlassWallpaper.LoadAsync(sender);
            _renderer.SetWallpaper(bitmap);
            Status = GlassWallpaper.Source + " · " + bitmap.SizeInPixels.Width + "×" + bitmap.SizeInPixels.Height;
            Invalidate();
        }
        catch (Exception ex)
        {
            Status = "玻璃不可用: " + ex.Message;
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!Enabled || !_renderer.Ready) return;
        Guard("绘制", () =>
        {
            var sw = Stopwatch.StartNew();
            DrawFrame(args.DrawingSession, new Size(sender.ActualWidth, sender.ActualHeight));
            _renderer.LastDrawMs = sw.Elapsed.TotalMilliseconds;
            Status = $"{_current.Count} 面 · {_renderer.LastDrawMs:F1} ms · {Params.Describe()}";
        });
    }

    /// <summary>真正画一帧: 背景图 + 所有玻璃面。</summary>
    private void DrawFrame(CanvasDrawingSession ds, Size size)
    {
        _renderer.DrawWallpaper(ds, size);
        var surfaces = _current.Count > 0 ? _current : _pending;

        // 11.7: 先画普通内容(气泡), 后画置顶 Chrome 条(顶栏、输入栏、状态栏)
        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            if (s.IsChrome) continue;
            _renderer.DrawSurface(ds, size, new GlassSurface
            {
                Rect = s.Rect,
                Radius = s.Radius,
                Tint = s.Tint,
                TintOpacity = s.Opacity,
            }, Params);
        }

        for (int i = 0; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            if (!s.IsChrome) continue;
            _renderer.DrawSurface(ds, size, new GlassSurface
            {
                Rect = s.Rect,
                Radius = s.Radius,
                Tint = s.Tint,
                TintOpacity = s.Opacity,
            }, Params);
        }
    }

    /// <summary>
    /// 把当前这一帧重画到一张 PNG 里。
    /// 桌面被别的窗口(全屏游戏之类)挡住时, PrintWindow/截屏都抓不到 Win2D 的内容, 用这个留证据。
    /// </summary>
    public async Task<bool> RenderToFileAsync(string path)
    {
        if (!_renderer.Ready) return false;
        float w = (float)_canvas.ActualWidth, h = (float)_canvas.ActualHeight;
        if (w < 16 || h < 16) return false;

        var target = new CanvasRenderTarget(_canvas, w, h, 96);
        var sw = Stopwatch.StartNew();
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            DrawFrame(ds, new Size(w, h));
        }
        await target.SaveAsync(path, CanvasBitmapFileFormat.Png);
        _renderer.LastDrawMs = sw.Elapsed.TotalMilliseconds;
        return true;   // 调用方(自检动作)自己 try/catch
    }
}
