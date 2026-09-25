using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace TCPChat10.Glass;

/// <summary>
/// 玻璃要"透过"的东西 —— 也就是背景图。
/// 优先用系统的桌面壁纸(和 liquid-glass 参考实现里的 Assets/home.jpg 一个角色),
/// 读不到就自己生成一张彩色渐变, 保证任何机器上都有东西可折射。
/// </summary>
public static class GlassWallpaper
{
    /// <summary>最近一次用的是哪种来源(给自检/底栏看)。</summary>
    public static string Source { get; private set; } = "未加载";

    /// <summary>桌面壁纸文件(Windows 会把当前壁纸转码成这张没有扩展名的 jpg)。</summary>
    public static string? DesktopWallpaperPath()
    {
        try
        {
            var transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            if (File.Exists(transcoded) && new FileInfo(transcoded).Length > 1024) return transcoded;

            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string path && File.Exists(path)) return path;
        }
        catch { }
        return null;
    }

    /// <summary>加载背景图(Win2D 位图)。canvas 必须已经可以创建资源。</summary>
    public static async Task<CanvasBitmap> LoadAsync(ICanvasResourceCreator canvas)
    {
        var path = DesktopWallpaperPath();
        if (path != null)
        {
            try
            {
                var bmp = await CanvasBitmap.LoadAsync(canvas, path);
                Source = "桌面壁纸 " + Path.GetFileName(path) + $" ({bmp.SizeInPixels.Width}×{bmp.SizeInPixels.Height})";
                return bmp;
            }
            catch { /* 读不了就自己生成 */ }
        }

        Source = "程序生成";
        return Generate(canvas);
    }

    /// <summary>生成一张 1280×800 的彩色渐变当背景(不需要外部素材, 单文件 exe 里也不占地方)。</summary>
    private static CanvasBitmap Generate(ICanvasResourceCreator canvas)
    {
        const int w = 1280, h = 800;
        var target = new CanvasRenderTarget(canvas, w, h, 96);

        // 参考实现用的是蓝色调的玻璃; 这里用一支冷色 + 一支暖色的斜向渐变, 折射时更有层次
        var stops = new CanvasGradientStop[]
        {
            new() { Position = 0.00f, Color = Color.FromArgb(255, 0x1B, 0x2A, 0x4A) },
            new() { Position = 0.35f, Color = Color.FromArgb(255, 0x2E, 0x5C, 0x8A) },
            new() { Position = 0.62f, Color = Color.FromArgb(255, 0x6E, 0x4B, 0x97) },
            new() { Position = 1.00f, Color = Color.FromArgb(255, 0xD9, 0x6B, 0x5B) },
        };

        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 0x14, 0x1B, 0x2E));
            var brush = new CanvasLinearGradientBrush(ds, stops)
            {
                StartPoint = new System.Numerics.Vector2(0, 0),
                EndPoint = new System.Numerics.Vector2(w, h),
            };
            ds.FillRectangle(new Rect(0, 0, w, h), brush);

            // 几个光斑, 让模糊/扭曲看得出方向感
            var blobs = new (float X, float Y, float R, Color C)[]
            {
                (0.22f, 0.28f, 260, Color.FromArgb(150, 0x8F, 0xD4, 0xFF)),
                (0.78f, 0.22f, 210, Color.FromArgb(130, 0xFF, 0xD8, 0x8F)),
                (0.62f, 0.78f, 300, Color.FromArgb(120, 0xFF, 0x8F, 0xB0)),
                (0.18f, 0.82f, 240, Color.FromArgb(120, 0x9B, 0xFF, 0xD0)),
            };
            foreach (var b in blobs)
            {
                var radial = new CanvasRadialGradientBrush(ds, new CanvasGradientStop[]
                {
                    new() { Position = 0.0f, Color = b.C },
                    new() { Position = 1.0f, Color = Color.FromArgb(0, b.C.R, b.C.G, b.C.B) },
                })
                {
                    Center = new System.Numerics.Vector2(w * b.X, h * b.Y),
                    RadiusX = b.R,
                    RadiusY = b.R,
                };
                ds.FillRectangle(new Rect(0, 0, w, h), radial);
            }
        }
        return target;
    }
}
