using System.Text;
using System.Text.Json;
using Markdig;
using TCPChat10.Glass;
using TCPChat10.Rendering;
using TCPChat10.Services;

/// <summary>离线单元测试: 加密往返 / 密码一致性 / 密文完整性 / 设置持久化 / 字体列表。</summary>
public static class CryptoTest
{
    static int _pass, _fail;
    static void Check(bool ok, string name)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name); }
    }

    public static int Run()
    {
        Console.WriteLine("=== A) 密钥派生: 密码/目录一致 -> 密钥一致 ===");
        var k1 = MessageCrypto.DeriveKey("hunter2", "nw集训/学生资料临存/聊天");
        var k2 = MessageCrypto.DeriveKey("hunter2", "nw集训/学生资料临存/聊天");
        var k3 = MessageCrypto.DeriveKey("hunter3", "nw集训/学生资料临存/聊天");
        var k4 = MessageCrypto.DeriveKey("hunter2", "别的目录");
        Check(k1.SequenceEqual(k2), "同一密码+同一目录 -> 同一密钥");
        Check(!k1.SequenceEqual(k3), "密码不同 -> 密钥不同");
        Check(!k1.SequenceEqual(k4), "目录不同 -> 密钥不同");
        Check(k1.Length == 32, "密钥长度 32 字节 (AES-256)");

        Console.WriteLine("=== B) 加解密往返 ===");
        const string text = "你好，这是一条加密消息 🔒 with ASCII and 中文混排";
        const string aad = "msg_1758000000000_abcdef12.json";
        var env1 = MessageCrypto.Encrypt(k1, aad, text);
        Check(env1.StartsWith(MessageCrypto.Prefix), "信封带前缀 " + MessageCrypto.Prefix);
        Check(MessageCrypto.TryDecrypt(k1, aad, env1) == text, "同一密钥解出原文");
        Check(!env1.Contains("你好"), "信封里看不到明文");
        var env2 = MessageCrypto.Encrypt(k1, aad, text);
        Check(env1 != env2, "同一明文两次加密结果不同 (nonce 随机)");
        Check(MessageCrypto.TryDecrypt(k1, aad, env2) == text, "第二次加密同样可解");

        Console.WriteLine("=== C) 密码/文件名不一致 -> 解不开 ===");
        Check(MessageCrypto.TryDecrypt(k3, aad, env1) == null, "错误密码解不开 (GCM 校验失败)");
        Check(MessageCrypto.TryDecrypt(k1, "msg_1758000000001_ffffffff.json", env1) == null, "换个文件名(AAD)解不开");
        Check(MessageCrypto.TryDecrypt(k1, aad, "AESGCM1:AAAA") == null, "残缺密文返回 null 而不是抛异常");
        Check(MessageCrypto.TryDecrypt(k1, aad, "PLAINTEXT:whatever") == null, "前缀不对返回 null");
        Check(MessageCrypto.TryDecrypt(k1, aad, null) == null, "空信封返回 null");

        var tampered = Tamper(env1);
        Check(MessageCrypto.TryDecrypt(k1, aad, tampered) == null, "篡改密文后解不开");

        Console.WriteLine("=== D) MessageCipher: 正文+引用打包 ===");
        var cipherA = new MessageCipher("共享密码", "nw集训/学生资料临存/聊天");
        var cipherB = new MessageCipher("共享密码", "nw集训/学生资料临存/聊天");
        var cipherC = new MessageCipher("另一个密码", "nw集训/学生资料临存/聊天");
        var cipherPlain = new MessageCipher("", "nw集训/学生资料临存/聊天");
        Check(cipherA.Enabled && !cipherPlain.Enabled, "空密码 = 不加密(Enabled=false)");

        var packed = cipherA.EncryptPayload(aad, "张三", text, "被引用的那句");
        Check(packed != null, "加密载荷生成成功");
        var unpacked = cipherB.TryDecryptPayload(aad, packed);
        Check(unpacked != null && unpacked.Text == text && unpacked.From == "张三" && unpacked.Quote == "被引用的那句",
              "对端(同密码)解出 发送者/正文/引用");
        Check(cipherC.TryDecryptPayload(aad, packed) == null, "密码不一致 -> 解不开 (界面显示无法解密)");
        Check(cipherPlain.EncryptPayload(aad, "张三", text, null) == null, "未启用加密时不产生密文");

        Console.WriteLine("=== E) 上行 JSON 不泄露明文 ===");
        const string secret = "考试答案在第三个抽屉";
        var wire = new TCPChat10.Models.ChatMessage
        {
            Version = 2,
            Id = "1758000000000_abcdef12",
            From = "张三",
            Time = DateTimeOffset.UtcNow,
            Enc = cipherA.EncryptPayload(aad, "张三", secret, null),
        };
        var wireJson = JsonSerializer.Serialize(wire, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        Check(!wireJson.Contains("考试答案"), "写到服务器上的 JSON 里没有明文正文");
        Check(wireJson.Contains("\"enc\""), "JSON 里有 enc 字段");
        var back = JsonSerializer.Deserialize<TCPChat10.Models.ChatMessage>(wireJson)!;
        Check(back.IsEncrypted, "反序列化后 IsEncrypted=true");
        var backPayload = cipherB.TryDecryptPayload(aad, back.Enc);
        Check(backPayload != null && backPayload.Text == secret, "接收端能还原出原正文");

        Console.WriteLine("=== F) 设置持久化 (字体 / 加密密码) ===");
        var tmpDir = Path.Combine(Path.GetTempPath(), "tcpchat10_settings_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmpDir);
        var tmpFile = Path.Combine(tmpDir, "settings.json");
        var oldEnv = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS");
        try
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", tmpFile);
            var s = new AppSettings
            {
                ServerUrl = "https://example.invalid",
                ChatFolder = "a/b",
                Nickname = "李四",
                FontFamily = "楷体",
                CryptoPassword = "两端一致的密码",
                Theme = 2,
            };
            s.Save();
            Check(File.Exists(tmpFile), "设置文件已写出: " + tmpFile);
            var raw = File.ReadAllText(tmpFile);
            Check(raw.Contains("fontFamily") && raw.Contains("cryptoPassword"), "JSON 里有 fontFamily / cryptoPassword");

            var loaded = AppSettings.Load();
            Check(loaded.FontFamily == "楷体", "字体选择读回来了: " + loaded.FontFamily);
            Check(loaded.CryptoPassword == "两端一致的密码", "加密密码读回来了");
            Check(loaded.Nickname == "李四" && loaded.Theme == 2, "其他设置项不受影响");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", oldEnv);
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        Console.WriteLine("=== F2) 设置文件位置 与 旧默认目录迁移 (10.2) ===");
        var tmpDir2 = Path.Combine(Path.GetTempPath(), "tcpchat10_migrate_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmpDir2);
        var tmpFile2 = Path.Combine(tmpDir2, "settings.json");
        var oldEnv2 = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS");
        try
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", tmpFile2);
            File.WriteAllText(tmpFile2, "{\"serverUrl\":\"https://dev.zhaohans.cn\",\"chatFolder\":\"nw集训/学生资料临存/聊天\",\"nickname\":\"老用户\"}");
            var migrated = AppSettings.Load();
            Check(migrated.ChatFolder == AppSettings.DefaultChatFolder,
                  "10.0/10.1 的旧默认目录自动上移一级 -> " + migrated.ChatFolder);
            Check(migrated.Nickname == "老用户", "其余设置项不受影响");

            File.WriteAllText(tmpFile2, "{\"chatFolder\":\"\",\"serverUrl\":\"\"}");
            var blank = AppSettings.Load();
            Check(blank.ChatFolder == AppSettings.DefaultChatFolder && blank.ServerUrl == AppSettings.DefaultServerUrl,
                  "空的目录/地址回落到默认值");

            File.WriteAllText(tmpFile2, "{\"chatFolder\":\"nw集训/学生资料临存/别的地方\"}");
            var kept = AppSettings.Load();
            Check(kept.ChatFolder == "nw集训/学生资料临存/别的地方", "用户自己改过的目录不会被迁移");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", oldEnv2);
            try { Directory.Delete(tmpDir2, true); } catch { }
        }

        Check(AppSettings.DefaultChatFolder == "nw集训/学生资料临存/tcp_chat", "默认聊天目录是共享目录下的 tcp_chat: " + AppSettings.DefaultChatFolder);
        Check(new AppSettings { ChatFolder = "nw集训/学生资料临存" }.NormalizeForTests().ChatFolder == "nw集训/学生资料临存/tcp_chat",
              "第一次升级: 老默认目录(共享目录本身)自动下移到 tcp_chat");
        Check(new AppSettings { ChatFolder = "nw集训/学生资料临存", FolderMigrated = true }.NormalizeForTests().ChatFolder == "nw集训/学生资料临存",
              "迁移过一次之后, 手动改回共享目录本身要能被尊重");
        Check(new AppSettings { ChatFolder = "别的/目录", FolderMigrated = true }.NormalizeForTests().ChatFolder == "别的/目录",
              "手动填的任意目录不会被改写");
        Check(AppSettings.FilePath.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase), "设置文件名: " + AppSettings.FilePath);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expectedDir = Path.Combine(localAppData, "TCPChat");
        Check(string.Equals(Path.GetDirectoryName(AppSettings.FilePath), expectedDir, StringComparison.OrdinalIgnoreCase),
              "设置文件放在系统统一位置: " + AppSettings.FilePath);
        Check(!AppSettings.FilePath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
              "设置文件不再写在 exe 目录里");
        Check(string.Equals(AppSettings.DataDir, expectedDir, StringComparison.OrdinalIgnoreCase),
              "崩溃日志等数据目录也是系统统一位置");
        Check(Path.GetFileName(expectedDir) == "TCPChat" && !expectedDir.EndsWith("TCPChat10", StringComparison.Ordinal),
              "10.7 起目录名是 TCPChat(不是 TCPChat10)");

        Console.WriteLine("=== J) 老数据目录 TCPChat10 -> TCPChat 搬迁 (10.7) ===");
        var oldTestData = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_DATADIR");
        var oldTestLegacy = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_LEGACYDIR");
        var moveRoot = Path.Combine(Path.GetTempPath(), "tcpchat107_" + Guid.NewGuid().ToString("N")[..6]);
        var fakeOld = Path.Combine(moveRoot, "TCPChat10");
        var fakeNew = Path.Combine(moveRoot, "TCPChat");
        try
        {
            Directory.CreateDirectory(Path.Combine(fakeOld, "cache"));
            File.WriteAllText(Path.Combine(fakeOld, "settings.json"),
                "{\"serverUrl\":\"https://dev.zhaohans.cn\",\"chatFolder\":\"a/b\",\"nickname\":\"迁移测试\"}");
            File.WriteAllBytes(Path.Combine(fakeOld, "cache", "att_9_x.png"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllText(Path.Combine(fakeOld, "crash.log"), "老的崩溃日志");

            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_DATADIR", fakeNew);
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_LEGACYDIR", fakeOld);
            AppSettings.ResetMigrationForTests();

            var moved = AppSettings.Load();
            Check(moved.Nickname == "迁移测试", "老目录里的设置搬过来了: " + moved.Nickname);
            Check(File.Exists(Path.Combine(fakeNew, "settings.json")), "新目录里有 settings.json");
            Check(File.Exists(Path.Combine(fakeNew, "cache", "att_9_x.png")), "附件缓存一起搬过来");
            Check(!Directory.Exists(fakeOld), "老 TCPChat10 目录已清理掉");
            Check(string.Equals(Path.GetDirectoryName(AppSettings.FilePath), fakeNew, StringComparison.OrdinalIgnoreCase),
                  "迁移后 Path 指向新目录: " + AppSettings.FilePath);

            // 新目录里已经有设置时, 老目录不覆盖它
            Directory.CreateDirectory(Path.Combine(fakeOld, "cache"));
            File.WriteAllText(Path.Combine(fakeOld, "settings.json"), "{\"nickname\":\"老名字\"}");
            File.WriteAllText(Path.Combine(fakeNew, "settings.json"), "{\"nickname\":\"新名字\",\"chatFolder\":\"a/b\"}");
            AppSettings.ResetMigrationForTests();
            var kept = AppSettings.Load();
            Check(kept.Nickname == "新名字", "新目录已有设置时不被老目录覆盖: " + kept.Nickname);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_DATADIR", oldTestData);
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_LEGACYDIR", oldTestLegacy);
            AppSettings.ResetMigrationForTests();
            try { Directory.Delete(moveRoot, true); } catch { }
        }

        Console.WriteLine("=== I) 附件与语音 (10.6) ===");
        var fileKey = MessageCrypto.DeriveKey("附件密码", "目录A");
        var blob = new byte[5000];
        new Random(7).NextBytes(blob);
        var sealed_ = MessageCrypto.EncryptBytes(fileKey, aad, blob);
        Check(sealed_.Length == blob.Length + MessageCrypto.NonceLength + MessageCrypto.TagLength,
              $"密文长度 = 明文 + nonce + tag ({sealed_.Length} = {blob.Length} + 28)");
        var opened = MessageCrypto.TryDecryptBytes(fileKey, aad, sealed_);
        Check(opened != null && opened.SequenceEqual(blob), "附件字节加解密往返一致");
        Check(MessageCrypto.TryDecryptBytes(MessageCrypto.DeriveKey("别的密码", "目录A"), aad, sealed_) == null,
              "密码不对解不开附件");
        Check(MessageCrypto.TryDecryptBytes(fileKey, "别的文件名", sealed_) == null, "文件名(AAD)不对解不开附件");
        var tamperedBlob = (byte[])sealed_.Clone();
        tamperedBlob[20] ^= 0x01;
        Check(MessageCrypto.TryDecryptBytes(fileKey, aad, tamperedBlob) == null, "附件密文被改动后解不开");

        var withAttach = new TCPChat10.Models.Attachment
        {
            Name = "voice.wav", Path = "chat/att_1_voice.wav", Size = 48044, Kind = 5, DurationMs = 1500,
        };
        var packed2 = cipherA.EncryptPayload(aad, "张三", "", null, withAttach);
        var unpacked2 = cipherB.TryDecryptPayload(aad, packed2);
        Check(unpacked2?.Attach != null && unpacked2.Attach.Name == "voice.wav" &&
              unpacked2.Attach.Kind == 5 && unpacked2.Attach.Size == 48044 && unpacked2.Attach.DurationMs == 1500,
              "附件信息(名字/类型/大小/时长)跟着密文一起走");

        Check(ChatService.SanitizeFileName("a/b\\c:d*e?.txt") == "a_b_c_d_e_.txt", "附件文件名会过滤非法字符");
        Check(ChatService.SanitizeFileName("") == "file", "空文件名有兜底");
        Check(ChatService.GuessKind(".PNG") == 2 && ChatService.GuessKind(".mp4") == 3 &&
              ChatService.GuessKind(".wav") == 4 && ChatService.GuessKind(".zip") == 1, "按扩展名猜类型");
        Check(ChatService.Human(48044) == "46 KB" && ChatService.Human(2032) == "1 KB", "大小显示: " + ChatService.Human(48044));
        Check(withAttach.DurationText == "1\"", "语音时长显示: " + withAttach.DurationText);

        Console.WriteLine("=== G) 系统字体列表 ===");
        var fonts = FontList.GetInstalledFamilies();
        Check(fonts.Count > 10, "枚举到 " + fonts.Count + " 个字体族");
        Check(fonts.All(f => f.Length > 0 && f[0] != '@'), "列表里没有空名/竖排(@)变体");
        Check(fonts.Distinct(StringComparer.OrdinalIgnoreCase).Count() == fonts.Count, "字体名不重复");
        Check(fonts.Contains("Arial", StringComparer.OrdinalIgnoreCase), "包含常见字体 Arial");
        Console.WriteLine("      前几个: " + string.Join(" / ", fonts.Take(8)));
        Console.WriteLine("      含 得意黑: " + fonts.Contains("得意黑") + " / 含 KaiTi: " +
            fonts.Any(f => f.Contains("KaiTi", StringComparison.OrdinalIgnoreCase)) + " / 含 楷体: " + fonts.Contains("楷体"));
        Check(FontList.Exists("Arial"), "FontList.Exists(Arial) = true");
        Check(!FontList.Exists("这个字体肯定不存在12345"), "FontList.Exists(不存在的字体) = false");
        Check(!FontList.Exists(""), "空字体名视为不存在(回退系统默认)");

        Console.WriteLine("=== H) 兼容 10.0 的老消息 ===");
        const string legacyAttach = "{\"v\":1,\"id\":\"1789800000000_aaaaaaaa\",\"from\":\"张三\"," +
                                    "\"time\":\"2026-09-19T08:20:52+00:00\",\"text\":\"\"," +
                                    "\"attach\":{\"name\":\"图.png\",\"path\":\"x/att_1_图.png\",\"size\":4855,\"kind\":2}}";
        var legacy = JsonSerializer.Deserialize<TCPChat10.Models.ChatMessage>(legacyAttach);
        Check(legacy != null && legacy.Enc == null, "10.0 的老消息(带附件)能正常解析, 不抛异常");
        Check(legacy != null && string.IsNullOrWhiteSpace(legacy.Text) && string.IsNullOrWhiteSpace(legacy.Quote),
              "纯附件的老消息没有正文");
        Check(legacy?.Attach != null && legacy.Attach.Kind == 2 && legacy.Attach.Size == 4855,
              "老消息里的附件信息能读出来(10.6 起会正常显示)");
        Check(!string.IsNullOrWhiteSpace(legacy?.Attach?.Name), "老附件文件名也在: " + legacy?.Attach?.Name);

        const string legacyText = "{\"v\":1,\"id\":\"1789800000001_bbbbbbbb\",\"from\":\"李四\"," +
                                  "\"time\":\"2026-09-19T08:21:00+00:00\",\"text\":\"老版本的明文消息\",\"quote\":\"被引用的那句\"}";
        var lt = JsonSerializer.Deserialize<TCPChat10.Models.ChatMessage>(legacyText);
        Check(lt != null && lt.Text == "老版本的明文消息" && lt.Quote == "被引用的那句" && !lt.IsEncrypted,
              "10.0 的明文消息照常读取");


        Console.WriteLine("=== K) Markdown 解析 (10.7) ===");
        const string TICK = "\u0060";   // C# 里 \u0060 就是反引号
        var md = string.Join("\n", new[]
        {
            "# 标题一",
            "",
            "**粗体** *斜体* ~~删除~~ " + TICK + "代码" + TICK + " [链接](https://example.com) :smile:",
            "",
            "- [x] 做完的",
            "- [ ] 没做的",
            "",
            "1. 第一",
            "2. 第二",
            "",
            "| 左 | 中 | 右 |",
            "|:---|:---:|---:|",
            "| a | b | c |",
            "",
            "> 引用一句",
            "",
            "> [!WARNING]",
            "> 注意安全",
            "",
            "~~~cs",
            "var x = 1; // 注释",
            "~~~",
            "",
            "---",
            "",
            "H~2~O x^2^ $e=mc^2$",
            "",
            "脚注[^1]",
            "",
            "[^1]: 脚注内容",
            "",
            "自动链接 <https://auto.link>",
            "",
            "普通一行",
            "换行也是换行",
        });
        var doc = MarkdownParser.Parse(md);

        static string TextOf(IEnumerable<MdInline> xs)
        {
            var sb = new StringBuilder();
            foreach (var x in xs)
            {
                switch (x)
                {
                    case MdText t: sb.Append(t.Text); break;
                    case MdCodeSpan c: sb.Append(c.Text); break;
                    case MdStyle s: sb.Append(TextOf(s.Children)); break;
                    case MdLink l: sb.Append(TextOf(l.Children)); break;
                    case MdImage i: sb.Append(i.Alt); break;
                    case MdMathSpan m: sb.Append(m.Text); break;
                    case MdBreak: sb.Append('\n'); break;
                }
            }
            return sb.ToString();
        }
        static bool Has<T>(IEnumerable<MdInline> xs) where T : MdInline => xs.Any(x => x is T
            || (x is MdStyle s && Has<T>(s.Children))
            || (x is MdLink l && Has<T>(l.Children)));

        var h1 = doc.Blocks.OfType<MdHeading>().FirstOrDefault(h => h.Level == 1);
        Check(h1 != null && TextOf(h1.Inlines) == "标题一", "一级标题");
        var rich = doc.Blocks.OfType<MdParagraph>().First();
        Check(Has<MdStyle>(rich.Inlines), "粗体/斜体/删除线解析成样式");
        Check(rich.Inlines.OfType<MdStyle>().Any(s => s.Kind == MdStyleKind.Bold), "**粗体** -> Bold");
        Check(rich.Inlines.OfType<MdStyle>().Any(s => s.Kind == MdStyleKind.Italic), "*斜体* -> Italic");
        Check(rich.Inlines.OfType<MdStyle>().Any(s => s.Kind == MdStyleKind.Strike), "~~删除~~ -> Strike");
        Check(Has<MdCodeSpan>(rich.Inlines), "行内代码");
        var link = rich.Inlines.OfType<MdLink>().FirstOrDefault();
        Check(link?.Url == "https://example.com", "[链接](url) -> " + link?.Url);
        Check(TextOf(rich.Inlines).Contains("\U0001F604"), ":smile: -> 😄");
        Check(!TextOf(rich.Inlines).Contains(":smile:"), "短代码 :smile: 不再原样显示出来");
        Check(MarkdownParser.LastError == null, "解析没有走兜底路径");

        var tasks = doc.Blocks.OfType<MdList>().FirstOrDefault(l => l.Items.Any(i => i.IsTask));
        Check(tasks != null && tasks.Items.Count == 2, "任务列表两项");
        Check(tasks!.Items[0].Checked && !tasks.Items[1].Checked, "- [x] / - [ ] 勾选状态正确");
        var ordered = doc.Blocks.OfType<MdList>().FirstOrDefault(l => l.Ordered);
        Check(ordered != null && ordered.Start == 1 && ordered.Items.Count == 2, "有序列表 1. 2.");

        var table = doc.Blocks.OfType<MdTable>().FirstOrDefault();
        Check(table != null && table.Headers.Count == 3 && table.Rows.Count == 1, "表格 3 列 1 行");
        Check(table!.Headers[0].Align == "Left" && table.Headers[1].Align == "Center" && table.Headers[2].Align == "Right",
              "表格对齐 :--- / :---: / ---:");
        Check(TextOf(table.Rows[0][2].Inlines) == "c", "表格单元格内容");

        var quotes = doc.Blocks.OfType<MdQuote>().ToList();
        Check(quotes.Count >= 2, "引用块解析");
        Check(quotes.Any(q => q.Alert == "WARNING"), "> [!WARNING] 提示块");

        var code = doc.Blocks.OfType<MdCodeBlock>().FirstOrDefault();
        Check(code?.Language == "cs" && code.Code.Contains("var x = 1;"), "围栏代码块 + 语言: " + code?.Language);
        Check(doc.Blocks.OfType<MdRule>().Any(), "--- 分割线");

        var sub = doc.Blocks.OfType<MdParagraph>().FirstOrDefault(p => Has<MdStyle>(p.Inlines) && TextOf(p.Inlines).Contains("H2O"));
        Check(sub != null && sub.Inlines.OfType<MdStyle>().Any(s => s.Kind == MdStyleKind.Sub), "H~2~O -> 下标");
        Check(sub!.Inlines.OfType<MdStyle>().Any(s => s.Kind == MdStyleKind.Sup), "x^2^ -> 上标");
        Check(Has<MdMathSpan>(sub.Inlines), "$e=mc^2$ -> 行内公式");

        Check(doc.Blocks.OfType<MdParagraph>().SelectMany(p => p.Inlines).Any(i => i is MdFootnoteRef), "脚注引用 [^1]");
        var notes = doc.Blocks.OfType<MdFootnotes>().FirstOrDefault();
        Check(notes != null && notes.Items.Count == 1 && TextOf(notes.Items[0].Blocks.OfType<MdParagraph>().First().Inlines) == "脚注内容",
              "脚注内容归到文末");

        var auto = doc.Blocks.OfType<MdParagraph>().SelectMany(p => p.Inlines).OfType<MdLink>().FirstOrDefault(l => l.Url.Contains("auto.link"));
        Check(auto != null, "<https://auto.link> -> 链接");
        var last = doc.Blocks.OfType<MdParagraph>().Last();
        Check(TextOf(last.Inlines).Contains("\n"), "聊天里单个换行 -> 换行(不当软换行吞掉)");

        Check(MarkdownParser.ToPlainText("**粗**\u4f53") == "粗体", "通知用纯文本: " + MarkdownParser.ToPlainText("**粗**\u4f53"));
        Check(MarkdownParser.ToPlainText("# 标题\n正文") == "标题\n正文", "纯文本保留换行");

        Check(!MarkdownParser.HasMarkup("今天下午三点开会，记得带材料"), "纯文本走快路径");
        Check(!MarkdownParser.HasMarkup("a - b = c"), "减号不当列表");
        Check(MarkdownParser.HasMarkup("**加粗**"), "有标记 -> 走解析器");
        Check(MarkdownParser.HasMarkup("- 列表"), "行首减号 -> 列表");
        Check(MarkdownParser.HasMarkup("1. 有序"), "行首数字点 -> 有序列表");
        Check(MarkdownParser.HasMarkup("看这个 https://a.example.com/x?y=1"), "裸网址 -> 自动链接");
        Check(MarkdownParser.HasMarkup("> 引用"), "行首大于号 -> 引用");
        Check(!MarkdownParser.HasMarkup("1.5 倍"), "1.5 不当有序列表");

        Check(MarkdownParser.Parse("").Blocks.Count == 0, "空文本不报错");
        Check(MarkdownParser.Parse("##### 六级").Blocks.OfType<MdHeading>().First().Level == 5, "五级标题");

        Console.WriteLine("=== Z) 缩放 (11.4) ===");
        Check(Math.Abs(UiZoom.Level - 1.0) < 0.0001, "默认 100%");
        UiZoom.Set(1.5);
        Check(Math.Abs(UiZoom.Level - 1.5) < 0.0001, "设置 1.5 生效: " + UiZoom.Percent);
        UiZoom.Step(+1);
        Check(Math.Abs(UiZoom.Level - 1.6) < 0.0001, "放大一档 +10%: " + UiZoom.Percent);
        UiZoom.Step(-1); UiZoom.Step(-1);
        Check(Math.Abs(UiZoom.Level - 1.4) < 0.0001, "缩小一档 -10%: " + UiZoom.Percent);
        UiZoom.Set(99);
        Check(Math.Abs(UiZoom.Level - UiZoom.Max) < 0.0001, "超上限夹到 " + UiZoom.Max);
        UiZoom.Set(-3);
        Check(Math.Abs(UiZoom.Level - 1.0) < 0.0001, "非法值(负数)回到 100%");
        UiZoom.Set(double.NaN);
        Check(Math.Abs(UiZoom.Level - 1.0) < 0.0001, "NaN 也回到 100%");
        UiZoom.Set(0.0001);
        Check(Math.Abs(UiZoom.Level - UiZoom.Min) < 0.0001, "超下限夹到 " + UiZoom.Min);
        UiZoom.Reset();
        Check(Math.Abs(UiZoom.Level - 1.0) < 0.0001, "Ctrl+0 复位到 100%");
        int zoomEvents = 0;
        Action onZoom = () => zoomEvents++;
        UiZoom.Changed += onZoom;
        UiZoom.Step(+1); UiZoom.Step(+1); UiZoom.Set(UiZoom.Level);
        UiZoom.Changed -= onZoom;
        Check(zoomEvents == 2, "缩放变化会通知界面(同值不重复通知): " + zoomEvents);
        UiZoom.Reset();

        Console.WriteLine("=== L) 液态玻璃参数 (11.0) ===");
        var gq0 = GlassParams.For(0);
        var gq25 = GlassParams.For(25);
        var gq60 = GlassParams.For(60);
        var gq100 = GlassParams.For(100);
        Check(gq0.Octaves == 1 && gq100.Octaves == 4, "质量越高湍流层数越多: " + gq0.Octaves + " -> " + gq100.Octaves);
        Check(gq0.BlurScale < gq100.BlurScale, "模糊倍率随质量上升: " + gq0.BlurScale + " -> " + gq100.BlurScale);
        Check(!gq0.ColorGrade && gq25.ColorGrade && gq60.ColorGrade && gq100.ColorGrade, "低质量不做色彩增强, 中/高质量做");
        Check(gq0.DpiCap < gq60.DpiCap && gq60.DpiCap < gq100.DpiCap, "渲染分辨率上限随质量上升: " + gq0.DpiCap + " / " + gq60.DpiCap + " / " + gq100.DpiCap);
        Check(GlassParams.For(-5).Quality == 0 && GlassParams.For(999).Quality == 100, "质量参数夹到 0~100");
        Check(gq60.Describe().Contains("60") && gq60.Describe().Contains("湍流"), "Describe 能显示给用户: " + gq60.Describe());
        bool monoOctaves = true; var prevOctaves = 0;
        for (int q = 0; q <= 100; q += 5) { var pp = GlassParams.For(q); if (pp.Octaves < prevOctaves) monoOctaves = false; prevOctaves = pp.Octaves; }
        Check(monoOctaves, "湍流层数随质量单调不降");

        var tmpGlass = Path.Combine(Path.GetTempPath(), "tcpchat110_" + Guid.NewGuid().ToString("N")[..6] + ".json");
        var oldEnvGlass = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS");
        try
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", tmpGlass);
            new AppSettings { GlassEnabled = false, GlassQuality = 37, ChatFolder = "a/b" }.Save();
            var backGlass = AppSettings.Load();
            Check(!backGlass.GlassEnabled && backGlass.GlassQuality == 37, "玻璃开关与质量能写进设置再读回来: " + backGlass.GlassQuality);
            File.WriteAllText(tmpGlass, "{\"glassQuality\":999,\"chatFolder\":\"a/b\"}");
            Check(AppSettings.Load().GlassQuality == 100, "设置里越界的质量会被夹到 100");
            File.WriteAllText(tmpGlass, "{\"glassQuality\":-8,\"chatFolder\":\"a/b\"}");
            Check(AppSettings.Load().GlassQuality == 0, "负数质量会被夹到 0");
            Check(new AppSettings().GlassEnabled, "玻璃默认是开的");
            File.WriteAllText(tmpGlass, "{\"zoom\":1.8,\"chatFolder\":\"a/b\"}");
            Check(Math.Abs(AppSettings.Load().Zoom - 1.8) < 0.0001, "缩放比例能从设置里读回来: " + AppSettings.Load().Zoom);
            File.WriteAllText(tmpGlass, "{\"zoom\":42,\"chatFolder\":\"a/b\"}");
            Check(Math.Abs(AppSettings.Load().Zoom - UiZoom.Max) < 0.0001, "设置里越界的缩放夹到上限 " + UiZoom.Max);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", oldEnvGlass);
            try { File.Delete(tmpGlass); } catch { }
        }

        Console.WriteLine("=== N) 密码列表与默认发送密码 (11.2) ===");
        var aadN = "msg_1790300000000_abcdef12.json";
        var keyA = MessageCrypto.DeriveKey("passA", "目录X");
        var keyB = MessageCrypto.DeriveKey("passB", "目录X");
        // 注意: 必须用 EncryptPayload 产出 JSON 载荷 —— TryDecryptPayload 解的是载荷,
        // 直接 Encrypt 一段纯文本会在反序列化时失败(我自己先踩了这个坑)
        var envA = new MessageCipher(new[] { "passA" }, 0, "目录X").EncryptPayload(aadN, "李四", "用A加密的消息", null)!;
        var envB = new MessageCipher(new[] { "passB" }, 0, "目录X").EncryptPayload(aadN, "李四", "用B加密的消息", null)!;
        var cipher = new MessageCipher(new[] { "passA", "passB" }, 1, "目录X");
        Check(cipher.Enabled && cipher.PasswordCount == 2, "两个密码都参与尝试: " + cipher.PasswordCount);
        Check(cipher.TryDecryptPayload(aadN, envB)?.Text == "用B加密的消息", "选中的发送密码能解开自己发的");
        Check(cipher.TryDecryptPayload(aadN, envA)?.Text == "用A加密的消息", "列表里另一把也能解开老消息");
        Check(new MessageCipher(new[] { "passB" }, 0, "目录X").TryDecryptPayload(aadN, envA) == null, "不在列表里的密码解不开");
        var outA = new MessageCipher(new[] { "passA", "passB" }, 0, "目录X").EncryptPayload(aadN, "张三", "发给对方", null);
        Check(MessageCrypto.TryDecrypt(keyA, aadN, outA) != null && MessageCrypto.TryDecrypt(keyB, aadN, outA) == null,
              "加密只用选中的那一把(选 1 -> A)");
        Check(new MessageCipher(new[] { "passA", "passB" }, 0, "目录X").TryDecryptPayload(aadN, "AESGCM1:AAAA") == null,
              "残缺密文返回 null 而不是抛异常");

        var tmpPw = Path.Combine(Path.GetTempPath(), "tcpchat112_" + Guid.NewGuid().ToString("N")[..6] + ".json");
        var oldEnvPw = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS");
        try
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", tmpPw);
            new AppSettings { CryptoPasswords = new List<string> { "a1", "b2", "c3" }, SendPasswordIndex = 1, ChatFolder = "x/y" }.Save();
            var backPw = AppSettings.Load();
            Check(backPw.CryptoPasswords.Count == 3 && backPw.SendPassword == "b2", "列表与默认发送密码能存能读: " + backPw.SendPassword);
            Check(backPw.DecryptCandidates[0] == "b2" && backPw.DecryptCandidates.Count == 3, "解密顺序: 发送那把排第一");
            File.WriteAllText(tmpPw, "{\"cryptoPassword\":\"老密码\",\"chatFolder\":\"x/y\"}");
            var legacyPw = AppSettings.Load();
            Check(legacyPw.CryptoPasswords.Count == 1 && legacyPw.SendPassword == "老密码", "老设置文件的单密码自动进列表");
            File.WriteAllText(tmpPw, "{\"cryptoPasswords\":[\"p1\",\"p2\"],\"sendPasswordIndex\":9,\"chatFolder\":\"x/y\"}");
            Check(AppSettings.Load().SendPasswordIndex == 1, "越界的默认下标被夹回列表范围");
            // 11.2: 空项代表"不加密(明文)"这一把, 要保留下来(相同内容只留一个)
            File.WriteAllText(tmpPw, "{\"cryptoPasswords\":[\"\",\"  \"],\"chatFolder\":\"x/y\"}");
            var blank = AppSettings.Load();
            Check(blank.CryptoPasswords.Count == 1 && blank.SendPassword == "", "空项被保留且去重(发送=不加密)");
            File.WriteAllText(tmpPw, "{\"cryptoPasswords\":[\"\",\"pw1\"],\"sendPasswordIndex\":0,\"chatFolder\":\"x/y\"}");
            var plainSend = AppSettings.Load();
            Check(plainSend.SendPassword == "" && plainSend.DecryptCandidates.Count == 1 && plainSend.DecryptCandidates[0] == "pw1",
                  "选中空项 = 明文发送, 同时仍能用 pw1 解密老消息");
            var plainCipher = new MessageCipher(plainSend.DecryptCandidates, 0, "x/y");
            Check(plainCipher.Enabled && plainCipher.PasswordCount == 1, "明文发送时不再加密, 但解密候选还在: " + plainCipher.PasswordCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", oldEnvPw);
            try { File.Delete(tmpPw); } catch { }
        }

        Console.WriteLine("=== M) 背景图 cover 摆放 (11.0) ===");
        var fitSame = CoverMath.Fit(1000, 500, 2000, 1000);
        Check(Math.Abs(fitSame.Scale - 0.5) < 1e-6 && Math.Abs(fitSame.OffsetX) < 1e-6, "宽高比一致时正好铺满");
        var fitTall = CoverMath.Fit(1000, 500, 500, 1000);
        Check(Math.Abs(fitTall.Scale - 2.0) < 1e-6 && Math.Abs(fitTall.OffsetY + 750) < 1e-6,
              "竖图放大到铺满, 上下对称溢出: scale=" + fitTall.Scale + " offsetY=" + fitTall.OffsetY);
        var fitWide = CoverMath.Fit(1000, 500, 4000, 1000);
        Check(Math.Abs(fitWide.Scale - 0.5) < 1e-6 && Math.Abs(fitWide.OffsetY) < 1e-6, "横图按宽度铺满: offsetY=" + fitWide.OffsetY);
        Check(CoverMath.Fit(1000, 500, 4000, 2000).Scale == 0.25, "按缩放比更大的一边算");
        var fitZero = CoverMath.Fit(0, 0, 100, 100);
        Check(fitZero.Scale == 1 && fitZero.OffsetX == 0, "尺寸为 0 时返回安全值(不崩)");

        Console.WriteLine();
        Console.WriteLine("==== 离线测试: " + _pass + " 通过, " + _fail + " 失败 ====");
        return _fail;
    }

    /// <summary>把信封里的密文翻一个 bit, 模拟传输/存储被改动。</summary>
    private static string Tamper(string envelope)
    {
        var raw = Convert.FromBase64String(envelope[MessageCrypto.Prefix.Length..]);
        raw[MessageCrypto.NonceLength] ^= 0x01;
        return MessageCrypto.Prefix + Convert.ToBase64String(raw);
    }
}
