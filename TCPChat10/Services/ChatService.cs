using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TCPChat10.Models;

namespace TCPChat10.Services;

/// <summary>
/// 聊天核心: 把 WebDAV 目录当作共享消息板。
///   发送 = PUT 一个 msg_*.json, 接收 = 定时 PROPFIND 目录并拉取新文件。
/// 一文件一消息, 因此不存在并发写冲突(WebDAV 没有原子追加)。
/// </summary>
public sealed class ChatService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebDavClient _dav;
    private readonly AppSettings _settings;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>新消息到达(在后台线程触发, UI 需自行切回主线程)。</summary>
    public event Action<ChatMessage>? MessageAdded;
    /// <summary>消息被删除。</summary>
    public event Action<string>? MessageRemoved;
    /// <summary>状态提示文本。</summary>
    public event Action<string>? StatusChanged;
    /// <summary>同步失败等错误。</summary>
    public event Action<string>? ErrorOccurred;
    /// <summary>诊断信息(仅在自测模式下被订阅)。</summary>
    public event Action<string>? Diag;

    public string ChatFolder => _settings.ChatFolder;
    public string Nickname { get; set; }
    public int PollSeconds => Math.Clamp(_settings.PollSeconds, 1, 60);
    public bool IsRunning => _loop is { IsCompleted: false };

    public ChatService(AppSettings settings)
    {
        _settings = settings;
        Nickname = settings.Nickname;
        _dav = new WebDavClient(settings.ServerUrl, settings.UserName, settings.Password);
    }

    public WebDavClient Dav => _dav;

    /// <summary>初始化: 测连通性并确保聊天目录存在。</summary>
    public async Task<(bool ok, string message)> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 常见情况: 目录已存在 -> 一次 PROPFIND 就够。
            // (旧实现每次启动都逐级 MKCOL, 服务器延迟高时要白等十几秒)
            var probe = await _dav.PropFindAsync(_settings.ChatFolder, 0, ct);
            if (probe.Count == 0)
            {
                Diag?.Invoke($"目录不存在或不可达, 尝试创建 {_settings.ChatFolder}");
                await _dav.EnsureCollectionAsync(_settings.ChatFolder, ct);
                probe = await _dav.PropFindAsync(_settings.ChatFolder, 0, ct);
                if (probe.Count == 0)
                {
                    var root = await _dav.PropFindAsync("", 0, ct);
                    return (false, root.Count == 0
                        ? "无法访问服务器(请检查网络与服务器地址)"
                        : "聊天目录不可用: " + _settings.ChatFolder);
                }
            }

            Diag?.Invoke($"连接就绪, 耗时 {sw.ElapsedMilliseconds} ms");
            return (true, "已连接");
        }
        catch (Exception ex)
        {
            return (false, "连接失败: " + ex.Message);
        }
    }

    // ---------- 发送 ----------

    private string NewId() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() + "_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>发送一条文本消息。</summary>
    public async Task<ChatMessage?> SendTextAsync(string text, string? quote = null, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        var msg = new ChatMessage
        {
            Id = NewId(),
            From = Nickname,
            Time = DateTimeOffset.UtcNow,
            Text = text,
            Quote = string.IsNullOrWhiteSpace(quote) ? null : quote,
            Pending = true,
        };

        var name = "msg_" + msg.Id + ".json";
        msg.RemoteName = name;
        _seen[name] = 1;                       // 已经知道它, 轮询时不再重复拉取

        try
        {
            var json = JsonSerializer.Serialize(msg, JsonOpts);
            var ok = await _dav.PutTextAsync(WebDavClient.Combine(_settings.ChatFolder, name), json, ct);
            if (ok) { msg.Pending = false; msg.Status = null; }
            else { msg.Status = "发送失败"; }
        }
        catch (Exception ex)
        {
            msg.Status = "发送失败: " + ex.Message;
        }
        return msg;
    }

    /// <summary>上传附件并发送一条带附件的消息。</summary>
    public async Task<ChatMessage?> SendFileAsync(string localPath, CancellationToken ct = default)
    {
        if (!File.Exists(localPath)) return null;
        var info = new FileInfo(localPath);
        var ext = info.Extension;
        var safeName = SanitizeFileName(info.Name);
        var remoteName = "att_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" +
                         Guid.NewGuid().ToString("N")[..6] + "_" + safeName;
        var remotePath = WebDavClient.Combine(_settings.ChatFolder, remoteName);

        StatusChanged?.Invoke("正在上传 " + safeName + " (" + Human(info.Length) + ") ...");
        try
        {
            var bytes = await File.ReadAllBytesAsync(localPath, ct);
            var ok = await _dav.PutAsync(remotePath, bytes, GuessContentType(ext), ct);
            if (!ok) { ErrorOccurred?.Invoke("附件上传失败: " + safeName); return null; }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke("附件上传失败: " + ex.Message);
            return null;
        }
        StatusChanged?.Invoke("附件已上传");

        var msg = new ChatMessage
        {
            Id = NewId(),
            From = Nickname,
            Time = DateTimeOffset.UtcNow,
            Text = "",
            Attach = new Attachment
            {
                Name = info.Name,
                Path = remotePath,
                Size = info.Length,
                Kind = GuessKind(ext),
            },
            Pending = true,
        };
        var name = "msg_" + msg.Id + ".json";
        msg.RemoteName = name;
        _seen[name] = 1;
        try
        {
            var json = JsonSerializer.Serialize(msg, JsonOpts);
            if (await _dav.PutTextAsync(WebDavClient.Combine(_settings.ChatFolder, name), json, ct))
                msg.Pending = false;
            else msg.Status = "发送失败";
        }
        catch (Exception ex) { msg.Status = "发送失败: " + ex.Message; }
        return msg;
    }

    /// <summary>删除自己发出的消息(含附件)。</summary>
    public async Task<bool> DeleteMessageAsync(ChatMessage msg, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(msg.RemoteName)) return false;
        try
        {
            var ok = await _dav.DeleteAsync(WebDavClient.Combine(_settings.ChatFolder, msg.RemoteName), ct);
            if (!ok) return false;
            if (msg.Attach != null && !string.IsNullOrEmpty(msg.Attach.Path))
                await _dav.DeleteAsync(msg.Attach.Path, ct);
            _deleted.Add(msg.RemoteName);
            MessageRemoved?.Invoke(msg.RemoteName);
            return true;
        }
        catch { return false; }
    }

    /// <summary>下载附件到本地缓存, 返回本地路径。</summary>
    public async Task<string?> DownloadAttachmentAsync(ChatMessage msg, CancellationToken ct = default)
    {
        if (msg.Attach == null) return null;
        var localName = Path.GetFileName(msg.Attach.Path);
        var localPath = Path.Combine(AppSettings.CacheDir, localName);
        if (File.Exists(localPath) && new FileInfo(localPath).Length == msg.Attach.Size)
            return localPath;

        try
        {
            var bytes = await _dav.GetBytesAsync(msg.Attach.Path, ct);
            if (bytes == null) return null;
            await File.WriteAllBytesAsync(localPath, bytes, ct);
            return localPath;
        }
        catch { return null; }
    }

    // ---------- 轮询 ----------

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // 第一轮立即拉取
        await SyncOnceAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(PollSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await SyncOnceAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorOccurred?.Invoke("轮询异常: " + ex.Message); }
    }

    /// <summary>同步一轮: 列目录 -> 找出新文件 -> 下载解析 -> 触发事件。</summary>
    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        List<WebDavEntry> entries;
        try
        {
            entries = await _dav.PropFindAsync(_settings.ChatFolder, 1, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke("列目录失败: " + ex.Message);
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _settings.HistoryDays));

        // 只处理消息文件, 按名字排序保证时间顺序
        var files = entries
            .Where(e => !e.IsCollection
                        && e.Name.StartsWith("msg_", StringComparison.OrdinalIgnoreCase)
                        && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.LastModified == null || e.LastModified >= cutoff)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        var fresh = files.Where(e => !_seen.ContainsKey(e.Name) && !_deleted.Contains(e.Name)).ToList();
        Diag?.Invoke($"同步: 目录 {entries.Count} 项 / 消息 {files.Count} 个 / 待取 {fresh.Count} 个");
        if (fresh.Count == 0) return;

        // 串行拉取时几十条历史消息要十几秒才显示完, 这里并发几路; 单条失败不置 seen, 下轮自动重试
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Parallel.ForEachAsync(fresh,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (entry, token) =>
            {
                try
                {
                    var fullPath = WebDavClient.Combine(_settings.ChatFolder, entry.Name);
                    var text = await _dav.GetStringAsync(fullPath, token);
                    if (string.IsNullOrWhiteSpace(text)) { _seen[entry.Name] = 1; return; }
                    var msg = JsonSerializer.Deserialize<ChatMessage>(text);
                    if (msg == null) { _seen[entry.Name] = 1; return; }

                    _seen[entry.Name] = 1;              // 只有解析成功才算已读
                    msg.RemoteName = entry.Name;
                    msg.IsSelf = !string.IsNullOrEmpty(Nickname) &&
                                 string.Equals(msg.From, Nickname, StringComparison.Ordinal);
                    MessageAdded?.Invoke(msg);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // 单个文件坏了不影响整体, 也不会被标记成已读
                    ErrorOccurred?.Invoke("读取 " + entry.Name + " 失败: " + ex.Message);
                }
            });
        Diag?.Invoke($"同步完成: {fresh.Count} 个文件, 耗时 {sw.ElapsedMilliseconds} ms");
    }

    // ---------- 工具 ----------

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars);
        if (s.Length > 80) s = s[^80..];
        return string.IsNullOrWhiteSpace(s) ? "file" : s;
    }

    public static int GuessKind(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".ico" or ".tif" or ".tiff" => 2,
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".wmv" or ".flv" or ".m4v" => 3,
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" or ".aac" or ".wma" => 4,
        _ => 1,
    };

    public static string GuessContentType(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".txt" => "text/plain; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };

    public static string Human(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024 * 1024)) + " MB",
        _ => (bytes / (1024L * 1024 * 1024)) + " GB",
    };

    public void Dispose()
    {
        Stop();
        _dav.Dispose();
    }
}
