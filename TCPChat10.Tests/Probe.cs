using System.Text;
using TCPChat10.Services;

public static class Probe
{
    public static async Task<int> RunAsync(string path)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var dav = new WebDavClient("https://dev.zhaohans.cn");
        var entries = await dav.PropFindAsync(path, 1);
        Console.WriteLine($"=== {path} ({entries.Count} 项) ===");
        foreach (var e in entries.OrderByDescending(x => x.IsCollection).ThenBy(x => x.Name))
            Console.WriteLine($"  {(e.IsCollection ? "[目录]" : "      ")} {e.Name}  {(e.IsCollection ? "" : e.Length + "B")}");
        return 0;
    }
}
