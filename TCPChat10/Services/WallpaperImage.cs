using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace TCPChat10.Services;

/// <summary>
/// 壁纸位图: 程序自己画的一张彩色渐变图。
///
/// 为什么要有它: 液态玻璃要"糊掉/折射底下的东西", 但实测 WinUI 3 里
/// Compositor.CreateBackdropBrush() 取不到窗口内的内容(它取的是窗口后面的桌面),
/// CompositionVisualSurface 也画不出 XAML 元素的 visual。
/// 所以干脆让**屏幕上的壁纸**和**玻璃取样的源**用同一张自己画的位图 —— 两边天然对齐, 玻璃感一模一样。
/// 换主题时重新生成。
/// </summary>
public static class WallpaperImage
{
    private const int W = 640, H = 480;

    /// <summary>屏幕上那张(XAML ImageBrush 用)。</summary>
    public static ImageSource? XamlSource { get; private set; }

    /// <summary>Composition 取样用的那张。</summary>
    public static LoadedImageSurface? Surface { get; private set; }

    /// <summary>像素尺寸(算取样对齐用)。</summary>
    public static Windows.Foundation.Size PixelSize => new(W, H);

    /// <summary>调试用: 画成高对比棋盘格, 这样"玻璃有没有真的糊掉背景"一眼就能看出来。</summary>
    public static bool TestPattern { get; set; }

    /// <summary>按当前主题重新生成(重复调用会重建, 换主题后要调一次)。</summary>
    public static void Refresh()
    {
        try
        {
            var bytes = Render();
            var stream = new InMemoryRandomAccessStream();
            stream.AsStreamForWrite().Write(bytes, 0, bytes.Length);
            stream.Seek(0);

            var bmp = new BitmapImage();
            bmp.SetSource(stream);
            XamlSource = bmp;

            stream.Seek(0);
            Surface = LoadedImageSurface.StartLoadFromStream(stream);
        }
        catch
        {
            XamlSource = null;
            Surface = null;
        }
    }

    /// <summary>把渐变 + 三个色块画成 BGRA 像素, 再编码成 PNG。</summary>
    private static byte[] Render()
    {
        var px = new byte[W * H * 4];

        var gradient = ThemeLookup.Brush("WallpaperBrush") as LinearGradientBrush;
        var c0 = gradient is { GradientStops.Count: > 0 }
            ? gradient.GradientStops[0].Color
            : Windows.UI.Color.FromArgb(255, 238, 243, 255);
        var c1 = gradient is { GradientStops.Count: > 1 }
            ? gradient.GradientStops[1].Color
            : Windows.UI.Color.FromArgb(255, 253, 243, 248);

        var blobs = new[]
        {
            (Color: ThemeLookup.Color("BlobColor1"), Cx: 0.04f, Cy: -0.10f, R: 0.62f),
            (Color: ThemeLookup.Color("BlobColor2"), Cx: 1.05f, Cy: 0.42f, R: 0.70f),
            (Color: ThemeLookup.Color("BlobColor3"), Cx: -0.05f, Cy: 1.06f, R: 0.60f),
        };

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                var u = (float)x / (W - 1);
                var v = (float)y / (H - 1);
                var t = Math.Clamp((u + v) * 0.5f, 0f, 1f);
                float r = c0.R + (c1.R - c0.R) * t;
                float g = c0.G + (c1.G - c0.G) * t;
                float b = c0.B + (c1.B - c0.B) * t;

                if (TestPattern)
                {
                    var on = (((x / 20) + (y / 20)) % 2) == 0;
                    var lum = on ? 235f : 35f;
                    r = g = b = lum;
                }

                foreach (var blob in blobs)
                {
                    var dx = u - blob.Cx;
                    var dy = (v - blob.Cy) * (float)H / W;
                    var d = MathF.Sqrt(dx * dx + dy * dy) / blob.R;
                    if (d >= 1f) continue;
                    var a = (1f - d) * (1f - d) * (blob.Color.A / 255f);
                    r = r * (1 - a) + blob.Color.R * a;
                    g = g * (1 - a) + blob.Color.G * a;
                    b = b * (1 - a) + blob.Color.B * a;
                }

                var i = (y * W + x) * 4;
                px[i + 0] = (byte)Math.Clamp(b, 0, 255);
                px[i + 1] = (byte)Math.Clamp(g, 0, 255);
                px[i + 2] = (byte)Math.Clamp(r, 0, 255);
                px[i + 3] = 255;
            }
        }

        using var ms = new MemoryStream();
        var enc = Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ms.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
        enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Straight, W, H, 96, 96, px);
        enc.FlushAsync().AsTask().GetAwaiter().GetResult();
        return ms.ToArray();
    }
}
