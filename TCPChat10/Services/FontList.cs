using System.Runtime.InteropServices;

namespace TCPChat10.Services;

/// <summary>
/// 枚举系统已安装的字体族(供设置里的"字体"下拉列表使用)。
/// 直接调 GDI 的 EnumFontFamiliesEx, 不引入 System.Drawing / Win2D 等额外依赖。
/// </summary>
public static class FontList
{
    /// <summary>枚举失败时的兜底列表(正常情况下用不到)。</summary>
    private static readonly string[] Fallback =
    {
        "Segoe UI", "Microsoft YaHei UI", "微软雅黑", "宋体", "黑体", "楷体",
        "Arial", "Times New Roman", "Consolas",
    };

    private static List<string>? _cache;      // 枚举一次就够(GDI 枚举要几十毫秒)

    /// <summary>返回排序去重后的字体族名字列表(如 "Microsoft YaHei UI"、"Arial")。</summary>
    public static IReadOnlyList<string> GetInstalledFamilies()
    {
        if (_cache != null) return _cache;

        var names = new SortedSet<string>(StringComparer.CurrentCulture);
        try
        {
            EnumFamilies(names);
        }
        catch { /* 拿不到就用兜底列表 */ }

        if (names.Count == 0)
        {
            foreach (var f in Fallback) names.Add(f);
        }
        _cache = names.ToList();
        return _cache;
    }

    /// <summary>字体是否存在于系统字体列表里(用于校验设置里存下来的名字)。</summary>
    public static bool Exists(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return false;
        foreach (var f in GetInstalledFamilies())
            if (string.Equals(f, family, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void EnumFamilies(SortedSet<string> names)
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return;
        try
        {
            var lf = new LOGFONT
            {
                lfCharSet = 1,              // DEFAULT_CHARSET: 各种字符集的字体都枚举到
                lfFaceName = "",
            };
            EnumFontFamExProc proc = (lpelfe, _, _, _) =>
            {
                var logfont = Marshal.PtrToStructure<LOGFONT>(lpelfe);
                var name = logfont.lfFaceName;
                // '@' 开头的是竖排变体, 与横排字体重名, 不列出来
                if (!string.IsNullOrWhiteSpace(name) && name[0] != '@')
                    names.Add(name.Trim());
                return 1;                   // 1 = 继续枚举
            };
            // 委托必须保持引用直到调用结束, 否则会被 GC 回收导致回调崩溃
            GC.KeepAlive(proc);
            EnumFontFamiliesEx(hdc, ref lf, proc, IntPtr.Zero, 0);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    private delegate int EnumFontFamExProc(IntPtr lpelfe, IntPtr lpntme, uint fontType, IntPtr lParam);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(IntPtr hdc, ref LOGFONT lpLogfont,
        EnumFontFamExProc lpEnumFontFamExProc, IntPtr lParam, uint dwFlags);
}
