using Microsoft.UI.Xaml;

namespace TCPChat10;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
        this.UnhandledException += (s, e) =>
        {
            try
            {
                var log = Path.Combine(Services.AppSettings.DataDir, "crash.log");
                File.AppendAllText(log, DateTime.Now + "  " + e.Message + Environment.NewLine + e.Exception + Environment.NewLine);
            }
            catch { }
        };
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
