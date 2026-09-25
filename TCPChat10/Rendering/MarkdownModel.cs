namespace TCPChat10.Rendering;

/// <summary>
/// Markdown 的中间模型。只描述"有哪些东西", 不碰任何界面类型 ——
/// 这样解析逻辑可以在没有 UI 的单元测试里跑(10.7)。
/// </summary>
public sealed class MdDocument
{
    public List<MdBlock> Blocks { get; set; } = new();
    /// <summary>原始 markdown 文本(渲染不出来时兜底显示)。</summary>
    public string Source { get; set; } = "";
    /// <summary>纯文本版本(系统通知/引用用)。</summary>
    public string PlainText { get; set; } = "";
}

public abstract class MdBlock { }

public sealed class MdHeading : MdBlock
{
    public int Level { get; set; } = 1;
    public List<MdInline> Inlines { get; set; } = new();
}

public sealed class MdParagraph : MdBlock
{
    public List<MdInline> Inlines { get; set; } = new();
}

/// <summary>围栏/缩进代码块。</summary>
public sealed class MdCodeBlock : MdBlock
{
    public string? Language { get; set; }
    public string Code { get; set; } = "";

    /// <summary>11.4 (洛谷语法): 首行写 line-numbers -> 显示行号。</summary>
    public bool LineNumbers { get; set; }

    /// <summary>11.4 (洛谷语法): 首行写 lines=6-9 -> 高亮这些行(1 基, 可多段)。</summary>
    public List<(int Start, int End)> HighlightLines { get; set; } = new();

    public bool HasHighlight => HighlightLines.Count > 0;

    /// <summary>某一行(1 基)是否要高亮。</summary>
    public bool IsHighlighted(int line)
    {
        foreach (var (start, end) in HighlightLines)
            if (line >= start && line <= end) return true;
        return false;
    }
}

/// <summary>$$ ... $$ / \[ ... \] 数学块(11.4 起真的排版, 见 MathView)。</summary>
public sealed class MdMathBlock : MdBlock
{
    public string Code { get; set; } = "";
}

public sealed class MdList : MdBlock
{
    public bool Ordered { get; set; }
    public int Start { get; set; } = 1;
    public List<MdListItem> Items { get; set; } = new();
}

public sealed class MdListItem
{
    public bool IsTask { get; set; }
    public bool Checked { get; set; }
    public List<MdBlock> Blocks { get; set; } = new();
}

/// <summary>引用块。Alert 非空时是 GitHub 那种 &gt;[!NOTE] 提示块。</summary>
public sealed class MdQuote : MdBlock
{
    public string? Alert { get; set; }
    public List<MdBlock> Blocks { get; set; } = new();
}

/// <summary>
/// ::: 容器(11.4 起按洛谷那套语法解析)。
///
/// 洛谷支持的写法: <c>:::info[标题]{open}</c> / success / warning / error(折叠框)、
/// <c>:::align{center|right|left}</c>(对齐)、<c>:::epigraph[——作者]</c>(引言),
/// 以及普通自定义容器。嵌套时外层多写一个冒号(::::info 里面套 :::warning)。
/// </summary>
public sealed class MdContainer : MdBlock
{
    /// <summary>容器名(小写): info / success / warning / error / align / epigraph, 其余原样。</summary>
    public string? Label { get; set; }

    /// <summary>方括号里的标题(折叠框/引言的署名), 可以是行内元素(支持公式)。</summary>
    public List<MdInline> Title { get; set; } = new();

    /// <summary>花括号里的参数, 原样保留(例如 open、center、tuack)。</summary>
    public List<string> Args { get; set; } = new();

    public List<MdBlock> Blocks { get; set; } = new();

    public bool IsCallout => Label is "info" or "success" or "warning" or "error" or "tip" or "note" or "important" or "caution";
    public bool HasArg(string name) => Args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    public string? Align => Label == "align" ? Args.FirstOrDefault()?.ToLowerInvariant() : null;
    /// <summary>折叠框是否默认展开(洛谷: 加 {open})。</summary>
    public bool Open => HasArg("open");
}

public sealed class MdTable : MdBlock
{
    public List<MdTableCell> Headers { get; set; } = new();
    public List<List<MdTableCell>> Rows { get; set; } = new();
    public int Columns => Math.Max(Headers.Count, Rows.Count == 0 ? 0 : Rows.Max(r => r.Count));

    /// <summary>11.4 (洛谷语法): 表前一行写 ::cute-table{tuack} -> 更"竞赛"的表(全边框 + 居中)。</summary>
    public bool Tuack { get; set; }

    /// <summary>把所有行摊平成网格(表头是第一行)。</summary>
    public List<List<MdTableCell>> Grid()
    {
        var grid = new List<List<MdTableCell>>();
        if (Headers.Count > 0) grid.Add(Headers);
        grid.AddRange(Rows);
        return grid;
    }

    /// <summary>
    /// 11.4 (洛谷语法): 算好合并 —— 标记为 ^ 的单元格把上面那个"吃掉"(RowSpan+1),
    /// 标记为 &lt; 的把左边那个吃掉(ColSpan+1); 被吃掉的单元格 Hidden=true, 不画。
    /// </summary>
    public void ResolveSpans()
    {
        var grid = Grid();
        for (int r = 0; r < grid.Count; r++)
        {
            for (int c = 0; c < grid[r].Count; c++)
            {
                var cell = grid[r][c];
                cell.RowSpan = Math.Max(1, cell.RowSpan);
                cell.ColSpan = Math.Max(1, cell.ColSpan);

                if (cell.MergeUp)
                {
                    for (int up = r - 1; up >= 0; up--)
                    {
                        if (c >= grid[up].Count) continue;
                        var target = grid[up][c];
                        if (target.Hidden) continue;          // 它也被合并了, 继续往上找
                        target.RowSpan++;
                        break;
                    }
                }
                else if (cell.MergeLeft)
                {
                    for (int left = c - 1; left >= 0; left--)
                    {
                        var target = grid[r][left];
                        if (target.Hidden) break;      // 左边紧邻的格子已经被并掉了, 这种写法没法再往左并
                        target.ColSpan++;
                        break;
                    }
                }
            }
        }
    }
}

public sealed class MdTableCell
{
    public List<MdInline> Inlines { get; set; } = new();
    /// <summary>Left / Center / Right。</summary>
    public string Align { get; set; } = "Left";
    public bool IsHeader { get; set; }

    /// <summary>11.4 (洛谷语法): 单元格内容就是一个 ^ -> 与上面合并。</summary>
    public bool MergeUp { get; set; }
    /// <summary>11.4 (洛谷语法): 单元格内容就是一个 &lt; -> 与左边合并。</summary>
    public bool MergeLeft { get; set; }

    /// <summary>被合并掉(不显示)的单元格。</summary>
    public bool Hidden => MergeUp || MergeLeft;

    /// <summary>纵向占几行(算完合并以后填)。</summary>
    public int RowSpan { get; set; } = 1;
    /// <summary>横向占几列(算完合并以后填)。</summary>
    public int ColSpan { get; set; } = 1;
}

/// <summary>--- 分割线。</summary>
public sealed class MdRule : MdBlock { }

/// <summary>HTML 块: 不执行, 原样显示。</summary>
public sealed class MdHtmlBlock : MdBlock
{
    public string Text { get; set; } = "";
}

/// <summary>定义列表: 词条 + 解释。</summary>
public sealed class MdDefinitionList : MdBlock
{
    public List<MdDefinitionItem> Items { get; set; } = new();
}

public sealed class MdDefinitionItem
{
    public List<MdInline> Term { get; set; } = new();
    public List<MdBlock> Blocks { get; set; } = new();
}

/// <summary>文末的脚注区。</summary>
public sealed class MdFootnotes : MdBlock
{
    public List<MdFootnote> Items { get; set; } = new();
}

public sealed class MdFootnote
{
    public string Label { get; set; } = "";
    public int Order { get; set; }
    public List<MdBlock> Blocks { get; set; } = new();
}

// ---------- 行内 ----------

public abstract class MdInline { }

public sealed class MdText : MdInline
{
    public string Text { get; set; } = "";
}

/// <summary>行内 `代码`。</summary>
public sealed class MdCodeSpan : MdInline
{
    public string Text { get; set; } = "";
}

public enum MdStyleKind { Bold, Italic, Strike, Sub, Sup, Mark }

public sealed class MdStyle : MdInline
{
    public MdStyleKind Kind { get; set; }
    public List<MdInline> Children { get; set; } = new();
}

public sealed class MdLink : MdInline
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public List<MdInline> Children { get; set; } = new();
}

public sealed class MdImage : MdInline
{
    public string Url { get; set; } = "";
    public string Alt { get; set; } = "";
}

public sealed class MdBreak : MdInline
{
    public bool Hard { get; set; }
}

/// <summary>行内 $x^2$ / \(x^2\) 数学(11.4 起真的排版, 见 MathView)。</summary>
public sealed class MdMathSpan : MdInline
{
    public string Text { get; set; } = "";
}

/// <summary>脚注引用 [^1]。</summary>
public sealed class MdFootnoteRef : MdInline
{
    public string Label { get; set; } = "";
}
