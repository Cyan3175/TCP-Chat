using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using TCPChat10.Models;

namespace TCPChat10.Services;

/// <summary>
/// 聊天核心: 把 WebDAV 目录当作共享消息板。
///   发送 = PUT 一个 msg_*.json, 接收 = 定时 PROPFIND 目录并拉取新文件。
/// 一文件一消息, 因此不存在并发写冲突(WebDAV 没有原子追加)。
/// 设置里填了加密密码时, 正文与引用用 AES-256-GCM 加密后再写进 enc 字段,
/// 收发双方密码不一致则解不开, 界面显示"无法解密"。
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
    private MessageCipher _cipher;
    private int _undecryptable;

    // 11.4: 本地消息缓存(启动先显示, 不用每次重载整份目录); 对方撤回的消息也会从缓存里摘掉
    private MessageCache? _cache;
    private MessageCache Cache => _cache ??= MessageCache.Load(_settings.ChatFolder);

    /// <summary>新消息到达(在后台线程触发, UI 需自行切回主线程)。</summary>
    public event Action<ChatMessage>? MessageAdded;
    /// <summary>消息被删除。</summary>
    public event Action<string>? MessageRemoved;
    /// <summary>状态提示文本(上传进度等)。</summary>
    public event Action<string>? StatusChanged;
    /// <summary>每成功同步完一轮就触发一次(不管有没有新消息), 界面上"最近同步"用它。</summary>
    public event Action<DateTimeOffset>? Synced;
    /// <summary>同步失败等错误。</summary>
    public event Action<string>? ErrorOccurred;
    /// <summary>诊断信息(仅在自测模式下被订阅)。</summary>
    public event Action<string>? Diag;

    public string ChatFolder => _settings.ChatFolder;
    public string Nickname { get; set; }
    /// <summary>同步周期(秒)。范围跟设置对话框一致(1~120), 否则底栏显示的周期和实际不一致。</summary>
    private int _sendIndex;

    /// <summary>底栏显示用: 当前用于发送的是第几个密码(1 基, 0 = 未启用)。</summary>
    public int SendPasswordNumber => _cipher.Enabled ? _sendIndex + 1 : 0;

    /// <summary>参与解密的密码个数。</summary>
    public int PasswordCount => _cipher.PasswordCount;

    public int PollSeconds => Math.Clamp(_settings.PollSeconds, 1, 120);
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>端到端加密是否启用(设置里填了加密密码)。</summary>
    public bool EncryptionEnabled => _cipher.Enabled;

    /// <summary>本次运行收到过多少条解不开的密文。</summary>
    public int UndecryptableCount => Volatile.Read(ref _undecryptable);

    /// <summary>最近一次成功同步的时间(本地时区); 还没同步过就是 null。</summary>
    public DateTimeOffset? LastSyncTime { get; private set; }

    public ChatService(AppSettings settings)
    {
        _settings = settings;
        Nickname = settings.Nickname;
        _dav = new WebDavClient(settings.ServerUrl);
        _cipher = new MessageCipher(settings.DecryptCandidates, settings.SendPasswordIndex, CryptoSaltSeed);
        _sendIndex = settings.CryptoPasswords.IndexOf(settings.SendPassword);
    }

    public WebDavClient Dav => _dav;

    /// <summary>
    /// 密钥派生用的盐种子。只取聊天目录: 收发双方在同一目录里读写, 因此盐一致;
    /// 换个目录即使是同一个密码也解不开。
    /// </summary>
    private string CryptoSaltSeed => _settings.ChatFolder.Trim('/');

    /// <summary>
    /// 改加密密码后重建密钥, 并清空"已读"记录让下一轮把服务器上的消息重新拉一遍。
    /// (密码变了以后, 之前解不开的消息要重新尝试解密)
    /// </summary>
    /// <summary>密码列表 / 默认发送密码变了: 重建密钥, 并让下一轮把所有消息重试一遍。</summary>
    public void ApplyCryptoPasswords(IReadOnlyList<string> passwords, int sendIndex)
    {
        _cipher = new MessageCipher(passwords, sendIndex, CryptoSaltSeed);
        _sendIndex = sendIndex;
        _seen.Clear();
        _deleted.Clear();
        Interlocked.Exchange(ref _undecryptable, 0);

        // 11.4: 有本地缓存的话, 直接用新密码把缓存重新解一遍(界面此时已被清空),
        // 不然要等下一轮把服务器上的消息重新下载一遍才看得到 —— 缓存里存的就是那份密文, 解出来一样。
        EmitCachedMessages();
    }

    /// <summary>
    /// 初始化: 测连通性并确认聊天目录可用。
    /// 10.2 起不再自动新建目录 —— 消息文件直接放进设置里那个目录, 服务器上不会被程序多建出文件夹。
    /// </summary>
    public async Task<(bool ok, string message)> InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 目录不存在时不同服务器给的状态码不一样(404/409/500 都有), 统一按"目录不可用"处理,
            // 再用根目录探一次区分"目录没建"和"服务器连不上"
            bool folderOk;
            try { folderOk = (await _dav.PropFindAsync(_settings.ChatFolder, 0, ct)).Count > 0; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { folderOk = false; }

            if (!folderOk)
            {
                bool serverOk;
                try { serverOk = (await _dav.PropFindAsync("", 0, ct)).Count > 0; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { serverOk = false; }

                Diag?.Invoke("聊天目录不可用: " + _settings.ChatFolder);
                return (false, serverOk
                    ? "聊天目录不存在：" + _settings.ChatFolder
                    : "无法访问服务器(请检查网络与服务器地址)");
            }

            Diag?.Invoke($"连接就绪, 耗时 {sw.ElapsedMilliseconds} ms" +
                         (_cipher.Enabled ? " (端到端加密已启用)" : " (未加密)"));
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

    /// <summary>发送一条文本消息。启用加密时服务器上只有密文, 本机仍保留明文用于回显。</summary>
    public async Task<ChatMessage?> SendTextAsync(string text, string? quote = null, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        return await SendAsync(text, quote, null, ct);
    }

    /// <summary>
    /// 上传一个本地文件并发一条带附件的消息(图片/文件/语音都走这里)。
    /// kind = 0 时按扩展名猜; durationMs 只有语音用得上。
    /// </summary>
    public async Task<ChatMessage?> SendFileAsync(string localPath, int kind = 0, int durationMs = 0, CancellationToken ct = default)
    {
        if (!File.Exists(localPath)) return null;
        var info = new FileInfo(localPath);
        var ext = info.Extension;
        var safeName = SanitizeFileName(info.Name);
        var remoteName = "att_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_" +
                         Guid.NewGuid().ToString("N")[..6] + "_" + safeName;
        var remotePath = WebDavClient.Combine(_settings.ChatFolder, remoteName);

        StatusChanged?.Invoke("正在上传 " + safeName + " (" + Human(info.Length) + ") …");
        try
        {
            var bytes = await File.ReadAllBytesAsync(localPath, ct);
            if (_cipher.Enabled)
            {
                // 设了加密密码: 附件内容也加密上传(文件名作为附加认证数据)
                var enc = _cipher.EncryptBytes(remoteName, bytes);
                if (enc == null) { ErrorOccurred?.Invoke("附件加密失败"); return null; }
                bytes = enc;
            }

            if (!await _dav.PutAsync(remotePath, bytes, GuessContentType(ext), ct))
            {
                ErrorOccurred?.Invoke("附件上传失败 (" + _dav.LastPutStatus + "): " + safeName);
                return null;
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke("附件上传失败: " + ex.Message);
            return null;
        }

        var attach = new Attachment
        {
            Name = info.Name,
            Path = remotePath,
            Size = info.Length,
            Kind = kind > 0 ? kind : GuessKind(ext),
            DurationMs = Math.Max(0, durationMs),
        };
        Diag?.Invoke($"附件已上传: {remoteName} ({Human(info.Length)}, kind={attach.Kind})");
        StatusChanged?.Invoke("已上传 " + safeName);

        if (attach.Kind == 5) return await SendAsync("", null, attach, ct);
        return await SendAsync("", null, attach, ct);
    }

    /// <summary>把附件下载到本地缓存; 解不开(密码不一致)返回 null。</summary>
    public async Task<string?> DownloadAttachmentAsync(ChatMessage msg, CancellationToken ct = default)
    {
        if (msg.Attach == null) return null;
        var remoteFileName = Path.GetFileName(msg.Attach.Path);
        var localPath = Path.Combine(AppSettings.CacheDir, remoteFileName);
        if (File.Exists(localPath) && new FileInfo(localPath).Length == msg.Attach.Size) return localPath;

        try
        {
            var bytes = await _dav.GetBytesAsync(msg.Attach.Path, ct);
            if (bytes == null) return null;

            if (_cipher.Enabled)
            {
                var plain = _cipher.TryDecryptBytes(remoteFileName, bytes);
                if (plain == null)
                {
                    Interlocked.Increment(ref _undecryptable);
                    ErrorOccurred?.Invoke("附件解密失败(密码与发送方不一致): " + msg.Attach.Name);
                    return null;
                }
                bytes = plain;
            }

            await File.WriteAllBytesAsync(localPath, bytes, ct);
            return localPath;
        }
        catch { return null; }
    }

    // ---------- 统一发送 ----------

    private async Task<ChatMessage?> SendAsync(string text, string? quote, Attachment? attach, CancellationToken ct)
    {
        var cleanQuote = string.IsNullOrWhiteSpace(quote) ? null : quote;
        var msg = new ChatMessage
        {
            Id = NewId(),
            From = Nickname,
            Time = DateTimeOffset.UtcNow,
            Text = text,
            Quote = cleanQuote,
            Attach = attach,
            Pending = true,
        };

        var name = "msg_" + msg.Id + ".json";
        msg.RemoteName = name;
        _seen[name] = 1;                       // 已经知道它, 轮询时不再重复拉取

        try
        {
            ChatMessage wire;
            if (_cipher.Enabled)
            {
                // 上行的 JSON 里不含明文: 正文/引用/附件信息打成一段 JSON 再整体加密
                msg.Enc = _cipher.EncryptPayload(name, Nickname, text, cleanQuote, attach);
                msg.Version = 2;
                wire = new ChatMessage
                {
                    Version = 2, Id = msg.Id, From = msg.From, Time = msg.Time, Enc = msg.Enc,
                };
            }
            else
            {
                wire = new ChatMessage
                {
                    Version = 1, Id = msg.Id, From = msg.From, Time = msg.Time,
                    Text = text, Quote = cleanQuote, Attach = attach,
                };
            }

            var json = JsonSerializer.Serialize(wire, JsonOpts);
            var ok = await PutWithRetryAsync(WebDavClient.Combine(_settings.ChatFolder, name), json, ct);
            if (ok)
            {
                msg.Pending = false;
                msg.Status = null;
                Cache.Put(name, json);            // 自己发的也进缓存, 下次启动不用再拉
                Cache.Save();
            }
            else { msg.Status = "发送失败 (" + _dav.LastPutStatus + ")"; }
        }
        catch (Exception ex)
        {
            msg.Status = "发送失败: " + ex.Message;
        }
        return msg;
    }

    /// <summary>
    /// 上传一条消息。学校的 WebDAV 偶发抽风(5xx / 请求被挂住), 实测一次回归里就撞到过,
    /// 所以失败自动重试两次, 仍失败才在气泡上标"发送失败"。
    /// </summary>
    private async Task<bool> PutWithRetryAsync(string path, string json, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (await _dav.PutTextAsync(path, json, ct)) return true;
                if (attempt >= 3) return false;
                Diag?.Invoke($"PUT 第 {attempt} 次失败 ({_dav.LastPutStatus}), 重试…");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (attempt >= 3) { Diag?.Invoke("PUT 连续失败: " + ex.Message); return false; }
                Diag?.Invoke($"PUT 第 {attempt} 次异常 ({ex.Message}), 重试…");
            }
            await Task.Delay(500 * attempt, ct);
        }
    }

    /// <summary>删除自己发出的消息。</summary>
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
            Cache.Remove(msg.RemoteName);         // 自己撤回的也从缓存里去掉
            Cache.Save();
            MessageRemoved?.Invoke(msg.RemoteName);
            return true;
        }
        catch { return false; }
    }

    // ---------- 轮询 ----------

    public void Start()
    {
        if (IsRunning) return;
        EmitCachedMessages();          // 11.4: 先把本地缓存显示出来, 不等这一轮同步
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 把本地缓存里的消息放出来(按文件名 = 时间顺序)。
    /// 缓存里存的是服务器上那份 JSON, 所以这里仍旧要解密; 窗口外的老消息不显示。
    /// </summary>
    public void EmitCachedMessages()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _settings.HistoryDays));
            int emitted = 0;
            foreach (var name in Cache.Names())
            {
                var time = MessageCache.TimeOf(name);
                if (time != null && time < cutoff) continue;

                var json = Cache.Get(name);
                if (string.IsNullOrEmpty(json)) continue;

                try
                {
                    var msg = JsonSerializer.Deserialize<ChatMessage>(json, JsonOpts);
                    if (msg == null) continue;
                    if (!msg.IsEncrypted && msg.Attach == null &&
                        string.IsNullOrWhiteSpace(msg.Text) && string.IsNullOrWhiteSpace(msg.Quote)) continue;

                    if (msg.IsEncrypted) Decrypt(msg, name);
                    msg.RemoteName = name;
                    msg.IsSelf = !string.IsNullOrEmpty(Nickname) &&
                                 string.Equals(msg.From, Nickname, StringComparison.Ordinal);
                    _seen[name] = 1;                      // 缓存里有的不再去服务器拉一遍
                    MessageAdded?.Invoke(msg);
                    emitted++;
                }
                catch { /* 单条坏了跳过 */ }
            }
            if (emitted > 0) Diag?.Invoke("本地缓存: 直接显示 " + emitted + " 条");
        }
        catch (Exception ex) { Diag?.Invoke("读本地缓存失败: " + ex.Message); }
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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 周期从"这一轮开始"算起, 而不是"这一轮结束后再等 N 秒":
                // 一轮同步本身要花几百毫秒, 用 PeriodicTimer/Task.Delay(N) 的话实际间隔会变成
                // N + 一轮耗时(看起来就像"周期多加了 1 秒")。这里把耗时扣掉。
                // 另外每轮都重新读设置 —— 在设置里改完同步周期立刻生效, 不用重启(10.7 修)。
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await SyncOnceAsync(ct);
                var wait = TimeSpan.FromSeconds(PollSeconds) - sw.Elapsed;
                if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;   // 这一轮超时了就直接接着下一轮
                await Task.Delay(wait, ct);
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

        LastSyncTime = DateTimeOffset.Now;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _settings.HistoryDays));

        // 只处理消息文件, 按名字排序保证时间顺序
        var files = entries
            .Where(e => !e.IsCollection
                        && e.Name.StartsWith("msg_", StringComparison.OrdinalIgnoreCase)
                        && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.LastModified == null || e.LastModified >= cutoff)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        // 11.4: 对方撤回(服务器上文件没了) —— 本地缓存和界面都要跟着去掉, 不能"漏掉"
        var gone = Cache.PruneMissing(files.Select(e => e.Name), cutoff);
        foreach (var name in gone)
        {
            _seen.TryRemove(name, out _);
            MessageRemoved?.Invoke(name);
            Diag?.Invoke("对方撤回, 本地移除 " + name);
        }

        var fresh = files.Where(e => !_seen.ContainsKey(e.Name) && !_deleted.Contains(e.Name)).ToList();
        Diag?.Invoke($"同步: 目录 {entries.Count} 项 / 消息 {files.Count} 个 / 待取 {fresh.Count} 个" +
                     (gone.Count > 0 ? $" / 撤回 {gone.Count} 个" : "") + $" / {Cache.Describe()}");
        if (fresh.Count == 0)
        {
            Cache.Save();
            Synced?.Invoke(LastSyncTime ?? DateTimeOffset.Now);
            return;
        }

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

                    // 既没有正文/引用、又没有附件的消息才丢掉(免得列表里出现空气泡);
                    // 带附件的消息(10.0 的老附件消息也算)要留下来
                    if (!msg.IsEncrypted && msg.Attach == null &&
                        string.IsNullOrWhiteSpace(msg.Text) && string.IsNullOrWhiteSpace(msg.Quote))
                    {
                        Diag?.Invoke("跳过没有正文的消息(10.0 的附件消息?): " + entry.Name);
                        _seen[entry.Name] = 1;
                        return;
                    }

                    if (msg.IsEncrypted) Decrypt(msg, entry.Name);

                    _seen[entry.Name] = 1;              // 只有解析成功才算已读
                    Cache.Put(entry.Name, text);        // 11.4: 顺手存进本地缓存, 下次启动直接显示
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
        Cache.Save();
        Diag?.Invoke($"同步完成: {fresh.Count} 个文件, 耗时 {sw.ElapsedMilliseconds} ms");
        Synced?.Invoke(LastSyncTime ?? DateTimeOffset.Now);
    }

    /// <summary>
    /// 解出正文。文件名作为附加认证数据参与校验: 密码不一致(或密文/文件名被改过)时
    /// 解不开, 消息标记为 DecryptFailed, 界面显示占位提示而不是乱码。
    /// </summary>
    private void Decrypt(ChatMessage msg, string remoteName)
    {
        var payload = _cipher.TryDecryptPayload(remoteName, msg.Enc);
        if (payload == null)
        {
            Interlocked.Increment(ref _undecryptable);
            msg.DecryptFailed = true;
            msg.Text = "";
            msg.Quote = null;
            Diag?.Invoke("解密失败(密码不一致?): " + remoteName);
            return;
        }

        msg.Text = payload.Text;
        msg.Quote = payload.Quote;
        msg.Attach = payload.Attach;
        if (!string.IsNullOrEmpty(payload.From)) msg.From = payload.From;
    }

    // ---------- 工具 ----------

    /// <summary>把危险字符换掉, 并限制长度(附件文件名要放进 URL)。</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars);
        if (s.Length > 80) s = s[^80..];
        return string.IsNullOrWhiteSpace(s) ? "file" : s;
    }

    /// <summary>按扩展名猜类型: 1=文件 2=图片 3=视频 4=音频。</summary>
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
