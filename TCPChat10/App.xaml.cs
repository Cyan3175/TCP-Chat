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
        _window = new Views.MainWindow();
        _window.Activate();
    }
}
