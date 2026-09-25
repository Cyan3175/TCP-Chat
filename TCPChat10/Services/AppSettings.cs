using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCPChat10.Services;

/// <summary>本地设置, 保存在 %LOCALAPPDATA%\TCPChat\settings.json。</summary>
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

    /// <summary>11.0: 液态玻璃(背景图 + 折射面板)。默认开。</summary>
    [JsonPropertyName("glassEnabled")] public bool GlassEnabled { get; set; } = true;

    /// <summary>11.0: 玻璃质量 0~100(湍流层数 / 模糊 / 色彩增强 / 渲染分辨率)。</summary>
    [JsonPropertyName("glassQuality")] public int GlassQuality { get; set; } = 60;

    /// <summary>数据目录名。10.7 起用 TCPChat(之前叫 TCPChat10)。</summary>
    private const string DirName = "TCPChat";

    /// <summary>10.0~10.6 用的老目录名, 第一次运行时搬过来。</summary>
    private const string LegacyDirName = "TCPChat10";

    // 测试钩子: 让单元测试在临时目录里演练"搬目录", 不碰真实用户配置
    private static string? TestDirOverride => Environment.GetEnvironmentVariable("TCPCHAT10_TEST_DATADIR");
    private static string? TestLegacyDirOverride => Environment.GetEnvironmentVariable("TCPCHAT10_TEST_LEGACYDIR");

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>%LOCALAPPDATA%\TCPChat(测试时可用 TCPCHAT10_TEST_DATADIR 指到别处)。</summary>
    internal static string Dir => TestDirOverride is { Length: > 0 } t
        ? t
        : Path.Combine(LocalAppData, DirName);

    /// <summary>10.6 及更早的 %LOCALAPPDATA%\TCPChat10。</summary>
    internal static string LegacyDir => TestLegacyDirOverride is { Length: > 0 } t
        ? t
        : Path.Combine(LocalAppData, LegacyDirName);

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
    /// 设置文件放在系统的统一位置: %LOCALAPPDATA%\TCPChat\settings.json
    /// (不往 exe 目录里写东西; 10.2~10.5 放在 exe 旁边的那份, 以及 10.6 的 TCPChat10 目录,
    ///  都会在第一次运行时自动搬过来)
    /// </summary>
    public static string FilePath => LocalFilePath;

    private static bool _migrated;

    /// <summary>单元测试用: 允许重复演练"搬目录"(正式运行一个进程只搬一次)。</summary>
    internal static void ResetMigrationForTests() => _migrated = false;

    /// <summary>
    /// 10.7: 数据目录从 %LOCALAPPDATA%\TCPChat10 改成 %LOCALAPPDATA%\TCPChat。
    /// 第一次运行时把老的 settings.json 和附件缓存搬过来, 老目录能删就删掉(不破坏用户数据)。
    /// </summary>
    internal static void MigrateLegacyDataDir()
    {
        if (_migrated) return;
        _migrated = true;
        try
        {
            var oldDir = LegacyDir;
            var newDir = Dir;
            if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(oldDir)) return;

            Directory.CreateDirectory(newDir);

            // 设置文件: 新目录里还没有才搬(新目录里的永远优先)
            var oldSettings = Path.Combine(oldDir, "settings.json");
            var newSettings = Path.Combine(newDir, "settings.json");
            if (File.Exists(oldSettings) && !File.Exists(newSettings))
                File.Copy(oldSettings, newSettings, overwrite: false);

            // 附件/语音缓存: 同名文件不覆盖
            var oldCache = Path.Combine(oldDir, "cache");
            var newCache = Path.Combine(newDir, "cache");
            if (Directory.Exists(oldCache))
            {
                Directory.CreateDirectory(newCache);
                foreach (var f in Directory.EnumerateFiles(oldCache))
                {
                    var dest = Path.Combine(newCache, Path.GetFileName(f));
                    if (!File.Exists(dest)) { try { File.Copy(f, dest, overwrite: false); } catch { } }
                }
            }

            // 老目录里已经没有我们需要的东西了, 删掉(失败也无所谓, 不影响使用)
            try { Directory.Delete(oldDir, recursive: true); } catch { }
        }
        catch { /* 迁移失败就继续用老目录里的东西也能跑(会重新存到新目录) */ }
    }

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
        GlassQuality = Math.Clamp(GlassQuality, 0, 100);
        return this;
    }

    public static AppSettings Load()
    {
        MigrateLegacyDataDir();

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
                    s.Save();                      // 写进 %LOCALAPPDATA%\TCPChat\
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

    /// <summary>附件/语音的本地缓存目录。</summary>
    public static string CacheDir
    {
        get
        {
            var d = Path.Combine(DataDir, "cache");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>程序数据目录(settings.json / crash.log 都在这儿, 即 %LOCALAPPDATA%\TCPChat)。</summary>
    public static string DataDir
    {
        get
        {
            MigrateLegacyDataDir();
            Directory.CreateDirectory(Dir);
            return Dir;
        }
    }
}
