using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TCPChat10.Models;
using TCPChat10.Rendering;
using TCPChat10.Services;
using Windows.UI;

namespace TCPChat10.ViewModels;

/// <summary>消息的显示模型(把 ChatMessage 转成界面能直接绑定的属性)。</summary>
public sealed class MessageVm : INotifyPropertyChanged
{
    public ChatMessage Model { get; }
    private readonly ChatService? _chat;

    /// <summary>窗口正在关闭时置位, 避免后台任务再去碰已经销毁的 XAML 对象。</summary>
    public static bool ShuttingDown;

    public MessageVm(ChatMessage model, ChatService? chat = null)
    {
        Model = model;
        _chat = chat;

        if (Model.Attach != null)
        {
            _imageVisible = HasThumb ? Visibility.Visible : Visibility.Collapsed;
            _cardVisible = IsPlainAttach ? Visibility.Visible : Visibility.Collapsed;
        }

        if (HasThumb) _ = LoadPreviewAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // ---------- 文本 ----------
    public string SenderText => Model.DecryptFailed
        ? "(加密消息)"
        : string.IsNullOrWhiteSpace(Model.From) ? "(匿名)" : Model.From;

    public string TimeText => Model.Time.ToLocalTime().ToString("MM-dd HH:mm:ss");

    public string BodyText => Model.DecryptFailed
        ? "🔒 无法解密：本机的加密密码与发送方不一致"
        : Model.Text ?? "";

    public string QuoteText => Model.DecryptFailed ? "" : Model.Quote ?? "";

    public Visibility HasQuote => !Model.DecryptFailed && !string.IsNullOrWhiteSpace(Model.Quote)
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HasBody => !string.IsNullOrWhiteSpace(BodyText) ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 正文(markdown 渲染, 10.7) ----------

    private UIElement? _body;
    private int _bodyStamp = -1;

    /// <summary>
    /// 气泡正文。有 markdown 标记的交给渲染器, 纯文本直接一个 TextBlock(消息多的时候不能每条都解析)。
    /// 主题/字体变了 MarkdownStyles.Version 会变, 这里跟着重建一次(颜色和字体是算好的, 不会自己更新)。
    /// </summary>
    public UIElement? Body
    {
        get
        {
            var text = BodyText;
            if (string.IsNullOrEmpty(text)) return null;

            var stamp = MarkdownStyles.Version;
            if (_body != null && _bodyStamp == stamp) return _body;

            try
            {
                var style = MarkdownStyles.For(IsSelf, Model.DecryptFailed);
                _body = text.Length > 20000 || !MarkdownParser.HasMarkup(text)
                    ? MarkdownView.BuildPlain(text, style)
                    : MarkdownView.Build(MarkdownParser.Parse(text), style);
                _bodyStamp = stamp;
            }
            catch
            {
                // 渲染出意外也不能让消息消失
                try { _body = MarkdownView.BuildPlain(text, MarkdownStyles.For(IsSelf, Model.DecryptFailed)); } catch { }
                _bodyStamp = stamp;
            }
            return _body;
        }
    }

    /// <summary>主题/字体变了: 让界面重新取一次 Body。</summary>
    public void InvalidateBody()
    {
        _body = null;
        _bodyStamp = -1;
        Raise(nameof(Body));
    }

    /// <summary>
    /// 主题变了(10.7 修)。
    /// 气泡底色、正文字色、昵称/时间颜色都是"算一次就存下来"的 Brush; 不逐条通知界面的话,
    /// 深色切浅色时它们还留着深色那套(浅底上浅灰字), 已经在屏幕上的消息就变得看不清了。
    /// </summary>
    public void RefreshTheme()
    {
        Raise(nameof(BubbleBrush));
        Raise(nameof(BodyBrush));
        Raise(nameof(MetaBrush));
        Raise(nameof(SenderBrush));
        Raise(nameof(QuoteBrush));
        Raise(nameof(StatusBrush));
        Raise(nameof(AttachBrush));
        InvalidateBody();
    }
    public Visibility HasStatus => string.IsNullOrEmpty(Model.Status) ? Visibility.Collapsed : Visibility.Visible;
    public string StatusText => Model.Status ?? "";
    public Visibility PendingVisible => Model.Pending ? Visibility.Visible : Visibility.Collapsed;

    public bool DecryptFailed => Model.DecryptFailed;

    public Visibility LockBadgeVisible => Model.IsEncrypted && !Model.DecryptFailed
        ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 附件 ----------
    public Attachment? Attach => Model.Attach;
    public Visibility AttachVisible => Model.Attach == null ? Visibility.Collapsed : Visibility.Visible;
    public string AttachName => Model.Attach?.Name ?? "";
    public string AttachSizeText => Model.Attach?.SizeText ?? "";
    public string AttachGlyph => Model.Attach?.Kind switch { 2 => "🖼", 3 => "🎬", 4 => "🎵", 5 => "🎤", _ => "📄" };

    public bool IsImageAttach => Model.Attach?.Kind == 2;
    public bool IsVideoAttach => Model.Attach?.Kind == 3;
    public bool IsAudioAttach => Model.Attach?.Kind == 4;
    public bool IsVoiceAttach => Model.Attach?.Kind == 5;

    /// <summary>图片/视频: 用缩略图预览。</summary>
    public bool HasThumb => IsImageAttach || IsVideoAttach;

    /// <summary>语音和音频都用同一条"播放"行。</summary>
    public bool HasPlayRow => IsVoiceAttach || IsAudioAttach;

    /// <summary>剩下的(文档/压缩包/未知类型)用文件卡片。</summary>
    public bool IsPlainAttach => Model.Attach != null && !HasThumb && !HasPlayRow;

    /// <summary>语音显示时长, 音频显示文件名。</summary>
    public string PlayRowText => IsVoiceAttach
        ? Model.Attach?.DurationText ?? ""
        : Model.Attach?.Name ?? "";

    public string PlayRowGlyph => IsVoiceAttach ? "🎤" : "🎵";
    public string PlayRowTip => IsVoiceAttach ? "语音" : "音频";
    public Visibility VoiceVisible => HasPlayRow ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>视频缩略图右下角那个播放角标。</summary>
    public Visibility VideoBadgeVisible => IsVideoAttach ? Visibility.Visible : Visibility.Collapsed;

    private Visibility _imageVisible = Visibility.Collapsed;
    private Visibility _cardVisible = Visibility.Collapsed;
    public Visibility ImageVisible { get => _imageVisible; private set { _imageVisible = value; Raise(); } }
    public Visibility CardVisible { get => _cardVisible; private set { _cardVisible = value; Raise(); } }

    private BitmapImage? _image;
    public BitmapImage? ImageSource
    {
        get => _image;
        private set { if (ShuttingDown) return; _image = value; Raise(); }
    }

    /// <summary>语音正在播放(按钮图标跟着变)。</summary>
    private bool _playing;
    public bool IsVoicePlaying
    {
        get => _playing;
        set
        {
            if (_playing == value) return;
            _playing = value;
            Raise();
            Raise(nameof(VoiceGlyph));
            Raise(nameof(VoiceHint));
        }
    }

    public string VoiceGlyph => _playing ? "\uE769" : "\uE768";     // Segoe 字体: 暂停 / 播放
    public string VoiceHint => _playing ? "正在播放" : "点击播放";

    public string? LocalAttachmentPath { get; private set; }

    /// <summary>图片直接读; 视频用 MediaClip 抠第一帧当缩略图。</summary>
    private async Task LoadPreviewAsync()
    {
        if (_chat == null || Model.Attach == null) return;
        try
        {
            var path = await _chat.DownloadAttachmentAsync(Model);
            if (path == null) { ShowCardFallback(); return; }
            LocalAttachmentPath = path;

            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            if (IsImageAttach)
            {
                using var fs = File.OpenRead(path);
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            }
            else
            {
                // 视频: 用 MediaComposition 取第 0 帧的缩略图(WinRT 自带, 不用额外解码器)
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var composition = new Windows.Media.Editing.MediaComposition();
                composition.Clips.Add(await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file));
                using var thumb = await composition.GetThumbnailAsync(TimeSpan.Zero, 480, 270,
                    Windows.Media.Editing.VideoFramePrecision.NearestFrame);
                if (thumb == null) { ShowCardFallback(); return; }
                await bmp.SetSourceAsync(thumb);
            }

            if (ShuttingDown) return;
            ImageSource = bmp;
        }
        catch { ShowCardFallback(); }
    }

    /// <summary>图片拿不到(比如密码不一致)就退回文件卡片, 至少还能右键另存为。</summary>
    private void ShowCardFallback()
    {
        if (ShuttingDown) return;
        ImageVisible = Visibility.Collapsed;
        CardVisible = Visibility.Visible;
    }

    /// <summary>拿到本地文件(缓存里没有就下载)。</summary>
    public async Task<string?> EnsureLocalAsync()
    {
        if (LocalAttachmentPath != null) return LocalAttachmentPath;
        if (_chat == null || Model.Attach == null) return null;
        LocalAttachmentPath = await _chat.DownloadAttachmentAsync(Model);
        return LocalAttachmentPath;
    }

    // ---------- 外观 ----------
    public bool IsSelf => Model.IsSelf;
    public HorizontalAlignment Alignment => IsSelf ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Thickness BubbleMargin => IsSelf ? new Thickness(60, 2, 0, 2) : new Thickness(0, 2, 60, 2);
    public CornerRadius BubbleRadius => IsSelf ? new CornerRadius(12, 12, 3, 12) : new CornerRadius(12, 12, 12, 3);

    public Brush BubbleBrush => IsSelf
        ? new SolidColorBrush(ThemeLookup.Color("BubbleSelfColor"))
        : ThemeLookup.Brush("BubbleOtherBrush");

    public Brush BodyBrush => Model.DecryptFailed
        ? ThemeLookup.Brush("MetaOtherBrush")
        : IsSelf
            ? new SolidColorBrush(Colors.White)
            : ThemeLookup.Brush("BodyOtherBrush");

    public Brush QuoteBrush => BodyBrush;
    public Brush StatusBrush => ThemeLookup.Brush("ErrorBrush");
    // 昵称和时间画在气泡"外面"(页面底色上), 不是画在气泡里 ——
    // 10.6 及以前自己这边用的是半透明白, 浅色模式下几乎看不见(10.7 修正)。
    public Brush MetaBrush => ThemeLookup.Brush("MetaOtherBrush");

    public Brush SenderBrush => IsSelf
        ? ThemeLookup.Brush("BodyOtherBrush")
        : ThemeLookup.Brush("AccentBrush");

    /// <summary>附件卡片/语音条的底色: 跟气泡区分开。</summary>
    public Brush AttachBrush => IsSelf
        ? new SolidColorBrush(Color.FromArgb(38, 255, 255, 255))
        : ThemeLookup.Brush("QuoteBrush");

    public string Key => Model.RemoteName.Length > 0 ? Model.RemoteName : Model.Id;
}
