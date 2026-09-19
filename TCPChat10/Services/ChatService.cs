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

    /// <summary>新消息到达(在后台线程触发, UI 需自行切回主线程)。</summary>
    public event Action<ChatMessage>? MessageAdded;
    /// <summary>消息被删除。</summary>
    public event Action<string>? MessageRemoved;
    /// <summary>同步失败等错误。</summary>
    public event Action<string>? ErrorOccurred;
    /// <summary>诊断信息(仅在自测模式下被订阅)。</summary>
    public event Action<string>? Diag;

    public string ChatFolder => _settings.ChatFolder;
    public string Nickname { get; set; }
    public int PollSeconds => Math.Clamp(_settings.PollSeconds, 1, 60);
    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>端到端加密是否启用(设置里填了加密密码)。</summary>
    public bool EncryptionEnabled => _cipher.Enabled;

    /// <summary>本次运行收到过多少条解不开的密文。</summary>
    public int UndecryptableCount => Volatile.Read(ref _undecryptable);

    public ChatService(AppSettings settings)
    {
        _settings = settings;
        Nickname = settings.Nickname;
        _dav = new WebDavClient(settings.ServerUrl);
        _cipher = new MessageCipher(settings.CryptoPassword, CryptoSaltSeed);
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
    public void ApplyCryptoPassword(string? password)
    {
        _cipher = new MessageCipher(password, CryptoSaltSeed);
        _seen.Clear();
        _deleted.Clear();
        Interlocked.Exchange(ref _undecryptable, 0);
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

        var cleanQuote = string.IsNullOrWhiteSpace(quote) ? null : quote;
        var msg = new ChatMessage
        {
            Id = NewId(),
            From = Nickname,
            Time = DateTimeOffset.UtcNow,
            Text = text,
            Quote = cleanQuote,
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
                // 上行的 JSON 里不含明文: 正文+引用打成一段 JSON 再整体加密
                msg.Enc = _cipher.EncryptPayload(name, Nickname, text, cleanQuote);
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
                    Text = text, Quote = cleanQuote,
                };
            }

            var json = JsonSerializer.Serialize(wire, JsonOpts);
            var ok = await PutWithRetryAsync(WebDavClient.Combine(_settings.ChatFolder, name), json, ct);
            if (ok) { msg.Pending = false; msg.Status = null; }
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
            _deleted.Add(msg.RemoteName);
            MessageRemoved?.Invoke(msg.RemoteName);
            return true;
        }
        catch { return false; }
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

                    // 10.0 的纯附件消息: 新版已经没有附件了, 直接忽略, 免得列表里出现空气泡
                    if (!msg.IsEncrypted && string.IsNullOrWhiteSpace(msg.Text) && string.IsNullOrWhiteSpace(msg.Quote))
                    {
                        Diag?.Invoke("跳过没有正文的消息(10.0 的附件消息?): " + entry.Name);
                        _seen[entry.Name] = 1;
                        return;
                    }

                    if (msg.IsEncrypted) Decrypt(msg, entry.Name);

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
        if (!string.IsNullOrEmpty(payload.From)) msg.From = payload.From;
    }

    public void Dispose()
    {
        Stop();
        _dav.Dispose();
    }
}
