using System.Text;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Emoji;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace TCPChat10.Rendering;

/// <summary>
/// Markdown -&gt; MdDocument。用 Markdig 解析(全套 CommonMark + 扩展语法), 再转成自己的简单模型。
/// 这里不碰任何 WinUI 类型, 所以单元测试能直接跑。
/// </summary>
public static class MarkdownParser
{
    /// <summary>
    /// UseAdvancedExtensions = CommonMark + 表格/任务列表/脚注/定义列表/上下标/删除线/自动链接/数学/
    /// 自定义容器/图/缩写/自动标识符/智能标点 等全部扩展(它只不含 Bootstrap、Emoji、SmartyPants、软换行当硬换行);
    /// 后面四个我们按聊天场景补上。
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseSmartyPants()
        .UseSoftlineBreakAsHardlineBreak()     // 聊天里敲回车就是换行, 不是"软换行"
        .Build();

    /// <summary>上次解析失败的原因(正常情况下是 null)。</summary>
    public static string? LastError { get; private set; }

    public static MdDocument Parse(string? text)
    {
        LastError = null;
        var doc = new MdDocument { Source = text ?? "" };
        if (string.IsNullOrEmpty(text)) return doc;

        try
        {
            var ctx = new Ctx(text);
            var md = Markdown.Parse(text, Pipeline);
            doc.Blocks = ParseBlocks(ctx, md);
            doc.PlainText = PlainText(doc).Trim();
            return doc;
        }
        catch (Exception ex)
        {
            // 解析器出意外也不能把消息吞掉: 退化成一段纯文本(错误留给测试/日志看)
            LastError = ex.GetType().Name + ": " + ex.Message;
            doc.Blocks = new List<MdBlock>
            {
                new MdParagraph { Inlines = { new MdText { Text = text } } },
            };
            doc.PlainText = text;
            return doc;
        }
    }

    /// <summary>纯文本(去掉标记), 给系统通知和"引用"用。</summary>
    public static string ToPlainText(string? text) => string.IsNullOrEmpty(text) ? "" : Parse(text).PlainText;

    /// <summary>
    /// 快速判断"这段文字里有没有 markdown 标记"。
    /// 绝大多数消息是纯文本, 走这条路可以完全不碰解析器(列表滚动时才不会卡)。
    /// </summary>
    public static bool HasMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // 裸网址也会被解析成链接(聊天里很常见), 所以 "://" 也算标记
        if (text.Contains("://", StringComparison.Ordinal)) return true;
        // ==高亮== 才算标记, 单独的等号("a = b")不算
        if (text.Contains("==", StringComparison.Ordinal)) return true;
        bool lineStart = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\': case '`': case '*': case '_': case '~': case '^':
                case '[': case ']': case '<': case '>': case '|': case '#':
                    return true;
                case '\n':
                    lineStart = true;
                    continue;
                case ' ':
                case '\t':
                    if (!lineStart) lineStart = false;
                    continue;
                case '-' when lineStart:
                case '+' when lineStart:
                    return true;
                case >= '0' and <= '9' when lineStart:
                    // "1. " / "1) " 有序列表
                    // "1. " / "1) " 才是有序列表; "1.5" 不算
                    int j = i;
                    while (j < text.Length && char.IsDigit(text[j])) j++;
                    if (j + 1 < text.Length && (text[j] == '.' || text[j] == ')')
                        && (text[j + 1] == ' ' || text[j + 1] == '\t')) return true;
                    lineStart = false;
                    continue;
                default:
                    if (c == ':' && i + 1 < text.Length && text[i + 1] == ':') return true;   // :emoji: / ::: 容器
                    lineStart = false;
                    continue;
            }
        }
        return false;
    }

    // ---------- 块 ----------

    private sealed class Ctx
    {
        public readonly string Source;
        public Ctx(string s) { Source = s; }
        public string Slice(SourceSpan span)
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start + span.Length > Source.Length) return "";
            return Source.Substring(span.Start, span.Length);
        }
    }

    private static List<MdBlock> ParseBlocks(Ctx ctx, ContainerBlock container)
    {
        var list = new List<MdBlock>();
        foreach (var b in container)
        {
            var blk = ParseBlock(ctx, b);
            if (blk != null) list.Add(blk);
        }
        return list;
    }

    private static MdBlock? ParseBlock(Ctx ctx, Block b) => b switch
    {
        // YAML 头(继承自 CodeBlock)不显示, 排最前面
        YamlFrontMatterBlock => null,
        HeadingBlock h => new MdHeading { Level = h.Level, Inlines = ParseInlines(ctx, h.Inline) },
        ParagraphBlock p => new MdParagraph { Inlines = ParseInlines(ctx, p.Inline) },

        // 这几个互相有继承关系(数学块 : 围栏块 : 代码块), 派生类必须排在基类前面
        MathBlock m => new MdMathBlock { Code = Code(m.Lines) },
        FencedCodeBlock f => new MdCodeBlock { Language = Clean(f.Info), Code = Code(f.Lines) },
        CodeBlock c => new MdCodeBlock { Language = null, Code = Code(c.Lines) },

        // AlertBlock 继承自 QuoteBlock, 也要排在前面
        AlertBlock a => new MdQuote { Alert = a.Kind.ToString(), Blocks = ParseBlocks(ctx, a) },
        QuoteBlock q => new MdQuote { Blocks = ParseBlocks(ctx, q) },

        ListBlock l => ParseList(ctx, l),
        Table t => ParseTable(ctx, t),
        FootnoteGroup fg => ParseFootnotes(ctx, fg),
        Footnote fn => new MdContainer { Label = fn.Label, Blocks = ParseBlocks(ctx, fn) },
        DefinitionList dl => ParseDefinitionList(ctx, dl),
        CustomContainer cc => new MdContainer { Label = Clean(cc.Info?.ToString()), Blocks = ParseBlocks(ctx, cc) },
        ThematicBreakBlock => new MdRule(),

        // 不显示的东西: 链接引用定义
        HtmlBlock hb => new MdHtmlBlock { Text = Code(hb.Lines).TrimEnd() },
        LinkReferenceDefinitionGroup => null,
        LinkReferenceDefinition => null,

        ContainerBlock cb => new MdContainer { Label = null, Blocks = ParseBlocks(ctx, cb) },
        LeafBlock leaf when leaf.Inline != null => new MdParagraph { Inlines = ParseInlines(ctx, leaf.Inline) },
        LeafBlock leaf => new MdParagraph { Inlines = { new MdText { Text = Code(leaf.Lines).TrimEnd() } } },
        _ => null,
    };

    private static MdList ParseList(Ctx ctx, ListBlock l)
    {
        var list = new MdList { Ordered = l.IsOrdered, Start = 1 };
        if (l.IsOrdered && int.TryParse(l.OrderedStart, out var start) && start > 0) list.Start = start;

        foreach (var item in l)
        {
            if (item is not ListItemBlock li) continue;
            var md = new MdListItem();

            // 任务列表: 勾选框藏在第一段的第一个行内元素里
            if (li.Count > 0 && li[0] is ParagraphBlock p0 && p0.Inline?.FirstChild is TaskList task)
            {
                md.IsTask = true;
                md.Checked = task.Checked;
            }
            md.Blocks = ParseBlocks(ctx, li);
            list.Items.Add(md);
        }
        return list;
    }

    private static MdTable ParseTable(Ctx ctx, Table t)
    {
        var table = new MdTable();
        foreach (var row in t)
        {
            if (row is not TableRow tr) continue;
            var cells = new List<MdTableCell>();
            int col = 0;
            foreach (var cell in tr)
            {
                if (cell is not TableCell tc) continue;
                var inlines = new List<MdInline>();
                foreach (var b in tc)
                    if (b is ParagraphBlock pb) inlines.AddRange(ParseInlines(ctx, pb.Inline));
                    else if (b is LeafBlock lb && lb.Inline != null) inlines.AddRange(ParseInlines(ctx, lb.Inline));

                cells.Add(new MdTableCell
                {
                    Inlines = inlines,
                    Align = ColumnAlign(t, col),
                    IsHeader = tr.IsHeader,
                });
                col++;
            }
            if (tr.IsHeader) table.Headers = cells;
            else table.Rows.Add(cells);
        }
        return table;
    }

    private static string ColumnAlign(Table t, int index)
    {
        try
        {
            var defs = t.ColumnDefinitions;
            if (defs != null && index >= 0 && index < defs.Count && defs[index] != null)
                return defs[index].Alignment?.ToString() ?? "Left";
        }
        catch { }
        return "Left";
    }

    private static MdFootnotes ParseFootnotes(Ctx ctx, FootnoteGroup g)
    {
        var notes = new MdFootnotes();
        foreach (var item in g)
            if (item is Footnote fn)
                notes.Items.Add(new MdFootnote
                {
                    Label = fn.Label ?? "",
                    Order = fn.Order,
                    Blocks = ParseBlocks(ctx, fn),
                });
        return notes;
    }

    private static MdDefinitionList ParseDefinitionList(Ctx ctx, DefinitionList dl)
    {
        var list = new MdDefinitionList();
        foreach (var item in dl)
        {
            if (item is not DefinitionItem di) continue;
            var entry = new MdDefinitionItem();
            foreach (var b in di)
            {
                switch (b)
                {
                    case DefinitionTerm term:
                        entry.Term.AddRange(ParseInlines(ctx, term.Inline));
                        break;
                    case ParagraphBlock p:
                        entry.Blocks.Add(new MdParagraph { Inlines = ParseInlines(ctx, p.Inline) });
                        break;
                    case ContainerBlock cb:
                        entry.Blocks.AddRange(ParseBlocks(ctx, cb));
                        break;
                    case LeafBlock lb when lb.Inline != null:
                        entry.Blocks.Add(new MdParagraph { Inlines = ParseInlines(ctx, lb.Inline) });
                        break;
                }
            }
            list.Items.Add(entry);
        }
        return list;
    }

    private static string Code(StringLineGroup lines) => lines.ToString() ?? "";

    private static string? Clean(string? s)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    // ---------- 行内 ----------

    private static List<MdInline> ParseInlines(Ctx ctx, ContainerInline? container)
    {
        var list = new List<MdInline>();
        if (container == null) return list;
        foreach (var inline in container) AddInline(ctx, list, inline);
        return list;
    }

    private static void AddInline(Ctx ctx, List<MdInline> list, Inline inline)
    {
        switch (inline)
        {
            // EmojiInline 继承自 LiteralInline, 必须排在它前面。
            // 注意: Match 里是短代码(:smile:), 真正的 emoji 字符在继承来的 Content 里
            case EmojiInline emoji:
                AddText(list, emoji.Content.ToString());
                break;

            case LiteralInline lit:
                AddText(list, lit.Content.ToString());
                break;

            case CodeInline code:
                list.Add(new MdCodeSpan { Text = code.Content });
                break;

            case LineBreakInline br:
                list.Add(new MdBreak { Hard = br.IsHard });
                break;

            case EmphasisInline em:
                list.Add(new MdStyle { Kind = StyleOf(em), Children = ParseInlines(ctx, em) });
                break;

            case TaskList:            // 勾选框由 MdListItem 画, 这里跳过
                break;

            case LinkInline link when link.IsImage:
                list.Add(new MdImage { Url = link.Url ?? "", Alt = InlineText(ParseInlines(ctx, link)) });
                break;

            case LinkInline link:
                list.Add(new MdLink
                {
                    Url = link.Url ?? "",
                    Title = link.Title,
                    Children = ParseInlines(ctx, link),
                });
                break;

            case AutolinkInline auto:
                list.Add(new MdLink { Url = auto.Url ?? "", Children = { new MdText { Text = auto.Url ?? "" } } });
                break;

            case MathInline math:
                list.Add(new MdMathSpan { Text = math.Content.ToString() });
                break;

            case FootnoteLink fn:
                list.Add(new MdFootnoteRef { Label = fn.Footnote?.Label ?? "", });
                break;

            case HtmlEntityInline ent:
                AddText(list, ent.Transcoded.ToString());
                break;

            case HtmlInline html:
                AddText(list, html.Tag);
                break;

            case DelimiterInline delim:   // 没配成对的标记符, 原样显示
                AddText(list, ctx.Slice(delim.Span));
                break;

            case ContainerInline nested:
                list.AddRange(ParseInlines(ctx, nested));
                break;

            case LeafInline leaf:
                AddText(list, ctx.Slice(leaf.Span));
                break;
        }
    }

    private static MdStyleKind StyleOf(EmphasisInline em) => em.DelimiterChar switch
    {
        '~' when em.DelimiterCount >= 2 => MdStyleKind.Strike,
        '~' => MdStyleKind.Sub,
        '^' => MdStyleKind.Sup,
        '=' => MdStyleKind.Mark,
        _ => em.DelimiterCount >= 2 ? MdStyleKind.Bold : MdStyleKind.Italic,
    };

    private static void AddText(List<MdInline> list, string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // 相邻的纯文本合并, 少建几个 Run
        if (list.Count > 0 && list[^1] is MdText last) last.Text += text;
        else list.Add(new MdText { Text = text });
    }

    private static string InlineText(List<MdInline> inlines)
    {
        var sb = new StringBuilder();
        AppendInlineText(sb, inlines);
        return sb.ToString().Trim();
    }

    // ---------- 纯文本 ----------

    private static void AppendInlineText(StringBuilder sb, List<MdInline> inlines)
    {
        foreach (var i in inlines)
        {
            switch (i)
            {
                case MdText t: sb.Append(t.Text); break;
                case MdCodeSpan c: sb.Append(c.Text); break;
                case MdStyle s: AppendInlineText(sb, s.Children); break;
                case MdLink l: AppendInlineText(sb, l.Children); break;
                case MdImage img: sb.Append(string.IsNullOrEmpty(img.Alt) ? img.Url : img.Alt); break;
                case MdBreak: sb.Append('\n'); break;
                case MdMathSpan m: sb.Append(m.Text); break;
                case MdFootnoteRef f: sb.Append('[').Append(f.Label).Append(']'); break;
            }
        }
    }

    private static string PlainText(MdDocument doc)
    {
        var sb = new StringBuilder();
        AppendBlocks(sb, doc.Blocks);
        return sb.ToString();
    }

    private static void AppendBlocks(StringBuilder sb, List<MdBlock> blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case MdHeading h: AppendInlineText(sb, h.Inlines); break;
                case MdParagraph p: AppendInlineText(sb, p.Inlines); break;
                case MdCodeBlock c: sb.Append(c.Code); break;
                case MdMathBlock m: sb.Append(m.Code); break;
                case MdHtmlBlock h: sb.Append(h.Text); break;
                case MdRule: break;
                case MdQuote q:
                    sb.Append("> ");
                    AppendBlocks(sb, q.Blocks);
                    break;
                case MdContainer c:
                    AppendBlocks(sb, c.Blocks);
                    break;
                case MdList l:
                    foreach (var item in l.Items)
                    {
                        sb.Append("• ");
                        AppendBlocks(sb, item.Blocks);
                        sb.Append('\n');
                    }
                    break;
                case MdTable t:
                    AppendInlineText(sb, t.Headers.SelectMany(c => c.Inlines).ToList());
                    foreach (var row in t.Rows)
                    {
                        sb.Append('\n');
                        foreach (var cell in row)
                        {
                            AppendInlineText(sb, cell.Inlines);
                            sb.Append('\t');
                        }
                    }
                    break;
                case MdDefinitionList dl:
                    foreach (var item in dl.Items)
                    {
                        AppendInlineText(sb, item.Term);
                        sb.Append(": ");
                        AppendBlocks(sb, item.Blocks);
                        sb.Append('\n');
                    }
                    break;
                case MdFootnotes notes:
                    foreach (var n in notes.Items)
                    {
                        sb.Append('[').Append(n.Order).Append("] ");
                        AppendBlocks(sb, n.Blocks);
                        sb.Append('\n');
                    }
                    break;
            }
            if (b is MdParagraph or MdHeading) sb.Append('\n');
        }
    }
}
