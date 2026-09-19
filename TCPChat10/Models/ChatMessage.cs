using System.Text.Json.Serialization;

namespace TCPChat10.Models;

/// <summary>一条聊天消息(序列化后即为 WebDAV 上的一个 .json 文件)。</summary>
public sealed class ChatMessage
{
    /// <summary>协议版本, 便于以后演进。</summary>
    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    /// <summary>消息 id: 发送时的 Unix 毫秒 + 随机后缀, 全局唯一。</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("from")] public string From { get; set; } = "";

    /// <summary>发送时间(UTC)。</summary>
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }

    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>引用文本(可空)。</summary>
    [JsonPropertyName("quote")] public string? Quote { get; set; }

    /// <summary>附件信息(可空)。</summary>
    [JsonPropertyName("attach")] public Attachment? Attach { get; set; }

    /// <summary>本地是否已确认写入服务器(仅 UI 用, 不序列化)。</summary>
    [JsonIgnore] public bool Pending { get; set; }

    /// <summary>本地状态文本(仅 UI 用)。</summary>
    [JsonIgnore] public string? Status { get; set; }

    /// <summary>对应的 WebDAV 文件名(仅 UI 用)。</summary>
    [JsonIgnore] public string RemoteName { get; set; } = "";

    [JsonIgnore] public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");

    [JsonIgnore] public bool IsSelf { get; set; }
}

/// <summary>附件(图片/文件), 与消息同目录存放。</summary>
public sealed class Attachment
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    /// <summary>1=文件 2=图片 3=视频 4=音频</summary>
    [JsonPropertyName("kind")] public int Kind { get; set; } = 1;

    [JsonIgnore]
    public string SizeText
    {
        get
        {
            if (Size < 1024) return Size + " B";
            if (Size < 1024 * 1024) return (Size / 1024) + " KB";
            if (Size < 1024L * 1024 * 1024) return (Size / (1024 * 1024)) + " MB";
            return (Size / (1024L * 1024 * 1024)) + " GB";
        }
    }
}
