using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace TCPChat10;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
        // 记下来并"吃掉": 聊天窗口宁可少画一帧, 也不该直接消失(用户报的"过一会自己退出")
        this.UnhandledException += (s, e) =>
        {
            LogCrash("XAML 未处理异常: " + e.Message, e.Exception);
            e.Handled = true;
        };

        // 11.0: 画玻璃是 GPU 上的活, 设备丢失/驱动重置这类异常如果发生在非 UI 线程,
        // 上面那个 XAML 事件收不到 —— 补两个兜底, 至少把原因写进 crash.log(以前只在 XAML 层记)。
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            LogCrash("AppDomain 未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("未观察的 Task 异常", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>把异常写进 %LOCALAPPDATA%\TCPChat\crash.log(全局可调, 玻璃层也在用)。</summary>
    internal static void LogCrash(string what, Exception? ex)
    {
        try
        {
            var log = Path.Combine(Services.AppSettings.DataDir, "crash.log");
            File.AppendAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + what +
                                   Environment.NewLine + (ex?.ToString() ?? "(无异常对象)") + Environment.NewLine);
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 应用级主题要在建窗口之前设: 弹出层(对话框/菜单)不继承窗口的 RequestedTheme,
        // 只有应用级主题能管到它们。窗口里改主题时, 新弹出的对话框会单独再设一次。
        try
        {
            var s = Services.AppSettings.Load();
            RequestedTheme = s.Theme switch
            {
                1 => ApplicationTheme.Light,
                2 => ApplicationTheme.Dark,
                _ => ApplicationTheme.Light,
            };
        }
        catch { }

        // 11.0 修订 3: 单文件 exe 会被解压到 %TEMP%\.net\<exe 名>\<hash>\,
        // 而 Windows App SDK 在"路径里带空格"时定位不到 ms-appx:/// 里的界面资源
        // (实测: "TCP-Chat-11.0 copy.exe" 必然启动崩溃, "TCP-Chat-11.0b.exe" 正常)。
        // 用户随手"复制一份"就会中招, 所以这里自动改用无空格的正式副本运行。
        if (TryRelaunchFromSafeLocation()) return;

        CleanOldExtractDirs();                 // 顺手清掉自己以前留下的解压目录

        Services.MessageNotifier.Init();      // 系统通知(收消息时弹)

        try
        {
            _window = new Views.MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            // 建窗口失败(多半还是上面那个资源定位问题): 给一句人话, 别直接消失
            LogCrash("建窗口失败", ex);
            FatalMessage("TCP Chat 启动失败", ex);
        }
    }

    /// <summary>exe 文件名带空格时, 复制一份到 %LOCALAPPDATA%\TCPChat\app\ 再运行它。</summary>
    private static bool TryRelaunchFromSafeLocation()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;
            var name = Path.GetFileName(exe);
            if (!name.Contains(' ')) return false;           // 文件名没问题, 正常启动

            var dir = Path.Combine(Services.AppSettings.DataDir, "app");
            Directory.CreateDirectory(dir);
            var safe = Path.Combine(dir, "TCP-Chat-11.0.exe");

            // 每次都刷新一份, 免得用户换了新版本而副本还是旧的
            File.Copy(exe, safe, overwrite: true);
            Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true });
            LogCrash("文件名带空格, 已改用 " + safe + " 启动(原文件: " + name + ")", null);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 清掉本程序以前版本留在 %TEMP%\.net 下的解压目录。
    /// 单文件 exe 每换一版就多一份(每份约 200 MB), 攒多了动辄几个 GB ——
    /// 用户看到临时目录这么大就会去清理, 一清就可能把正在运行的那份删掉, 程序随即崩溃。
    /// 所以自己收拾干净: 只删 TCP-Chat 开头、且不是当前正在用的那些。
    /// </summary>
    private static void CleanOldExtractDirs()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), ".net");
            if (!Directory.Exists(root)) return;

            var current = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            foreach (var appDir in Directory.GetDirectories(root, "TCP-Chat*"))
            {
                foreach (var versionDir in Directory.GetDirectories(appDir))
                {
                    if (string.Equals(versionDir.TrimEnd(Path.DirectorySeparatorChar), current, StringComparison.OrdinalIgnoreCase))
                        continue;                                   // 正在用的这份不能动
                    try { Directory.Delete(versionDir, recursive: true); } catch { }
                }
                try { if (Directory.GetFileSystemEntries(appDir).Length == 0) Directory.Delete(appDir); } catch { }
            }
        }
        catch { }
    }

    /// <summary>弹一个系统消息框说明失败原因(比静默退出强)。</summary>
    private static void FatalMessage(string title, Exception ex)
    {
        try
        {
            var text = "程序启动时出错了：\n\n" + ex.GetType().Name + ": " + ex.Message +
                       "\n\n常见原因：exe 被改成了带空格的名字（例如 \"TCP-Chat-11.0 - 副本.exe\"）。" +
                       "请把文件名里的空格去掉，或重新下载一份 TCP-Chat-11.0.exe。\n\n" +
                       "详细信息写在 %LOCALAPPDATA%\\TCPChat\\crash.log";
            MessageBox(IntPtr.Zero, text, title, 0x10);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
