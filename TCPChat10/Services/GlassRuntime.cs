using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;

namespace TCPChat10.Services;

/// <summary>玻璃实现方式(启动时二选一, 也可以在设置里强制)。</summary>
public enum GlassBackend
{
    /// <summary>启动时自动探测: 能建 D3D 设备就用折射, 否则用画刷。</summary>
    Auto = 0,
    /// <summary>画刷: 半透明染色 + 高光描边, 不依赖显卡, 任何环境都能用。</summary>
    Brush = 1,
    /// <summary>折射: Win2D + Composition, 背景真模糊 + 边缘折射。</summary>
    Refraction = 2,
}

/// <summary>
/// 玻璃运行时: 负责在启动时挑一套实现, 并把玻璃层垫到各个元素后面。
///
/// 为什么要两套: 折射那套要 D3D11 设备(装显卡驱动、远程桌面、虚拟机里可能拿不到),
/// 拿不到就整套退回画刷, 界面不会因此崩掉或者变成一块灰。
/// </summary>
public static class GlassRuntime
{
    private static RefractionGlass? _refraction;
    private static readonly Dictionary<FrameworkElement, int> Attached = new();

    /// <summary>当前真正在用的实现。</summary>
    public static GlassBackend Active { get; private set; } = GlassBackend.Brush;

    /// <summary>选这套实现的原因(写在日志/界面上, 方便排查)。</summary>
    public static string Detail { get; private set; } = "未初始化";

    public static bool RefractionActive => Active == GlassBackend.Refraction;

    /// <summary>折射后端最近一次失败的原因。</summary>
    public static string LastError => _refraction?.LastError ?? "";

    /// <summary>调试: 只垫纯色/固定源效果, 定位渲染链哪一层断了。</summary>
    public static bool ApplyDebug(FrameworkElement element, string kind) =>
        _refraction?.ApplyDebug(element, kind) ?? false;

    /// <summary>换了实现 / 换了质量: 控件重新上一遍材质。</summary>
    public static event EventHandler? MaterialChanged;

    private static void RaiseMaterialChanged() => MaterialChanged?.Invoke(null, EventArgs.Empty);

    /// <summary>启动时挑一套: 设置说 Auto 就探测一次 D3D, 探测失败自动用画刷。</summary>
    public static void Init(Compositor? compositor, GlassBackend wanted)
    {
        _refraction?.Dispose();
        _refraction = null;
        Attached.Clear();

        if (wanted == GlassBackend.Refraction && compositor == null)
        {
            Active = GlassBackend.Brush;
            Detail = "窗口还没准备好, 先用画刷";
            return;
        }

        if (wanted == GlassBackend.Brush || compositor == null)
        {
            Active = GlassBackend.Brush;
            Detail = "设置里指定用画刷";
            RaiseMaterialChanged();
            return;
        }

        var impl = RefractionGlass.TryCreate(compositor, out var reason);
        if (impl == null)
        {
            Active = GlassBackend.Brush;
            Detail = (wanted == GlassBackend.Auto ? "自动探测失败, 退回画刷: " : "强制折射失败, 退回画刷: ") + reason;
            return;
        }

        _refraction = impl;
        Active = GlassBackend.Refraction;
        Detail = (wanted == GlassBackend.Auto ? "自动探测: " : "强制: ") + reason;
        RaiseMaterialChanged();
    }

    /// <summary>折射取样的背景(壁纸层)。</summary>
    public static void SetSource(FrameworkElement element) => _refraction?.SetSource(element);

    /// <summary>给元素垫一层玻璃(画刷模式什么也不做, 材质由 XAML 画刷负责)。</summary>
    public static void Attach(FrameworkElement element, int quality)
    {
        Attached[element] = quality;
        _refraction?.Apply(element, quality);
    }

    public static void Detach(FrameworkElement element)
    {
        Attached.Remove(element);
        _refraction?.Clear(element);
    }

    /// <summary>开关或质量变了: 已经垫过玻璃的元素重新来一遍。</summary>
    public static void RefreshAll(int quality, bool enabled)
    {
        if (!enabled)
        {
            foreach (var element in Attached.Keys.ToList()) _refraction?.Clear(element);
            return;
        }

        foreach (var element in Attached.Keys.ToList())
        {
            Attached[element] = quality;
            _refraction?.Apply(element, quality);
        }
        RaiseMaterialChanged();
    }

    public static void Shutdown()
    {
        _refraction?.Dispose();
        _refraction = null;
        Attached.Clear();
    }
}
