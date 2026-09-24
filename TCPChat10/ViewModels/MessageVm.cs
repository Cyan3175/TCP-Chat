using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TCPChat10.Models;
using TCPChat10.Services;
using Windows.UI;

namespace TCPChat10.ViewModels;

/// <summary>消息的显示模型(把 ChatMessage 转成界面能直接绑定的属性)。</summary>
public sealed class MessageVm : INotifyPropertyChanged
{
    public ChatMessage Model { get; }

    public MessageVm(ChatMessage model) => Model = model;

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
    public Visibility HasStatus => string.IsNullOrEmpty(Model.Status) ? Visibility.Collapsed : Visibility.Visible;
    public string StatusText => Model.Status ?? "";
    public Visibility PendingVisible => Model.Pending ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>解不开的密文: 正文位置显示锁定提示, 不显示引用块。</summary>
    public bool DecryptFailed => Model.DecryptFailed;

    /// <summary>成功解密的密文消息上挂一把小锁, 便于确认"这条是加密发过来的"。</summary>
    public Visibility LockBadgeVisible => Model.IsEncrypted && !Model.DecryptFailed
        ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>引用块里的字: 跟着气泡正文的颜色走, 否则在蓝色气泡上会是黑字 / 深色下看不清。</summary>
    public Brush QuoteBrush => BodyBrush;

    /// <summary>发送失败等状态文字: 深浅色各一套红, 深色下不能再用暗红。</summary>
    public Brush StatusBrush => ThemeLookup.Brush("ErrorBrush");

    public Brush MetaBrush => IsSelf
        ? new SolidColorBrush(Color.FromArgb(215, 255, 255, 255))
        : ThemeLookup.Brush("MetaOtherBrush");

    public Brush SenderBrush => IsSelf
        ? new SolidColorBrush(Color.FromArgb(245, 255, 255, 255))
        : ThemeLookup.Brush("AccentBrush");

    public string Key => Model.RemoteName.Length > 0 ? Model.RemoteName : Model.Id;
}
