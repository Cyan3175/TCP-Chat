using System.Text;

namespace TCPChat10.Rendering;

/// <summary>
/// 进 Markdig 之前的文本预处理(11.4)。
///
/// 1. LaTeX 定界符归一化(见 MathDelimiters);
/// 2. 洛谷的 <c>::cute-table{tuack}</c>: 它写在表格前面一行, 直接交给 Markdig 会被并进表头
///    (第一行多出一个单元格), 所以在它后面补一个空行, 让它自己独立成段, 解析时再认回来。
/// </summary>
public static class MarkdownPreprocess
{
    public static string Apply(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return CuteTableBlankLine(MathDelimiters.Normalize(text));
    }

    /// <summary>是不是"洛谷的 cute-table 标记行"。</summary>
    public static bool IsCuteTableMarker(string? line) =>
        line != null && line.TrimStart().StartsWith("::cute-table", StringComparison.OrdinalIgnoreCase);

    private static string CuteTableBlankLine(string text)
    {
        if (text.IndexOf("::cute-table", StringComparison.OrdinalIgnoreCase) < 0) return text;

        var sb = new StringBuilder(text.Length + 16);
        int i = 0;
        bool atLineStart = true;
        while (i < text.Length)
        {
            // 逐行搬, 遇到独立的 ::cute-table{...} 行就在后面补一个空行
            int lineEnd = text.IndexOf('\n', i);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text[i..lineEnd];
            sb.Append(line);

            var trimmed = line.TrimEnd('\r');
            if (atLineStart && IsCuteTableMarker(trimmed) && trimmed.Trim().IndexOf(' ') < 0)
                sb.Append('\n');                       // 补空行

            if (lineEnd < text.Length) sb.Append('\n');
            atLineStart = true;
            i = lineEnd + 1;
        }
        return sb.ToString();
    }
}
