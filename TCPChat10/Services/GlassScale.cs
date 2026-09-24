namespace TCPChat10.Services;

/// <summary>
/// 液态玻璃的质量档位(纯数学, 不碰 UI, 便于离线测试)。
/// 1 = 最清淡(几乎全透明), 10 = 最厚磨砂。
/// </summary>
public static class GlassScale
{
    public const int Min = 1;
    public const int Max = 10;
    public const int Default = 6;

    public static int Clamp(int quality) => Math.Clamp(quality, Min, Max);

    /// <summary>质量 -> 0..1 的强度(1 档 = 0, 10 档 = 1)。</summary>
    public static double Strength(int quality) => (Clamp(quality) - Min) / (double)(Max - Min);

    /// <summary>磨砂浓度百分比(45% ~ 95%), 直接显示给用户看滑杆在调什么。</summary>
    public static int FrostPercent(int quality) => (int)Math.Round(45 + 50 * Strength(quality));
}
