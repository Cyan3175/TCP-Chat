using Windows.Media.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace TCPChat10.Services;

/// <summary>
/// 麦克风权限(10.7)。
///
/// 非打包(单文件 exe)程序拿不到"每个应用单独询问"的弹窗 —— Windows 只认
/// 设置 -&gt; 隐私和安全性 -&gt; 麦克风 里那个"让桌面应用访问你的麦克风"总开关。
/// 所以这里做三件事:
///   1. 用 AppCapability 查当前状态;
///   2. 查不到/没把握时, 真的开一次 MediaCapture 试(能开就是有权限);
///   3. 被拒绝时直接把用户送到那个设置页, 省得自己找。
/// </summary>
public static class MicPermission
{
    private static bool _granted;          // 本次运行已经确认可用, 不再重复探测

    /// <summary>最近一次检查结果(给界面/自检看): Allowed / Denied / NoDevice / 说明文字。</summary>
    public static string Status { get; private set; } = "未检查";

    public static string StatusText => Status switch
    {
        "Allowed" => "已允许",
        "Denied" => "未允许",
        "NoDevice" => "没有找到麦克风",
        _ => Status,
    };

    /// <summary>能不能录音。已经允许过就直接返回, 不再探测(探测要开设备, 有几百毫秒开销)。</summary>
    public static async Task<bool> EnsureAsync()
    {
        if (_granted) return true;

        // 1) 先问系统。打包/已授权时这里是准的; 非打包程序可能直接抛异常, 那就走第 2 步
        try
        {
            var capability = AppCapability.Create("microphone");
            var access = capability.CheckAccess();
            if (access == AppCapabilityAccessStatus.Allowed)
            {
                _granted = true;
                Status = "Allowed";
                return true;
            }
            if (access == AppCapabilityAccessStatus.DeniedByUser ||
                access == AppCapabilityAccessStatus.DeniedBySystem)
            {
                Status = "Denied";
                return false;
            }
        }
        catch { /* 非打包场景不支持 AppCapability, 继续往下试 */ }

        // 2) 真开一次录音设备: 这是最靠谱的判断
        try
        {
            var probe = new MediaCapture();
            await probe.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
            });
            probe.Dispose();
            _granted = true;
            Status = "Allowed";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Status = "Denied";
            return false;
        }
        catch (Exception ex)
        {
            // 没有麦克风、被别的程序独占等等
            Status = "NoDevice: " + ex.Message;
            return false;
        }
    }

    /// <summary>已经确认可用时不再探测(录音开始前调用, 避免多开一次设备)。</summary>
    public static void MarkGranted() => _granted = true;

    /// <summary>打开 Windows 的麦克风隐私设置页。</summary>
    public static async Task<bool> OpenSettingsAsync()
    {
        try { return await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone")); }
        catch { return false; }
    }

    /// <summary>给自检/日志用的一行状态。</summary>
    public static string Diag => "麦克风=" + StatusText;
}
