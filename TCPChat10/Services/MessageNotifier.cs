using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace TCPChat10.Services;

/// <summary>
/// 收到新消息时: 弹一个系统通知(标题=发送者, 正文=消息) + 让任务栏图标闪几下。
///
/// 未打包的桌面程序也能用 Windows App SDK 的 AppNotificationManager:
/// Register() 会在注册表里登记 AUMID 与 COM 激活入口, 之后就能像商店应用一样弹通知。
/// 拿不到(系统关掉了通知/策略限制)就只闪烁, 不影响收消息。
/// </summary>
public static class MessageNotifier
{
    /// <summary>系统通知是否可用。</summary>
    public static bool Available { get; private set; }

    /// <summary>状态说明(写进日志, 方便排查"为什么没弹通知")。</summary>
    public static string Status { get; private set; } = "未初始化";

    /// <summary>最近一次弹出的结果(成功/失败原因)。</summary>
    public static string LastResult { get; private set; } = "(还没弹过)";

    /// <summary>启动时注册一次。</summary>
    public static void Init()
    {
        try
        {
            AppNotificationManager.Default.Register();
            Available = true;
            Status = "系统通知已就绪";
        }
        catch (Exception ex)
        {
            Available = false;
            Status = "系统通知不可用(只闪烁): " + ex.Message;
        }
    }

    /// <summary>弹一条通知。</summary>
    public static void Notify(string from, string body)
    {
        if (!Available) return;
        try
        {
            var text = string.IsNullOrWhiteSpace(body) ? "(空消息)" : body;
            if (text.Length > 140) text = text[..140] + "…";

            var notification = new AppNotificationBuilder()
                .AddText(string.IsNullOrWhiteSpace(from) ? "(匿名)" : from)
                .AddText(text)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
            LastResult = "已提交给系统 " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            LastResult = ex.GetType().Name + ": " + ex.Message;   // 弹不出来就算了, 不能影响收消息
        }
    }

    // ---------- 任务栏闪烁 ----------

    private const uint FLASHW_ALL = 0x00000003;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>任务栏图标闪烁(窗口已经在前台时系统会自动忽略)。</summary>
    public static void FlashTaskbar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 5,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch { }
    }
}
