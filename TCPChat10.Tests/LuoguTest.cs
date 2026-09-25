using System.Text;
using TCPChat10.Rendering;

/// <summary>离线单元测试: 洛谷那套 markdown 扩展语法(11.4)。</summary>
public static class LuoguTest
{
    static int _pass, _fail;
    static void Check(bool ok, string name)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name); }
    }

    static readonly string Fence = new string((char)96, 3);

    static MdContainer Container(string md) =>
        MarkdownParser.Parse(md).Blocks.OfType<MdContainer>().First();

    static IEnumerable<string> Flatten(List<MdInline> inlines)
    {
        foreach (var i in inlines)
        {
            switch (i)
            {
                case MdText t: yield return t.Text; break;
                case MdMathSpan m: yield return m.Text; break;
                case MdStyle s: foreach (var x in Flatten(s.Children)) yield return x; break;
                case MdLink l: foreach (var x in Flatten(l.Children)) yield return x; break;
            }
        }
    }
    static string TextOf(List<MdInline> inlines) => string.Concat(Flatten(inlines));

    public static int Run()
    {
        Console.WriteLine("=== T) 洛谷扩展语法: 折叠框 / 对齐 / 引言 (11.4) ===");

        var info = Container(":::info[我是标题]\n内容\n:::\n");
        Check(info.Label == "info" && info.IsCallout, ":::info 解析成折叠框: " + info.Label);
        Check(TextOf(info.Title) == "我是标题", "方括号里的标题: " + TextOf(info.Title));
        Check(!info.Open, "默认是收起的(和洛谷一致)");

        var open = Container("::::info[标题]{open}\n内容\n::::\n");
        Check(open.Open, "{open} 参数 -> 默认展开");
        Check(open.Label == "info", "{参数} 不会混进容器名: " + open.Label);

        Check(Container(":::success[x]\n:::") .Label == "success", ":::success");
        Check(Container(":::warning[x]\n:::") .Label == "warning", ":::warning");
        Check(Container(":::error[x]\n:::") .Label == "error", ":::error");

        var outer = MarkdownParser.Parse(":::::warning[外]\n::::warning[内]\n内容\n::::\n:::::\n")
                                  .Blocks.OfType<MdContainer>().First();
        Check(outer.Blocks.OfType<MdContainer>().FirstOrDefault()?.Label == "warning", "冒号更多 = 嵌套(外层套内层)");

        Check(Container(":::align{center}\n内容\n:::\n").Align == "center", ":::align{center} 居中");
        Check(Container(":::align{right}\n内容\n:::\n").Align == "right", ":::align{right} 居右");

        var epi = Container(":::epigraph[——otto]\n大家好啊\n:::\n");
        Check(epi.Label == "epigraph" && TextOf(epi.Title) == "——otto", "引言 + 署名: " + TextOf(epi.Title));

        Console.WriteLine("=== U) 洛谷扩展语法: 代码块参数 (11.4) ===");
        var code = MarkdownParser.Parse(Fence + "cpp line-numbers lines=6-9\nint main(){}\n" + Fence)
                                .Blocks.OfType<MdCodeBlock>().First();
        Check(code.Language == "cpp", "语言还是 cpp: " + code.Language);
        Check(code.LineNumbers, "line-numbers -> 显示行号");
        Check(code.HighlightLines.Count == 1 && code.HighlightLines[0] == (6, 9), "lines=6-9 解析成范围");
        Check(code.IsHighlighted(7) && !code.IsHighlighted(5), "第 7 行要高亮, 第 5 行不高亮");

        var multi = MarkdownParser.Parse(Fence + "cpp lines=1,3-5\ncode\n" + Fence)
                                 .Blocks.OfType<MdCodeBlock>().First();
        Check(multi.HighlightLines.Count == 2 && multi.IsHighlighted(1) && multi.IsHighlighted(4) && !multi.IsHighlighted(2),
              "lines=1,3-5 多段高亮");
        Check(!multi.LineNumbers, "没写 line-numbers 就不显示行号");

        var plain = MarkdownParser.Parse(Fence + "cpp\ncode\n" + Fence).Blocks.OfType<MdCodeBlock>().First();
        Check(!plain.LineNumbers && !plain.HasHighlight && plain.Language == "cpp", "普通代码块不受影响");

        Console.WriteLine("=== V) 洛谷扩展语法: 表格合并 / Tuack (11.4) ===");
        var up = MarkdownParser.Parse("| 编号 | 范围 |\n| :-: | :-: |\n| 1 | 1e5 |\n| 2 | ^ |\n")
                                .Blocks.OfType<MdTable>().First();
        Check(up.Rows.Count == 2, "表格两行数据: " + up.Rows.Count);
        Check(up.Rows[0][1].RowSpan == 2, "向上合并: 上一格 RowSpan=2");
        Check(up.Rows[1][1].Hidden, "写 ^ 的格子自己不画");

        var left = MarkdownParser.Parse("| 编号 | 范围 | 备注 |\n| :-: | :-: | :-: |\n| 1 | 1e5 | 跨列 |\n| 2 | 3e5 | < |\n")
                                 .Blocks.OfType<MdTable>().First();
        Check(left.Rows[1][2].Hidden, "写 < 的格子自己不画");
        Check(left.Rows[1][1].ColSpan == 2, "向左合并: 左格 ColSpan=2");

        var notMerge = MarkdownParser.Parse("| a | b |\n| :-: | :-: |\n| ^2 | <3 |\n")
                                    .Blocks.OfType<MdTable>().First();
        Check(!notMerge.Rows[0][0].Hidden && !notMerge.Rows[0][1].Hidden, "不是「只有一个 ^」的格子不合并");

        var tuackDoc = MarkdownParser.Parse("::cute-table{tuack}\n| a | b |\n| :-: | :-: |\n| 1 | 2 |\n");
        var tuack = tuackDoc.Blocks.OfType<MdTable>().FirstOrDefault();
        Check(tuack != null && tuack.Tuack, "::cute-table{tuack} 认出来了");
        Check(tuack!.Columns == 2, "标记行不会被并进表头: " + tuack.Columns + " 列");
        Check(!tuackDoc.Blocks.Any(b => b is MdParagraph p && TextOf(p.Inlines).Contains("cute-table")), "标记行本身不显示");

        Console.WriteLine("=== W) 洛谷扩展语法: 原有语法不受影响 (11.4) ===");
        // 洛谷不解析原始 HTML: 标签按普通文字显示出来
        Check(MarkdownParser.ToPlainText("<b>粗</b>").Contains("<b>"), "原始 HTML 不解析(和洛谷一致)");
        Check(!MarkdownParser.ToPlainText("<b>粗</b>").Contains("粗体"), "原始 HTML 不解析(和洛谷一致)");
        var doc = MarkdownParser.Parse("# 标题\n**加粗** 和 ~~删除~~\n- [x] 任务\n> 引用\n" +
                                       Fence + "js\nlet a = 1;\n" + Fence + "\n");
        Check(doc.Blocks.OfType<MdHeading>().Any(), "标题照旧");
        Check(doc.Blocks.OfType<MdList>().Any(l => l.Items.Any(i => i.IsTask && i.Checked)), "任务列表照旧");
        Check(doc.Blocks.OfType<MdQuote>().Any(), "引用照旧");
        Check(doc.Blocks.OfType<MdCodeBlock>().Any(c => c.Language == "js"), "代码块照旧");

        Console.WriteLine();
        Console.WriteLine("洛谷语法部分: 通过 " + _pass + " 项, 失败 " + _fail + " 项");
        return _fail;
    }
}
