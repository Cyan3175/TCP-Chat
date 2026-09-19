using System.Text.Json.Serialization;

namespace TCPChat10.Models;

/// <summary>一条聊天消息(序列化后即为 WebDAV 上的一个 .json 文件)。</summary>
public sealed class ChatMessage
{
    /// <summary>协议版本: 1 = 明文, 2 = 正文加密(看 enc 字段)。</summary>
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    /// <summary>消息 id: 发送时的 Unix 毫秒 + 随机后缀, 全局唯一。</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>发送者昵称(明文, 接收方要靠它显示"谁发的"; 未加密时也用它填 payload)。</summary>
    [JsonPropertyName("from")] public string From { get; set; } = "";

    /// <summary>发送时间(UTC)。</summary>
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }

    /// <summary>正文。启用加密时不写入服务器(服务器上只有 enc), 这里保留明文供本机回显。</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>引用文本(可空)。启用加密时不写入服务器。</summary>
    [JsonPropertyName("quote")] public string? Quote { get; set; }

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
