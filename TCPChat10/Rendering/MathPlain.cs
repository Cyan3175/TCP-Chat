using System.Text;

namespace TCPChat10.Rendering;

/// <summary>
/// 公式的纯文本形式(系统通知 / 引用预览用)。
/// 目标是"一眼能看懂", 不追求还原排版: \frac{a}{b} -&gt; (a)/(b), \sqrt{x} -&gt; √(x), 上下标写成 ^{} _{}。
/// </summary>
internal static class MathPlain
{
    public static string Render(MathNode node)
    {
        var sb = new StringBuilder();
        Append(sb, node);
        return sb.ToString().Trim();
    }

    private static void Append(StringBuilder sb, MathNode? node)
    {
        switch (node)
        {
            case null:
                return;

            case MathSym sym:
                sb.Append(sym.Text);
                return;

            case MathRow row:
                for (int i = 0; i < row.Items.Count; i++)
                {
                    var item = row.Items[i];
                    if (NeedsSpace(item, i > 0 ? row.Items[i - 1] : null)) sb.Append(' ');
                    Append(sb, item);
                }
                return;

            case MathSpace space:
                if (space.Em >= 0.3) sb.Append(' ');
                return;

            case MathFrac frac:
            {
                var top = Render(frac.Num);
                var bottom = Render(frac.Den);
                if (Simple(frac.Num) && Simple(frac.Den)) sb.Append(top).Append('/').Append(bottom);
                else sb.Append('(').Append(top).Append(")/(").Append(bottom).Append(')');
                return;
            }

            case MathSqrt sqrt:
                sb.Append('√');
                if (sqrt.Index != null) sb.Append("^(").Append(Render(sqrt.Index)).Append(") ");
                sb.Append('(').Append(Render(sqrt.Radicand)).Append(')');
                return;

            case MathScript script:
            {
                Append(sb, script.Base);
                if (script.Sub != null) AppendScript(sb, '_', script.Sub);
                if (script.Sup != null) AppendScript(sb, '^', script.Sup);
                return;
            }

            case MathDelim delim:
                if (!string.IsNullOrEmpty(delim.Left)) sb.Append(delim.Left);
                Append(sb, delim.Body);
                if (!string.IsNullOrEmpty(delim.Right)) sb.Append(delim.Right);
                return;

            case MathStyled styled:
                Append(sb, styled.Body);
                return;

            case MathFrame frame:
                Append(sb, frame.Body);
                return;

            case MathAccent accent:
                Append(sb, accent.Base);
                return;

            case MathEnv env:
            {
                for (int r = 0; r < env.Rows.Count; r++)
                {
                    if (r > 0) sb.Append(" ; ");
                    var cells = env.Rows[r];
                    for (int c = 0; c < cells.Count; c++)
                    {
                        if (c > 0) sb.Append(' ');
                        Append(sb, cells[c]);
                    }
                }
                return;
            }
        }
    }

    private static void AppendScript(StringBuilder sb, char mark, MathNode body)
    {
        var text = Render(body);
        sb.Append(mark);
        if (text.Length > 1) sb.Append('{').Append(text).Append('}');
        else sb.Append(text);
    }

    /// <summary>单项(一个符号/数字)在纯文本里可以直接写在分数线上下。</summary>
    private static bool Simple(MathNode node) => node switch
    {
        MathSym { Text.Length: 1 } => true,
        MathScript { Sub: null, Sup: null } => true,
        MathRow { Items.Count: 1 } row => Simple(row.Items[0]),
        _ => false,
    };

    /// <summary>二元运算符/关系符两侧留个空格, 纯文本才读得通。</summary>
    private static bool NeedsSpace(MathNode node, MathNode? prev)
    {
        if (prev == null) return false;
        var kind = KindOf(node);
        var before = KindOf(prev);
        return kind is MathKind.Bin or MathKind.Rel || before is MathKind.Bin or MathKind.Rel;
    }

    private static MathKind KindOf(MathNode node) => node switch
    {
        MathSym s => s.Kind,
        MathScript s => KindOf(s.Base),
        MathStyled s => KindOf(s.Body),
        _ => MathKind.Ord,
    };
}
