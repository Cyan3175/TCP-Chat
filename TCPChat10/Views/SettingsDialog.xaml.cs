using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TCPChat10.Services;

namespace TCPChat10.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private const string SystemDefaultFont = "（系统默认）";

    private readonly AppSettings _settings;
    private readonly string _origUrl;
    private readonly string _origFolder;
    /// <summary>拖动玻璃开关/质量滑杆时的实时预览回调(不落盘, 取消就还原)。</summary>
    private readonly Action<bool, int>? _preview;
    /// <summary>初始化期间控件事件会乱跑, 装完才允许预览。</summary>
    private bool _ready;
    /// <summary>与 FontBox.Items 一一对应的字体名, 空字符串表示系统默认。</summary>
    private readonly List<string> _fontValues = new();

    /// <summary>服务器地址或聊天目录变了, 需要重连。</summary>
    public bool NeedReconnect { get; private set; }

    public SettingsDialog(AppSettings settings, Action<bool, int>? preview = null)
    {
        this.InitializeComponent();
        _settings = settings;
        _preview = preview;
        _origUrl = settings.ServerUrl;
        _origFolder = settings.ChatFolder;

        UrlBox.Text = settings.ServerUrl;
        FolderBox.Text = settings.ChatFolder;
        NickBox.Text = settings.Nickname;
        CryptoBox.Password = settings.CryptoPassword;
        CryptoBox2.Password = settings.CryptoPassword;
        PollBox.Value = settings.PollSeconds;
        DaysBox.Value = settings.HistoryDays;
        AutoScrollBox.IsChecked = settings.AutoScroll;
        ThemeBox.SelectedIndex = Math.Clamp(settings.Theme, 0, 2);
        LoadFonts(settings.FontFamily);

        GlassBox.IsOn = settings.GlassEffect;
        GlassQualityBox.Value = GlassScale.Clamp(settings.GlassQuality);
        UpdateGlassControls();
        _ready = true;

        this.PrimaryButtonClick += OnSave;
    }

    /// <summary>开关关掉时滑杆变灰(值留着, 下次打开还在)。</summary>
    private void UpdateGlassControls()
    {
        GlassQualityBox.IsEnabled = GlassBox.IsOn;
        GlassHint.Opacity = GlassBox.IsOn ? 0.65 : 0.4;
    }

    private void OnGlassToggled(object sender, RoutedEventArgs e)
    {
        UpdateGlassControls();
        if (_ready) _preview?.Invoke(GlassBox.IsOn, (int)GlassQualityBox.Value);
    }

    private void OnGlassQualityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // 把档位和对应的模糊半径直接写在标题上, 不用猜
        GlassQualityBox.Header = $"玻璃质量：{e.NewValue:F0} 档 · 磨砂浓度约 {GlassScale.FrostPercent((int)e.NewValue)}%";
        if (_ready) _preview?.Invoke(GlassBox.IsOn, (int)e.NewValue);
    }

    /// <summary>把系统已安装的字体填进下拉框, 每项用它自己的字体渲染, 顺便选中当前设置。</summary>
    private void LoadFonts(string current)
    {
        FontBox.Items.Clear();
        _fontValues.Clear();

        AddFontItem(SystemDefaultFont, "");
        foreach (var name in FontList.GetInstalledFamilies())
            AddFontItem(name, name);

        int idx = 0;
        for (int i = 0; i < _fontValues.Count; i++)
        {
            if (string.Equals(_fontValues[i], current ?? "", StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        }
        FontBox.SelectedIndex = idx;
    }

    private void AddFontItem(string display, string value)
    {
        var item = new ComboBoxItem { Content = display, Tag = value };
        if (value.Length > 0)
        {
            try { item.FontFamily = new FontFamily(value); } catch { }
        }
        FontBox.Items.Add(item);
        _fontValues.Add(value);
    }

    /// <summary>把对话框内容滚到指定位置(0=顶部 1=底部), 供界面自测截图核对下半部分。</summary>
    public void ScrollTo(double fraction)
    {
        SettingsScroll.ChangeView(null, SettingsScroll.ScrollableHeight * Math.Clamp(fraction, 0, 1), null, true);
    }

    /// <summary>展开字体下拉列表(供界面自测截图, 弹出层不在窗口视觉树里, 只能抓屏幕)。</summary>
    public void OpenFontDropDown() => FontBox.IsDropDownOpen = true;

    /// <summary>当前对话框里各控件的取值(用于自动化测试核对)。</summary>
    public string Describe() =>
        $"url={UrlBox.Text} folder={FolderBox.Text} nick={NickBox.Text} " +
        $"poll={PollBox.Value} days={DaysBox.Value} autoscroll={AutoScrollBox.IsChecked} theme={ThemeBox.SelectedIndex} " +
        $"fonts={_fontValues.Count - 1} font={SelectedFontValue()} crypto={CryptoBox.Password.Length switch { 0 => "off", _ => "on:" + CryptoBox.Password.Length + "位" }} " +
        $"glass={(GlassBox.IsOn ? "on" : "off")} quality={(int)GlassQualityBox.Value}";

    private string SelectedFontValue()
    {
        var i = FontBox.SelectedIndex;
        return i >= 0 && i < _fontValues.Count ? _fontValues[i] : "";
    }

    /// <summary>输入时把"两次不一致"的提示收起来。</summary>
    private void OnCryptoChanged(object sender, RoutedEventArgs e)
    {
        if (CryptoError != null) CryptoError.Visibility = Visibility.Collapsed;
    }

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

        // 收发两端密码必须一致, 输入笔误要当场拦下来
        if (!string.Equals(CryptoBox.Password, CryptoBox2.Password, StringComparison.Ordinal))
        {
            args.Cancel = true;
            CryptoError.Visibility = Visibility.Visible;
            return;
        }

        NeedReconnect = !string.Equals(url, _origUrl, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(folder, _origFolder, StringComparison.Ordinal);

        _settings.ServerUrl = url;
        _settings.ChatFolder = folder;
        _settings.Nickname = NickBox.Text.Trim();
        _settings.CryptoPassword = CryptoBox.Password;
        _settings.PollSeconds = (int)Math.Clamp(PollBox.Value, 1, 120);
        _settings.HistoryDays = (int)Math.Clamp(DaysBox.Value, 1, 365);
        _settings.AutoScroll = AutoScrollBox.IsChecked == true;
        _settings.Theme = ThemeBox.SelectedIndex;
        _settings.FontFamily = SelectedFontValue();
        _settings.GlassEffect = GlassBox.IsOn;
        _settings.GlassQuality = GlassScale.Clamp((int)GlassQualityBox.Value);
    }
}
