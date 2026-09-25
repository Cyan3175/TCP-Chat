namespace TCPChat10.Services;

/// <summary>
/// 11.4: 界面缩放(Ctrl + 加号 / 减号 / 0, 以及 Ctrl + 滚轮)。
///
/// 做法是把"消息区用的基准字号"乘上这个比例: markdown 正文、代码块、表格、公式、
/// 昵称和时间都会跟着一起变(颜色/换行都是重新算的, 不会糊)。数值写进 settings.json。
/// </summary>
public static class UiZoom
{
    public const double Min = 0.6;
    public const double Max = 2.4;

    /// <summary>消息正文的原始字号。</summary>
    public const double BaseFontSize = 14;

    /// <summary>一档的步长(10%)。</summary>
    public const double StepSize = 0.1;

    public static double Level { get; private set; } = 1.0;

    /// <summary>缩放变了(界面层收到后重建消息并刷新底栏)。</summary>
    public static event Action? Changed;

    /// <summary>把比例夹到允许范围, 并四舍五入到 10%(避免 0.9999 这种数)。</summary>
    public static double Clamp(double level)
    {
        if (double.IsNaN(level) || double.IsInfinity(level) || level <= 0) return 1.0;
        return Math.Round(Math.Clamp(level, Min, Max), 2);
    }

    /// <summary>设置缩放; save = false 时只改内存(启动时用)。</summary>
    public static void Set(double level, bool notify = true)
    {
        var next = Clamp(level);
        if (Math.Abs(next - Level) < 0.0001) return;
        Level = next;
        if (notify) Changed?.Invoke();
    }

    /// <summary>放大 / 缩小一档。</summary>
    public static void Step(int direction) => Set(Level + direction * StepSize);

    /// <summary>Ctrl+0: 回到 100%。</summary>
    public static void Reset() => Set(1.0);

    /// <summary>底栏提示用, 例如 "125%"。</summary>
    public static string Percent => Math.Round(Level * 100) + "%";
}
