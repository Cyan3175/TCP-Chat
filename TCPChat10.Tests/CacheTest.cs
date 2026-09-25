using TCPChat10.Services;

/// <summary>离线单元测试: 本地消息缓存(11.5) —— 存取、目录隔离、对方撤回的清理。</summary>
public static class CacheTest
{
    static int _pass, _fail;
    static void Check(bool ok, string name)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name); }
    }

    public static int Run()
    {
        Console.WriteLine("=== X) 本地消息缓存 (11.5) ===");

        var old = Environment.GetEnvironmentVariable("TCPCHAT10_TEST_DATADIR");
        var dir = Path.Combine(Path.GetTempPath(), "tcpchat_cache_" + Guid.NewGuid().ToString("N")[..6]);
        Environment.SetEnvironmentVariable("TCPCHAT10_TEST_DATADIR", dir);
        try
        {
            // 文件名里的时间戳
            var t = MessageCache.TimeOf("msg_1790300000000_abcdef12.json");
            Check(t != null && t.Value.ToUnixTimeMilliseconds() == 1790300000000, "从文件名取时间: " + t);
            Check(MessageCache.TimeOf("msg_bad.json") == null, "名字不规范返回 null");
            Check(MessageCache.TimeOf("att_123_x.png") == null, "附件文件名不当消息");

            // 存 -> 读
            var cache = MessageCache.Load("nw/目录A");
            cache.Put("msg_1790300000000_aaaaaaaa.json", "{\"text\":\"一\"}");
            cache.Put("msg_1790300001000_bbbbbbbb.json", "{\"text\":\"二\"}");
            Check(cache.Count == 2 && cache.Dirty, "放两条进缓存: " + cache.Count);
            cache.Save();
            Check(!cache.Dirty, "存盘后不再是脏的");

            var back = MessageCache.Load("nw/目录A");
            Check(back.Count == 2, "重新读回来: " + back.Count);
            Check(back.Names()[0].EndsWith("aaaaaaaa.json"), "按文件名(时间)排序");
            Check(back.Get("msg_1790300001000_bbbbbbbb.json")!.Contains("二"), "内容原样: " + back.Get("msg_1790300001000_bbbbbbbb.json"));

            // 换目录: 不能串消息
            var other = MessageCache.Load("nw/目录B");
            Check(other.Count == 0, "换聊天目录后缓存是空的(不串消息)");

            // 对方撤回
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            var gone = back.PruneMissing(new[] { "msg_1790300000000_aaaaaaaa.json" }, cutoff);
            Check(gone.Count == 1 && gone[0].Contains("bbbbbbbb"), "服务器上没有的 -> 判定为撤回并清掉: " + gone.Count);
            Check(back.Count == 1, "缓存里只剩存在的那个: " + back.Count);
            Check(back.PruneMissing(Array.Empty<string>(), cutoff).Count == 0, "服务器返回空列表时不动手(免得误清)");

            // 窗口外的老消息不算撤回
            var old2 = MessageCache.Load("nw/目录C");
            old2.Put("msg_1600000000000_cccccccc.json", "{\"text\":\"很老\"}");
            old2.Put("msg_1790300002000_dddddddd.json", "{\"text\":\"新\"}");
            var gone2 = old2.PruneMissing(new[] { "msg_1790300002000_dddddddd.json" }, cutoff);
            Check(gone2.Count == 0 && old2.Count == 2, "窗口外的老消息不算撤回(留着)");

            // 自己撤回
            back.Remove("msg_1790300000000_aaaaaaaa.json");
            Check(back.Count == 0, "自己撤回后缓存里也没了");

            Check(MessageCache.PathFor("a").EndsWith(".json") && MessageCache.PathFor("a") != MessageCache.PathFor("b"),
                  "不同目录用不同缓存文件");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TCPCHAT10_TEST_DATADIR", old);
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("本地缓存部分: 通过 " + _pass + " 项, 失败 " + _fail + " 项");
        return _fail;
    }
}
