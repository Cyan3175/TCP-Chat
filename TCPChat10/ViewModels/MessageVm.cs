using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TCPChat10.Models;
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

    public MessageVm(ChatMessage model, ChatService? chat)
    {
        Model = model;
        _chat = chat;

        // 有附件: 图片先占位等异步加载, 其余直接显示文件卡片
        if (Model.Attach != null)
        {
            _imageVisible = IsImageAttach ? Visibility.Visible : Visibility.Collapsed;
            _cardVisible = IsImageAttach ? Visibility.Collapsed : Visibility.Visible;
        }

        if (IsImageAttach) _ = LoadImageAsync();
        if (IsVideoAttach || IsAudioAttach) _ = LoadThumbAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    // ---------- 文本 ----------
    public string SenderText => string.IsNullOrWhiteSpace(Model.From) ? "(匿名)" : Model.From;
    public string TimeText => Model.Time.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public string BodyText => Model.Text ?? "";
    public string QuoteText => Model.Quote ?? "";

    public Visibility HasQuote => string.IsNullOrWhiteSpace(Model.Quote) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasBody => string.IsNullOrWhiteSpace(Model.Text) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HasStatus => string.IsNullOrEmpty(Model.Status) ? Visibility.Collapsed : Visibility.Visible;
    public string StatusText => Model.Status ?? "";
    public Visibility PendingVisible => Model.Pending ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 附件 ----------
    public Attachment? Attach => Model.Attach;
    public Visibility HasAttach => Model.Attach == null ? Visibility.Collapsed : Visibility.Visible;
    public string AttachName => Model.Attach?.Name ?? "";
    public string AttachSizeText => Model.Attach?.SizeText ?? "";
    public string AttachGlyph => Model.Attach?.Kind switch { 2 => "🖼", 3 => "🎬", 4 => "🎵", _ => "📄" };
    public bool IsImageAttach => Model.Attach?.Kind == 2;
    public bool IsVideoAttach => Model.Attach?.Kind == 3;
    public bool IsAudioAttach => Model.Attach?.Kind == 4;
    public bool IsPlainAttach => Model.Attach != null && Model.Attach.Kind == 1;
    public bool IsThumbAttach => IsVideoAttach || IsAudioAttach;

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

    private BitmapImage? _thumb;
    public BitmapImage? ThumbSource { get => _thumb; private set { _thumb = value; Raise(); } }

    public string? LocalAttachmentPath { get; private set; }

    private async Task LoadImageAsync()
    {
        if (_chat == null || Model.Attach == null) return;
        try
        {
            var path = await _chat.DownloadAttachmentAsync(Model);
            if (path == null) { ShowCardFallback(); return; }
            LocalAttachmentPath = path;

            var bmp = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            using (var fs = File.OpenRead(path))
            {
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            }
            if (ShuttingDown) return;
            ImageSource = bmp;
        }
        catch { ShowCardFallback(); }
    }

    /// <summary>图片拿不到就退回文件卡片, 至少还能右键另存为。</summary>
    private void ShowCardFallback()
    {
        if (ShuttingDown) return;
        ImageVisible = Visibility.Collapsed;
        CardVisible = Visibility.Visible;
    }

    private async Task LoadThumbAsync()
    {
        // 视频/音频没有内置解码, 只显示卡片; 这里保留占位以便将来接缩略图
        await Task.CompletedTask;
        Raise(nameof(ThumbSource));
    }

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
        ? new SolidColorBrush(Color.FromArgb(255, 0x2B, 0x6C, 0xB0))
        : (Brush)Application.Current.Resources["BubbleOtherBrush"];
    public Brush BodyBrush => IsSelf
        ? new SolidColorBrush(Colors.White)
        : (Brush)Application.Current.Resources["BodyOtherBrush"];
    public Brush MetaBrush => IsSelf
        ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
        : (Brush)Application.Current.Resources["MetaOtherBrush"];
    public Brush SenderBrush => IsSelf
        ? new SolidColorBrush(Color.FromArgb(235, 255, 255, 255))
        : (Brush)Application.Current.Resources["AccentBrush"];

    public string Key => Model.RemoteName.Length > 0 ? Model.RemoteName : Model.Id;
}
