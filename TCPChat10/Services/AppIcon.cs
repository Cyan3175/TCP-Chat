using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace TCPChat10.Services;

/// <summary>
/// 程序图标(11.4)。
///
/// 图标选自 MahApps.Metro.IconPacks 6.2.1 的 FontAwesome6 图标包 —— <c>CommentDotsSolid</c>
/// (聊天气泡 + 三个点, 实心字形, 缩到 16px 也认得出), 用 .build/iconmake 把它渲染成
/// <c>Assets\app.ico</c>(多尺寸) 与 <c>Assets\app-icon.png</c>, 再作为嵌入资源跟着程序走:
/// 单文件发布时 exe 旁边并没有 Assets 目录, 所以运行时从资源里解出来交给系统。
/// </summary>
public static class AppIcon
{
    private static bool _done;
    private static string? _icoPath;

    /// <summary>给窗口设置图标(标题栏、任务栏、Alt+Tab 都跟着变)。</summary>
    public static void Apply(AppWindow? window)
    {
        try
        {
            EnsureExtracted();
            if (window != null && _icoPath != null && File.Exists(_icoPath)) window.SetIcon(_icoPath);
        }
        catch (Exception ex) { App.LogCrash("设置窗口图标失败", ex); }
    }

    /// <summary>顶栏上显示的小图标(用解出来的那份 PNG)。</summary>
    public static async Task<BitmapImage?> LoadMarkAsync()
    {
        try
        {
            EnsureExtracted();
            var path = Path.Combine(AppSettings.DataDir, "app-icon.png");
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            using var fs = File.OpenRead(path);
            await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            return bmp;
        }
        catch (Exception ex)
        {
            App.LogCrash("加载顶栏图标失败", ex);
            return null;
        }
    }

    /// <summary>把两个图标文件解到数据目录(只在内容变了的时候写一次)。</summary>
    private static void EnsureExtracted()
    {
        if (_done) return;
        _done = true;
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var ico = Path.Combine(AppSettings.DataDir, "app.ico");
            Extract("app.ico", ico);
            Extract("app-icon.png", Path.Combine(AppSettings.DataDir, "app-icon.png"));
            _icoPath = ico;
        }
        catch (Exception ex) { App.LogCrash("导出程序图标失败", ex); }
    }

    private static void Extract(string endsWith, string path)
    {
        try
        {
            using var src = Resource(endsWith);
            if (src == null) return;
            if (File.Exists(path) && new FileInfo(path).Length == src.Length) return;   // 已经是最新的一份
            using var dst = File.Create(path);
            src.CopyTo(dst);
        }
        catch (Exception ex) { App.LogCrash("写图标文件失败 " + path, ex); }
    }

    /// <summary>按后缀找嵌入资源(资源的完整名字由根命名空间拼出来, 这里不写死)。</summary>
    private static Stream? Resource(string endsWith)
    {
        var asm = typeof(AppIcon).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
            if (name.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase))
                return asm.GetManifestResourceStream(name);
        return null;
    }
}
