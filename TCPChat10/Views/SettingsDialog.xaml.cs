using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TCPChat10.Glass;
using TCPChat10.Services;

namespace TCPChat10.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private const string SystemDefaultFont = "（系统默认）";

    private readonly AppSettings _settings;
    private readonly string _origUrl;
    private readonly string _origFolder;
    /// <summary>与 FontBox.Items 一一对应的字体名, 空字符串表示系统默认。</summary>
    private readonly List<string> _fontValues = new();

    /// <summary>服务器地址或聊天目录变了, 需要重连。</summary>
    public bool NeedReconnect { get; private set; }

    /// <summary>玻璃开关/质量被改动(立即生效用): 主窗口订这个事件重新应用。</summary>
    public event Action? GlassChanged;

    /// <summary>自检用: 导出开关(关/开)与滑块这三张玻璃控件图。</summary>
    public async Task<bool> RenderGlassControlsAsync(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            GlassSwitch.SetOnWithoutNotify(false);
            var a = await GlassSwitch.RenderToFileAsync(Path.Combine(dir, "glass_switch_off.png"));
            GlassSwitch.SetOnWithoutNotify(true);
            var b = await GlassSwitch.RenderToFileAsync(Path.Combine(dir, "glass_switch_on.png"));
            var c = await GlassQualitySlider.RenderToFileAsync(Path.Combine(dir, "glass_slider.png"));
            GlassSwitch.SetOnWithoutNotify(_settings.GlassEnabled);
            return a && b && c;
        }
        catch { return false; }
    }

    private void OnGlassToggled()
    {
        _settings.GlassEnabled = GlassSwitch.IsOn;
        GlassChanged?.Invoke();
    }

    private void OnGlassQualityChanged(double value)
    {
        var quality = (int)Math.Round(value);
        UpdateGlassText(quality);
        _settings.GlassQuality = quality;
        GlassChanged?.Invoke();
    }

    private void UpdateGlassText(int quality)
    {
        GlassQualityText.Text = quality.ToString();
        GlassHint.Text = GlassParams.For(quality).Describe() +
                         "　·　背景用系统桌面壁纸（读不到就程序生成），改完立即生效。";
    }

    public SettingsDialog(AppSettings settings)
    {
        this.InitializeComponent();
        _settings = settings;
        // 对话框是弹出层, 不继承窗口的 RequestedTheme, 这里自己跟设置走
        RequestedTheme = settings.Theme switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _origUrl = settings.ServerUrl;
        _origFolder = settings.ChatFolder;

        UrlBox.Text = settings.ServerUrl;
        FolderBox.Text = settings.ChatFolder;
        NickBox.Text = settings.Nickname;
        LoadCrypto(settings);
        CryptoListBox.TextChanged += (_, _) => OnCryptoListChanged();
        SendPasswordBox.SelectionChanged += (_, _) => OnSendPasswordChanged();
        PollBox.Value = settings.PollSeconds;
        DaysBox.Value = settings.HistoryDays;
        AutoScrollBox.IsChecked = settings.AutoScroll;
        ThemeBox.SelectedIndex = Math.Clamp(settings.Theme, 0, 2);
        LoadFonts(settings.FontFamily);

        // 液态玻璃(11.0): 开关与质量滑块都是"改了就立刻生效", 不用等保存。
        // 回填不会触发事件(SetOnWithoutNotify / Value 的 setter 只在用户操作时回调)
        GlassSwitch.SetOnWithoutNotify(settings.GlassEnabled);
        GlassQualitySlider.Value = Math.Clamp(settings.GlassQuality, 0, 100);
        GlassSwitch.Toggled += OnGlassToggled;
        GlassQualitySlider.ValueChanged += OnGlassQualityChanged;
        UpdateGlassText(settings.GlassQuality);

        this.PrimaryButtonClick += OnSave;
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
        $"fonts={_fontValues.Count - 1} font={SelectedFontValue()} " +
        $"crypto={(_settings.CryptoPasswords.Count == 0 ? "off" : "on:" + _settings.CryptoPasswords.Count + "个/发送第" + (_settings.SendPasswordIndex + 1) + "个")} " +
        $"cryptoLines={CryptoPasswords().Count} sendPick={SendPasswordBox.SelectedIndex}";

    private string SelectedFontValue()
    {
        var i = FontBox.SelectedIndex;
        return i >= 0 && i < _fontValues.Count ? _fontValues[i] : "";
    }

    /// <summary>输入时把"两次不一致"的提示收起来。</summary>
    /// <summary>把密码列表 / 默认发送密码回填到控件(回填不触发事件)。</summary>
    private void LoadCrypto(AppSettings settings)
    {
        CryptoListBox.Text = string.Join(Environment.NewLine, settings.CryptoPasswords);
        RefreshSendPasswordItems(settings.SendPasswordIndex);
    }

    /// <summary>按当前列表重建"默认用哪一个加密"下拉项。</summary>
    private void RefreshSendPasswordItems(int selected)
    {
        var lines = CryptoPasswords();
        SendPasswordBox.Items.Clear();
        for (int i = 0; i < lines.Count; i++)
            SendPasswordBox.Items.Add("第 " + (i + 1) + " 个（" + lines[i].Length + " 位）");
        SendPasswordBox.SelectedIndex = lines.Count == 0 ? -1 : Math.Clamp(selected, 0, lines.Count - 1);
        SendPasswordBox.IsEnabled = lines.Count > 0;
    }

    private List<string> CryptoPasswords() =>
        (CryptoListBox.Text ?? "")
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

    /// <summary>列表变了: 刷新下拉项并清掉错误提示。</summary>
    private void OnCryptoListChanged()
    {
        var keep = SendPasswordBox.SelectedIndex;
        RefreshSendPasswordItems(keep < 0 ? 0 : keep);
        CryptoError.Visibility = Visibility.Collapsed;
    }

    private void OnSendPasswordChanged()
    {
        CryptoError.Visibility = Visibility.Collapsed;
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

        // 11.2: 密码列表 —— 不允许出现空行(粘贴时容易多出空行), 也不能选不出默认发送密码
        var lines = CryptoPasswords();
        var rawLines = (CryptoListBox.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (rawLines.Any(l => l.Trim().Length == 0 && rawLines.Length > 1 && rawLines.Any(x => x.Trim().Length > 0)))
        {
            args.Cancel = true;
            CryptoError.Text = "密码列表里有空行，请删掉空行（每行一个密码）。";
            CryptoError.Visibility = Visibility.Visible;
            return;
        }
        if (lines.Count > 0 && SendPasswordBox.SelectedIndex < 0)
        {
            args.Cancel = true;
            CryptoError.Text = "请选择默认用哪一个密码加密发送。";
            CryptoError.Visibility = Visibility.Visible;
            return;
        }

        NeedReconnect = !string.Equals(url, _origUrl, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(folder, _origFolder, StringComparison.Ordinal);

        _settings.ServerUrl = url;
        _settings.ChatFolder = folder;
        _settings.Nickname = NickBox.Text.Trim();
        _settings.CryptoPasswords = CryptoPasswords();
        _settings.SendPasswordIndex = Math.Max(0, SendPasswordBox.SelectedIndex);
        _settings.PollSeconds = (int)Math.Clamp(PollBox.Value, 1, 120);
        _settings.HistoryDays = (int)Math.Clamp(DaysBox.Value, 1, 365);
        _settings.AutoScroll = AutoScrollBox.IsChecked == true;
        _settings.Theme = ThemeBox.SelectedIndex;
        _settings.FontFamily = SelectedFontValue();
    }
}
