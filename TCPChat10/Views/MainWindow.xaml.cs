using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TCPChat10.Models;
using TCPChat10.Services;
using TCPChat10.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace TCPChat10.Views;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ChatService _chat;
    private DateTimeOffset _lastSync = DateTimeOffset.Now;

    public ObservableCollection<MessageVm> Messages { get; } = new();

    private SettingsDialog? _dlg;
    private MenuFlyout? _menu;

    public MainWindow()
    {
        this.InitializeComponent();

        _settings = AppSettings.Load();
        _chat = new ChatService(_settings);

        _chat.MessageAdded += OnMessageAdded;
        _chat.MessageRemoved += OnMessageRemoved;
        _chat.ErrorOccurred += s => DispatcherQueue.TryEnqueue(() => SetFooter("⚠ " + s));

        // 直接赋 ItemsSource: 避免 Window 上 x:Bind 的 OneTime 绑定在某些情况下不生效
        MessageList.ItemsSource = Messages;

        ResizeWindow(1120, 780);
        this.AppWindow.Closing += (s, e) => _chat.Stop();

        ApplyTheme();
        ApplyFont();
        UpdateLockText();
        MeText.Text = string.IsNullOrWhiteSpace(_settings.Nickname) ? "(未设置昵称)" : "我：" + _settings.Nickname;
        Title = "TCP Chat 10.2 — " + _settings.ChatFolder;

        RootLoaded();

        var script = Environment.GetEnvironmentVariable("TCPCHAT10_AUTOTEST");
        if (!string.IsNullOrWhiteSpace(script))
        {
            // 自测模式下把同步诊断写进日志, 便于定位"消息没出来"这类问题
            _chat.Diag += AutoLog;
            _chat.MessageAdded += m => AutoLog($"  + 收到 {m.RemoteName} from={m.From}{(m.DecryptFailed ? " [解密失败]" : m.IsEncrypted ? " [已解密]" : "")}");
            _ = RunAutoTestAsync(script!);
        }
    }

    // ==================== 自动化测试钩子 ====================
    // 通过环境变量 TCPCHAT10_AUTOTEST 驱动, 动作以 ';' 分隔:
    //   wait:N          等待 N 秒
    //   send:文本       发送一条消息
    //   sendq:引用|正文 发送带引用的消息
    //   font:字体名     改字体并保存(名字写 default 表示恢复系统默认)
    //   fonts           打印系统字体数量
    //   crypto:密码     改加密密码并保存(留空 = 关闭加密)
    //   shot:路径       把当前界面渲染成 PNG
    //   log:文本        输出一行到控制台
    //   quit            退出
    private static readonly object _logLock = new();
    private static void AutoLog(string s)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s;
        Console.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_LOG");
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { lock (_logLock) File.AppendAllText(path!, line + Environment.NewLine); } catch { }
        }
    }

    private async Task RunAutoTestAsync(string script)
    {
        AutoLog("autotest 开始, 脚本=" + script);
        await Task.Delay(2500);   // 等首次连接与同步完成
        foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var a = raw.Trim();
            try
            {
                if (a.StartsWith("wait:"))
                {
                    await Task.Delay((int)(double.Parse(a[5..]) * 1000));
                }
                else if (a.StartsWith("send:"))
                {
                    var text = a[5..];
                    if (string.IsNullOrWhiteSpace(_settings.Nickname)) { _settings.Nickname = "测试用户"; _chat.Nickname = _settings.Nickname; }
                    var m = await _chat.SendTextAsync(text);
                    if (m != null) { m.IsSelf = true; var lv = new MessageVm(m); Messages.Add(lv); ScrollToBottom(); }
                    AutoLog("AUTO send -> " + (m != null ? "ok" : "fail"));
                }
                else if (a.StartsWith("sendq:"))
                {
                    // sendq:引用内容|正文
                    var parts = a[6..].Split('|', 2);
                    var qt = parts[0];
                    var bt = parts.Length > 1 ? parts[1] : "收到";
                    var m = await _chat.SendTextAsync(bt, qt);
                    if (m != null) { m.IsSelf = true; Messages.Add(new MessageVm(m)); ScrollToBottom(); }
                    AutoLog("AUTO sendq -> " + (m != null ? "ok" : "fail"));
                }
                else if (a.StartsWith("font:"))
                {
                    var name = a[5..].Trim();
                    _settings.FontFamily = name.Equals("default", StringComparison.OrdinalIgnoreCase) ? "" : name;
                    _settings.Save();
                    ApplyFont();
                    AutoLog("AUTO font -> " + (string.IsNullOrEmpty(_settings.FontFamily) ? "系统默认" : _settings.FontFamily));
                }
                else if (a == "fonts")
                {
                    var list = FontList.GetInstalledFamilies();
                    AutoLog($"AUTO fonts -> 系统字体 {list.Count} 个: " + string.Join(" / ", list.Take(8)));
                }
                else if (a.StartsWith("crypto:"))
                {
                    _settings.CryptoPassword = a[7..];
                    _settings.Save();
                    RebuildCrypto();
                    UpdateLockText();
                    AutoLog("AUTO crypto -> " + (_chat.EncryptionEnabled ? "加密已启用" : "加密已关闭"));
                }
                else if (a == "settings")
                {
                    _dlg = new SettingsDialog(_settings) { XamlRoot = Content.XamlRoot };
                    _ = ShowDialogLoggedAsync(_dlg, "settings");
                    await Task.Delay(1500);
                    AutoLog("AUTO settings 已打开 " + _dlg.Describe());
                }
                else if (a.StartsWith("dshot:"))
                {
                    var ok = _dlg != null && await CaptureElementAsync(_dlg, a[6..]);
                    AutoLog("AUTO dshot -> " + (ok ? "ok " + a[6..] : "fail"));
                }
                else if (a.StartsWith("dscroll:"))
                {
                    // 把设置对话框滚到指定位置(0=顶部 1=底部), 便于截图核对下半部分
                    var frac = Math.Clamp(double.Parse(a[8..]), 0, 1);
                    _dlg?.ScrollTo(frac);
                    await Task.Delay(400);
                    AutoLog($"AUTO dscroll {frac:F2} -> " + (_dlg == null ? "对话框未打开" : "ok"));
                }
                else if (a == "dfontdrop")
                {
                    _dlg?.OpenFontDropDown();
                    await Task.Delay(300);
                    AutoLog("AUTO dfontdrop -> 字体下拉已展开");
                }
                else if (a == "closedlg")
                {
                    try { _dlg?.Hide(); } catch { }
                    _dlg = null;
                    await Task.Delay(600);
                    AutoLog("AUTO 对话框已关闭");
                }
                else if (a == "menu")
                {
                    var target = Messages.LastOrDefault();
                    if (target != null)
                    {
                        _menu = BuildMessageMenu(target);
                        _menu.ShowAt(MessageList, new FlyoutShowOptions { Position = new Windows.Foundation.Point(320, 240) });
                        await Task.Delay(900);
                    }
                    var items = _menu == null ? "" : string.Join(" | ", _menu.Items.Select(
                        i => i is MenuFlyoutItem mi ? mi.Text : "---"));
                    AutoLog("AUTO menu -> " + (target != null ? "ok [" + items + "]" : "无消息"));
                }
                else if (a == "hidemenu")
                {
                    try { _menu?.Hide(); } catch { }
                    _menu = null;
                    await Task.Delay(500);
                }
                else if (a.StartsWith("waitmsg:"))
                {
                    int want = (int)double.Parse(a[8..]);
                    var w2 = System.Diagnostics.Stopwatch.StartNew();
                    while (Messages.Count < want && w2.ElapsedMilliseconds < 40000) await Task.Delay(200);
                    AutoLog($"AUTO waitmsg 目标={want} 实际={Messages.Count} 用时={w2.ElapsedMilliseconds}ms");
                }
                else if (a == "scroll")
                {
                    ScrollToBottom();
                    await Task.Delay(600);
                    ScrollToBottom();
                    AutoLog("AUTO scroll ok");
                }
                else if (a.StartsWith("shot:"))
                {
                    var path = a[5..];
                    await Task.Delay(900);         // 等一帧布局完成
                    AutoLog($"诊断: Messages={Messages.Count} ListItems={MessageList.Items.Count} " +
                            $"字体={MessageList.FontFamily?.Source ?? "(默认)"} " +
                            $"ListView H={MessageList.ActualHeight:F0} W={MessageList.ActualWidth:F0}");
                    var ok = await CaptureAsync(path);
                    AutoLog("AUTO shot -> " + (ok ? "ok " + path : "fail"));
                }
                else if (a.StartsWith("log:"))
                {
                    AutoLog("AUTO " + a[4..] + $"  [消息数={Messages.Count} 加密={(_chat.EncryptionEnabled ? "开" : "关")} " +
                            $"解不开={_chat.UndecryptableCount}]");
                }
                else if (a == "quit")
                {
                    AutoLog("AUTO quit  [最终消息数=" + Messages.Count + "]");
                    Close();
                }
            }
            catch (Exception ex) { AutoLog("AUTO 错误 " + a + " : " + ex.Message); }
        }
    }

    private async Task ShowDialogLoggedAsync(ContentDialog d, string tag)
    {
        try
        {
            var r = await d.ShowAsync();
            AutoLog($"AUTO {tag} 对话框返回 {r}");
        }
        catch (Exception ex)
        {
            AutoLog($"AUTO {tag} 对话框异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把窗口内容渲染成 PNG(用于界面回归验证)。</summary>
    private Task<bool> CaptureAsync(string path) => CaptureElementAsync(this.Content as UIElement, path);

    /// <summary>把任意元素渲染成 PNG(弹出层不在窗口视觉树里, 需要单独渲染)。</summary>
    private async Task<bool> CaptureElementAsync(UIElement? root, string path)
    {
        try
        {
            if (root == null) return false;
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(root);
            var buffer = await rtb.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(pixels);
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = File.Create(path);
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            AutoLog("AUTO 截图失败: " + ex.Message);
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>按 DIP 设置窗口大小(高 DPI 下必须乘缩放比, 否则窗口会小得只剩半屏)。</summary>
    private void ResizeWindow(int wDip, int hDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        var aw = AppWindow.GetFromWindowId(id);

        double scale = 1.0;
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi >= 72) scale = dpi / 96.0;
        }
        catch { }

        int w = (int)Math.Round(wDip * scale);
        int h = (int)Math.Round(hDip * scale);

        // 不要超出工作区
        var area = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        w = Math.Min(w, area.Width - 40);
        h = Math.Min(h, area.Height - 40);

        aw.Resize(new Windows.Graphics.SizeInt32(w, h));

        // Resize 不会挪窗口, 默认位置可能把窗口顶到屏幕外, 这里居中一下
        try
        {
            aw.Move(new Windows.Graphics.PointInt32(
                area.X + Math.Max(0, (area.Width - w) / 2),
                area.Y + Math.Max(0, (area.Height - h) / 2)));
        }
        catch { }
    }

    private async void RootLoaded()
    {
        SetFooter("正在连接 " + _settings.ServerUrl + " …");
        SetStatus(false, "连接中…");

        var (ok, msg) = await _chat.InitializeAsync();
        if (!ok)
        {
            SetStatus(false, "连接失败");
            SetFooter("⚠ " + msg);
            var dlg = new ContentDialog
            {
                Title = "无法连接",
                Content = msg + "\n\n请检查设置里的服务器地址与聊天目录。\n" +
                          "程序不会自己新建目录：消息文件直接放进该目录，所以目录要事先在服务器上存在，" +
                          "或者改成已有的目录。",
                CloseButtonText = "打开设置",
                PrimaryButtonText = "重试",
                XamlRoot = Content.XamlRoot,
            };
            var r = await dlg.ShowAsync();
            if (r == ContentDialogResult.Primary) RootLoaded();
            else await ShowSettingsAsync();
            return;
        }

        SetStatus(true, "已连接");
        SetFooter("已连接 " + _settings.ServerUrl + "  ·  目录 " + _settings.ChatFolder);
        _chat.Start();
        BusyRing.IsActive = true;

        if (string.IsNullOrWhiteSpace(_settings.Nickname))
        {
            await PromptNicknameAsync(firstTime: true);
        }
        InputBox.Focus(FocusState.Programmatic);
    }

    // ---------- 事件 ----------

    /// <summary>可靠地滚动到最新一条(虚拟化面板下 ScrollIntoView 常常滚不动)。</summary>
    private void ScrollToBottom()
    {
        if (Messages.Count == 0) return;
        MessageList.ScrollIntoView(Messages[^1], ScrollIntoViewAlignment.Default);
        MessageList.UpdateLayout();
        if (FindScrollViewer(MessageList) is ScrollViewer sv)
            sv.ChangeView(null, sv.ScrollableHeight, null, true);

        // 容器高度要等一轮布局才确定,布局结束后再纠正一次
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            MessageList.UpdateLayout();
            if (FindScrollViewer(MessageList) is ScrollViewer sv2)
                sv2.ChangeView(null, sv2.ScrollableHeight, null, true);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is ScrollViewer deep) return deep;
        }
        return null;
    }

    private void OnMessageAdded(ChatMessage m)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Messages.Any(v => v.Key == (m.RemoteName.Length > 0 ? m.RemoteName : m.Id))) return;
            var vm = new MessageVm(m);

            // 按时间插入到正确位置(轮询可能乱序)
            int idx = Messages.Count;
            while (idx > 0 && Messages[idx - 1].Model.Time > m.Time) idx--;
            Messages.Insert(idx, vm);

            _lastSync = DateTimeOffset.Now;
            BusyRing.IsActive = false;
            UpdateFooter();

            if (idx >= Messages.Count - 1 || _settings.AutoScroll)
                ScrollToBottom();
        });
    }

    private void OnMessageRemoved(string remoteName)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var vm = Messages.FirstOrDefault(v => v.Key == remoteName);
            if (vm != null) Messages.Remove(vm);
        });
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift);
        bool shiftDown = shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shiftDown) return;      // Shift+Enter 换行
        e.Handled = true;
        await SendCurrentAsync();
    }

    private string? _quoteText;

    private async Task SendCurrentAsync()
    {
        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (string.IsNullOrWhiteSpace(_settings.Nickname))
        {
            await PromptNicknameAsync(firstTime: false);
            if (string.IsNullOrWhiteSpace(_settings.Nickname)) return;
        }

        InputBox.Text = "";
        BusyRing.IsActive = true;
        var vm = await _chat.SendTextAsync(text, _quoteText);
        _quoteText = null;
        BusyRing.IsActive = false;

        if (vm != null)
        {
            // 本地立即回显
            vm.IsSelf = true;
            var local = new MessageVm(vm);
            Messages.Add(local);
            UpdateFooter();
            ScrollToBottom();
        }
        InputBox.Focus(FocusState.Programmatic);
    }

    private async void OnMessageRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MessageVm vm) return;
        e.Handled = true;

        var flyout = BuildMessageMenu(vm);
        flyout.ShowAt(fe, new FlyoutShowOptions { Position = e.GetPosition(fe) });
        await Task.CompletedTask;
    }

    /// <summary>右键菜单(复制 / 引用 / 撤回)。</summary>
    private MenuFlyout BuildMessageMenu(MessageVm vm)
    {
        var flyout = new MenuFlyout();
        var miCopy = new MenuFlyoutItem { Text = "复制文本", Icon = new FontIcon { Glyph = "\uE8C8" } };
        miCopy.Click += (_, _) =>
        {
            var dp = new DataPackage();
            dp.SetText(vm.BodyText);
            Clipboard.SetContent(dp);
            SetFooter("已复制");
        };
        flyout.Items.Add(miCopy);

        var miQuote = new MenuFlyoutItem { Text = "引用", Icon = new FontIcon { Glyph = "\uE8BD" } };
        miQuote.Click += (_, _) =>
        {
            _quoteText = vm.BodyText.Length > 120 ? vm.BodyText[..120] + "…" : vm.BodyText;
            InputBox.Text = "> " + _quoteText + "\n" + InputBox.Text;
            InputBox.Focus(FocusState.Programmatic);
            SetFooter("已引用，可继续输入");
        };
        flyout.Items.Add(miQuote);

        if (vm.IsSelf)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var miDel = new MenuFlyoutItem { Text = "撤回（从服务器删除）", Icon = new FontIcon { Glyph = "\uE74D" } };
            miDel.Click += async (_, _) =>
            {
                var ok = await _chat.DeleteMessageAsync(vm.Model);
                SetFooter(ok ? "已撤回" : "撤回失败（可能没有删除权限）");
            };
            flyout.Items.Add(miDel);
        }

        return flyout;
    }

    private async void OnChangeNickClick(object sender, RoutedEventArgs e) => await PromptNicknameAsync(false);

    private async Task PromptNicknameAsync(bool firstTime)
    {
        var box = new TextBox
        {
            PlaceholderText = "例如：张三",
            Text = _settings.Nickname,
            MaxLength = 24,
        };
        var dlg = new ContentDialog
        {
            Title = firstTime ? "给自己起个名字" : "修改昵称",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "这个名字会显示在每条消息上。", FontSize = 12, Opacity = 0.7 },
                    box,
                },
            },
            PrimaryButtonText = "确定",
            CloseButtonText = firstTime ? "稍后" : "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var r = await dlg.ShowAsync();
        if (r == ContentDialogResult.Primary)
        {
            var nick = box.Text.Trim();
            if (nick.Length > 0)
            {
                _settings.Nickname = nick;
                _chat.Nickname = nick;
                _settings.Save();
                MeText.Text = "我：" + nick;
                SetFooter("昵称已设为 " + nick);
            }
        }
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync()
    {
        // 对话框保存时会直接改 _settings, 这里先把"当前生效"的值记下来
        var oldCrypto = _settings.CryptoPassword;
        var oldFont = _settings.FontFamily;

        var dlg = new SettingsDialog(_settings) { XamlRoot = Content.XamlRoot };
        var r = await dlg.ShowAsync();
        if (r != ContentDialogResult.Primary) return;

        var restart = dlg.NeedReconnect;
        var cryptoChanged = !string.Equals(oldCrypto, _settings.CryptoPassword, StringComparison.Ordinal);
        var fontChanged = !string.Equals(oldFont, _settings.FontFamily, StringComparison.Ordinal);
        _settings.Save();

        if (restart)
        {
            _chat.Stop();
            Messages.Clear();
            var dlg2 = new ContentDialog
            {
                Title = "设置已保存",
                Content = "服务器或目录已更改，需要重新连接后生效。请重启程序。",
                CloseButtonText = "知道了",
                XamlRoot = Content.XamlRoot,
            };
            await dlg2.ShowAsync();
            return;
        }

        _chat.Nickname = _settings.Nickname;
        ApplyTheme();
        if (fontChanged) ApplyFont();
        MeText.Text = string.IsNullOrWhiteSpace(_settings.Nickname) ? "(未设置昵称)" : "我：" + _settings.Nickname;

        if (cryptoChanged)
        {
            RebuildCrypto();       // 密钥变了: 重新拉一遍消息
            UpdateLockText();
            SetFooter(_chat.EncryptionEnabled ? "加密密码已更新，正在用新密码重新读取消息…" : "已关闭加密，正在重新读取消息…");
        }
        else
        {
            SetFooter("设置已保存");
        }
        UpdateFooter();
    }

    /// <summary>加密密码变了: 重建密钥、清掉已读记录, 让下一轮同步重新拉取并解密。</summary>
    private void RebuildCrypto()
    {
        Messages.Clear();
        _chat.ApplyCryptoPassword(_settings.CryptoPassword);
        BusyRing.IsActive = true;
    }

    /// <summary>把设置里的主题应用到整窗(0=跟随系统 1=浅色 2=深色)。</summary>
    private void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = _settings.Theme switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    /// <summary>把设置里的字体应用到消息列表与输入框(留空 = 系统默认字体)。</summary>
    private void ApplyFont()
    {
        var family = string.IsNullOrWhiteSpace(_settings.FontFamily)
            ? FontFamily.XamlAutoFontFamily
            : new FontFamily(_settings.FontFamily);
        MessageList.FontFamily = family;
        InputBox.FontFamily = family;
        FooterText.FontFamily = family;
    }

    // ---------- 界面辅助 ----------

    private void SetStatus(bool ok, string text)
    {
        StatusDot.Fill = new SolidColorBrush(ok ? Color.FromArgb(255, 0x3F, 0xA9, 0x5C)
                                                : Color.FromArgb(255, 0xE0, 0x8B, 0x2E));
        HeaderStatus.Text = text;
    }

    /// <summary>顶栏上显示当前是否启用端到端加密。</summary>
    private void UpdateLockText()
    {
        if (_chat.EncryptionEnabled)
        {
            LockText.Text = "🔒 端到端加密已启用";
            LockText.Foreground = new SolidColorBrush(Color.FromArgb(255, 0x3F, 0xA9, 0x5C));
        }
        else
        {
            LockText.Text = "未加密（明文发送）";
            LockText.Foreground = (Brush)Application.Current.Resources["MetaOtherBrush"];
        }
    }

    private void SetFooter(string s) { FooterText.Text = s; }

    private void UpdateFooter()
    {
        var bad = _chat.UndecryptableCount;
        FooterText.Text = string.Format("共 {0} 条消息  ·  每 {1} 秒同步  ·  最近同步 {2:HH:mm:ss}  ·  {3}  ·  {4}{5}",
            Messages.Count, _chat.PollSeconds, _lastSync, _settings.ChatFolder,
            _chat.EncryptionEnabled ? "已加密" : "未加密",
            bad > 0 ? "  ·  ⚠ " + bad + " 条无法解密（密码不一致）" : "");
    }
}
