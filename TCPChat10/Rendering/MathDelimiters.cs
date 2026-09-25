using System.Text;

namespace TCPChat10.Rendering;

/// <summary>
/// 把 LaTeX 的定界符 \[ ... \] / \( ... \) 换成 Markdig 认识的 $$ ... $$ / $ ... $。
///
/// 起因: Markdig 的数学扩展只认美元符号, 直接写 \[ \] 会被当成"转义的方括号"混进正文,
/// 公式里的 _ ^ { } 还会被当成 markdown 标记, 整段公式就散了(\frac{b}{4a} 会变成 \frac{4a})。
///
/// 顺手也把"跨行的 $$ ... $$"摆成块级: 这种写法前面只要有别的内容, Markdig 就不认它是公式。
/// 围栏代码块与行内代码里的内容一律不动 —— 那里贴的是 LaTeX 源码, 本就该原样显示。
/// 纯字符串处理, 离线测试能直接跑。
/// </summary>
public static class MathDelimiters
{
    private const char Tick = (char)96;          // 反引号

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // 只有真的可能带公式时才走这一遍
        if (text.IndexOf("\\[", StringComparison.Ordinal) < 0 &&
            text.IndexOf("\\(", StringComparison.Ordinal) < 0 &&
            text.IndexOf("$$", StringComparison.Ordinal) < 0) return text;

        var sb = new StringBuilder(text.Length + 16);
        bool inFence = false;
        int fenceRun = 0;
        char fenceChar = ' ';
        bool atLineStart = true;
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            // ---- 行首: 看看是不是围栏代码块的开头/结尾 ----
            if (atLineStart)
            {
                atLineStart = false;
                int j = i;
                while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
                if (j < text.Length && (text[j] == Tick || text[j] == '~'))
                {
                    char m = text[j];
                    int run = 0;
                    while (j + run < text.Length && text[j + run] == m) run++;
                    if (run >= 3)
                    {
                        if (!inFence) { inFence = true; fenceChar = m; fenceRun = run; }
                        else if (m == fenceChar && run >= fenceRun) inFence = false;
                    }
                }
            }

            if (inFence)
            {
                sb.Append(c);
                if (c == '\n' || c == '\r') atLineStart = true;
                i++;
                continue;
            }

            // ---- 行内代码: 整段照抄 ----
            if (c == Tick)
            {
                int run = 0;
                while (i + run < text.Length && text[i + run] == Tick) run++;
                var ticks = new string(Tick, run);
                int close = text.IndexOf(ticks, i + run, StringComparison.Ordinal);
                if (close >= 0)
                {
                    sb.Append(text, i, close + run - i);
                    i = close + run;
                    continue;
                }
            }

            // ---- 跨行的 $$ ... $$: 摆成块级, 否则 Markdig 只当成普通文字(公式就成了乱码) ----
            if (c == '$')
            {
                int dollars = 0;
                while (i + dollars < text.Length && text[i + dollars] == '$') dollars++;
                if (dollars >= 2)
                {
                    int close = FindDollarClose(text, i + dollars);
                    if (close > 0)
                    {
                        var body = text[(i + dollars)..close].Trim();
                        // "规范形式" = 开头的 $$ 独占一行(后面没别的内容)、收尾的 $$ 也独占一行;
                        // 只要不满足(Markdig 只认这一种), 就重排成规范形式
                        bool canonical = OwnLine(sb) && RestOfLineBlank(text, i + dollars) &&
                                         AtLineStart(text, close) && RestOfLineBlank(text, close + dollars);
                        if ((body.IndexOf('\n') >= 0 || body.IndexOf('\r') >= 0) && !canonical)
                        {
                            if (!OwnLine(sb)) sb.Append('\n');
                            sb.Append("$$\n").Append(body).Append("\n$$");
                            if (!RestOfLineBlank(text, close + dollars)) sb.Append('\n');
                            i = close + dollars;
                            continue;
                        }
                    }
                }
            }

            // ---- \[ ... \] 与 \( ... \) ----
            if (c == '\\' && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (n == '[' || n == '(')
                {
                    char closeChar = n == '[' ? ']' : ')';
                    int end = FindClose(text, i + 2, closeChar);
                    if (end > 0)
                    {
                        var inner = text[(i + 2)..end].Trim();
                        bool multiline = inner.IndexOf('\n') >= 0 || inner.IndexOf('\r') >= 0;
                        bool display = n == '[' || multiline;

                        if (display && multiline)
                        {
                            // 跨行的公式(只有一种可能: \( \) 里带了换行): 行内 $$ 装不下换行, 只能当块级
                            if (!OwnLine(sb)) sb.Append('\n');
                            sb.Append("$$\n").Append(inner).Append("\n$$");
                            if (!RestOfLineBlank(text, end + 2)) sb.Append('\n');
                        }
                        else if (display && OwnLine(sb) && RestOfLineBlank(text, end + 2))
                            sb.Append("$$\n").Append(inner).Append("\n$$");
                        else if (display)
                            sb.Append("$$").Append(inner).Append("$$");
                        else
                            sb.Append('$').Append(inner).Append('$');

                        i = end + 2;
                        continue;
                    }
                }
            }

            sb.Append(c);
            if (c == '\n' || c == '\r') atLineStart = true;
            i++;
        }

        return sb.ToString();
    }

    /// <summary>找配对的 \] / \)(跳过 \\ 转义), 返回那个反斜杠的位置; 找不到返回 -1。</summary>
    private static int FindClose(string text, int from, char closeChar)
    {
        for (int i = from; i < text.Length - 1; i++)
        {
            if (text[i] != '\\') continue;
            char n = text[i + 1];
            if (n == '\\') { i++; continue; }         // \\ 是"公式里换行", 跳过这两个字符
            if (n == closeChar) return i;
        }
        return -1;
    }

    /// <summary>找配对的 $$(>=2 个美元符号), 返回它的起始位置; 找不到返回 -1。</summary>
    private static int FindDollarClose(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] != '$') continue;
            int run = 0;
            while (i + run < text.Length && text[i + run] == '$') run++;
            if (run >= 2) return i;
        }
        return -1;
    }

    /// <summary>text[index] 前面(同一行内)除了空白没有别的东西。</summary>
    private static bool AtLineStart(string text, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '\n' || c == '\r') return true;      // 只写 \r 的消息(从别处粘来的)也要认
            if (c != ' ' && c != '\t') return false;
        }
        return true;
    }

    /// <summary>当前这一行(已经写出去的部分)除了空白什么都没有。</summary>
    private static bool OwnLine(StringBuilder sb)
    {
        for (int i = sb.Length - 1; i >= 0; i--)
        {
            char c = sb[i];
            if (c == '\n' || c == '\r') return true;
            if (c != ' ' && c != '\t') return false;
        }
        return true;
    }

    /// <summary>后面到行尾为止全是空白。</summary>
    private static bool RestOfLineBlank(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r') return true;
            if (c != ' ' && c != '\t') return false;
        }
        return true;
    }
}
