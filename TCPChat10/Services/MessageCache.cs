using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TCPChat10.Models;

namespace TCPChat10.Services;

/// <summary>
/// 11.4: 本地消息缓存 —— 启动时先把上次同步到的消息直接显示出来, 不用每次重载整个目录。
///
/// 存的是**服务器上那份原始 JSON**(加密时就是密文), 所以本地缓存不会比服务器多泄露任何明文;
/// 密码换了只是换个姿势解密同一份密文, 缓存不用作废。
///
/// 一并发愁"对方撤回": 服务器上文件没了 -> PruneMissing 把它从缓存里摘掉, 界面随之移除。
/// 缓存按聊天目录分开存(换目录不会串消息), 文件名里的时间戳用来判断是否还在"历史天数"窗口内。
/// </summary>
public sealed class MessageCache
{
    private sealed class Entry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("json")] public string Json { get; set; } = "";
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("folder")] public string Folder { get; set; } = "";
        [JsonPropertyName("savedAt")] public string SavedAt { get; set; } = "";
        [JsonPropertyName("items")] public List<Entry> Items { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    private readonly string _folder;
    private readonly string _path;
    private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private MessageCache(string folder, string path)
    {
        _folder = folder;
        _path = path;
    }

    /// <summary>这条目录的缓存文件(目录名哈希一下, 免得路径里有斜杠/中文)。</summary>
    public static string PathFor(string chatFolder)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chatFolder ?? "")))[..16];
        return Path.Combine(AppSettings.CacheDir, "msgs_" + hash + ".json");
    }

    /// <summary>读缓存; 文件坏了/目录对不上都当空的处理, 不影响正常同步。</summary>
    public static MessageCache Load(string chatFolder)
    {
        var path = PathFor(chatFolder);
        var cache = new MessageCache(chatFolder ?? "", path);
        try
        {
            if (!File.Exists(path)) return cache;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), Opts);
            if (file == null) return cache;
            if (!string.Equals(file.Folder, chatFolder ?? "", StringComparison.Ordinal))
            {
                cache._dirty = true;                 // 目录变了: 这份缓存作废, 下次同步重写
                return cache;
            }
            foreach (var e in file.Items)
                if (!string.IsNullOrEmpty(e.Name) && !string.IsNullOrEmpty(e.Json))
                    cache._items[e.Name] = e.Json;
        }
        catch { /* 读不出来就当没有缓存 */ }
        return cache;
    }

    private bool _dirty;
    public bool Dirty => _dirty;
    public int Count { get { lock (_lock) return _items.Count; } }

    /// <summary>缓存里的消息文件名(按文件名排序 = 按时间排序)。</summary>
    public List<string> Names()
    {
        lock (_lock) return _items.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public string? Get(string name)
    {
        lock (_lock) return _items.TryGetValue(name, out var json) ? json : null;
    }

    public void Put(string name, string json)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(json)) return;
        lock (_lock)
        {
            _items[name] = json;
            _dirty = true;
        }
    }

    public void Remove(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_lock)
        {
            if (_items.Remove(name)) _dirty = true;
        }
    }

    /// <summary>清空(密码/目录变了以后要重新拉时用)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_items.Count == 0) return;
            _items.Clear();
            _dirty = true;
        }
    }

    /// <summary>
    /// 对方撤回: 服务器上已经没有的文件, 从缓存里摘掉并返回名字。
    /// - remote 为空**不动手** —— 服务器偶发抽风返回空列表时不能把本地全清了
    /// - 只有窗口内(文件名时间戳 >= cutoff)的才算撤回, 老消息本来就不在同步范围里
    /// </summary>
    public List<string> PruneMissing(IEnumerable<string> remoteNames, DateTimeOffset cutoff)
    {
        var remote = new HashSet<string>(remoteNames, StringComparer.OrdinalIgnoreCase);
        if (remote.Count == 0) return new List<string>();

        var gone = new List<string>();
        lock (_lock)
        {
            foreach (var name in _items.Keys.ToList())
            {
                if (remote.Contains(name)) continue;
                var time = TimeOf(name);
                if (time == null || time < cutoff) continue;      // 窗口外的不管
                _items.Remove(name);
                gone.Add(name);
            }
            if (gone.Count > 0) _dirty = true;
        }
        gone.Sort(StringComparer.Ordinal);
        return gone;
    }

    /// <summary>从 msg_&lt;unix毫秒&gt;_xxxx.json 里取出时间。</summary>
    public static DateTimeOffset? TimeOf(string name)
    {
        try
        {
            var s = name ?? "";
            if (!s.StartsWith("msg_", StringComparison.OrdinalIgnoreCase)) return null;
            s = s[4..];
            int us = s.IndexOf('_');
            if (us > 0) s = s[..us];
            return long.TryParse(s, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
        catch { return null; }
    }

    /// <summary>写盘(先写临时文件再替换, 断电/崩溃不会留下半个坏文件)。</summary>
    public void Save()
    {
        try
        {
            CacheFile file;
            lock (_lock)
            {
                if (!_dirty) return;
                file = new CacheFile
                {
                    Folder = _folder,
                    SavedAt = DateTimeOffset.Now.ToString("o"),
                    Items = _items.Select(kv => new Entry { Name = kv.Key, Json = kv.Value }).ToList(),
                };
                _dirty = false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Opts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* 写不进去不影响使用, 下次同步再试 */ }
    }

    /// <summary>给底栏/自检看的一句话。</summary>
    public string Describe() => $"本地缓存 {Count} 条";
}
