using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCPChat10.Services;

/// <summary>本地设置, 保存在 exe 同目录的 settings.json(目录不可写时退回 %LOCALAPPDATA%\TCPChat10)。</summary>
public sealed class AppSettings
{
    /// <summary>共享目录里直接放消息文件, 不再另建"聊天"子文件夹。</summary>
    public const string DefaultChatFolder = "nw集训/学生资料临存";

    /// <summary>10.0/10.1 的默认值: 会在共享目录下单独建一个"聊天"文件夹, 读到它自动上移一级。</summary>
    public const string LegacyDefaultChatFolder = "nw集训/学生资料临存/聊天";

    public const string DefaultServerUrl = "https://dev.zhaohans.cn";

    [JsonPropertyName("serverUrl")] public string ServerUrl { get; set; } = DefaultServerUrl;
    [JsonPropertyName("chatFolder")] public string ChatFolder { get; set; } = DefaultChatFolder;
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("pollSeconds")] public int PollSeconds { get; set; } = 3;
    [JsonPropertyName("historyDays")] public int HistoryDays { get; set; } = 7;
    [JsonPropertyName("autoScroll")] public bool AutoScroll { get; set; } = true;
    [JsonPropertyName("theme")] public int Theme { get; set; } = 0;   // 0=跟随系统 1=浅色 2=深色
    [JsonPropertyName("fontFamily")] public string FontFamily { get; set; } = "";   // 空 = 系统默认字体

    /// <summary>端到端加密密码: 收发双方必须完全一致, 留空表示不加密(明文发送)。</summary>
    [JsonPropertyName("cryptoPassword")] public string CryptoPassword { get; set; } = "";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCPChat10");
    private static string LocalFilePath => Path.Combine(Dir, "settings.json");

    /// <summary>
    /// exe 所在目录。注意不能用 AppContext.BaseDirectory: 单文件发布时它指向 %TEMP%\.net 下的解压目录,
    /// 只用来找"老版本留在 exe 旁边的那份设置"做一次性迁移, 新设置不再写到这里。
    /// </summary>
    public static string ExeDir
    {
        get
        {
            var exe = Environment.ProcessPath;
            var dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
            return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) : dir!;
        }
    }

    /// <summary>
    /// 设置文件放在系统的统一位置: %LOCALAPPDATA%\TCPChat10\settings.json
    /// (不往 exe 目录里写东西; 早期 10.2~10.5 放在 exe 旁边的那份会在第一次运行时自动搬过来)
    /// </summary>
    public static string FilePath => LocalFilePath;

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    /// <summary>缺失/非法值兜底, 并把 10.0/10.1 的旧默认目录迁移到新默认目录。</summary>
    private AppSettings Normalize()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl)) ServerUrl = DefaultServerUrl;
        if (string.IsNullOrWhiteSpace(ChatFolder)) ChatFolder = DefaultChatFolder;
        ChatFolder = ChatFolder.Trim().Trim('/');
        if (string.Equals(ChatFolder, LegacyDefaultChatFolder, StringComparison.Ordinal))
            ChatFolder = DefaultChatFolder;
        if (PollSeconds < 1) PollSeconds = 3;
        if (HistoryDays < 1) HistoryDays = 7;
        return this;
    }

    public static AppSettings Load()
    {
        // 测试钩子: 用独立配置文件, 避免污染真实设置
        var overridePath = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(overridePath!));
                if (s != null) return s.Normalize();
            }
            catch { }
        }
        try
        {
            var path = FilePath;
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (s != null) return s.Normalize();
            }

            // 10.2~10.5 早期版本把设置放在 exe 旁边: 第一次运行时搬进系统目录, 搬完把旧文件删掉
            var legacy = Path.Combine(ExeDir, "settings.json");
            if (!string.Equals(legacy, path, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy))
            {
                var old = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(legacy));
                if (old != null)
                {
                    var s = old.Normalize();
                    s.Save();                      // 写进 %LOCALAPPDATA%\TCPChat10\
                    try { File.Delete(legacy); } catch { }
                    return s;
                }
            }
        }
        catch { /* 读失败就用默认值 */ }
        return new AppSettings();
    }

    // 测试钩子下保存到独立的文件, 否则写回真实设置
    private static string SavePath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS");
            return string.IsNullOrWhiteSpace(overridePath) ? FilePath : overridePath!;
        }
    }

    public void Save()
    {
        try
        {
            var path = SavePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(Normalize(), Opts));
        }
        catch { /* 忽略保存失败 */ }
    }

    /// <summary>程序数据目录(settings.json / crash.log 都在这儿, 即 %LOCALAPPDATA%\TCPChat10)。</summary>
    public static string DataDir
    {
        get
        {
            Directory.CreateDirectory(Dir);
            return Dir;
        }
    }
}
