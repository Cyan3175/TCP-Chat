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

    private readonly record struct Surface(Rect Rect, Color Tint, double Opacity, double Radius);

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

    public void Register(GlassEntry entry)
    {
        _entries.Add(entry);
        Invalidate();
    }

    public void Unregister(FrameworkElement element)
    {
        var n = _entries.RemoveAll(e => e.Element == element);
        if (n > 0) Invalidate();
    }

    public void Clear()
    {
        _entries.Clear();
        Invalidate();
    }

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
        try { _canvas.Invalidate(); } catch { }
    }

    /// <summary>
    /// 重新量一遍每个面的位置。滚动、换行、窗口缩放、气泡新增都会调到这里;
    /// 位置没变就不重画(否则每帧都在跑效果链)。
    /// </summary>
    public void Refresh()
    {
        if (!Enabled || !_renderer.Ready)
        {
            if (_current.Count > 0) { _current.Clear(); Invalidate(); }
            return;
        }

        _pending.Clear();
        foreach (var entry in _entries)
        {
            var el = entry.Element;
            if (el.Visibility != Visibility.Visible) continue;
            if (el.ActualWidth < 8 || el.ActualHeight < 8) continue;

            Rect rect;
            try
            {
                var transform = el.TransformToVisual(_canvas);
                rect = transform.TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            }
            catch { continue; }

            if (rect.Width < 8 || rect.Height < 8) continue;

            // 视口裁剪: 滚出去的面不画(列表里几十个气泡时, 这一条能省掉大半开销)
            if (rect.Bottom <= 0 || rect.Right <= 0 ||
                rect.X >= _canvas.ActualWidth || rect.Y >= _canvas.ActualHeight) continue;

            var (tint, opacity) = entry.Style();
            _pending.Add(new Surface(rect, tint, opacity, entry.Radius));
        }

        if (Same(_pending, _current)) return;
        _current.Clear();
        _current.AddRange(_pending);
        Invalidate();
    }

    private static bool Same(List<Surface> a, List<Surface> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (Math.Abs(x.Rect.X - y.Rect.X) > 0.5 || Math.Abs(x.Rect.Y - y.Rect.Y) > 0.5 ||
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
        var sw = Stopwatch.StartNew();
        DrawFrame(args.DrawingSession, new Size(sender.ActualWidth, sender.ActualHeight));
        _renderer.LastDrawMs = sw.Elapsed.TotalMilliseconds;
        Status = $"{_current.Count} 面 · {_renderer.LastDrawMs:F1} ms · {Params.Describe()}";
    }

    /// <summary>真正画一帧: 背景图 + 所有玻璃面。</summary>
    private void DrawFrame(CanvasDrawingSession ds, Size size)
    {
        _renderer.DrawWallpaper(ds, size);
        var surfaces = _current.Count > 0 ? _current : _pending;
        foreach (var s in surfaces)
        {
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
        return true;
    }
}
