using System.Text.Json.Serialization;

namespace TCPChat10.Models;

/// <summary>一条聊天消息(序列化后即为 WebDAV 上的一个 .json 文件)。</summary>
public sealed class ChatMessage
{
    /// <summary>协议版本: 1 = 明文, 2 = 正文加密(看 enc 字段)。</summary>
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    /// <summary>消息 id: 发送时的 Unix 毫秒 + 随机后缀, 全局唯一。</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>发送者昵称(明文, 接收方要靠它显示"谁发的"; 加密时也会写进密文里一份)。</summary>
    [JsonPropertyName("from")] public string From { get; set; } = "";

    /// <summary>发送时间(UTC)。</summary>
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }

    /// <summary>正文。启用加密时不写入服务器(服务器上只有 enc), 这里保留明文供本机回显。</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>引用文本(可空)。启用加密时不写入服务器。</summary>
    [JsonPropertyName("quote")] public string? Quote { get; set; }

    /// <summary>附件(文件/图片/语音, 可空)。启用加密时不写入服务器, 跟正文一起进密文。</summary>
    [JsonPropertyName("attach")] public Attachment? Attach { get; set; }

    /// <summary>加密后的正文信封 "AESGCM1:base64(nonce||密文||tag)"; 未加密为 null。</summary>
    [JsonPropertyName("enc")] public string? Enc { get; set; }

    /// <summary>本地是否已确认写入服务器(仅 UI 用, 不序列化)。</summary>
    [JsonIgnore] public bool Pending { get; set; }

    /// <summary>本地状态文本(仅 UI 用)。</summary>
    [JsonIgnore] public string? Status { get; set; }

    /// <summary>对应的 WebDAV 文件名(仅 UI 用)。</summary>
    [JsonIgnore] public string RemoteName { get; set; } = "";

    [JsonIgnore] public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");

    [JsonIgnore] public bool IsSelf { get; set; }

    /// <summary>这条消息带密文。</summary>
    [JsonIgnore] public bool IsEncrypted => !string.IsNullOrEmpty(Enc);

    /// <summary>收到的是密文, 但本机密码解不开(密码与发送方不一致, 或密文被改过)。</summary>
    [JsonIgnore] public bool DecryptFailed { get; set; }
}

/// <summary>附件(文件 / 图片 / 语音), 与消息同目录存放, 文件名前缀 att_。</summary>
public sealed class Attachment
{
    /// <summary>原始文件名(语音消息是 "语音 0:05.wav" 这种)。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>服务器上的相对路径。</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>字节数。</summary>
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>1=文件 2=图片 3=视频 4=音频 5=语音</summary>
    [JsonPropertyName("kind")] public int Kind { get; set; } = 1;

    /// <summary>语音时长(毫秒, 只有 kind=5 有意义)。</summary>
    [JsonPropertyName("dur")] public int DurationMs { get; set; }

    [JsonIgnore]
    public string SizeText => Size switch
    {
        < 1024 => Size + " B",
        < 1024 * 1024 => (Size / 1024) + " KB",
        < 1024L * 1024 * 1024 => (Size / (1024 * 1024)) + " MB",
        _ => (Size / (1024L * 1024 * 1024)) + " GB",
    };

    /// <summary>语音时长, 形如 0:07。</summary>
    [JsonIgnore]
    public string DurationText
    {
        get
        {
            var total = Math.Max(0, DurationMs) / 1000.0;
            return total >= 60
                ? $"{(int)(total / 60)}:{(int)(total % 60):00}"
                : $"{(int)total}\"";
        }
    }
}
