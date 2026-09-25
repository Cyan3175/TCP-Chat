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
                                   Environment.NewLine + ex + Environment.NewLine);
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

        Services.MessageNotifier.Init();      // 系统通知(收消息时弹)

        _window = new Views.MainWindow();
        _window.Activate();
    }
}
