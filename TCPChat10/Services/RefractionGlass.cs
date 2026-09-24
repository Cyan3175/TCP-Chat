using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics.Effects;
using Windows.Storage.Streams;

namespace TCPChat10.Services;

/// <summary>
/// "真玻璃"后端: 用 Composition 的 BackdropBrush 取到元素**后面**已经画好的画面,
/// 交给 Win2D 的效果图做 高斯模糊 → 边缘折射(DisplacementMap) → 提饱和, 再垫回元素底下。
/// 这是苹果液态玻璃的关键: 背景真的被糊掉、并且在玻璃边缘被"折射"弯曲。
///
/// 环境要求: 能创建 D3D11 设备(装显卡驱动中 / 远程桌面 / 虚拟机里可能拿不到)。
/// 启动时先 Probe(), 失败就整套退回画刷后端 —— 这就是"启动时分开两套"。
/// </summary>
public sealed class RefractionGlass : IDisposable
{
    private readonly CanvasDevice _device;
    private readonly Compositor _compositor;
    /// <summary>元素 -> 这层玻璃的 SpriteVisual(尺寸变化时要跟着改)。</summary>
    private readonly ConditionalWeakTable<FrameworkElement, SpriteVisual> _applied = new();
    private readonly Dictionary<FrameworkElement, CompositionRoundedRectangleGeometry> _clips = new();

    public RefractionGlass(CanvasDevice device, Compositor compositor)
    {
        _device = device;
        _compositor = compositor;
    }

    /// <summary>最近一次失败的原因(排查"玻璃没出来"用)。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// 折射取样的来源。
    /// 原本想用 Compositor.CreateBackdropBrush() 直接取"元素后面的画面", 但在 WinUI 3 里
    /// 它取的是**窗口背后**的后备背景(我们的窗口是不透明的, 于是什么都取不到 —— 实测垫上去是透明的)。
    /// 所以改成: 把壁纸层(彩色渐变那个 Grid)做成一张 CompositionVisualSurface,
    /// 按元素相对壁纸的位置平移取样 —— 玻璃折射/模糊的就是它底下那块壁纸。
    /// </summary>
    public FrameworkElement? SourceElement { get; set; }
    private Visual? _sourceVisual;
    private Vector2 _sourceSize;
    private Vector2 _wallpaperPixels;
    private CompositionSurfaceBrush? _wallpaperBrush;

    /// <summary>告诉折射后端"背景"是哪一块(壁纸层), 以及它的尺寸; 换主题后要重新调一次。</summary>
    public void SetSource(FrameworkElement element)
    {
        SourceElement = element;
        _sourceVisual = ElementCompositionPreview.GetElementVisual(element);
        _sourceSize = new Vector2((float)Math.Max(1, element.ActualWidth), (float)Math.Max(1, element.ActualHeight));
        _wallpaperBrush = null;      // 主题/尺寸变了, 壁纸位图重新画
        _wallpaperPixels = Vector2.Zero;
    }

    /// <summary>调试用: 只垫一块纯色 / 只做效果不做背景取样, 用来定位到底是哪一层没渲染。</summary>
    public bool ApplyDebug(FrameworkElement element, string kind)
    {
        try
        {
            Clear(element);
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Size = CurrentSize(element);
            sprite.Brush = kind switch
            {
                "color" => _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(160, 255, 0, 0)),
                _ => CreateSolidSourceBlurBrush(),
            };
            ElementCompositionPreview.SetElementChildVisual(element, sprite);
            _applied.Remove(element);
            _applied.Add(element, sprite);
            element.SizeChanged -= OnSizeChanged;
            element.SizeChanged += OnSizeChanged;
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>效果图固定画源(不取样背景), 只验证 Win2D 效果能不能出场。</summary>
    private CompositionBrush CreateSolidSourceBlurBrush()
    {
        var blur = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = 20f,
            Source = new CompositionEffectSourceParameter("src"),
        };
        var brush = _compositor.CreateEffectFactory(blur).CreateBrush();
        brush.SetSourceParameter("src", _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 255)));
        return brush;
    }

    public static RefractionGlass? TryCreate(Compositor compositor, out string reason)
    {
        try
        {
            // 没有硬件设备时会退到 WARP(软件光栅), 太慢, 这里当作不可用
            var device = CanvasDevice.GetSharedDevice();
            if (device == null) { reason = "拿不到 D3D 设备"; return null; }
            reason = "D3D 设备就绪";
            return new RefractionGlass(device, compositor);
        }
        catch (Exception ex)
        {
            reason = "D3D 设备创建失败: " + ex.Message;
            return null;
        }
    }

    /// <summary>给元素垫一层"真玻璃"。</summary>
    public bool Apply(FrameworkElement element, int quality)
    {
        try
        {
            Clear(element);

            var hostVisual = ElementCompositionPreview.GetElementVisual(element);
            var sprite = _compositor.CreateSpriteVisual();
            var size = CurrentSize(element);
            sprite.Size = size;

            // 圆角裁剪: 不然高斯模糊会糊到圆角外面去
            var radius = element is Microsoft.UI.Xaml.Controls.Border b && b.CornerRadius.TopLeft > 0
                ? (float)b.CornerRadius.TopLeft
                : 16f;
            var geometry = _compositor.CreateRoundedRectangleGeometry();
            geometry.Size = size;
            geometry.CornerRadius = new Vector2(radius, radius);
            sprite.Clip = _compositor.CreateGeometricClip(geometry);
            _clips[element] = geometry;

            if (_sourceVisual != null && SourceElement != null)
                _sourceSize = new Vector2((float)Math.Max(1, SourceElement.ActualWidth), (float)Math.Max(1, SourceElement.ActualHeight));

            sprite.Brush = CreateGlassBrush(quality, element);
            ElementCompositionPreview.SetElementChildVisual(element, sprite);

            _applied.Remove(element);
            _applied.Add(element, sprite);
            element.SizeChanged -= OnSizeChanged;
            element.SizeChanged += OnSizeChanged;
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    public void Clear(FrameworkElement element)
    {
        try
        {
            ElementCompositionPreview.SetElementChildVisual(element, null);
            _applied.Remove(element);
            _clips.Remove(element);
        }
        catch { }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement element || !_applied.TryGetValue(element, out var sprite)) return;
        var size = new Vector2((float)Math.Max(1, e.NewSize.Width), (float)Math.Max(1, e.NewSize.Height));
        sprite.Size = size;
        if (_clips.TryGetValue(element, out var clip)) clip.Size = size;
    }

    private static Vector2 CurrentSize(FrameworkElement element) =>
        new((float)Math.Max(1, element.ActualWidth), (float)Math.Max(1, element.ActualHeight));

    // ---------- 效果图 ----------

    /// <summary>质量档位对应的参数(苹果那套: 边缘折射 + 背景模糊 + 提饱和)。</summary>
    private static (float Blur, float Refract, float Saturation) Tuning(int quality)
    {
        var s = (float)GlassScale.Strength(quality);
        return (Blur: 8f + 26f * s, Refract: 10f + 26f * s, Saturation: 1.25f + 0.55f * s);
    }

    private CompositionBrush CreateGlassBrush(int quality, FrameworkElement element)
    {
        var (blurAmount, refract, saturation) = Tuning(quality);

        IGraphicsEffectSource source = new CompositionEffectSourceParameter("backdrop");
        var blur = new GaussianBlurEffect
        {
            Name = "GlassBlur",
            BlurAmount = blurAmount,
            BorderMode = EffectBorderMode.Hard,
            Source = source,
        };
        var sat = new SaturationEffect
        {
            Name = "GlassSaturation",
            Saturation = saturation,
            Source = blur,
        };
        var lens = new DisplacementMapEffect
        {
            Name = "GlassLens",
            Source = sat,
            Displacement = new CompositionEffectSourceParameter("lens"),
            Amount = refract,
        };

        var factory = _compositor.CreateEffectFactory(lens);
        var brush = factory.CreateBrush();
        brush.SetSourceParameter("backdrop", CreateSourceBrush(element));
        brush.SetSourceParameter("lens", CreateLensBrush());
        return brush;
    }

    /// <summary>
    /// 取样源: 壁纸层里"正好垫在这个元素底下"的那一块。
    ///
    /// 实测 WinUI 3 里 CreateBackdropBrush() 取不到窗口内的内容(它取的是窗口后面的桌面),
    /// CompositionVisualSurface 拿 XAML 元素的 visual 也画不出来, 所以这里自己把壁纸画成一张位图
    /// (颜色直接从主题里读, 和 XAML 那层壁纸一致), 再用 SurfaceBrush 平移对齐到元素底下。
    /// 效果一样: 玻璃底下糊掉/折射的就是它自己那块壁纸。
    /// </summary>
    private CompositionBrush CreateSourceBrush(FrameworkElement element)
    {
        var surfaceBrush = EnsureWallpaperBrush();
        if (surfaceBrush == null) return _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        var brush = surfaceBrush.Compositor.CreateSurfaceBrush(surfaceBrush.Surface);
        brush.Stretch = CompositionStretch.None;
        try
        {
            var offset = SourceElement != null
                ? element.TransformToVisual(SourceElement).TransformPoint(new Windows.Foundation.Point(0, 0))
                : new Windows.Foundation.Point(0, 0);
            var scale = _sourceSize.X > 1 ? _wallpaperPixels.X / _sourceSize.X : 1f;
            var m = System.Numerics.Matrix3x2.CreateScale(scale);
            m *= System.Numerics.Matrix3x2.CreateTranslation((float)-offset.X * scale, (float)-offset.Y * scale);
            brush.TransformMatrix = m;
        }
        catch { }
        return brush;
    }

    // ---------- 壁纸位图(玻璃的取样源) ----------

    /// <summary>用 WallpaperImage 那张位图(屏幕上贴的也是它, 天然对齐)。</summary>
    private CompositionSurfaceBrush? EnsureWallpaperBrush()
    {
        if (_wallpaperBrush != null) return _wallpaperBrush;
        try
        {
            if (WallpaperImage.Surface == null) WallpaperImage.Refresh();
            if (WallpaperImage.Surface == null) return null;
            _wallpaperPixels = new Vector2((float)WallpaperImage.PixelSize.Width, (float)WallpaperImage.PixelSize.Height);
            _wallpaperBrush = _compositor.CreateSurfaceBrush(WallpaperImage.Surface);
            _wallpaperBrush.Stretch = CompositionStretch.None;
            return _wallpaperBrush;
        }
        catch (Exception ex)
        {
            LastError = "壁纸位图取用失败: " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// 折射贴图: 圆角矩形的"边缘透镜"法线图。
    /// 中间是平的(RG = 0.5), 靠近边缘处法线朝外倾斜, 于是背景在边上被弯折 —— 这就是玻璃的厚度感。
    /// </summary>
    private CompositionBrush CreateLensBrush()
    {
        const int N = 128;
        var pixels = new byte[N * N * 4];
        for (int y = 0; y < N; y++)
        {
            for (int x = 0; x < N; x++)
            {
                var (nx, ny) = LensNormal((x + 0.5f) / N, (y + 0.5f) / N);
                var i = (y * N + x) * 4;
                pixels[i + 0] = (byte)Math.Clamp(128 + nx * 127f, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(128 + ny * 127f, 0, 255);
                pixels[i + 2] = 128;
                pixels[i + 3] = 255;
            }
        }

        var stream = new InMemoryRandomAccessStream();
        var encoder = Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Straight, N, N, 96, 96, pixels);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        stream.Seek(0);

        var surface = Microsoft.UI.Xaml.Media.LoadedImageSurface.StartLoadFromStream(stream);
        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        return brush;
    }

    /// <summary>圆角矩形的边缘法线(u,v 是 0..1 的归一化坐标)。</summary>
    private static (float X, float Y) LensNormal(float u, float v)
    {
        // 把归一化坐标换算成"到最近边缘的距离"(圆角处按圆算)
        const float radius = 0.35f;      // 归一化圆角半径
        const float bevel = 0.28f;       // 边缘倒角宽度(法线在这段里从 0 增到最大)

        float dx = Math.Min(u, 1f - u);
        float dy = Math.Min(v, 1f - v);
        float d = Math.Min(dx, dy);
        if (d >= bevel) return (0f, 0f);          // 玻璃中间是平的

        // 朝外的方向(离哪条边近就往哪边弯)
        float sx = dx <= dy ? (u < 0.5f ? -1f : 1f) : 0f;
        float sy = dy < dx ? (v < 0.5f ? -1f : 1f) : 0f;
        if (sx == 0f && sy == 0f) sy = v < 0.5f ? -1f : 1f;

        // 圆角区: 方向沿对角线, 保证四个角是圆润的
        bool inCornerX = dx < radius, inCornerY = dy < radius;
        if (inCornerX && inCornerY)
        {
            float cx = u < 0.5f ? radius : 1f - radius;
            float cy = v < 0.5f ? radius : 1f - radius;
            var vx = u - cx;
            var vy = v - cy;
            var len = MathF.Sqrt(vx * vx + vy * vy);
            if (len > 1e-4f)
            {
                sx = vx / len;
                sy = vy / len;
                d = Math.Clamp(radius - len, 0f, bevel);
            }
        }

        // 越靠边弯得越厉害(1 - t)^1.5 的坡度, 接近苹果那种"边缘迅速收进去"的手感
        var t = 1f - Math.Clamp(d / bevel, 0f, 1f);
        var strength = MathF.Pow(t, 1.5f);
        return (sx * strength, sy * strength);
    }

    public void Dispose()
    {
        _applied.Clear();
        _clips.Clear();
    }
}
