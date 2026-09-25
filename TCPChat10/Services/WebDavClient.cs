using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace TCPChat10.Services;

/// <summary>WebDAV 上的一个条目。</summary>
public sealed class WebDavEntry
{
    public string Href { get; init; } = "";
    /// <summary>相对根路径的路径, 已 URL 解码, 形如 "nw集训/学生资料临存"。</summary>
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsCollection { get; init; }
    public long Length { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string ETag { get; init; } = "";
}

/// <summary>
/// 极简 WebDAV 客户端。只依赖 HttpClient, 使用 PROPFIND / GET / PUT / DELETE / MKCOL。
/// 路径中的中文按 UTF-8 百分号编码, 斜杠保留。
/// </summary>
public sealed class WebDavClient : IDisposable
{
    private static readonly XNamespace DAV = "DAV:";
    private readonly HttpClient _http;

    public string BaseUrl { get; }

    /// <summary>最近一次 PUT 的结果("200 OK" / "403 Forbidden" ...), 用于把失败原因显示给用户。</summary>
    public string LastPutStatus { get; private set; } = "";

    /// <summary>匿名访问(10.2 起不再需要 WebDAV 账号)。</summary>
    public WebDavClient(string baseUrl)
    {
        // 规范化: 末尾必须有一个斜杠
        BaseUrl = baseUrl.TrimEnd('/') + "/";

        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TCPChat10/10.7");
    }

    /// <summary>
    /// 给单次请求加超时。服务器偶尔会把某个请求挂住几十秒, 不能让它拖死整个轮询循环。
    /// </summary>
    private static CancellationTokenSource WithTimeout(CancellationToken ct, int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    // ---------- URL 处理 ----------

    /// <summary>把 "nw集训/学生资料临存/聊天" 这样的路径转成完整 URL(逐段百分号编码)。</summary>
    public string BuildUrl(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(BaseUrl);
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0) sb.Append('/');
            sb.Append(Uri.EscapeDataString(segments[i]));
        }
        if (segments.Length > 0) sb.Append('/');   // 目录一律带尾斜杠
        return sb.ToString();
    }

    /// <summary>相对路径拼接(自动补斜杠)。</summary>
    public static string Combine(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b.Trim('/');
        if (string.IsNullOrEmpty(b)) return a.Trim('/');
        return a.Trim('/') + "/" + b.Trim('/');
    }

    // ---------- 基本操作 ----------

    /// <summary>PROPFIND。depth = 0 只查自己, 1 查直接子项。</summary>
    public async Task<List<WebDavEntry>> PropFindAsync(string path, int depth, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), BuildUrl(path));
        req.Headers.Add("Depth", depth.ToString());
        req.Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<D:propfind xmlns:D=\"DAV:\"><D:prop>" +
            "<D:displayname/><D:resourcetype/><D:getcontentlength/><D:getlastmodified/><D:getetag/>" +
            "</D:prop></D:propfind>",
            Encoding.UTF8, "application/xml");

        using var cts = WithTimeout(ct, 25);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new List<WebDavEntry>();
        if ((int)resp.StatusCode == 207)
        {
            var xml = await resp.Content.ReadAsStringAsync(cts.Token);
            return ParseMultiStatus(xml);
        }
        resp.EnsureSuccessStatusCode();
        return new List<WebDavEntry>();
    }

    /// <summary>解析 207 Multi-Status 响应(需要实例, 因为要把 href 前缀按 BaseUrl 归一化)。</summary>
    public List<WebDavEntry> ParseMultiStatus(string xml)
    {
        var list = new List<WebDavEntry>();
        if (string.IsNullOrWhiteSpace(xml)) return list;
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return list; }

        foreach (var resp in doc.Descendants(DAV + "response"))
        {
            var hrefEl = resp.Element(DAV + "href");
            if (hrefEl == null) continue;
            var href = hrefEl.Value;

            var prop = resp.Descendants(DAV + "prop").FirstOrDefault();
            bool isCollection = prop?.Element(DAV + "resourcetype")?.Element(DAV + "collection") != null;
            long len = 0;
            long.TryParse(prop?.Element(DAV + "getcontentlength")?.Value, out len);
            DateTimeOffset? lm = null;
            var lmText = prop?.Element(DAV + "getlastmodified")?.Value;
            if (!string.IsNullOrEmpty(lmText) && DateTimeOffset.TryParse(lmText, out var parsed)) lm = parsed;

            var path = HrefToPath(href);
            var name = path.Length == 0 ? "" : path[(path.LastIndexOf('/') + 1)..];
            if (name.Length == 0 && path.Length > 0)
            {
                var trimmed = path.TrimEnd('/');
                name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
            }

            list.Add(new WebDavEntry
            {
                Href = href,
                Path = path,
                Name = name,
                IsCollection = isCollection,
                Length = len,
                LastModified = lm,
                ETag = prop?.Element(DAV + "getetag")?.Value ?? "",
            });
        }
        return list;
    }

    /// <summary>把 PROPFIND 返回的 href 转成相对 BaseUrl 的路径(已解码, 无首尾斜杠)。</summary>
    private string HrefToPath(string href)
    {
        var s = href;
        // href 可能是绝对 URL, 也可能是绝对路径
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(s, UriKind.Absolute, out var u)) s = u.AbsolutePath;
        }
        s = Uri.UnescapeDataString(s);
        s = s.Trim('/');

        // 去掉 BaseUrl 对应的路径前缀
        if (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri))
        {
            var prefix = Uri.UnescapeDataString(baseUri.AbsolutePath).Trim('/');
            if (prefix.Length > 0)
            {
                if (s.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return "";
                if (s.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) s = s[(prefix.Length + 1)..];
            }
        }
        return s;
    }

    public async Task<byte[]?> GetBytesAsync(string path, CancellationToken ct = default, int timeoutSeconds = 60)
    {
        using var cts = WithTimeout(ct, timeoutSeconds);   // 附件可能很大, 给足时间
        using var resp = await _http.GetAsync(BuildUrl(path), cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(cts.Token);
    }

    public async Task<string?> GetStringAsync(string path, CancellationToken ct = default)
    {
        // 消息 JSON 只有几百字节, 卡住就是异常, 不必等满 60 秒
        var bytes = await GetBytesAsync(path, ct, 25);
        if (bytes == null) return null;
        // 去掉可能的 UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task<bool> PutAsync(string path, byte[] data, string contentType = "application/octet-stream",
                                     CancellationToken ct = default)
    {
        var content = new ByteArrayContent(data);
        // 用 Parse 而不是构造函数: 后者不接受 "application/json; charset=utf-8" 这种带参数的写法
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var cts = WithTimeout(ct, 60);
        using var resp = await _http.PutAsync(BuildUrl(path), content, cts.Token);
        LastPutStatus = (int)resp.StatusCode + " " + resp.ReasonPhrase;
        return resp.IsSuccessStatusCode;
    }

    public Task<bool> PutTextAsync(string path, string text, CancellationToken ct = default)
        => PutAsync(path, new UTF8Encoding(false).GetBytes(text), "application/json; charset=utf-8", ct);

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        using var cts = WithTimeout(ct, 25);
        using var resp = await _http.DeleteAsync(BuildUrl(path), cts.Token);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>创建目录。已存在时返回 false(不视为错误)。</summary>
    public async Task<bool> MkColAsync(string path, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(new HttpMethod("MKCOL"), BuildUrl(path));
        using var cts = WithTimeout(ct, 25);
        using var resp = await _http.SendAsync(req, cts.Token);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>确保目录存在(逐级创建)。</summary>
    public async Task EnsureCollectionAsync(string path, CancellationToken ct = default)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = "";
        foreach (var p in parts)
        {
            cur = Combine(cur, p);
            await MkColAsync(cur, ct);
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var entries = await PropFindAsync(path, 0, ct);
            return entries.Count > 0;
        }
        catch { return false; }
    }

    /// <summary>简单连通性测试。</summary>
    public async Task<bool> TestAsync(CancellationToken ct = default)
    {
        try
        {
            await PropFindAsync("", 0, ct);
            return true;
        }
        catch { return false; }
    }

    public void Dispose() => _http.Dispose();
}
