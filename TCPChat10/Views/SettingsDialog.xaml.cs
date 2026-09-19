using Microsoft.UI.Xaml.Controls;
using TCPChat10.Services;

namespace TCPChat10.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings _settings;
    private readonly string _origUrl;
    private readonly string _origFolder;

    /// <summary>服务器地址或聊天目录变了, 需要重连。</summary>
    public bool NeedReconnect { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        this.InitializeComponent();
        _settings = settings;
        _origUrl = settings.ServerUrl;
        _origFolder = settings.ChatFolder;

        UrlBox.Text = settings.ServerUrl;
        FolderBox.Text = settings.ChatFolder;
        NickBox.Text = settings.Nickname;
        UserBox.Text = settings.UserName;
        PassBox.Password = settings.Password;
        PollBox.Value = settings.PollSeconds;
        DaysBox.Value = settings.HistoryDays;
        AutoScrollBox.IsChecked = settings.AutoScroll;
        ThemeBox.SelectedIndex = Math.Clamp(settings.Theme, 0, 2);

        this.PrimaryButtonClick += OnSave;
    }

    /// <summary>当前对话框里各控件的取值(用于自动化测试核对)。</summary>
    public string Describe() =>
        $"url={UrlBox.Text} folder={FolderBox.Text} nick={NickBox.Text} user={UserBox.Text} " +
        $"poll={PollBox.Value} days={DaysBox.Value} autoscroll={AutoScrollBox.IsChecked} theme={ThemeBox.SelectedIndex}";

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var url = UrlBox.Text.Trim();
        var folder = FolderBox.Text.Trim().Trim('/');
        if (url.Length == 0 || folder.Length == 0)
        {
            args.Cancel = true;
            return;
        }
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;

        NeedReconnect = !string.Equals(url, _origUrl, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(folder, _origFolder, StringComparison.Ordinal);

        _settings.ServerUrl = url;
        _settings.ChatFolder = folder;
        _settings.Nickname = NickBox.Text.Trim();
        _settings.UserName = UserBox.Text.Trim();
        _settings.Password = PassBox.Password;
        _settings.PollSeconds = (int)Math.Clamp(PollBox.Value, 1, 120);
        _settings.HistoryDays = (int)Math.Clamp(DaysBox.Value, 1, 365);
        _settings.AutoScroll = AutoScrollBox.IsChecked == true;
        _settings.Theme = ThemeBox.SelectedIndex;
    }
}
