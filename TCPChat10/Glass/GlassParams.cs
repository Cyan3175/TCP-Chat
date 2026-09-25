namespace TCPChat10.Glass;

/// <summary>
/// 效果参数(纯数据)。质量滑块 -&gt; 实际参数的映射放在这里, 不依赖 Win2D, 所以能离线测。
/// </summary>
public readonly struct GlassParams
{
    /// <summary>0~100, 来自设置里的滑块。</summary>
    public int Quality { get; init; }

    /// <summary>湍流层数: 越多细节越丰富, 也越慢(参考实现是固定 3)。</summary>
    public int Octaves { get; init; }

    /// <summary>磨砂模糊倍率(参考实现是 1.0)。</summary>
    public float BlurScale { get; init; }

    /// <summary>湍流再模糊一次, 让扭曲更"液态"(参考实现是 2.0)。</summary>
    public float TurbulenceBlur { get; init; }

    /// <summary>是否做饱和度/对比度增强(参考实现的最后两级)。</summary>
    public bool ColorGrade { get; init; }

    /// <summary>渲染分辨率上限(相对屏幕缩放)。低质量时降到 1.0, 让 GPU 少画几个像素。</summary>
    public float DpiCap { get; init; }

    /// <summary>径向遮罩的渐变停靠点数量: 1 = 只有两端(便宜)。</summary>
    public int MaskSteps { get; init; }

    public static GlassParams For(int quality)
    {
        quality = Math.Clamp(quality, 0, 100);
        return quality switch
        {
            < 20 => new GlassParams
            {
                Quality = quality, Octaves = 1, BlurScale = 0.6f, TurbulenceBlur = 0.5f,
                ColorGrade = false, DpiCap = 1.0f, MaskSteps = 1,
            },
            < 50 => new GlassParams
            {
                Quality = quality, Octaves = 2, BlurScale = 0.8f, TurbulenceBlur = 1.0f,
                ColorGrade = true, DpiCap = 1.5f, MaskSteps = 1,
            },
            < 80 => new GlassParams
            {
                Quality = quality, Octaves = 3, BlurScale = 1.0f, TurbulenceBlur = 2.0f,
                ColorGrade = true, DpiCap = 2.5f, MaskSteps = 3,
            },
            _ => new GlassParams
            {
                Quality = quality, Octaves = 4, BlurScale = 1.15f, TurbulenceBlur = 2.6f,
                ColorGrade = true, DpiCap = 4.0f, MaskSteps = 3,
            },
        };
    }

    /// <summary>设置界面里显示给人看的一句话。</summary>
    public string Describe()
    {
        var grade = ColorGrade ? "色彩增强" : "仅模糊";
        return $"{Quality} · 湍流 {Octaves} 层 · 模糊 ×{BlurScale:0.0} · {grade}";
    }
}

/// <summary>
/// 背景图的 "cover" 摆放计算(和 CSS background-size: cover 一样)。
/// 画壁纸和取玻璃样本都用它, 所以单独拿出来(纯数学, 可离线测)。
/// </summary>
public static class CoverMath
{
    /// <summary>把图片按铺满的方式放进画布, 返回缩放比与偏移(图片像素 -&gt; 画布 DIP)。</summary>
    public static (double Scale, double OffsetX, double OffsetY) Fit(double canvasW, double canvasH,
                                                                   double imageW, double imageH)
    {
        if (canvasW <= 0 || canvasH <= 0 || imageW <= 0 || imageH <= 0) return (1, 0, 0);
        var scale = Math.Max(canvasW / imageW, canvasH / imageH);
        return (scale, (canvasW - imageW * scale) / 2, (canvasH - imageH * scale) / 2);
    }
}
