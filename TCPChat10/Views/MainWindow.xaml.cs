using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TCPChat10.Glass;
using TCPChat10.Models;
using TCPChat10.Rendering;
using TCPChat10.Services;
using TCPChat10.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace TCPChat10.Views;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ChatService _chat;
    private DateTimeOffset _lastSync = DateTimeOffset.Now;
    /// <summary>窗口句柄(任务栏闪烁要用)。</summary>
    private readonly IntPtr _hwnd;

    // ---------- 语音录制 / 播放 ----------
    private MediaCapture? _capture;
    private StorageFile? _voiceFile;
    private DateTimeOffset _recordStart;
    private bool _recording;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _recordTimer;
    private readonly MediaPlayer _player = new();
    private MessageVm? _playingVm;

    /// <summary>10.7: 正在等用户去系统设置里打开麦克风开关 —— 回到窗口时自动接着录音。</summary>
    private bool _waitingMic;

    /// <summary>11.0: 液态玻璃层(整窗 Win2D 画布 + 注册的玻璃面)。</summary>
    private GlassHost? _glass;

    public ObservableCollection<MessageVm> Messages { get; } = new();

    // ---------- 搜索 ----------
    private string _searchQuery = "";
    private int _searchMatchIndex = -1;
    private readonly List<MessageVm> _searchMatches = new();

    private SettingsDialog? _dlg;
    private MenuFlyout? _menu;

    public MainWindow()
    {
        this.InitializeComponent();

        _settings = AppSettings.Load();
        _chat = new ChatService(_settings);

        _chat.MessageAdded += OnMessageAdded;
        _chat.StatusChanged += s => DispatcherQueue.TryEnqueue(() => SetFooter(s));
        // 每轮同步完都把底栏刷新一下(以前只在收到新消息时才更新, 所以"最近同步"一直停在旧时间)
        _chat.Synced += t => DispatcherQueue.TryEnqueue(() => { _lastSync = t; UpdateFooter(); });
        _chat.MessageRemoved += OnMessageRemoved;
        _chat.ErrorOccurred += s => DispatcherQueue.TryEnqueue(() => SetFooter("⚠ " + s));

        // 直接赋 ItemsSource: 避免 Window 上 x:Bind 的 OneTime 绑定在某些情况下不生效
        MessageList.ItemsSource = Messages;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // 兜底再挂一次: TextBox 内部把 Enter 标记成已处理时, 普通 XAML 事件收不到,
        // 用 handledEventsToo 才能听见(正常情况下 PreviewKeyDown 已经处理掉了,
        // 那次到这里时输入框已经清空, SendCurrentAsync 会直接返回, 不会重复发送)
        InputBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnInputKeyDown), true);

        ResizeWindow(1120, 780);
        AppIcon.Apply(AppWindow);          // 11.4: 窗口/任务栏图标
        _ = LoadAppMarkAsync();            // 顶栏左上角的小图标
        InitZoom();                        // 11.4: Ctrl +/-/0、Ctrl+滚轮 缩放
        this.AppWindow.Closing += (s, e) =>
        {
            MessageVm.ShuttingDown = true;
            _chat.Stop();
            try { if (_recording) _capture?.StopRecordAsync(); } catch { }
            try { _capture?.Dispose(); } catch { }
            try { _player.Dispose(); } catch { }
        };

        // 语音播完了把按钮图标换回"播放"
        _player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_playingVm != null) _playingVm.IsVoicePlaying = false;
            _playingVm = null;
        });

        // 10.7: 从"麦克风设置"页回到窗口时自动继续(用户不用再点一次语音)
        this.Activated += OnWindowActivated;

        InitGlass();

        // 顶栏那几个按钮也是直角, 加载完统一刷圆角
        if (Content is FrameworkElement cornerRoot)
            cornerRoot.Loaded += (_, _) =>
            {
                UiFont.Apply(cornerRoot);        // 树真正生成后再刷一次字体(顶栏按钮等)
                UiFont.RoundAll(cornerRoot, 8);  // 圆角
            };

        ApplyTheme();
        ApplyFont();
        UpdateLockText();
        MeText.Text = string.IsNullOrWhiteSpace(_settings.Nickname) ? "(未设置昵称)" : "我：" + _settings.Nickname;
        Title = "TCP Chat 11.7 — " + _settings.ChatFolder;

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
                    if (m != null) { m.IsSelf = true; var lv = new MessageVm(m, _chat); Messages.Add(lv); ScrollToBottom(); }
                    AutoLog("AUTO send -> " + (m != null ? "ok" : "fail"));
                }
                else if (a.StartsWith("sendmd:"))
                {
                    // sendmd:文件路径 —— 把文件内容(markdown)当成一条消息发出去(多行文本没法塞进脚本)
                    var text = File.ReadAllText(a[7..].Trim(), System.Text.Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(_settings.Nickname)) { _settings.Nickname = "测试用户"; _chat.Nickname = _settings.Nickname; }
                    var m = await _chat.SendTextAsync(text);
                    if (m != null) { m.IsSelf = true; Messages.Add(new MessageVm(m, _chat)); ScrollToBottom(); }
                    AutoLog("AUTO sendmd -> " + (m != null ? "ok " + text.Length + " 字符" : "fail"));
                }
                else if (a.StartsWith("sendq:"))
                {
                    // sendq:引用内容|正文
                    var parts = a[6..].Split('|', 2);
                    var qt = parts[0];
                    var bt = parts.Length > 1 ? parts[1] : "收到";
                    var m = await _chat.SendTextAsync(bt, qt);
                    if (m != null) { m.IsSelf = true; Messages.Add(new MessageVm(m, _chat)); ScrollToBottom(); }
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
                    var spec = a[7..].Trim();
                    var send = 0;
                    if (spec.StartsWith("!"))                       // crypto:!2|密码A|密码B
                    {
                        var bar = spec.IndexOf('|');
                        if (bar > 0) { send = Math.Max(0, int.Parse(spec[1..bar]) - 1); spec = spec[(bar + 1)..]; }
                    }
                    _settings.CryptoPasswords = spec.Split('|', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                    _settings.SendPasswordIndex = send;
                    _settings.Save();
                    RebuildCrypto();
                    UpdateLockText();
                    AutoLog("AUTO crypto -> " + (_chat.EncryptionEnabled ? "加密已启用" : "加密已关闭"));
                }
                else if (a == "receivecheck")
                {
                    // 收到的一方: 把带附件的消息都下载一遍, 报本地文件大小(验证附件真的取回来了)
                    var withAttach = Messages.Where(v => v.Attach != null).ToList();
                    var parts = new List<string>();
                    foreach (var item in withAttach)
                    {
                        var local = await item.EnsureLocalAsync();
                        var size = local != null && File.Exists(local) ? new FileInfo(local).Length : -1;
                        parts.Add($"{item.Attach!.Name} kind={item.Attach.Kind} 声明={item.Attach.Size}B 本地={size}B");
                    }
                    AutoLog($"AUTO receivecheck 共 {withAttach.Count} 条带附件: " + string.Join(" | ", parts));
                }
                else if (a.StartsWith("attach:"))
                {
                    var path = a[7..].Trim();
                    var m = await _chat.SendFileAsync(path);
                    if (m != null) { m.IsSelf = true; Messages.Add(new MessageVm(m, _chat)); ScrollToBottom(); }
                    AutoLog("AUTO attach -> " + (m != null ? $"ok {m.Attach?.Name} {m.Attach?.SizeText} kind={m.Attach?.Kind}" : "fail"));
                }
                else if (a.StartsWith("voicefile:"))
                {
                    // voicefile:路径|时长毫秒  (录音要麦克风, 自测用现成的 wav 走同一条发送路径)
                    var parts = a[10..].Split('|');
                    var path = parts[0].Trim();
                    var ms = parts.Length > 1 ? int.Parse(parts[1]) : 3000;
                    var m = await _chat.SendFileAsync(path, kind: 5, durationMs: ms);
                    if (m != null) { m.IsSelf = true; Messages.Add(new MessageVm(m, _chat)); ScrollToBottom(); }
                    AutoLog("AUTO voicefile -> " + (m != null ? $"ok {m.Attach?.Name} 时长={m.Attach?.DurationText}" : "fail"));
                }
                else if (a.StartsWith("glass:"))
                {
                    _settings.GlassEnabled = a[6..].Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                    _settings.Save();
                    ApplyGlass();
                    AutoLog("AUTO glass -> " + (_settings.GlassEnabled ? "开" : "关") + " [" + (_glass?.Status ?? "无") + "]");
                }
                else if (a.StartsWith("glassq:"))
                {
                    _settings.GlassQuality = (int)Math.Clamp(double.Parse(a[7..]), 0, 100);
                    _settings.Save();
                    ApplyGlass();
                    AutoLog("AUTO glassq -> " + _settings.GlassQuality + " [" + (_glass?.Status ?? "无") + "]");
                }
                else if (a.StartsWith("glassshot:"))
                {
                    // 直接把玻璃画布自己的内容存成 PNG(桌面被别的窗口挡住时也能核对效果)
                    var path = a[10..].Trim();
                    try
                    {
                        var ok = _glass != null && await _glass.RenderToFileAsync(path);
                        AutoLog("AUTO glassshot -> " + (ok ? "ok " + path + " (" + _glass!.LastDrawMs.ToString("F1") + " ms)" : "画布还没准备好"));
                        if (_glass != null)
                        {
                            foreach (var s in _glass.DescribeSurfaces()) AutoLog("   玻璃面 " + s);
                            var byState = new Dictionary<string, int>();
                            foreach (var line in _glass.DescribeEntries())
                            {
                                AutoLog("   注册面 " + line);
                                var key = line.Contains("画 (") ? "画" : line.Contains("视口外") ? "视口外" : "其它没画";
                                key += line.StartsWith("自己") ? "/自己" : "/别人";
                                byState[key] = byState.GetValueOrDefault(key) + 1;
                            }
                            AutoLog("   注册面统计: " + string.Join(", ", byState.Select(kv => kv.Key + "=" + kv.Value)));
                        }
                    }
                    catch (Exception ex) { AutoLog("AUTO glassshot -> 失败: " + ex.Message); }
                }
                else if (a.StartsWith("glassctl:"))
                {
                    var dir = a[9..].Trim();
                    var ok = _dlg != null && await _dlg.RenderGlassControlsAsync(dir);
                    AutoLog("AUTO glassctl -> " + (ok ? "ok " + dir : "设置对话框没开着"));
                }
                else if (a.StartsWith("mdpreview:"))
                {
                    // mdpreview:文件路径[|other] —— 把本地的 markdown 文件当成一条消息渲染出来。
                    // 只加进列表、不发送(核对 markdown/公式的排版用, 不打扰真实的聊天记录)
                    var spec = a[10..].Trim();
                    var other = false;
                    var bar = spec.LastIndexOf('|');
                    if (bar > 0 && spec[(bar + 1)..].Trim().Equals("other", StringComparison.OrdinalIgnoreCase))
                    {
                        other = true;
                        spec = spec[..bar].Trim();
                    }
                    var text = File.ReadAllText(spec, System.Text.Encoding.UTF8);
                    var preview = new ChatMessage
                    {
                        Id = "preview_" + DateTime.Now.Ticks,
                        From = other ? "同学" : (string.IsNullOrWhiteSpace(_settings.Nickname) ? "我" : _settings.Nickname),
                        Time = DateTimeOffset.Now,
                        Text = text,
                        IsSelf = !other,
                        RemoteName = "preview.json",
                    };
                    Messages.Add(new MessageVm(preview, null));
                    ScrollToBottom();
                    AutoLog("AUTO mdpreview -> 渲染 " + spec + (other ? " (别人)" : " (自己)") + " " + text.Length + " 字符");
                    // 顺带把解析结果报一遍: 公式没排出来时, 一眼能看出是被当成普通段落还是解析失败
                    var parsed = MarkdownParser.Parse(text);
                    AutoLog("AUTO mdpreview 解析 -> 块 " + parsed.Blocks.Count +
                            ", 公式块 " + parsed.Blocks.OfType<MdMathBlock>().Count() +
                            ", 行内公式 " + parsed.Blocks.OfType<MdParagraph>().Sum(p => p.Inlines.OfType<MdMathSpan>().Count()));
                    foreach (var b in parsed.Blocks)
                    {
                        if (b is MdMathBlock mb)
                        {
                            MathParser.Parse(mb.Code);
                            AutoLog("   · 公式块 [" + Cut(mb.Code, 60) + "] 解析=" + (MathParser.LastError ?? "正常"));
                        }
                        else if (b is MdCodeBlock cb) AutoLog("   · 代码块(" + cb.Language + ") [" + Cut(cb.Code, 50) + "]");
                        else if (b is MdParagraph pp) AutoLog("   · 段落 [" + Cut(string.Concat(pp.Inlines.Select(InlineText)), 70) + "]");
                    }
                }
                else if (a.StartsWith("zoom:"))
                {
                    // zoom:+ / zoom:- / zoom:0(复位) / zoom:1.25
                    var spec = a[5..].Trim();
                    if (spec == "+") UiZoom.Step(+1);
                    else if (spec == "-") UiZoom.Step(-1);
                    else if (spec == "0") UiZoom.Reset();
                    else UiZoom.Set(double.Parse(spec));
                    AutoLog("AUTO zoom -> " + UiZoom.Percent + " (正文基准 " + (UiZoom.BaseFontSize * UiZoom.Level).ToString("F1") + ")");
                }
                else if (a == "glassinfo")
                {
                    AutoLog("AUTO glassinfo -> 开关=" + (_settings.GlassEnabled ? "开" : "关") +
                            " 质量=" + _settings.GlassQuality + " 玻璃面=" + (_glass?.SurfaceCount ?? 0) +
                            " 画布=" + GlassCanvas.ActualWidth.ToString("F0") + "x" + GlassCanvas.ActualHeight.ToString("F0") +
                            " 绘制=" + (_glass?.LastDrawMs ?? 0).ToString("F1") + "ms" +
                            " 背景=" + (_glass?.Status ?? "无"));
                }
                else if (a.StartsWith("poll:"))
                {
                    _settings.PollSeconds = (int)Math.Clamp(double.Parse(a[5..]), 1, 120);
                    _settings.Save();
                    AutoLog("AUTO poll -> 同步周期改成 " + _settings.PollSeconds + " 秒 (不重启)");
                }
                else if (a.StartsWith("theme:"))
                {
                    _settings.Theme = (int)Math.Clamp(double.Parse(a[6..]), 0, 2);
                    _settings.Save();
                    ApplyTheme();
                    AutoLog("AUTO theme -> " + _settings.Theme + " (0=跟随系统 1=浅色 2=深色)");
                }
                else if (a.StartsWith("notify:"))
                {
                    var text = a[7..];
                    MessageNotifier.Notify("通知自测", text);
                    MessageNotifier.FlashTaskbar(_hwnd);
                    await Task.Delay(1500);
                    AutoLog($"AUTO notify -> {MessageNotifier.Status} | 弹出结果: {MessageNotifier.LastResult}");
                }
                else if (a == "keytest")
                {
                    // 验证 Enter / Shift+Enter / Ctrl+Enter 的分工
                    InputBox.Text = "第一行";
                    InputBox.SelectionStart = InputBox.Text.Length;
                    var before = Messages.Count;
                    var shiftHandled = HandleEnterKey(shift: true, ctrl: false, out var shiftSend);
                    var afterShift = InputBox.Text.Replace("\r", "\\n");
                    var ctrlHandled = HandleEnterKey(shift: false, ctrl: true, out var ctrlSend);
                    var afterCtrl = InputBox.Text.Replace("\r", "\\n");
                    var enterHandled = HandleEnterKey(shift: false, ctrl: false, out var enterSend);
                    if (enterSend) await SendCurrentAsync();
                    AutoLog($"AUTO keytest Shift+Enter: handled={shiftHandled} 发送={shiftSend} 文本=[{afterShift}] | " +
                            $"Ctrl+Enter: handled={ctrlHandled} 发送={ctrlSend} 文本=[{afterCtrl}] | " +
                            $"Enter: handled={enterHandled} 发送={enterSend} 输入框=[{InputBox.Text}] 消息 {before}->{Messages.Count}");
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
                else if (a.StartsWith("mscroll:"))
                {
                    // 把消息列表滚到指定比例(0=最上面 1=最下面), 便于截图核对长消息
                    var frac = Math.Clamp(double.Parse(a[8..]), 0, 1);
                    if (FindScrollViewer(MessageList) is ScrollViewer sv)
                    {
                        sv.ChangeView(null, sv.ScrollableHeight * frac, null, true);
                        await Task.Delay(500);
                        AutoLog($"AUTO mscroll {frac:F2} -> ok (可滚 {sv.ScrollableHeight:F0} px)");
                    }
                    else AutoLog("AUTO mscroll -> 没找到 ScrollViewer");
                }
                else if (a.StartsWith("shot:"))
                {
                    var path = a[5..];
                    await Task.Delay(900);         // 等一帧布局完成
                    AutoLog($"诊断: Messages={Messages.Count} ListItems={MessageList.Items.Count} " +
                            $"字体={MessageList.FontFamily?.Source ?? "(默认)"} " +
                            $"ListView H={MessageList.ActualHeight:F0} W={MessageList.ActualWidth:F0} " +
                            MicPermission.Diag);
                    var ok = await CaptureAsync(path);
                    AutoLog("AUTO shot -> " + (ok ? "ok " + path : "fail"));
                }
                else if (a.StartsWith("log:"))
                {
                    AutoLog("AUTO " + a[4..] + $"  [底栏={FooterText.Text}] [消息数={Messages.Count} 加密={(_chat.EncryptionEnabled ? "开" : "关")} " +
                            $"解不开={_chat.UndecryptableCount} 输入框焦点={InputBox.FocusState} 文本=[{InputBox.Text.Replace("\r", "/")}]]");
                }
                else if (a == "mic")
                {
                    var allowed = await MicPermission.EnsureAsync();
                    AutoLog("AUTO mic -> " + (allowed ? "有权限" : "没有权限") + " [" + MicPermission.Diag + "]");
                }
                else if (a == "voice")
                {
                    OnVoiceClick(BtnVoice, new RoutedEventArgs());
                    await Task.Delay(1200);
                    AutoLog("AUTO voice -> 录音中=" + _recording + " 按钮=" + BtnVoice.Content);
                }
                else if (a.StartsWith("search:"))
                {
                    var sq = a[7..].Trim();
                    SearchBox.Text = sq;
                    await Task.Delay(300);
                    AutoLog("AUTO search -> 关键字=[" + sq + "] 命中=" + _searchMatches.Count + " 当前=" + (_searchMatchIndex + 1));
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

    /// <summary>自测日志里截断长文本用。</summary>
    private static string Cut(string? s, int n)
    {
        s = (s ?? "").Replace("\r", "").Replace("\n", "¶");
        return s.Length <= n ? s : s[..n] + "…";
    }

    /// <summary>自测日志里把一段行内内容摊平成文字。</summary>
    private static string InlineText(MdInline inline) => inline switch
    {
        MdText t => t.Text,
        MdCodeSpan c => (char)96 + c.Text + (char)96,
        MdMathSpan m => "⟨公式:" + m.Text + "⟩",
        MdStyle s => string.Concat(s.Children.Select(InlineText)),
        MdLink l => string.Concat(l.Children.Select(InlineText)),
        MdBreak => "¶",
        _ => "",
    };

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

    // ---------- 缩放(11.4) ----------

    /// <summary>Ctrl + 加号/减号/0, 以及 Ctrl + 滚轮。</summary>
    private void InitZoom()
    {
        UiZoom.Set(_settings.Zoom, notify: false);     // 上次的缩放比例
        UiZoom.Changed += OnZoomChanged;

        if (Content is UIElement root)
        {
            // 用 Preview(隧道) + handledEventsToo: 输入框/列表先处理了也能收到
            root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnGlobalPreviewKeyDown), true);
            root.PointerWheelChanged += OnZoomWheel;
        }
    }

    private static bool CtrlDown()
    {
        try
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        }
        catch { return false; }
    }

    private void OnGlobalPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!CtrlDown()) return;
        switch ((int)e.Key)
        {
            case 187:                                    // = / +(Shift)
            case (int)VirtualKey.Add:                    // 小键盘 +
                UiZoom.Step(+1);
                e.Handled = true;
                break;
            case 189:                                    // - / _
            case (int)VirtualKey.Subtract:               // 小键盘 -
                UiZoom.Step(-1);
                e.Handled = true;
                break;
            case (int)VirtualKey.Number0:
            case (int)VirtualKey.NumberPad0:
                UiZoom.Reset();
                e.Handled = true;
                break;
            case (int)VirtualKey.F:                       // Ctrl+F: 搜索
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void OnZoomWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!CtrlDown()) return;
        try
        {
            var delta = e.GetCurrentPoint(MessageList).Properties.MouseWheelDelta;
            if (delta == 0) return;
            UiZoom.Step(delta > 0 ? +1 : -1);
            e.Handled = true;
        }
        catch { }
    }

    /// <summary>缩放变了: 重排所有消息(颜色/字号/公式都是算好的, 必须重建), 并写进设置。</summary>
    private void OnZoomChanged()
    {
        try
        {
            MarkdownStyles.Invalidate();
            RefreshBodies();
            MessageList.UpdateLayout();
            _glass?.Refresh();
            _settings.Zoom = UiZoom.Level;
            _settings.Save();
            SetFooter("缩放 " + UiZoom.Percent + "　·　Ctrl+0 复位, Ctrl+滚轮 也行");
        }
        catch (Exception ex) { App.LogCrash("应用缩放失败", ex); }
    }

    /// <summary>顶栏小图标(嵌在程序里的那份, 单文件发布也能用)。</summary>
    private async Task LoadAppMarkAsync()
    {
        var bmp = await AppIcon.LoadMarkAsync();
        if (bmp == null) return;
        DispatcherQueue.TryEnqueue(() => { try { AppMark.Source = bmp; } catch { } });
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
            ApplyThemeTo(dlg);
            ApplyFontTo(dlg);
        DressDialog(dlg);
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

    /// <summary>
    /// 11.1: 把消息列表裁在自己的行里 —— 很高的 markdown 消息/图片、以及滚动时
    /// ListView 复用容器, 都有可能被画到行外面(看起来就是"消息压到顶栏/底栏上")。
    /// </summary>
    private void OnListHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            ListHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, Math.Max(0, e.NewSize.Height - 2)),
            };
        }
        catch { }
    }

    /// <summary>可靠地滚动到最新一条(虚拟化面板下 ScrollIntoView 常常滚不动)。</summary>
    private void ScrollToBottom()
    {
        if (Messages.Count == 0) return;
        EnsureScrollHook();
        StartScrollTracking(25);
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
            StartScrollTracking(15);
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

    // ---------- 搜索 ----------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text?.Trim() ?? "";
        BtnClearSearch.Visibility = query.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        if (query == _searchQuery) return;
        _searchQuery = query;
        RunSearch();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _searchMatches.Count > 0)
        {
            e.Handled = true;
            // Shift+Enter = 上一个, Enter = 下一个
            if (IsKeyDown(VirtualKey.Shift))
                _searchMatchIndex = (_searchMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
            else
                _searchMatchIndex = (_searchMatchIndex + 1) % _searchMatches.Count;
            ScrollToMatch();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            SearchBox.Text = "";
            InputBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ClearSearchHighlight();
        InputBox.Focus(FocusState.Programmatic);
    }

    private void RunSearch()
    {
        ClearSearchHighlight();
        _searchMatches.Clear();
        _searchMatchIndex = -1;

        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            UpdateSearchFooter();
            return;
        }

        foreach (var vm in Messages)
        {
            if (MessageMatchesSearch(vm, _searchQuery))
                _searchMatches.Add(vm);
        }

        if (_searchMatches.Count > 0)
        {
            _searchMatchIndex = _searchMatches.Count - 1; // 从最新的开始
            ScrollToMatch();
        }
        UpdateSearchFooter();
    }

    private void ScrollToMatch()
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count) return;
        var target = _searchMatches[_searchMatchIndex];
        // 高亮当前匹配项
        foreach (var vm in _searchMatches) vm.IsSearchHit = true;
        target.IsSearchCurrent = true;
        MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
        UpdateSearchFooter();
    }

    private void ClearSearchHighlight()
    {
        foreach (var vm in Messages) { vm.IsSearchHit = false; vm.IsSearchCurrent = false; }
    }

    private void UpdateSearchFooter()
    {
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            UpdateFooter();
            return;
        }
        if (_searchMatches.Count == 0)
            SetFooter("搜索「" + _searchQuery + "」— 没有匹配");
        else
            SetFooter("搜索「" + _searchQuery + "」— " + (_searchMatchIndex + 1) + " / " + _searchMatches.Count + " 条匹配  (Enter 下一个 · Shift+Enter 上一个 · Esc 退出)");
    }

    private static bool MessageMatchesSearch(MessageVm vm, string query)
    {
        var m = vm.Model;
        return (m.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (m.Quote?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || (m.From?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            || vm.TimeText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnMessageAdded(ChatMessage m)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Messages.Any(v => v.Key == (m.RemoteName.Length > 0 ? m.RemoteName : m.Id))) return;
            var vm = new MessageVm(m, _chat);

            // 按时间插入到正确位置(轮询可能乱序)
            int idx = Messages.Count;
            while (idx > 0 && Messages[idx - 1].Model.Time > m.Time) idx--;
            Messages.Insert(idx, vm);

            // 别人刚发来的消息: 弹系统通知 + 任务栏闪一下(历史消息、自己发的不打扰)
            if (!m.IsSelf && DateTimeOffset.UtcNow - m.Time < TimeSpan.FromMinutes(2))
            {
                // 通知里显示纯文本(去掉 ** 之类的标记), 太长就截断
                var body = m.DecryptFailed
                    ? "🔒 无法解密：加密密码与发送方不一致"
                    : MarkdownParser.ToPlainText(m.Text);
                if (body.Length > 160) body = body[..160] + "…";
                MessageNotifier.Notify(m.From, body);
                MessageNotifier.FlashTaskbar(_hwnd);
            }

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

    /// <summary>
    /// 回车键: Enter 发送, Shift+Enter / Ctrl+Enter 换行。
    ///
    /// 用 PreviewKeyDown(隧道路由)而不是 KeyDown: TextBox 自己会先把 Enter 当成换行处理掉,
    /// 那个事件到不了我们手上(以前按键按了没反应就是这个原因)。
    /// 注意 e.Handled 必须在 await 之前同步设好, 否则异步发送期间输入框还会插一个换行。
    /// </summary>
    private void OnInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        var shift = IsKeyDown(VirtualKey.Shift);
        var ctrl = IsKeyDown(VirtualKey.Control);
        if (!HandleEnterKey(shift, ctrl, out var send)) return;

        e.Handled = true;
        if (send) _ = SendCurrentAsync();
    }

    /// <summary>
    /// 回车键的同步部分: 返回 false = 这次按键不归我们管(交给 TextBox 换行);
    /// send = true 表示调用方该去发送了。
    /// </summary>
    private bool HandleEnterKey(bool shift, bool ctrl, out bool send)
    {
        send = false;
        if (shift) return false;         // Shift+Enter: TextBox 自己会换行

        if (ctrl)
        {
            InsertNewline();             // Ctrl+Enter: TextBox 不认, 自己插一个换行
            return true;
        }

        send = true;
        return true;
    }

    /// <summary>在光标处插一个换行符(WinUI 的 TextBox 内部用 \r 换行)。</summary>
    private void InsertNewline()
    {
        var caret = InputBox.SelectionStart;
        InputBox.Text = InputBox.Text.Insert(caret, "\r");
        InputBox.SelectionStart = caret + 1;
    }

    /// <summary>
    /// 兜底(handledEventsToo): 有的输入法/环境不走 PreviewKeyDown, 这里再收一次。
    /// 只负责"发送", 换行一律交给预览处理器 —— 否则 Ctrl+Enter 会被插两个换行。
    /// 预览处理器已经把输入框清空的情况, SendCurrentAsync 会因为文本为空直接返回, 不会重复发送。
    /// </summary>
    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        if (IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.Control)) return;
        e.Handled = true;
        _ = SendCurrentAsync();
    }

    private static bool IsKeyDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

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
            var local = new MessageVm(vm, _chat);
            Messages.Add(local);
            UpdateFooter();
            ScrollToBottom();
        }
        InputBox.Focus(FocusState.Programmatic);
    }

    // ---------- 文件 / 语音 ----------

    /// <summary>📎 文件: 选一个文件上传, 图片/视频/音频按类型显示。</summary>
    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_settings.Nickname))
        {
            await PromptNicknameAsync(false);
            if (string.IsNullOrWhiteSpace(_settings.Nickname)) return;
        }

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        BusyRing.IsActive = true;
        var msg = await _chat.SendFileAsync(file.Path);
        BusyRing.IsActive = false;
        if (msg == null) { SetFooter("⚠ 附件发送失败"); return; }

        msg.IsSelf = true;
        Messages.Add(new MessageVm(msg, _chat));
        UpdateFooter();
        ScrollToBottom();
    }

    /// <summary>🎤 语音: 第一次点开始录, 再点一下停止并发送。</summary>
    private async void OnVoiceClick(object sender, RoutedEventArgs e)
    {
        if (_recording) { await StopRecordingAsync(); return; }
        _waitingMic = false;

        if (string.IsNullOrWhiteSpace(_settings.Nickname))
        {
            await PromptNicknameAsync(false);
            if (string.IsNullOrWhiteSpace(_settings.Nickname)) return;
        }

        // 10.7: 先自动确认麦克风权限 —— 没权限就把用户送到系统设置页, 回来自动继续
        if (!await EnsureMicAsync()) return;

        await StartRecordingAsync();
    }

    /// <summary>从麦克风设置页回到窗口: 有权限就直接开始录音。</summary>
    private async void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (!_waitingMic || e.WindowActivationState == WindowActivationState.Deactivated) return;

        await Task.Delay(500);      // 设置写入有一点点延迟
        if (await MicPermission.EnsureAsync())
        {
            _waitingMic = false;
            AutoLog("AUTO mic -> 权限已开启, 自动开始录音");
            SetFooter("麦克风权限已打开，开始录音…");
            await StartRecordingAsync();
        }
        else
        {
            _waitingMic = false;
            SetFooter("仍然没有麦克风权限：" + MicPermission.StatusText +
                      "（设置 → 隐私和安全性 → 麦克风 → 让桌面应用访问你的麦克风）");
        }
    }

    /// <summary>
    /// 录音前确认麦克风权限。非打包程序没有系统弹窗, 只有"让桌面应用访问你的麦克风"这个总开关,
    /// 所以这里直接帮用户把那个设置页打开(10.7)。
    /// </summary>
    private async Task<bool> EnsureMicAsync()
    {
        if (await MicPermission.EnsureAsync()) return true;
        AutoLog("AUTO mic " + MicPermission.Diag);

        bool denied = MicPermission.Status == "Denied";
        var dlg = new ContentDialog
        {
            Title = denied ? "需要麦克风权限" : "打不开麦克风",
            Content = denied
                ? "Windows 还没有允许本程序使用麦克风。\n\n" +
                  "点「打开设置」，把「让桌面应用访问你的麦克风」打开；" +
                  "回到本窗口后会自动开始录音，不用再点一次。"
                : "没有找到可用的麦克风，或者它正被别的程序占用。\n\n状态：" + MicPermission.StatusText,
            PrimaryButtonText = denied ? "打开设置" : "重试",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        ApplyThemeTo(dlg);
        ApplyFontTo(dlg);
        DressDialog(dlg);

        ContentDialogResult r;
        try { r = await dlg.ShowAsync(); }
        catch { return false; }
        if (r != ContentDialogResult.Primary) return false;

        if (!denied) return await MicPermission.EnsureAsync();

        if (await MicPermission.OpenSettingsAsync())
        {
            _waitingMic = true;
            SetFooter("已打开系统的麦克风设置：打开开关后回到本窗口会自动继续");
            await Task.Delay(700);
            if (await MicPermission.EnsureAsync()) { _waitingMic = false; return true; }
        }
        else
        {
            SetFooter("⚠ 打不开系统设置页，请手动到 设置 → 隐私和安全性 → 麦克风 里允许桌面应用");
        }
        return false;
    }

    /// <summary>真正开始录音(权限已经确认过了)。</summary>
    private async Task StartRecordingAsync()
    {
        try
        {
            _capture = new MediaCapture();
            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
            });

            var path = Path.Combine(AppSettings.CacheDir, "voice_" + Guid.NewGuid().ToString("N")[..8] + ".wav");
            // StartRecordToStorageFileAsync 要的是一个"已经存在"的文件 —— 直接对新路径调用
            // GetFileFromPathAsync 会抛 FileNotFoundException(0x80070002), 表现就是
            // "麦克风权限明明开着却录不了"(10.7 修)。先把空文件建出来再交给它写。
            using (File.Create(path)) { }
            var file = await StorageFile.GetFileFromPathAsync(path);
            await _capture.StartRecordToStorageFileAsync(MediaEncodingProfile.CreateWav(AudioEncodingQuality.Medium), file);

            _voiceFile = file;
            _recordStart = DateTimeOffset.Now;
            _recording = true;
            BtnVoice.Content = "⏹ 停止";
            SetFooter("正在录音… 再点一下「停止」发送");

            _recordTimer = DispatcherQueue.CreateTimer();
            _recordTimer.Interval = TimeSpan.FromMilliseconds(500);
            _recordTimer.Tick += (_, _) =>
            {
                if (_recording)
                    SetFooter($"正在录音 {(DateTimeOffset.Now - _recordStart).TotalSeconds:F0} 秒… 再点一下「停止」发送");
            };
            _recordTimer.Start();
        }
        catch (Exception ex)
        {
            _recording = false;
            BtnVoice.Content = "🎤 语音";
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            // 权限问题和"设备/文件问题"分开说, 免得明明是别的原因却让用户去翻设置
            var why = MicPermission.Status == "Denied" || ex.HResult == unchecked((int)0x80070005)
                ? "（系统没允许本程序用麦克风：设置 → 隐私和安全性 → 麦克风 → 让桌面应用访问你的麦克风）"
                : "（错误码 0x" + ex.HResult.ToString("X8") + "，可到设置里换一个输入设备或检查麦克风是否被别的程序占用）";
            SetFooter("⚠ 录音打不开: " + ex.Message + why);
        }
    }

    private async Task StopRecordingAsync()
    {
        _recordTimer?.Stop();
        _recordTimer = null;
        _recording = false;
        BtnVoice.Content = "🎤 语音";

        var capture = _capture;
        var file = _voiceFile;
        _capture = null;
        _voiceFile = null;
        if (capture == null || file == null) return;

        var durationMs = (int)(DateTimeOffset.Now - _recordStart).TotalMilliseconds;
        try
        {
            await capture.StopRecordAsync();
            capture.Dispose();

            if (durationMs < 700)
            {
                SetFooter("录音太短, 已丢弃");
                try { File.Delete(file.Path); } catch { }
                return;
            }

            var seconds = (int)Math.Round(durationMs / 1000.0);
            var target = Path.Combine(AppSettings.CacheDir, $"语音 {seconds}秒.wav");
            File.Copy(file.Path, target, true);
            try { File.Delete(file.Path); } catch { }

            BusyRing.IsActive = true;
            var msg = await _chat.SendFileAsync(target, kind: 5, durationMs: durationMs);
            BusyRing.IsActive = false;
            if (msg == null) { SetFooter("⚠ 语音发送失败"); return; }

            msg.IsSelf = true;
            Messages.Add(new MessageVm(msg, _chat));
            UpdateFooter();
            ScrollToBottom();
            SetFooter($"语音已发送 ({seconds} 秒)");
        }
        catch (Exception ex)
        {
            try { capture.Dispose(); } catch { }
            SetFooter("⚠ 语音发送失败: " + ex.Message);
        }
    }

    /// <summary>点语音条上的播放/暂停。</summary>
    private async void OnVoicePlayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not MessageVm vm) return;

        if (_playingVm == vm && _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
            vm.IsVoicePlaying = false;
            return;
        }

        var path = await vm.EnsureLocalAsync();
        if (path == null || !File.Exists(path)) { SetFooter("⚠ 语音下载失败(密码不一致或网络问题)"); return; }

        _playingVm?.IsVoicePlaying = false;
        _playingVm = vm;
        _player.Source = MediaSource.CreateFromUri(new Uri(path));
        _player.Play();
        vm.IsVoicePlaying = true;
    }

    private async void OnImageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MessageVm vm) await OpenAttachmentAsync(vm);
    }

    private async void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MessageVm vm) await OpenAttachmentAsync(vm);
    }

    /// <summary>下载附件并用系统默认程序打开。</summary>
    private async Task OpenAttachmentAsync(MessageVm vm)
    {
        try
        {
            var path = await vm.EnsureLocalAsync();
            if (path == null || !File.Exists(path)) { SetFooter("⚠ 附件下载失败(密码不一致或网络问题)"); return; }
            var file = await StorageFile.GetFileFromPathAsync(path);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex) { SetFooter("打开失败: " + ex.Message); }
    }

    /// <summary>把附件另存到用户选的位置。</summary>
    private async Task SaveAttachmentAsync(MessageVm vm)
    {
        try
        {
            var path = await vm.EnsureLocalAsync();
            if (path == null || !File.Exists(path)) { SetFooter("⚠ 附件下载失败"); return; }

            var picker = new FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.SuggestedFileName = vm.AttachName;
            var ext = Path.GetExtension(vm.AttachName);
            if (!string.IsNullOrEmpty(ext))
                picker.FileTypeChoices.Add(ext.TrimStart('.').ToUpperInvariant(), new List<string> { ext });
            var target = await picker.PickSaveFileAsync();
            if (target == null) return;

            File.Copy(path, target.Path, true);
            SetFooter("已保存到 " + target.Path);
        }
        catch (Exception ex) { SetFooter("保存失败: " + ex.Message); }
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
            var quoted = MarkdownParser.ToPlainText(vm.BodyText);      // 引用进去的是纯文本, 免得标记符号又露出来
            _quoteText = quoted.Length > 120 ? quoted[..120] + "…" : quoted;
            InputBox.Text = "> " + _quoteText + "\n" + InputBox.Text;
            InputBox.Focus(FocusState.Programmatic);
            SetFooter("已引用，可继续输入");
        };
        flyout.Items.Add(miQuote);

        if (vm.Attach != null)
        {
            var miOpen = new MenuFlyoutItem { Text = "打开附件", Icon = new FontIcon { Glyph = "\uE8E5" } };
            miOpen.Click += async (_, _) => await OpenAttachmentAsync(vm);
            flyout.Items.Add(miOpen);

            var miSave = new MenuFlyoutItem { Text = "另存为…", Icon = new FontIcon { Glyph = "\uE74E" } };
            miSave.Click += async (_, _) => await SaveAttachmentAsync(vm);
            flyout.Items.Add(miSave);
        }

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

        // 菜单是弹出层, 不继承窗口主题和字体, 逐项刷(免得深色下弹出一片白 + 字体不一致)
        foreach (var item in flyout.Items)
            if (item is FrameworkElement fe) { ApplyThemeTo(fe); ApplyFontTo(fe); }

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
        ApplyThemeTo(dlg);
        ApplyFontTo(dlg);
        DressDialog(dlg);
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
        var oldCrypto = _settings.SendPassword + "|" + string.Join("\u0001", _settings.CryptoPasswords) + "|" + _settings.SendPasswordIndex;
        var oldFont = _settings.FontFamily;
        var oldGlass = _settings.GlassEnabled;
        var oldGlassQuality = _settings.GlassQuality;

        var dlg = new SettingsDialog(_settings) { XamlRoot = Content.XamlRoot };
        ApplyThemeTo(dlg);
        ApplyFontTo(dlg);
        DressDialog(dlg);
        // 按钮在对话框模板里, 要等 Opened 之后才拿得到 —— 那时统一刷圆角

        // 玻璃的开关/滑块是实时生效的, 所以对话框里一动就重新应用
        dlg.GlassChanged += ApplyGlass;
        ContentDialogResult r;
        try
        {
            r = await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            // 已经有一个弹窗开着时 ShowAsync 会直接抛, 不能让它把程序带崩
            SetFooter("⚠ 打不开设置窗口: " + ex.Message);
            return;
        }
        if (r != ContentDialogResult.Primary)
        {
            // 取消: 把实时改掉的玻璃设置恢复回去
            if (_settings.GlassEnabled != oldGlass || _settings.GlassQuality != oldGlassQuality)
            {
                _settings.GlassEnabled = oldGlass;
                _settings.GlassQuality = oldGlassQuality;
                ApplyGlass();
            }
            return;
        }

        var restart = dlg.NeedReconnect;
        var newCrypto = _settings.SendPassword + "|" + string.Join("\u0001", _settings.CryptoPasswords) + "|" + _settings.SendPasswordIndex;
        var cryptoChanged = !string.Equals(oldCrypto, newCrypto, StringComparison.Ordinal);
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
            ApplyThemeTo(dlg2);
            ApplyFontTo(dlg2);
            DressDialog(dlg2);
            try { await dlg2.ShowAsync(); } catch { }
            return;
        }

        _chat.Nickname = _settings.Nickname;
        ApplyTheme();
        ApplyGlass();
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
        _chat.ApplyCryptoPasswords(_settings.DecryptCandidates, _settings.SendPasswordIndex);
        BusyRing.IsActive = true;
    }

    /// <summary>设置里的主题对应的 ElementTheme(0=跟随系统 1=浅色 2=深色)。</summary>
    private ElementTheme CurrentTheme => _settings.Theme switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    // ---------- 液态玻璃(11.0) ----------

    // ---------- 11.7: 液态玻璃滚动同步 ----------
    private ScrollViewer? _messageScrollViewer;
    private bool _scrollHooked;
    private int _scrollingFramesLeft = 0;
    private bool _renderingHooked = false;

    private void EnsureScrollHook()
    {
        if (_scrollHooked) return;
        _messageScrollViewer ??= FindScrollViewer(MessageList);
        if (_messageScrollViewer == null) return;
        _scrollHooked = true;

        _messageScrollViewer.ViewChanging += OnScrollViewChanging;
        _messageScrollViewer.ViewChanged += OnScrollViewChanged;
    }

    private void StartScrollTracking(int frames = 30)
    {
        if (!_settings.GlassEnabled || _glass == null) return;
        _scrollingFramesLeft = Math.Max(_scrollingFramesLeft, frames);
        if (!_renderingHooked)
        {
            _renderingHooked = true;
            CompositionTarget.Rendering += OnCompositionRendering;
        }
        _glass.Refresh();
    }

    private void OnScrollViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        StartScrollTracking(20);
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
        {
            StartScrollTracking(15);
        }
        else
        {
            // 滚动停止: 多刷几帧确保最终静止位置分毫不差
            StartScrollTracking(8);
        }
    }

    private void OnCompositionRendering(object? sender, object e)
    {
        if (_scrollingFramesLeft <= 0)
        {
            if (_renderingHooked)
            {
                _renderingHooked = false;
                CompositionTarget.Rendering -= OnCompositionRendering;
            }
            return;
        }
        _scrollingFramesLeft--;
        _glass?.Refresh();
    }

    /// <summary>建玻璃层: 顶栏/输入栏/状态栏先注册, 气泡在加载时自己注册(见 OnBubbleLoaded)。</summary>
    private void InitGlass()
    {
        _glass = new GlassHost(GlassCanvas);

        foreach (var bar in new[] { TopBar, InputBar, FooterBar })
            _glass.Register(new GlassEntry
            {
                Element = bar,
                Radius = 0,
                Style = ChromeGlassStyle,
                IsChrome = true,
            });

        // 布局一变(换行/窗口缩放/新消息)就重新量一遍玻璃面的位置
        if (Content is FrameworkElement root)
            root.LayoutUpdated += (_, _) => _glass?.Refresh();

        // 11.7: 滚动跟手同步 —— 挂接 ScrollViewer 事件与 Composition 渲染循环
        EnsureScrollHook();
        MessageList.Loaded += (_, _) => EnsureScrollHook();
        MessageList.PointerWheelChanged += (_, _) => { EnsureScrollHook(); StartScrollTracking(30); };
        ListHost.SizeChanged += (_, _) => _glass?.Refresh();

        ApplyGlass();
    }

    /// <summary>开/关 + 质量: 立即生效(不用重启)。</summary>
    private void ApplyGlass()
    {
        var on = _settings.GlassEnabled;
        GlassCanvas.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MessageVm.GlassBubbles = on;                 // 气泡底色改成透明, 让画布上的玻璃透出来
        _glass?.Configure(on, _settings.GlassQuality);

        if (on)
        {
            // 面板底色交给玻璃层画, XAML 这边留透明
            RootGrid.Background = TransparentBrush;
            TopBar.Background = TransparentBrush;
            InputBar.Background = TransparentBrush;
            FooterBar.Background = TransparentBrush;
        }
        else
        {
            RootGrid.Background = ThemeLookup.Brush("PageBrush");
            TopBar.Background = ThemeLookup.Brush("PanelBrush");
            InputBar.Background = ThemeLookup.Brush("PanelBrush");
            FooterBar.Background = ThemeLookup.Brush("PanelBrush");
        }

        RefreshBodies();        // 气泡底色跟着变, 让绑定重新求值
        if (!on && _renderingHooked)
        {
            _renderingHooked = false;
            CompositionTarget.Rendering -= OnCompositionRendering;
            _scrollingFramesLeft = 0;
        }
        _glass?.Refresh();
        UpdateFooter();
    }

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    /// <summary>顶栏/输入栏/状态栏的玻璃色调: 浅色主题用白、深色用近黑, 保证上面的字看得清。</summary>
    private static (Color Tint, double Opacity) ChromeGlassStyle()
        => ThemeLookup.IsDark
            ? (Color.FromArgb(255, 22, 24, 30), 0.84)
            : (Color.FromArgb(255, 255, 255, 255), 0.86);

    /// <summary>
    /// 气泡的玻璃色调。
    ///
    /// 11.0 修: 自己的气泡以前直接用主题蓝 (#2B6CB0) 的 62% 透明度铺在折射背景上 ——
    /// 浅色壁纸本来就亮, 蓝色再被冲淡一次, 白字就糊在浅蓝上了(对比度只有 2.8:1 左右)。
    /// 现在自己的气泡改用"更深的蓝 + 更高不透明度": 即使壁纸全白, 白字对比度也有 ~5:1 (WCAG AA)。
    /// 备注: 亮度按 WCAG 相对亮度算, 阈值取 4.5:1。
    /// </summary>
    private static (Color Tint, double Opacity) BubbleGlassStyle(MessageVm? vm)
    {
        if (vm is { IsSelf: true })
        {
            // 深蓝底 + 0.86: 最坏情况(纯白壁纸)下白字对比度 ≈ 5:1
            return ThemeLookup.IsDark
                ? (Color.FromArgb(255, 0x1E, 0x46, 0x7A), 0.86)
                : (Color.FromArgb(255, 0x18, 0x40, 0x74), 0.88);
        }

        // 别人的气泡: 浅色主题用白玻璃(深色字), 深色主题用近黑玻璃(浅色字)。
        //
        // 11.5.2: 这里原来是 0.80 / 0.74 —— 太实了, 壁纸几乎透不过来, 别人的普通消息看着就是一块
        // 纯白(或纯黑)的方块, 完全没有玻璃感。现在降到 0.58 / 0.65(壁纸能透出四成左右):
        //   * 浅色: 白玻璃压在最暗的壁纸上 -> 底 ≈ #949494(L 0.30), 深色字 #1B1B1B(L 0.012) 对比度 ≈ 5.7:1
        //   * 深色: 近黑玻璃压在最亮的壁纸上 -> 底 ≈ #676767(L 0.14), 浅色字 #EDEDF2(L 0.85) 对比度 ≈ 4.7:1
        // 都还在 WCAG AA(4.5:1) 之上, 但壁纸的层次、模糊和折射都能看出来了。
        return ThemeLookup.IsDark
            ? (Color.FromArgb(255, 20, 22, 28), 0.65)
            : (Color.FromArgb(255, 255, 255, 255), 0.58);
    }

    /// <summary>气泡出现在列表里: 注册成一个玻璃面(位置由 LayoutUpdated 统一量)。</summary>
    private void OnBubbleLoaded(object sender, RoutedEventArgs e) => RegisterBubble(sender);

    /// <summary>
    /// 11.5.2: 列表回收气泡容器时, 有的气泡只是换了 DataContext(没有重新 Loaded),
    /// 于是它早就被 Unloaded 注销过、再也没注册回来 —— 表现就是"别人的消息没有玻璃效果"。
    /// DataContext 一变就重新注册一次, 保证屏幕上的每个气泡都在玻璃层里。
    /// </summary>
    private void OnBubbleDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => RegisterBubble(sender);

    private void RegisterBubble(object sender)
    {
        // 气泡的加载/卸载是 XAML 回调, 这里抛异常 = 程序直接退出, 所以全部包起来
        try
        {
            if (_glass == null || sender is not Border border) return;
            // 色调要"画的时候"再问当前的 DataContext, 不能在这里把 vm 抓死 ——
            // 列表复用气泡以后 DataContext 会换成别的消息, 抓死的 vm 会把"自己/别人"判错。
            EnsureScrollHook();
            _glass.Register(new GlassEntry
            {
                Element = border,
                Radius = 10,
                Style = () => BubbleGlassStyle(border.DataContext as MessageVm),
                ClipHost = ListHost,
                IsChrome = false,
            });
            _glass.Refresh();
        }
        catch (Exception ex) { App.LogCrash("气泡注册玻璃失败", ex); }
    }

    private void OnBubbleUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement element) _glass?.Unregister(element);
        }
        catch (Exception ex) { App.LogCrash("气泡移除玻璃失败", ex); }
    }

    /// <summary>把设置里的主题应用到整窗。</summary>
    private void ApplyTheme()
    {
        ThemeLookup.Current = CurrentTheme;
        if (Content is FrameworkElement root) root.RequestedTheme = CurrentTheme;
        RefreshBodies();          // 气泡里的 markdown 颜色是算好的, 换主题要重建
    }

    /// <summary>
    /// 主题或字体变了: 让所有气泡重取配色并重画正文。
    /// 必须逐条通知 —— 气泡上的颜色是 MessageVm 里算好的 Brush, 不通知就不会重新求值
    /// (深色切浅色"消息和时间戳看不清"就是这么来的)。
    /// </summary>
    private void RefreshBodies()
    {
        MarkdownStyles.Invalidate();
        foreach (var vm in Messages) vm.RefreshTheme();
    }

    /// <summary>
    /// 弹出的对话框/右键菜单不在窗口的可视树里, 不会继承 RequestedTheme ——
    /// 不显式指定的话, 程序里设成深色时它们还是系统那套, 白底黑字突然闪出来。
    /// </summary>
    private void ApplyThemeTo(FrameworkElement popup) => popup.RequestedTheme = CurrentTheme;

    /// <summary>
    /// 把设置里的字体应用到所有界面(10.7)。以前只设了消息列表/输入框/状态栏,
    /// 顶栏、按钮、右键菜单、对话框还是系统字体。
    /// </summary>
    private void ApplyFont()
    {
        UiFont.Use(_settings.FontFamily);
        MessageList.FontFamily = UiFont.Family;
        InputBox.FontFamily = UiFont.Family;
        FooterText.FontFamily = UiFont.Family;
        if (Content is FrameworkElement root) UiFont.Apply(root);
        RefreshBodies();          // 代码块/行内代码的等宽字体也在这儿重建
        UpdateFooter();           // 字体没装的话, UpdateFooter 会把它挂在底栏
    }

    /// <summary>弹出层(对话框/菜单)不继承窗口字体, 单独刷一遍。</summary>
    private static void ApplyFontTo(FrameworkElement popup) => UiFont.ApplyTo(popup);

    /// <summary>
    /// 11.2 修"字体不跟随设置": ContentDialog 的按钮在它的模板里, ShowAsync 之前那棵树里还没有它们,
    /// 所以打开时再补刷一次字体 + 圆角 —— 保存/取消这类按钮才会跟着设置走。
    /// </summary>
    private static void DressDialog(ContentDialog dlg)
    {
        dlg.Opened += (_, _) =>
        {
            UiFont.Apply(dlg);
            UiFont.RoundAll(dlg, 8);
        };
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
            LockText.Foreground = ThemeLookup.Brush("MetaOtherBrush");
        }
    }

    private void SetFooter(string s) { FooterText.Text = s; }

    private void UpdateFooter()
    {
        var syncTime = _chat.LastSyncTime?.ToString("HH:mm:ss") ?? "尚未同步";
        // 字体没装的话一直挂在这儿(只在设置里提示一次容易被之后的同步状态覆盖掉)
        var fontNote = UiFont.MissingFont.Length > 0
            ? "  ·  ⚠ 字体「" + UiFont.MissingFont + "」本机没有，已用系统默认"
            : "";
        // 11.5.1: 底栏不再提示「N 条无法解密」(解不开的消息在自己的气泡上已经写清楚了,
        // 底栏一直挂着一句警告只是噪音)。计数本身还留着, 自测日志里能看到。
        FooterText.Text = string.Format("共 {0} 条消息  ·  每 {1} 秒同步  ·  最近同步 {2}  ·  {3}  ·  {4}{5}",
            Messages.Count, _chat.PollSeconds, syncTime, _settings.ChatFolder,
            _chat.EncryptionEnabled
                ? "已加密(发送用第 " + _chat.SendPasswordNumber + " 个 / 共 " + _chat.PasswordCount + " 个密码)"
                : "未加密",
            fontNote);
    }
}
