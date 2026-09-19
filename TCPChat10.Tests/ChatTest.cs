using System.Text;
using TCPChat10.Models;
using TCPChat10.Services;

public static class ChatTest
{
    const string BaseUrl = "https://dev.zhaohans.cn";
    const string TestFolder = "nw集训/学生资料临存/_tcpchat_selftest";

    static int _pass, _fail;
    static void Check(bool ok, string name)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name); }
    }

    public static async Task<int> RunAsync()
    {
        var settings = new AppSettings
        {
            ServerUrl = BaseUrl,
            ChatFolder = TestFolder,
            Nickname = "张三",
            HistoryDays = 7,
            PollSeconds = 2,
        };

        using var chat = new ChatService(settings);

        Console.WriteLine("=== 1) 初始化(建目录) ===");
        var (ok, msg) = await chat.InitializeAsync();
        Check(ok, "InitializeAsync -> " + msg);

        Console.WriteLine("=== 2) 发送文本消息 ===");
        var m1 = await chat.SendTextAsync("你好，这是第一条测试消息");
        Check(m1 != null, "SendTextAsync 返回消息对象");
        Check(m1!.Pending == false, "消息已确认写入服务器 (Pending=false)");
        Console.WriteLine("      id=" + m1.Id + "  文件=" + m1.RemoteName);

        var m2 = await chat.SendTextAsync("第二条：带引用的消息", quote: "你好，这是第一条测试消息");
        Check(m2 != null && m2.Quote != null, "带引用的消息已发送");

        Console.WriteLine("=== 3) 另一个客户端(全新实例)能否读到 ===");
        var settings2 = new AppSettings { ServerUrl = BaseUrl, ChatFolder = TestFolder, Nickname = "李四", HistoryDays = 7 };
        using var chat2 = new ChatService(settings2);
        var received = new List<ChatMessage>();
        chat2.MessageAdded += m => { lock (received) received.Add(m); };
        await chat2.SyncOnceAsync();
        Check(received.Count >= 2, "新实例同步到 " + received.Count + " 条消息");
        var first = received.FirstOrDefault(m => m.Text.Contains("第一条"));
        Check(first != null && first.From == "张三", "消息发送者=张三");
        Check(first != null && first.IsSelf == false, "对李四而言 IsSelf=false");
        var q = received.FirstOrDefault(m => m.Quote != null);
        Check(q != null && q.Quote!.Contains("第一条"), "引用内容完整");

        Console.WriteLine("=== 4) 增量: 第二次同步不应重复 ===");
        int before = received.Count;
        await chat2.SyncOnceAsync();
        Check(received.Count == before, "第二次同步无重复 (仍为 " + received.Count + " 条)");

        Console.WriteLine("=== 5) 附件上传/下载 ===");
        string tmp = Path.Combine(Path.GetTempPath(), "tcpchat_test_" + Guid.NewGuid().ToString("N")[..6] + ".bin");
        var payload = new byte[20000];
        new Random(11).NextBytes(payload);
        await File.WriteAllBytesAsync(tmp, payload);
        var mf = await chat.SendFileAsync(tmp);
        Check(mf != null && mf.Attach != null, "附件消息已发送");
        Check(mf!.Attach!.Size == payload.Length, "附件大小记录正确: " + mf.Attach.Size);

        var received2 = new List<ChatMessage>();
        chat2.MessageAdded += m => { lock (received2) received2.Add(m); };
        await chat2.SyncOnceAsync();
        var attachMsg = received2.FirstOrDefault(m => m.Attach != null);
        Check(attachMsg != null, "对端收到附件消息");
        if (attachMsg != null)
        {
            var local = await chat2.DownloadAttachmentAsync(attachMsg);
            Check(local != null && File.Exists(local), "附件下载成功");
            if (local != null)
            {
                var got = await File.ReadAllBytesAsync(local);
                Check(got.Length == payload.Length && got.SequenceEqual(payload), "附件内容逐字节一致 (" + got.Length + " 字节)");
            }
        }

        Console.WriteLine("=== 6) 删除自己发的消息 ===");
        bool del = await chat.DeleteMessageAsync(m1);
        Check(del, "DeleteMessageAsync 成功");
        var after = await new WebDavClient(BaseUrl).PropFindAsync(TestFolder, 1);
        Check(after.All(e => e.Name != m1.RemoteName), "服务器上已无该消息文件");

        Console.WriteLine("=== 7) 目录最后状态 ===");
        foreach (var e in after.Where(e => !e.IsCollection))
            Console.WriteLine("      " + e.Name + "  " + e.Length + " 字节");

        Console.WriteLine();
        Console.WriteLine("==== ChatService: " + _pass + " 通过, " + _fail + " 失败 ====");
        return _fail == 0 ? 0 : 1;
    }
}
