using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCPChat10.Services;

/// <summary>本地设置, 保存在 %LOCALAPPDATA%\TCPChat10\settings.json。</summary>
public sealed class AppSettings
{
    [JsonPropertyName("serverUrl")] public string ServerUrl { get; set; } = "https://dev.zhaohans.cn";
    [JsonPropertyName("chatFolder")] public string ChatFolder { get; set; } = "nw集训/学生资料临存/聊天";
    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("userName")] public string UserName { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("pollSeconds")] public int PollSeconds { get; set; } = 3;
    [JsonPropertyName("historyDays")] public int HistoryDays { get; set; } = 7;
    [JsonPropertyName("autoScroll")] public bool AutoScroll { get; set; } = true;
    [JsonPropertyName("theme")] public int Theme { get; set; } = 0;   // 0=跟随系统 1=浅色 2=深色

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCPChat10");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        // 测试钩子: 用独立配置文件, 避免污染真实设置
        var overridePath = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(overridePath!));
                if (s != null) return s;
            }
            catch { }
        }
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null)
                {
                    if (string.IsNullOrWhiteSpace(s.ServerUrl)) s.ServerUrl = "https://dev.zhaohans.cn";
                    if (string.IsNullOrWhiteSpace(s.ChatFolder)) s.ChatFolder = "nw集训/学生资料临存/聊天";
                    if (s.PollSeconds < 1) s.PollSeconds = 3;
                    return s;
                }
            }
        }
        catch { /* 读失败就用默认值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* 忽略保存失败 */ }
    }

    public static string DataDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCPChat10");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>本地附件缓存目录。</summary>
    public static string CacheDir
    {
        get
        {
            var d = Path.Combine(DataDir, "cache");
            Directory.CreateDirectory(d);
            return d;
        }
    }
}
