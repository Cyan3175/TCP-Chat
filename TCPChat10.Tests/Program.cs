using System.Text;
using TCPChat10.Services;

Console.OutputEncoding = Encoding.UTF8;

// 维护模式: dotnet run -- clean <远端目录> [--rmdir]
// 用来清空自测期间写在服务器上的临时消息(中文路径走 C# 字符串, 避免 PS 5.1 的编码坑)
if (args.Length >= 2 && args[0] == "mkdir")
{
    var folder = args[1].Trim('/');
    using var dav0 = new WebDavClient("https://dev.zhaohans.cn");
    await dav0.EnsureCollectionAsync(folder);
    var chk = await dav0.PropFindAsync(folder, 0);
    Console.WriteLine(chk.Count > 0 ? "目录已就绪: " + folder : "创建失败: " + folder);
    return chk.Count > 0 ? 0 : 1;
}

if (args.Length >= 2 && args[0] == "clean")
{
    var folder = args[1].Trim('/');
    bool rmdir = args.Contains("--rmdir");
    using var dav = new WebDavClient("https://dev.zhaohans.cn");

    var entries = await dav.PropFindAsync(folder, 1);
    int n = 0;
    foreach (var e in entries.Where(x => !x.IsCollection))
    {
        var p = WebDavClient.Combine(folder, e.Name);
        if (await dav.DeleteAsync(p)) { n++; Console.WriteLine("  已删除 " + p); }
        else Console.WriteLine("  删除失败 " + p);
    }
    Console.WriteLine($"共删除 {n} 个文件");

    if (rmdir && await dav.DeleteAsync(folder)) Console.WriteLine("已删除目录 " + folder);
    return 0;
}

int rc = await ChatTest.RunAsync();
return rc;