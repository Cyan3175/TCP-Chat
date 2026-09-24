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

    private static readonly Brush NoEdge = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    /// <summary>气泡底色: 开液态玻璃时是半透明磨砂(壁纸会透出来), 关掉就是原来的实心气泡。</summary>
    public Brush BubbleBrush => LiquidGlass.Enabled
        ? LiquidGlass.BubbleTint(IsSelf, LiquidGlass.Quality)
        : new SolidColorBrush(ThemeLookup.Color(IsSelf ? "BubbleSelfColor" : "BubbleOtherColor"));

    /// <summary>气泡边缘的高光描边, 让半透明气泡看起来像一块玻璃。</summary>
    public Brush BubbleEdgeBrush => LiquidGlass.Enabled
        ? LiquidGlass.BubbleEdge(IsSelf, LiquidGlass.Quality)
        : NoEdge;

    /// <summary>玻璃质量变了: 让已经显示出来的气泡重新取一次颜色(配合 OneWay 绑定)。</summary>
    public void RefreshGlass()
    {
        Raise(nameof(BubbleBrush));
        Raise(nameof(BubbleEdgeBrush));
    }

    public Brush BodyBrush => Model.DecryptFailed
        ? ThemeLookup.Brush("MetaOtherBrush")
        : IsSelf
            ? new SolidColorBrush(Colors.White)
            : ThemeLookup.Brush("BodyOtherBrush");

    public Brush MetaBrush => IsSelf
        ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
        : ThemeLookup.Brush("MetaOtherBrush");

    public Brush SenderBrush => IsSelf
        ? new SolidColorBrush(Color.FromArgb(235, 255, 255, 255))
        : ThemeLookup.Brush("AccentBrush");

    public string Key => Model.RemoteName.Length > 0 ? Model.RemoteName : Model.Id;
}
