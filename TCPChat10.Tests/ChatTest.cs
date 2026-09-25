using System.Text;
using TCPChat10.Models;
using TCPChat10.Services;

public static class ChatTest
{
    const string BaseUrl = "https://dev.zhaohans.cn";
    const string TestFolder = "nw集训/学生资料临存/_tcpchat_selftest";
    /// <summary>收发两端约定的加密密码, 测试里故意再造一个不一样的来验证"必须一致"。</summary>
    const string SharedPwd = "晚自习六点-两端一致";

    static int _pass, _fail;
    static void Check(bool ok, string name)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name); }
    }

    static AppSettings Make(string nick, string? crypto = null) => new()
    {
        ServerUrl = BaseUrl,
        ChatFolder = TestFolder,
        Nickname = nick,
        HistoryDays = 7,
        PollSeconds = 2,
        CryptoPassword = crypto ?? "",
    };

    public static async Task<int> RunAsync()
    {
        // 10.2 起程序不再自动新建目录, 测试目录由测试自己建(和真实用户一样: 目录得先在服务器上存在)
        using (var setup = new WebDavClient(BaseUrl))
            await setup.EnsureCollectionAsync(TestFolder);

        var settings = Make("张三");
        using var chat = new ChatService(settings);

        Console.WriteLine("=== 1) 初始化 ===");
        var (ok, msg) = await chat.InitializeAsync();
        Check(ok, "InitializeAsync -> " + msg);
        Check(!chat.EncryptionEnabled, "没填加密密码时不加密");

        Console.WriteLine("=== 1b) 目录不存在时不再自动新建 ===");
        var missing = TestFolder + "/_nope_" + Guid.NewGuid().ToString("N")[..6];
        using (var missSvc = new ChatService(new AppSettings { ServerUrl = BaseUrl, ChatFolder = missing, Nickname = "测试" }))
        {
            var (okMissing, msgMissing) = await missSvc.InitializeAsync();
            Check(!okMissing, "InitializeAsync 失败而不是偷偷建目录");
            Check(msgMissing.Contains("不存在"), "提示目录不存在: " + msgMissing);
        }
        using (var probeDav = new WebDavClient(BaseUrl))
        {
            bool made;
            try { made = (await probeDav.PropFindAsync(missing, 0)).Count > 0; }
            catch { made = false; }    // 有的服务器对不存在的路径直接报错而不是 404
            Check(!made, "服务器上确实没有多出这个目录");
        }

        Console.WriteLine("=== 2) 发送明文消息 ===");
        var m1 = await chat.SendTextAsync("你好，这是第一条测试消息");
        Check(m1 != null, "SendTextAsync 返回消息对象");
        Check(m1!.Pending == false, "消息已确认写入服务器 (Pending=false)");
        Check(m1.Enc == null && m1.Version == 1, "明文消息不带 enc 密文");
        Console.WriteLine("      id=" + m1.Id + "  文件=" + m1.RemoteName);

        var m2 = await chat.SendTextAsync("第二条：带引用的消息", quote: "你好，这是第一条测试消息");
        Check(m2 != null && m2.Quote != null, "带引用的消息已发送");

        Console.WriteLine("=== 3) 另一个客户端(全新实例)能否读到 ===");
        using var chat2 = new ChatService(Make("李四"));
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

        Console.WriteLine("=== 4b) 附件与语音传输 (10.6) ===");
        var tmpFile = Path.Combine(Path.GetTempPath(), "tcpchat106_" + Guid.NewGuid().ToString("N")[..6] + ".bin");
        var payload = new byte[20000];
        new Random(11).NextBytes(payload);
        await File.WriteAllBytesAsync(tmpFile, payload);

        var mf = await chat.SendFileAsync(tmpFile);
        Check(mf?.Attach != null, "附件消息已发送: " + mf?.Attach?.Name);
        Check(mf?.Attach?.Size == payload.Length, "附件大小记录正确: " + mf?.Attach?.Size);
        Check(mf?.Attach?.Kind == 1, "未知扩展名按文件处理 (kind=1)");

        var got2 = new List<ChatMessage>();
        chat2.MessageAdded += m => { lock (got2) got2.Add(m); };
        await chat2.SyncOnceAsync();
        var attachMsg = got2.FirstOrDefault(m => m.RemoteName == mf!.RemoteName);
        Check(attachMsg?.Attach != null, "对端收到附件消息");
        if (attachMsg?.Attach != null)
        {
            var local = await chat2.DownloadAttachmentAsync(attachMsg);
            Check(local != null && File.Exists(local), "附件下载成功");
            if (local != null)
            {
                var round = await File.ReadAllBytesAsync(local);
                Check(round.Length == payload.Length && round.SequenceEqual(payload),
                      "附件内容逐字节一致 (" + round.Length + " 字节)");
            }
        }

        var voice = await chat.SendFileAsync(tmpFile, kind: 5, durationMs: 1500);
        Check(voice?.Attach?.Kind == 5 && voice.Attach.DurationMs == 1500, "语音消息带 kind=5 与时长");
        Check(voice?.Attach?.DurationText == "1\"", "语音时长显示: " + voice?.Attach?.DurationText);

        Console.WriteLine("=== 5) 端到端加密: 发送端 ===");
        using var sender = new ChatService(Make("王五", SharedPwd));
        Check(sender.EncryptionEnabled, "填了加密密码 -> EncryptionEnabled");
        var em = await sender.SendTextAsync("加密正文：晚自习改成六点", quote: "旧安排");
        Check(em != null && em.Enc != null, "加密消息带 enc 密文");
        Check(em != null && em.Version == 2, "协议版本 = 2");
        Check(em != null && em.Text.Contains("晚自习"), "发送端本机回显仍是明文");
        Check(em != null && em.Pending == false, "加密消息已确认写入服务器" + (string.IsNullOrEmpty(em?.Status) ? "" : " -> " + em!.Status));

        using (var rawDav = new WebDavClient(BaseUrl))
        {
            var rawJson = await rawDav.GetStringAsync(WebDavClient.Combine(TestFolder, em!.RemoteName));
            Check(rawJson != null && rawJson.Contains("\"enc\""), "服务器上确实写的是 enc 密文");
            Check(rawJson != null && !rawJson.Contains("晚自习"), "服务器上的文件里没有明文正文");
            Check(rawJson != null && !rawJson.Contains("旧安排"), "引用也没有明文");
        }

        Console.WriteLine("=== 6) 端到端加密: 对方密码一致才读得到 ===");
        using var peer = new ChatService(Make("赵六", SharedPwd));
        var peerGot = new List<ChatMessage>();
        peer.MessageAdded += m => { lock (peerGot) peerGot.Add(m); };
        await peer.SyncOnceAsync();
        var dec = peerGot.FirstOrDefault(m => m.RemoteName == em!.RemoteName);
        Check(dec != null, "对端收到这条加密消息");
        Check(dec != null && !dec.DecryptFailed, "密码一致 -> 解密成功");
        Check(dec != null && dec.Text.Contains("晚自习"), "解出的正文正确: " + (dec?.Text ?? ""));
        Check(dec != null && dec.Quote == "旧安排", "解出的引用正确");
        Check(dec != null && dec.From == "王五", "发送者仍是王五");

        Console.WriteLine("=== 7) 端到端加密: 密码不一致 ===");
        using var stranger = new ChatService(Make("陌生人", "猜的密码"));
        var strangerGot = new List<ChatMessage>();
        stranger.MessageAdded += m => { lock (strangerGot) strangerGot.Add(m); };
        await stranger.SyncOnceAsync();
        var locked = strangerGot.FirstOrDefault(m => m.RemoteName == em!.RemoteName);
        Check(locked != null && locked.DecryptFailed, "密码不一致 -> 标记 DecryptFailed");
        Check(locked != null && locked.Text.Length == 0, "解不开时不显示任何乱码/明文");
        Check(stranger.UndecryptableCount >= 1, "解不开计数 = " + stranger.UndecryptableCount);

        using var noPwd = new ChatService(Make("没设密码的人"));
        var noPwdGot = new List<ChatMessage>();
        noPwd.MessageAdded += m => { lock (noPwdGot) noPwdGot.Add(m); };
        await noPwd.SyncOnceAsync();
        var locked2 = noPwdGot.FirstOrDefault(m => m.RemoteName == em!.RemoteName);
        Check(locked2 != null && locked2.DecryptFailed, "完全没填密码的客户端同样解不开");

        Console.WriteLine("=== 8) 换密码后重新同步能解开 ===");
        stranger.ApplyCryptoPassword(SharedPwd);      // 相当于在设置里改成对方的密码
        var again = new List<ChatMessage>();
        stranger.MessageAdded += m => { lock (again) again.Add(m); };
        await stranger.SyncOnceAsync();
        var reopened = again.FirstOrDefault(m => m.RemoteName == em!.RemoteName);
        Check(reopened != null && !reopened.DecryptFailed && reopened.Text.Contains("晚自习"),
              "改成正确密码后重新拉取即可解密");
        Check(stranger.UndecryptableCount == 0, "计数清零");

        Console.WriteLine("=== 9) 10.0 的老附件消息(10.6 起正常显示) ===");
        using (var davLegacy = new WebDavClient(BaseUrl))
        {
            var legacyName = "msg_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "_0badf00d.json";
            var legacyJson = "{\"v\":1,\"id\":\"legacy\",\"from\":\"老版本\",\"time\":\"" +
                             DateTimeOffset.UtcNow.ToString("o") + "\",\"text\":\"\"," +
                             "\"attach\":{\"name\":\"x.bin\",\"path\":\"p\",\"size\":1,\"kind\":1}}";
            Check(await davLegacy.PutTextAsync(WebDavClient.Combine(TestFolder, legacyName), legacyJson), "老格式消息文件已写入服务器");

            using var fresh = new ChatService(Make("新版本客户端"));
            var freshGot = new List<ChatMessage>();
            fresh.MessageAdded += m => { lock (freshGot) freshGot.Add(m); };
            await fresh.SyncOnceAsync();
            var legacySeen = freshGot.FirstOrDefault(m => m.RemoteName == legacyName);
            Check(legacySeen != null, "10.0 的老附件消息能显示出来");
            Check(legacySeen?.Attach != null && legacySeen.Attach.Kind == 1, "老消息的附件信息读得出来");
            Check((await davLegacy.PropFindAsync(TestFolder, 1)).Any(e => e.Name == legacyName), "老消息文件仍留在服务器上");
            await davLegacy.DeleteAsync(WebDavClient.Combine(TestFolder, legacyName));
        }

        Console.WriteLine("=== 10) 删除自己发的消息 ===");
        bool del = await chat.DeleteMessageAsync(m1);
        Check(del, "DeleteMessageAsync 成功");
        bool delEnc = await sender.DeleteMessageAsync(em!);
        Check(delEnc, "加密消息也能撤回");
        var after = await new WebDavClient(BaseUrl).PropFindAsync(TestFolder, 1);
        Check(after.All(e => e.Name != m1.RemoteName), "服务器上已无该消息文件");
        Check(after.All(e => e.Name != em!.RemoteName), "服务器上已无该加密消息文件");

        Console.WriteLine("=== 11) 目录最后状态 ===");
        foreach (var e in after.Where(e => !e.IsCollection).Take(12))
            Console.WriteLine("      " + e.Name + "  " + e.Length + " 字节");

        Console.WriteLine();
        Console.WriteLine("==== ChatService: " + _pass + " 通过, " + _fail + " 失败 ====");
        return _fail == 0 ? 0 : 1;
    }
}
