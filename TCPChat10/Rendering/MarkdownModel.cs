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
}

/// <summary>$$ ... $$ 数学块(不做 LaTeX 排版, 原样等宽显示)。</summary>
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

/// <summary>::: 自定义容器(Info 是容器名)。</summary>
public sealed class MdContainer : MdBlock
{
    public string? Label { get; set; }
    public List<MdBlock> Blocks { get; set; } = new();
}

public sealed class MdTable : MdBlock
{
    public List<MdTableCell> Headers { get; set; } = new();
    public List<List<MdTableCell>> Rows { get; set; } = new();
    public int Columns => Math.Max(Headers.Count, Rows.Count == 0 ? 0 : Rows.Max(r => r.Count));
}

public sealed class MdTableCell
{
    public List<MdInline> Inlines { get; set; } = new();
    /// <summary>Left / Center / Right。</summary>
    public string Align { get; set; } = "Left";
    public bool IsHeader { get; set; }
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

/// <summary>行内 $x^2$ 数学。</summary>
public sealed class MdMathSpan : MdInline
{
    public string Text { get; set; } = "";
}

/// <summary>脚注引用 [^1]。</summary>
public sealed class MdFootnoteRef : MdInline
{
    public string Label { get; set; } = "";
}
