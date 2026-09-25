using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TCPChat10.Glass;
using TCPChat10.Services;
using Windows.Foundation;
using Windows.UI;

namespace TCPChat10.Controls;

/// <summary>
/// 液态玻璃的公共部分: 一张 Win2D 画布 + 渲染器 + 背景采样位图。
/// 开关和滑块都在设置对话框里, 对话框是弹出层, 画不到主窗口的玻璃层上,
/// 所以这两个控件自己画玻璃 —— 采样源仍然是那张背景图(桌面壁纸), 看起来就像透过卡片折射。
/// </summary>
public abstract class GlassControlBase : UserControl
{
    protected readonly CanvasControl Surface;
    private readonly LiquidGlassRenderer _renderer = new();
    private bool _loading;

    protected GlassControlBase()
    {
        Surface = new CanvasControl { ClearColor = Colors.Transparent };
        Surface.CreateResources += OnCreateResources;
        Surface.Draw += (s, e) => OnDraw(e.DrawingSession, new Size(s.ActualWidth, s.ActualHeight));
        Content = Surface;
    }

    /// <summary>玻璃参数: 固定用偏高的质量(控件很小, 开销可以忽略)。</summary>
    protected static GlassParams Params => GlassParams.For(85);

    protected bool Ready => _renderer.Ready;

    protected void Redraw() => Surface.Invalidate();

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (_loading || _renderer.Ready) return;
        _loading = true;
        args.TrackAsyncAction(LoadAsync(sender).AsAsyncAction());
    }

    private async Task LoadAsync(CanvasControl sender)
    {
        try
        {
            var bitmap = await GlassWallpaper.LoadAsync(sender);
            _renderer.SetWallpaper(bitmap);
            Redraw();
        }
        catch { /* 拿不到背景图就什么都不画, 控件本身仍可用 */ }
        finally { _loading = false; }
    }

    /// <summary>画一个玻璃面(底色不画, 只画玻璃本身)。</summary>
    protected void DrawGlass(CanvasDrawingSession ds, Size size, Rect rect, double radius, Color tint, double opacity)
    {
        if (!_renderer.Ready) return;
        _renderer.DrawSurface(ds, size, new GlassSurface
        {
            Rect = rect,
            Radius = radius,
            Tint = tint,
            TintOpacity = opacity,
        }, Params);
    }

    protected abstract void OnDraw(CanvasDrawingSession ds, Size size);

    /// <summary>自检用: 把这块玻璃控件自己重画到 PNG(弹出的对话框里 Win2D 内容截屏抓不到)。</summary>
    public async Task<bool> RenderToFileAsync(string path)
    {
        float w = (float)Surface.ActualWidth, h = (float)Surface.ActualHeight;
        if (w < 8 || h < 8) return false;
        var target = new CanvasRenderTarget(Surface, w, h, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            OnDraw(ds, new Size(w, h));
        }
        await target.SaveAsync(path, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
        return true;
    }

    protected static bool Dark => ThemeLookup.IsDark;
}
