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
        $"cryptoRows={_passwords.Count} sendPick={_sendIndex}";

    private string SelectedFontValue()
    {
        var i = FontBox.SelectedIndex;
        return i >= 0 && i < _fontValues.Count ? _fontValues[i] : "";
    }

    /// <summary>输入时把"两次不一致"的提示收起来。</summary>
    /// <summary>密码列表(界面上的真身)与默认用哪一把加密。</summary>
    private readonly List<string> _passwords = new();
    private int _sendIndex;

    /// <summary>把密码列表回填成一行行的密码框(遮蔽显示, 不再是明文)。</summary>
    private void LoadCrypto(AppSettings settings)
    {
        _passwords.Clear();
        _passwords.AddRange(settings.CryptoPasswords);
        _sendIndex = Math.Clamp(settings.SendPasswordIndex, 0, Math.Max(0, _passwords.Count - 1));
        RebuildPasswordRows();
    }

    /// <summary>重建密码行: [密码框] [用这个发送] [删除]。</summary>
    private void RebuildPasswordRows()
    {
        PasswordRows.Children.Clear();
        for (int i = 0; i < _passwords.Count; i++)
        {
            int index = i;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new PasswordBox
            {
                Password = _passwords[index],
                PasswordRevealMode = PasswordRevealMode.Peek,
                PlaceholderText = "第 " + (index + 1) + " 个密码",
            };
            box.PasswordChanged += (_, _) => _passwords[index] = box.Password;
            if (_passwords[index].Length == 0)
            {
                box.PlaceholderText = "（空 = 不加密 / 明文发送）";
                box.PasswordRevealMode = PasswordRevealMode.Visible;
            }
            Grid.SetColumn(box, 0);
            row.Children.Add(box);

            var pick = new RadioButton
            {
                Content = "用于发送",
                GroupName = "sendpwd",
                IsChecked = index == _sendIndex,
                MinWidth = 96,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pick.Checked += (_, _) => _sendIndex = index;
            Grid.SetColumn(pick, 1);
            row.Children.Add(pick);

            var del = new Button { Content = "删除", VerticalAlignment = VerticalAlignment.Center };
            del.Click += (_, _) =>
            {
                _passwords.RemoveAt(index);
                if (_sendIndex >= _passwords.Count) _sendIndex = Math.Max(0, _passwords.Count - 1);
                RebuildPasswordRows();
            };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);

            PasswordRows.Children.Add(row);
        }
        if (_passwords.Count == 0)
            PasswordRows.Children.Add(new TextBlock
            {
                Text = "（还没有密码 = 不加密，消息正文以明文写在服务器上）",
                FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap,
            });
        CryptoError.Visibility = Visibility.Collapsed;
    }

    /// <summary>加一条空项 = "不加密/明文"这一把。</summary>
    private void OnAddPlainClick(object sender, RoutedEventArgs e)
    {
        if (_passwords.Contains("", StringComparer.Ordinal))
        {
            CryptoError.Text = "列表里已经有一条「不加密」了。";
            CryptoError.Visibility = Visibility.Visible;
            return;
        }
        _passwords.Add("");
        RebuildPasswordRows();
    }

    private void OnAddPasswordClick(object sender, RoutedEventArgs e)
    {
        var pwd = NewPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(pwd)) { CryptoError.Text = "请先输入要添加的密码（想加「不加密」请点右边那个按钮）。"; CryptoError.Visibility = Visibility.Visible; return; }
        if (_passwords.Contains(pwd, StringComparer.Ordinal)) { CryptoError.Text = "这个密码已经在列表里了。"; CryptoError.Visibility = Visibility.Visible; return; }
        _passwords.Add(pwd);
        NewPasswordBox.Password = "";
        RebuildPasswordRows();
    }

    /// <summary>列表里的密码(空行/空白会被忽略)。</summary>
    private List<string> CryptoPasswords() =>
        _passwords.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

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

        // 11.2: 密码列表校验 —— 不能有空密码, 有密码就必须指定一把用于发送
        var lines = CryptoPasswords();
        if (lines.Count != _passwords.Count)
        {
            args.Cancel = true;
            CryptoError.Text = "密码列表里不能有空项，请把空的删掉或补上内容。";
            CryptoError.Visibility = Visibility.Visible;
            return;
        }
        if (lines.Count > 0 && _sendIndex >= lines.Count)
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
        _settings.SendPasswordIndex = Math.Max(0, _sendIndex);
        _settings.PollSeconds = (int)Math.Clamp(PollBox.Value, 1, 120);
        _settings.HistoryDays = (int)Math.Clamp(DaysBox.Value, 1, 365);
        _settings.AutoScroll = AutoScrollBox.IsChecked == true;
        _settings.Theme = ThemeBox.SelectedIndex;
        _settings.FontFamily = SelectedFontValue();
    }
}
