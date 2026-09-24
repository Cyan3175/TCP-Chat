using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TCPChat10.Services;

/// <summary>
/// 苹果"液态玻璃"(Liquid Glass)观感。
///
/// 说明: Windows App SDK 2.x 只开放了 Composition 的 SceneLightingEffect, 没有高斯模糊效果
/// (Microsoft.UI.Composition.Effects 里能用的就两个接口), 拿不到"把面板后面的东西糊掉"的能力。
/// 好在我们的背景是自己画的柔和色块壁纸 —— 本来就是糊的, 所以玻璃感用这套做法达成:
///   半透明染色(上亮下暗, 像玻璃接光) + 上边缘高光描边 + 浮起来的圆角面板,
///   底下的彩色壁纸透过面板微微染色的部分就是"折射"。
/// 质量滑杆控制磨砂浓度: 1 = 几乎全透明最清淡, 10 = 厚磨砂(更白/更暗、高光更亮)。
/// </summary>
public static class LiquidGlass
{
    /// <summary>当前是否启用(主窗口应用设置时写入, 气泡等显示模型直接读)。</summary>
    public static bool Enabled { get; set; }

    /// <summary>当前质量(1-10, 见 GlassScale)。</summary>
    public static int Quality { get; set; } = GlassScale.Default;

    /// <summary>面板底色: 上亮下暗的半透明染色, 质量越高越厚。</summary>
    public static Brush PanelTint(int quality)
    {
        var s = GlassScale.Strength(quality);
        var baseColor = ThemeLookup.Color("GlassTintColor");
        return Vertical(WithAlpha(baseColor, 0.26 + 0.44 * s),
                        WithAlpha(baseColor, 0.42 + 0.44 * s));
    }

    /// <summary>面板描边: 上边缘那一道接光的高光, 往下淡出。</summary>
    public static Brush PanelEdge(int quality)
    {
        var s = GlassScale.Strength(quality);
        var color = ThemeLookup.Color("GlassEdgeColor");
        return Vertical(WithAlpha(color, 0.40 + 0.50 * s),
                        WithAlpha(color, 0.05 + 0.15 * s));
    }

    /// <summary>气泡底色: 自己发的偏蓝, 别人的偏中性, 都是半透明玻璃。</summary>
    public static Brush BubbleTint(bool self, int quality)
    {
        var s = GlassScale.Strength(quality);
        var color = ThemeLookup.Color(self ? "BubbleSelfColor" : "BubbleOtherColor");
        var top = self ? 0.66 + 0.26 * s : 0.46 + 0.36 * s;
        return Vertical(WithAlpha(color, top + 0.10), WithAlpha(color, top));
    }

    /// <summary>气泡的边缘高光。</summary>
    public static Brush BubbleEdge(bool self, int quality)
    {
        var s = GlassScale.Strength(quality);
        var color = ThemeLookup.Color(self ? "GlassEdgeColor" : "BubbleOtherEdgeColor");
        var top = self ? 0.30 + 0.30 * s : 0.45 + 0.35 * s;
        return Vertical(WithAlpha(color, top), WithAlpha(color, top * 0.25));
    }

    private static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    private static Brush Vertical(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        return brush;
    }
}
