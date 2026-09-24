using System.Text.Json;
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

        Check(AppSettings.DefaultChatFolder == "nw集训/学生资料临存", "新默认聊天目录就是共享目录本身, 不再带 聊天 子文件夹");
        Check(AppSettings.FilePath.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase), "设置文件名: " + AppSettings.FilePath);
        Check(string.Equals(Path.GetDirectoryName(AppSettings.FilePath), AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
              "设置文件默认直接放在程序目录, 不再单独建文件夹");

        Console.WriteLine("=== F3) 液态玻璃设置 (10.3) ===");
        Check(new AppSettings().GlassEffect, "默认开液态玻璃");
        Check(new AppSettings().GlassQuality == GlassScale.Default, "默认质量 = " + GlassScale.Default);
        Check(GlassScale.Clamp(0) == GlassScale.Min && GlassScale.Clamp(99) == GlassScale.Max, "质量档位夹在 1..10");
        Check(Math.Abs(GlassScale.Strength(GlassScale.Min)) < 1e-9 && Math.Abs(GlassScale.Strength(GlassScale.Max) - 1) < 1e-9,
              "强度: 1 档 = 0, 10 档 = 1");
        Check(GlassScale.Strength(3) < GlassScale.Strength(7), "强度随档位单调上升");
        Check(GlassScale.FrostPercent(GlassScale.Min) < GlassScale.FrostPercent(GlassScale.Max) &&
              GlassScale.FrostPercent(GlassScale.Min) >= 40 && GlassScale.FrostPercent(GlassScale.Max) <= 100,
              $"磨砂浓度 {GlassScale.FrostPercent(GlassScale.Min)}% ~ {GlassScale.FrostPercent(GlassScale.Max)}%");

        var tmpDir3 = Path.Combine(Path.GetTempPath(), "tcpchat10_glass_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmpDir3);
        var tmpFile3 = Path.Combine(tmpDir3, "settings.json");
        var oldEnv3 = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS");
        try
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", tmpFile3);
            new AppSettings { GlassEffect = false, GlassQuality = 3 }.Save();
            var g1 = AppSettings.Load();
            Check(!g1.GlassEffect && g1.GlassQuality == 3, "玻璃开关与质量读回来: off / 3");

            File.WriteAllText(tmpFile3, "{\"glassEffect\":true,\"glassQuality\":42}");
            var g2 = AppSettings.Load();
            Check(g2.GlassEffect && g2.GlassQuality == GlassScale.Max, "超范围的质量在读取时夹到 " + GlassScale.Max);

            File.WriteAllText(tmpFile3, "{\"glassEffect\":false,\"glassQuality\":-5}");
            var g3 = AppSettings.Load();
            Check(!g3.GlassEffect && g3.GlassQuality == GlassScale.Min, "负数的质量在读取时夹到 " + GlassScale.Min);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_SETTINGS", oldEnv3);
            try { Directory.Delete(tmpDir3, true); } catch { }
        }

        Console.WriteLine("=== G) 系统字体列表 ===");
        var fonts = FontList.GetInstalledFamilies();
        Check(fonts.Count > 10, "枚举到 " + fonts.Count + " 个字体族");
        Check(fonts.All(f => f.Length > 0 && f[0] != '@'), "列表里没有空名/竖排(@)变体");
        Check(fonts.Distinct(StringComparer.OrdinalIgnoreCase).Count() == fonts.Count, "字体名不重复");
        Check(fonts.Contains("Arial", StringComparer.OrdinalIgnoreCase), "包含常见字体 Arial");
        Console.WriteLine("      前几个: " + string.Join(" / ", fonts.Take(8)));
        Check(FontList.Exists("Arial"), "FontList.Exists(Arial) = true");
        Check(!FontList.Exists("这个字体肯定不存在12345"), "FontList.Exists(不存在的字体) = false");
        Check(!FontList.Exists(""), "空字体名视为不存在(回退系统默认)");

        Console.WriteLine("=== H) 兼容 10.0 的老消息 ===");
        const string legacyAttach = "{\"v\":1,\"id\":\"1789800000000_aaaaaaaa\",\"from\":\"张三\"," +
                                    "\"time\":\"2026-09-19T08:20:52+00:00\",\"text\":\"\"," +
                                    "\"attach\":{\"name\":\"图.png\",\"path\":\"x/att_1_图.png\",\"size\":4855,\"kind\":2}}";
        var legacy = JsonSerializer.Deserialize<TCPChat10.Models.ChatMessage>(legacyAttach);
        Check(legacy != null && legacy.Enc == null, "10.0 的附件字段被安全忽略, 不抛异常");
        Check(legacy != null && string.IsNullOrWhiteSpace(legacy.Text) && string.IsNullOrWhiteSpace(legacy.Quote),
              "纯附件的老消息 = \"没有正文\", 同步时会被跳过");

        const string legacyText = "{\"v\":1,\"id\":\"1789800000001_bbbbbbbb\",\"from\":\"李四\"," +
                                  "\"time\":\"2026-09-19T08:21:00+00:00\",\"text\":\"老版本的明文消息\",\"quote\":\"被引用的那句\"}";
        var lt = JsonSerializer.Deserialize<TCPChat10.Models.ChatMessage>(legacyText);
        Check(lt != null && lt.Text == "老版本的明文消息" && lt.Quote == "被引用的那句" && !lt.IsEncrypted,
              "10.0 的明文消息照常读取");

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
