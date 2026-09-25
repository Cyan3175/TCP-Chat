using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCPChat10.Services;

/// <summary>
/// 消息正文的端到端加密(AES-256-GCM)。
///
/// 规则: 收发双方必须在设置里填写**完全相同**的加密密码, 否则收到的正文解不开,
/// 界面上显示为"无法解密"的占位提示。
///
/// 约定:
///   * 密钥 = PBKDF2-HMAC-SHA256(密码, 盐, 100000 次, 32 字节)
///     盐 = SHA256("TCPChat10/v2|crypto|" + 聊天目录) 的前 16 字节 —— 双方目录相同, 因此盐相同
///     (盐是固定的而不是每条消息随机, 这样一次会话只做一次 10 万次迭代的密钥派生;
///      目录不同的两份聊天记录即使密码相同也互不可解)
///   * 每条消息一个随机 96 位 nonce, 密文 = AES-GCM(密钥, nonce, UTF8(JSON{from,text,quote}), AAD = 消息文件名)
///     把文件名作为附加认证数据, 密文被改名/搬运到别的文件下会直接解密失败
///   * 信封字符串 = "AESGCM1:" + Base64(nonce(12) || 密文 || tag(16)), 存进消息 JSON 的 "enc" 字段
///   * 昵称与时间仍是明文(接收方需要用它显示是谁、什么时候发的), 正文与引用内容加密
/// </summary>
public static class MessageCrypto
{
    /// <summary>信封前缀。以后更换算法时靠它区分新旧格式。</summary>
    public const string Prefix = "AESGCM1:";

    public const int SaltLength = 16;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int KeyLength = 32;
    public const int Iterations = 100_000;

    /// <summary>由密码与盐派生 32 字节密钥(同一对参数在收发两端得到同一把钥匙)。</summary>
    public static byte[] DeriveKey(string password, string saltSeed)
    {
        var material = Encoding.UTF8.GetBytes("TCPChat10/v2|crypto|" + (saltSeed ?? ""));
        var salt = SHA256.HashData(material)[..SaltLength];
        return Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
    }

    /// <summary>加密一段文本, 返回可直接放进消息 JSON 的信封字符串。</summary>
    public static string Encrypt(byte[] key, string aad, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength))
        {
            aes.Encrypt(nonce, pt, ct, tag, Encoding.UTF8.GetBytes(aad));
        }

        var buf = new byte[NonceLength + ct.Length + TagLength];
        nonce.CopyTo(buf, 0);
        ct.CopyTo(buf, NonceLength);
        tag.CopyTo(buf, NonceLength + ct.Length);
        return Prefix + Convert.ToBase64String(buf);
    }

    /// <summary>加密一段字节(附件/语音用), 返回 nonce||密文||tag 的裸字节。</summary>
    public static byte[] EncryptBytes(byte[] key, string aad, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ct = new byte[plain.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength))
        {
            // 签名是 Encrypt(nonce, 明文, 密文, tag, aad) —— 顺序别写反
            aes.Encrypt(nonce, plain, ct, tag, Encoding.UTF8.GetBytes(aad));
        }

        var buf = new byte[NonceLength + ct.Length + TagLength];
        nonce.CopyTo(buf, 0);
        ct.CopyTo(buf, NonceLength);
        tag.CopyTo(buf, NonceLength + ct.Length);
        return buf;
    }

    /// <summary>解密附件字节; 密码不对/被改过返回 null。</summary>
    public static byte[]? TryDecryptBytes(byte[] key, string aad, byte[]? blob)
    {
        if (blob == null || blob.Length < NonceLength + TagLength) return null;
        try
        {
            var nonce = blob.AsSpan(0, NonceLength);
            var ct = blob.AsSpan(NonceLength, blob.Length - NonceLength - TagLength);
            var tag = blob.AsSpan(blob.Length - TagLength, TagLength);
            var pt = new byte[ct.Length];
            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(nonce, ct, tag, pt, Encoding.UTF8.GetBytes(aad));
            }
            return pt;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>解密信封; 密码不对 / 密文被改动 / 文件名被改过都返回 null。</summary>
    public static string? TryDecrypt(byte[] key, string aad, string? envelope)
    {
        if (string.IsNullOrEmpty(envelope)) return null;
        if (!envelope.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var buf = Convert.FromBase64String(envelope[Prefix.Length..]);
            if (buf.Length < NonceLength + TagLength) return null;

            var nonce = buf.AsSpan(0, NonceLength);
            var ct = buf.AsSpan(NonceLength, buf.Length - NonceLength - TagLength);
            var tag = buf.AsSpan(buf.Length - TagLength, TagLength);
            var pt = new byte[ct.Length];

            using (var aes = new AesGcm(key, TagLength))
            {
                aes.Decrypt(nonce, ct, tag, pt, Encoding.UTF8.GetBytes(aad));
            }
            return Encoding.UTF8.GetString(pt);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>加密后的正文载荷(序列化成 JSON 再加密)。</summary>
public sealed class CryptoPayload
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("quote")] public string? Quote { get; set; }
    /// <summary>附件信息也放进密文, 这样文件名/大小不会明文躺在服务器上。</summary>
    [JsonPropertyName("attach")] public TCPChat10.Models.Attachment? Attach { get; set; }
}

/// <summary>
/// 一把会话密钥的包装: ChatService 持有它, 发送时加密正文, 收到时尝试解密。
/// 密码为空 = 不加密(Enabled=false, 明文收发, 与旧版本互通)。
/// </summary>
public sealed class MessageCipher
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly byte[]? _key;                 // 发送用的钥匙
    private readonly List<byte[]> _tryKeys = new();  // 解密时依次尝试(发送那把排第一)

    public MessageCipher(string? password, string saltSeed)
        : this(string.IsNullOrEmpty(password) ? Array.Empty<string>() : new[] { password! }, 0, saltSeed) { }

    /// <summary>11.2: 密码列表 + 用第几把发送。解密会把整张列表依次试一遍。</summary>
    public MessageCipher(IReadOnlyList<string> passwords, int sendIndex, string saltSeed)
    {
        var list = (passwords ?? Array.Empty<string>())
            .Select(p => (p ?? "").Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0) return;

        sendIndex = Math.Clamp(sendIndex, 0, list.Count - 1);
        _key = MessageCrypto.DeriveKey(list[sendIndex], saltSeed);
        _tryKeys.Add(_key);
        for (int i = 0; i < list.Count; i++)
            if (i != sendIndex) _tryKeys.Add(MessageCrypto.DeriveKey(list[i], saltSeed));
    }

    public bool Enabled => _key != null;

    /// <summary>参与解密的密码个数(底栏显示用)。</summary>
    public int PasswordCount => _tryKeys.Count;

    /// <summary>把正文 / 引用 / 附件信息打包加密。未启用加密时返回 null。</summary>
    public string? EncryptPayload(string aad, string from, string text, string? quote,
                                 TCPChat10.Models.Attachment? attach = null)
    {
        if (_key == null) return null;
        var payload = new CryptoPayload { From = from, Text = text, Quote = quote, Attach = attach };
        return MessageCrypto.Encrypt(_key, aad, JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>加密附件字节(未启用加密返回 null)。</summary>
    public byte[]? EncryptBytes(string aad, byte[] plain) =>
        _key == null ? null : MessageCrypto.EncryptBytes(_key, aad, plain);

    /// <summary>解密附件字节: 挨个密码试。</summary>
    public byte[]? TryDecryptBytes(string aad, byte[] blob)
    {
        foreach (var key in _tryKeys)
        {
            var plain = MessageCrypto.TryDecryptBytes(key, aad, blob);
            if (plain != null) return plain;
        }
        return null;
    }

    /// <summary>解密正文载荷; 密码不一致返回 null。</summary>
    public CryptoPayload? TryDecryptPayload(string aad, string? envelope)
    {
        if (_key == null) return null;
        string? json = null;
        foreach (var key in _tryKeys)
        {
            json = MessageCrypto.TryDecrypt(key, aad, envelope);
            if (!string.IsNullOrEmpty(json)) break;
        }
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<CryptoPayload>(json); }
        catch { return null; }
    }
}
