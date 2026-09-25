using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace TCPChat10.Glass;

/// <summary>一个玻璃面: 画面上的矩形 + 圆角 + 色调。</summary>
public sealed class GlassSurface
{
    public Rect Rect { get; set; }
    public double Radius { get; set; } = 12;
    public Color Tint { get; set; } = Color.FromArgb(255, 255, 255, 255);
    public double TintOpacity { get; set; } = 0.45;
}

/// <summary>
/// 液态玻璃的渲染核心 —— 从 liquid-glass-WinUI 参考实现移植:
///
///   背景采样 → 高斯模糊 → 湍流噪声 →(边缘遮罩)→ 位移贴图 → 叠色调 → 饱和度/对比度
///
/// 参考实现固定用一个 300×200 的 CanvasControl 采样 Assets/home.jpg; 这里改成
/// "一整张画布上画背景图 + 若干个玻璃面", 每个面自己决定位置/圆角/色调, 这样
/// 顶栏、输入栏、状态栏、气泡都能变成玻璃。
/// 质量滑块影响: 湍流层数、模糊倍率、是否做色彩增强、渲染分辨率上限。
///
/// 注意: 效果链每个面每帧重建一次(和参考实现一样)。
/// 试过缓存复用, 但 Win2D 的命令列表"当过图像就不能再录", 换 Source 之后
/// 尺寸校验会抛 ArgumentException, 所以宁可每帧重建 —— 实测内存稳定(几百 MB 不涨),
/// 真正的开销大头是 GPU 上的中间纹理, 已经用"位置不变就不重画 + 视口裁剪"压住了。
/// </summary>
public sealed class LiquidGlassRenderer
{
    /// <summary>采样时向外多取一圈, 位移扭曲才不会在边缘把内容撕掉。</summary>
    private const float Overscan = 42f;

    private const float BaseFrostBlur = 2.0f;          // 参考实现: Frost Blur = 2
    private const float NoiseFrequency = 0.008f;       // 参考实现: Noise Freq = 0.008
    private const float Distortion = 54f;              // 参考实现: 77 * 0.7
    private const float BaseSaturation = 1.08f;        // 参考实现: 1.08
    private const float BaseContrast = 0.08f;          // 参考实现: 0.08
    private const int NoiseSeed = 92;                  // 参考实现: 固定种子, 保证每次一样

    private CanvasBitmap? _wallpaper;
    private readonly Dictionary<string, CanvasCommandList> _masks = new();

    public bool Ready => _wallpaper != null;
    public double LastDrawMs { get; internal set; }

    public void SetWallpaper(CanvasBitmap bitmap)
    {
        _wallpaper = bitmap;
        foreach (var m in _masks.Values) { try { m.Dispose(); } catch { } }
        _masks.Clear();
    }

    /// <summary>把背景图按 cover 铺满画布。</summary>
    public void DrawWallpaper(CanvasDrawingSession ds, Size canvas)
    {
        var bmp = _wallpaper;
        if (bmp == null) return;
        var (scale, ox, oy) = CoverMath.Fit(canvas.Width, canvas.Height,
                                            bmp.SizeInPixels.Width, bmp.SizeInPixels.Height);
        var full = new Rect(0, 0, bmp.SizeInPixels.Width, bmp.SizeInPixels.Height);
        ds.DrawImage(bmp, new Rect(ox, oy, full.Width * scale, full.Height * scale), full);
    }

    /// <summary>画一个玻璃面。</summary>
    public void DrawSurface(CanvasDrawingSession ds, Size canvas, GlassSurface surface, GlassParams p)
    {
        var bmp = _wallpaper;
        if (bmp == null) return;
        var rect = surface.Rect;
        if (rect.Width < 8 || rect.Height < 8) return;

        float w = (float)rect.Width, h = (float)rect.Height;
        float o = Overscan;
        var want = new Rect(rect.X - o, rect.Y - o, w + 2 * o, h + 2 * o);
        float radius = (float)Math.Min(surface.Radius, Math.Min(w, h) / 2);

        // 采样用的命令列表: 用完就释放, 别攒着占显存
        var sample = new CanvasCommandList(ds);
        try
        {
            RecordSample(sample, canvas, want);

            // 1) 磨砂模糊
            var blur = new GaussianBlurEffect
            {
                Source = sample,
                BlurAmount = BaseFrostBlur * p.BlurScale,
                BorderMode = EffectBorderMode.Soft,
            };

            // 2) 湍流噪声 —— "液态"的来源
            var turbulence = new TurbulenceEffect
            {
                Frequency = new Vector2(NoiseFrequency, NoiseFrequency),
                Octaves = p.Octaves,
                Seed = NoiseSeed,
            };
            var turbulenceBlur = new GaussianBlurEffect
            {
                Source = turbulence,
                BlurAmount = p.TurbulenceBlur,
                BorderMode = EffectBorderMode.Soft,
            };

            // 3) 边缘遮罩: 越靠边位移越弱, 免得玻璃四周被撕开(参考实现用的是径向渐变)
            var mask = BuildMask(ds, want, radius, p);

            // 4) 噪声 × 遮罩
            var maskedNoise = new ArithmeticCompositeEffect
            {
                Source1 = turbulenceBlur,
                Source2 = mask,
                MultiplyAmount = 1f,
                Source1Amount = 0f,
                Source2Amount = 0f,
                Offset = 0f,
            };

            // 5) 位移贴图: 用噪声场去"推"模糊后的背景 = 折射
            var displacement = new DisplacementMapEffect
            {
                Source = blur,
                Displacement = maskedNoise,
                Amount = (float)Math.Min(Distortion, Math.Min(w, h) * 0.55),
                XChannelSelect = EffectChannelSelect.Red,
                YChannelSelect = EffectChannelSelect.Green,
            };

            // 6) 色调叠加 + 可选的饱和度/对比度增强
            ICanvasImage result = new BlendEffect
            {
                Background = displacement,
                Foreground = new ColorSourceEffect
                {
                    Color = Color.FromArgb(255, surface.Tint.R, surface.Tint.G, surface.Tint.B),
                },
                Mode = BlendEffectMode.Overlay,
            };
            if (p.ColorGrade)
            {
                result = new ContrastEffect
                {
                    Source = new SaturationEffect { Source = result, Saturation = BaseSaturation },
                    Contrast = BaseContrast,
                };
            }

            // 7) 按圆角裁出来, 铺一层色调(保证文字对比度), 再压高光和亮边
            using var clip = CanvasGeometry.CreateRoundedRectangle(ds, rect, radius, radius);
            using (ds.CreateLayer(1f, clip))
            {
                ds.DrawImage(result, want, new Rect(0, 0, want.Width, want.Height));

                var alpha = (byte)Math.Clamp(surface.TintOpacity * 255, 0, 255);
                if (alpha > 0)
                    ds.FillRectangle(rect, Color.FromArgb(alpha, surface.Tint.R, surface.Tint.G, surface.Tint.B));

                var sheen = new CanvasLinearGradientBrush(ds, new CanvasGradientStop[]
                {
                    new() { Position = 0.0f, Color = Color.FromArgb(38, 255, 255, 255) },
                    new() { Position = 0.45f, Color = Color.FromArgb(0, 255, 255, 255) },
                })
                {
                    StartPoint = new Vector2((float)rect.X, (float)rect.Y),
                    EndPoint = new Vector2((float)rect.X, (float)(rect.Y + rect.Height)),
                };
                ds.FillRectangle(rect, sheen);

                ds.DrawGeometry(clip, Color.FromArgb(70, 255, 255, 255), 1f);
            }
        }
        finally
        {
            try { sample.Dispose(); } catch { }
        }
    }

    /// <summary>把背景图对应的一块录进命令列表(坐标原点 = want 的左上角)。</summary>
    private void RecordSample(CanvasCommandList list, Size canvas, Rect want)
    {
        var bmp = _wallpaper!;
        var (scale, ox, oy) = CoverMath.Fit(canvas.Width, canvas.Height,
                                            bmp.SizeInPixels.Width, bmp.SizeInPixels.Height);
        var placed = new Rect(ox, oy, bmp.SizeInPixels.Width * scale, bmp.SizeInPixels.Height * scale);

        // 只画两张矩形真正重叠的部分 —— 源矩形一定落在位图里面, 不会越界采样
        double x1 = Math.Max(want.X, placed.X), y1 = Math.Max(want.Y, placed.Y);
        double x2 = Math.Min(want.X + want.Width, placed.X + placed.Width);
        double y2 = Math.Min(want.Y + want.Height, placed.Y + placed.Height);
        if (x2 <= x1 || y2 <= y1) return;

        var src = new Rect((x1 - ox) / scale, (y1 - oy) / scale, (x2 - x1) / scale, (y2 - y1) / scale);
        var dst = new Rect(x1 - want.X, y1 - want.Y, x2 - x1, y2 - y1);

        using var cds = list.CreateDrawingSession();
        cds.DrawImage(bmp, dst, src);
    }

    /// <summary>边缘遮罩: 中间全白、四周渐隐, 位移强度在边上自然衰减。</summary>
    private CanvasCommandList BuildMask(CanvasDrawingSession ds, Rect want, float radius, GlassParams p)
    {
        var key = $"{want.Width:0}x{want.Height:0}:{radius:0}:{p.MaskSteps}";
        if (_masks.TryGetValue(key, out var cached)) return cached;

        float fade = Math.Min(p.MaskSteps >= 3 ? 26f : 40f, Math.Min((float)want.Width, (float)want.Height) * 0.32f);
        var list = new CanvasCommandList(ds);

        using (var mds = list.CreateDrawingSession())
        {
            mds.Clear(Color.FromArgb(255, 0, 0, 0));
            var inner = new Rect(fade, fade, Math.Max(1, want.Width - 2 * fade), Math.Max(1, want.Height - 2 * fade));
            using var geo = CanvasGeometry.CreateRoundedRectangle(mds, inner, radius, radius);
            mds.FillGeometry(geo, Colors.White);
        }

        var soft = new GaussianBlurEffect
        {
            Source = list,
            BlurAmount = fade * 0.6f,
            BorderMode = EffectBorderMode.Soft,
        };

        var result = new CanvasCommandList(ds);
        using (var rds = result.CreateDrawingSession())
            rds.DrawImage(soft);

        if (_masks.Count > 24)
        {
            foreach (var m in _masks.Values) { try { m.Dispose(); } catch { } }
            _masks.Clear();
        }
        _masks[key] = result;
        return result;
    }
}
