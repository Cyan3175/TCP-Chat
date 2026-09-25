using System.Text;
using TCPChat10.Rendering;

/// <summary>离线单元测试: LaTeX 公式解析/排版 + 定界符归一化 + markdown 接线(11.4)。</summary>
public static class MathTest
{
    static int _pass, _fail;
    static void Check(bool ok, string name)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + name); }
        else { _fail++; Console.WriteLine("  FAIL  " + name); }
    }

    /// <summary>假度量: 宽度按字符数折算, 高度固定 —— 只看结构算得对不对, 不看真实字体。</summary>
    sealed class FakeMetrics : IMathMetrics
    {
        public double Width(string text, MathFontKind font, bool italic, double size) => text.Length * size * 0.5;

        public (double Ascent, double Descent) Vertical(string text, MathFontKind font, double size)
        {
            bool descends = text.Any(c => "gjpqy()[]{},;".Contains(c));
            return (size * 0.78, descends ? size * 0.22 : 0);
        }
    }

    static readonly FakeMetrics M = new();

    static MathBox Lay(string latex, bool display = true) => MathLayout.Build(MathParser.Parse(latex), 14, M, display);

    /// <summary>把树里某类节点都挑出来。</summary>
    static List<T> Collect<T>(MathNode node) where T : MathNode
    {
        var list = new List<T>();
        void Walk(MathNode n)
        {
            if (n is T t) list.Add(t);
            switch (n)
            {
                case MathRow r: foreach (var i in r.Items) Walk(i); break;
                case MathFrac f: Walk(f.Num); Walk(f.Den); break;
                case MathSqrt s: Walk(s.Radicand); if (s.Index != null) Walk(s.Index); break;
                case MathScript s: Walk(s.Base); if (s.Sub != null) Walk(s.Sub); if (s.Sup != null) Walk(s.Sup); break;
                case MathDelim d: Walk(d.Body); break;
                case MathStyled s: Walk(s.Body); break;
                case MathFrame f: Walk(f.Body); break;
                case MathAccent a: Walk(a.Base); break;
                case MathEnv e: foreach (var row in e.Rows) foreach (var cell in row) Walk(cell); break;
            }
        }
        Walk(node);
        return list;
    }

    static string TextOf(MathNode node) => string.Concat(Collect<MathSym>(node).Select(s => s.Text));
    static T First<T>(MathNode node) where T : MathNode => Collect<T>(node).First();

    public static int Run()
    {
        Console.WriteLine("=== O) LaTeX 公式: 解析 (11.4) ===");

        var frac = First<MathFrac>(MathParser.Parse(@"\frac{a}{b}"));
        Check(TextOf(frac.Num) == "a" && TextOf(frac.Den) == "b", @"\frac{a}{b} 的分子分母是 a / b");

        var sqrt = First<MathSqrt>(MathParser.Parse(@"\sqrt{x}"));
        Check(sqrt.Index == null, @"\sqrt{x} 解析成根号(没有次数)");
        var sqrt3 = First<MathSqrt>(MathParser.Parse(@"\sqrt[3]{x}"));
        Check(sqrt3.Index != null && TextOf(sqrt3.Index!) == "3", @"\sqrt[3]{x} 带上次数");

        var script = First<MathScript>(MathParser.Parse("x_i^2"));
        Check(script.Sub != null && script.Sup != null, "x_i^2 同时有上下标");

        var sums = First<MathScript>(MathParser.Parse(@"\sum_{i=1}^{n} a_i"));
        Check(sums.Limits, @"\sum 的上下标摆正上下(Limits=true)");
        var ints = First<MathScript>(MathParser.Parse(@"\int_a^b f"));
        Check(!ints.Limits, @"\int 的上下标仍旧摆旁边");

        var aligned = First<MathEnv>(MathParser.Parse(@"\begin{aligned}
p&=q,\\
r&=s.
\end{aligned}"));
        Check(aligned.Rows.Count == 2, "aligned 两行: " + aligned.Rows.Count);
        Check(aligned.Rows.All(r => r.Count == 2), "每行两个单元格(& 分列)");
        Check(aligned.Kind == MathEnvKind.Aligned, "识别成 aligned(按 & 对齐)");

        var cases = First<MathEnv>(MathParser.Parse(@"\begin{cases} a & x>0 \\ b & x\le 0 \end{cases}"));
        Check(cases.Kind == MathEnvKind.Cases && cases.Rows.Count == 2, "cases 环境: " + cases.Kind + " / " + cases.Rows.Count + " 行");

        var pmat = First<MathEnv>(MathParser.Parse(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}"));
        Check(pmat.Kind == MathEnvKind.Matrix && pmat.LeftDelim == "(" && pmat.RightDelim == ")", "pmatrix 带圆括号");

        Check(First<MathFrame>(MathParser.Parse(@"\boxed{x}")) != null, @"\boxed{x} 解析成方框");

        var textSym = Collect<MathSym>(MathParser.Parse(@"\text{若 }")).FirstOrDefault(s => s.Font == MathFontKind.Text);
        Check(textSym?.Text == "若 ", @"\text{...} 里保留空格与中文: [" + textSym?.Text + "]");

        var delim = First<MathDelim>(MathParser.Parse(@"\left(\frac{a}{b}\right)"));
        Check(delim.Left == "(" && delim.Right == ")" && delim.Auto, @"\left( \right) 带自动放大的括号");

        Check(TextOf(MathParser.Parse(@"\alpha\beta\gamma")) == "αβγ", @"希腊字母: alpha/beta/gamma");
        Check(TextOf(MathParser.Parse(@"\pm\mp\times\cdot\div")) == "±∓×⋅÷", "运算符符号表");
        Check(TextOf(MathParser.Parse(@"a\ne b\le c\ge d\to e")) == "a≠b≤c≥d→e", "关系符/箭头符号表");
        Check(TextOf(MathParser.Parse(@"\infty\partial\nabla\forall\exists\emptyset")) == "∞∂∇∀∃∅", "常用符号表");
        Check(TextOf(MathParser.Parse(@"\ldots\cdots\vdots\ddots")) == "…⋯⋮⋱", "省略号");
        Check(TextOf(MathParser.Parse(@"\sin x+\log y+\ln z")) == "sinx+logy+lnz", "函数名直立显示");
        Check(TextOf(MathParser.Parse(@"\bar{x}+\vec{v}+\hat{y}+\overline{AB}")) == "x+v+y+AB", "重音命令不会吞掉内容");

        var prime = First<MathScript>(MathParser.Parse("x'"));
        Check(prime.Sup != null && TextOf(prime.Sup!) == "′", "x' 解析成上标撇号");

        Check(Collect<MathSym>(MathParser.Parse(@"\foobar")).Any(s => s.Text.Contains("foobar")), @"不认识的命令按原样显示: \foobar");

        var italicX = Collect<MathSym>(MathParser.Parse("x")).First();
        var upright2 = Collect<MathSym>(MathParser.Parse("2")).First();
        Check(italicX.Italic && !upright2.Italic, "字母斜体、数字直立");

        var space = Collect<MathSpace>(MathParser.Parse(@"a\qquad b")).FirstOrDefault();
        Check(space != null && Math.Abs(space.Em - 2.0) < 0.001, @"\qquad 是 2em 空白: " + space?.Em);

        var gathered = First<MathEnv>(MathParser.Parse(@"a \\ b"));
        Check(gathered.Rows.Count == 2, "顶层的换行符会拆成两行");

        foreach (var bad in new[] { @"\frac{a}{", "{{{", @"\begin{aligned}", @"\sqrt[", "", @"\left(" })
        {
            try
            {
                var node = MathParser.Parse(bad);
                var box = MathLayout.Build(node, 14, M, true);
                Check(node != null && !double.IsNaN(box.Width), "坏输入不抛异常: [" + bad + "]");
            }
            catch (Exception ex)
            {
                Check(false, "坏输入抛异常: [" + bad + "] " + ex.GetType().Name);
            }
        }

        Console.WriteLine("=== P) LaTeX 公式: 排版几何 (11.4) ===");
        var xBox = Lay("x", false);
        var fracBox = Lay(@"\frac{a}{b}");
        Check(fracBox.Width > xBox.Width, "分数的宽度比单个字母大: " + fracBox.Width.ToString("F1"));
        Check(fracBox.TotalHeight > xBox.TotalHeight * 2, "分数比字母高得多(有分数线): " + fracBox.TotalHeight.ToString("F1"));
        Check(fracBox.Height > 0 && fracBox.Depth > 0, "分数在基线上下都有内容");

        var sqrtBox = Lay(@"\sqrt{x}");
        Check(sqrtBox.TotalHeight > xBox.TotalHeight, "根号比被开方内容高(要容下横线)");
        Check(sqrtBox.Width > xBox.Width * 1.5, "根号左边那一笔也占宽度: " + sqrtBox.Width.ToString("F1"));
        Check(Lay(@"\sqrt[3]{x}").Width > sqrtBox.Width, "带次数的根号更宽");

        Check(Lay("x^2", false).Height > xBox.Height, "上标把高度抬起来");
        Check(Lay("x_i", false).Depth > xBox.Depth, "下标把深度压下去");

        var sumDisplay = Lay(@"\sum_{i=1}^{n} a_i", true);
        var sumInline = Lay(@"\sum_{i=1}^{n} a_i", false);
        Check(sumDisplay.TotalHeight > sumInline.TotalHeight, "块级公式里 sum 的上下限叠起来(更高): " +
              sumDisplay.TotalHeight.ToString("F1") + " > " + sumInline.TotalHeight.ToString("F1"));

        var twoLines = Lay(@"\begin{aligned} a&=b \\ c&=d \end{aligned}");
        Check(twoLines.TotalHeight > Lay("a=b", false).TotalHeight * 1.8, "aligned 两行比一行高得多: " + twoLines.TotalHeight.ToString("F1"));

        var boxed = Lay(@"\boxed{a=b}");
        Check(boxed.Width > Lay("a=b").Width && boxed.TotalHeight > Lay("a=b").TotalHeight, @"\boxed 比方框里的内容大一圈(留白)");

        Check(Lay(@"\left(\frac{a}{b}\right)", false).TotalHeight > Lay(@"\frac{a}{b}", false).TotalHeight, @"\left( \right) 的括号跟着内容变大");
        Check(Lay(@"\frac{\frac{a}{b}}{\frac{c}{d}}").TotalHeight > fracBox.TotalHeight, "嵌套分数排得出来");

        var sample = new[]
        {
            @"ax^4+bx^3+cx^2+dx+e=0\qquad(a\ne0)",
            @"x=y-\frac{b}{4a},",
            @"\begin{aligned}
p&=\frac{8ac-3b^2}{8a^2},\\
q&=\frac{b^3-4abc+8a^2d}{8a^3},\\
r&=\frac{-3b^4+16ab^2c-64a^2bd+256a^3e}{256a^4}.
\end{aligned}",
            @"u_{\pm}=\frac{-p\pm\sqrt{p^2-4r}}2.",
            @"y=\pm\sqrt{u_+},\qquad y=\pm\sqrt{u_-}.",
            @"z^3+2pz^2+(p^2-4r)z-q^2=0.",
            @"\beta=\frac{p+z-q/\alpha}{2},\qquad \gamma=\frac{p+z+q/\alpha}{2}.",
            @"(y^2+\alpha y+\beta)(y^2-\alpha y+\gamma)=0.",
            @"\boxed{
x_{1,2}=-\frac{b}{4a}+\frac{-\alpha\pm\sqrt{\alpha^2-4\beta}}2,
}",
            @"x_i=y_i-\frac{b}{4a}\qquad(i=1,2,3,4).",
        };
        int okBoxes = 0, badBoxes = 0;
        foreach (var latex in sample)
        {
            var box = Lay(latex);
            bool ok = box.Width > 1 && box.TotalHeight > 1 &&
                      !double.IsNaN(box.Width) && !double.IsInfinity(box.Width) &&
                      !double.IsNaN(box.TotalHeight) && !double.IsInfinity(box.TotalHeight) &&
                      MathParser.LastError == null;
            if (ok) okBoxes++;
            else { badBoxes++; Console.WriteLine("       排不出来: " + latex.Replace("\n", " ") + " -> " + box.Width + "x" + box.TotalHeight + " " + MathParser.LastError); }
        }
        Check(badBoxes == 0, "样例里的 " + sample.Length + " 个公式全部排得出来(" + okBoxes + " 个)");

        Console.WriteLine("=== Q) LaTeX 公式: 纯文本 (通知/引用用) (11.4) ===");
        Check(MathParser.ToPlainText(@"\frac{a}{b}") == "a/b", "简单分数写成 a/b: " + MathParser.ToPlainText(@"\frac{a}{b}"));
        Check(MathParser.ToPlainText(@"\frac{a+b}{c}") == "(a + b)/(c)", "复杂分子加括号: " + MathParser.ToPlainText(@"\frac{a+b}{c}"));
        Check(MathParser.ToPlainText(@"\sqrt{x}") == "√(x)", "根号写成 √(x): " + MathParser.ToPlainText(@"\sqrt{x}"));
        Check(MathParser.ToPlainText(@"\alpha+\beta") == "α + β", "符号直接换成对应字符: " + MathParser.ToPlainText(@"\alpha+\beta"));
        Check(!MathParser.ToPlainText(@"\frac{-b\pm\sqrt{b^2-4ac}}{2a}").Contains('\\'), "纯文本里不留反斜杠");
        var alignedPlain = MathParser.ToPlainText(@"\begin{aligned} a&=b \\ c&=d \end{aligned}");
        Check(!alignedPlain.Contains('\\') && alignedPlain.Contains('a') && alignedPlain.Contains('c'),
              "aligned 的纯文本把两行都带上: " + alignedPlain);

        Console.WriteLine("=== R) 定界符归一化 (11.4) ===");
        var own1 = MathDelimiters.Normalize(@"\[
x^2
\]");
        Check(own1.Contains("$$") && own1.Contains("x^2"), "独占一行的方括号公式变成 $$ 块");
        Check(MathDelimiters.Normalize(@"前 \(a\ne0\) 后") == @"前 $a\ne0$ 后", "行内圆括号公式变成 $ $");
        // 行内公式两边的空白会被去掉: Markdig 的行内数学要求 $ 后面不能紧跟空格
        Check(MathDelimiters.Normalize(@"显示 \[ x \] 结束").Contains("显示 $$x$$ 结束"), "行内的方括号公式变成行内 $$ $$");
        Check(MathDelimiters.Normalize(@"没配对 \[ x") == @"没配对 \[ x", "没配对的定界符原样不动");
        var fence = "```latex" + Environment.NewLine + @"\[ x^2 \]" + Environment.NewLine + "```";
        Check(MathDelimiters.Normalize(fence) == fence, "围栏代码块里的 LaTeX 不动");
        var inlineCode = "看 " + "`" + @"\[x\]" + "`" + " 这样写";
        Check(MathDelimiters.Normalize(inlineCode) == inlineCode, "行内代码里的 LaTeX 不动");
        Check(MathDelimiters.Normalize("$$x^2$$") == "$$x^2$$", "本来就用 $$ 的不动");
        // 跨行的 $$ ... $$ 前面有别的内容时, Markdig 不认它是公式块 -> 归一化时要摆到独占一行
        var dollarMulti = MarkdownParser.Parse("公式:$$" + Environment.NewLine + @"\begin{aligned}" + Environment.NewLine + "a&=b" + Environment.NewLine + @"\end{aligned}" + Environment.NewLine + "$$");
        Check(dollarMulti.Blocks.OfType<MdMathBlock>().Any(), "跨行的 $$ ... $$ 也变成公式块");
        // $$ 后面紧跟着内容(\boxed{ 这种写法)时 Markdig 也不认, 同样要重排
        var dollarTight = MarkdownParser.Parse("因此:" + Environment.NewLine + "$$" + @"\boxed{" + Environment.NewLine + "x=1" + Environment.NewLine + "}" + Environment.NewLine + "$$");
        Check(dollarTight.Blocks.OfType<MdMathBlock>().Any(), "$$ 后面直接跟内容时也变成公式块");
        Check(MarkdownParser.Parse("公式:$$x^2$$ 结束").Blocks.OfType<MdParagraph>().SelectMany(p => p.Inlines).OfType<MdMathSpan>().Any() ||
              MarkdownParser.Parse("公式:$$x^2$$ 结束").Blocks.OfType<MdMathBlock>().Any(), "单行的 $$ ... $$ 仍旧是公式");
        // \( \) 里带了换行: 行内数学装不下换行, 只能按块级公式处理
        var multi = MarkdownParser.Parse(@"行内 \(a" + Environment.NewLine + @"+b\) 结束");
        Check(multi.Blocks.OfType<MdMathBlock>().Any(), "跨行的圆括号公式按块级处理");
        Check(MathDelimiters.Normalize("").Length == 0 && MathDelimiters.Normalize(null).Length == 0, "空文本不报错");

        Console.WriteLine("=== S) markdown 接线: 公式进消息 (11.4) ===");
        var blockDoc = MarkdownParser.Parse(@"\[
x^2+y^2=z^2
\]");
        Check(blockDoc.Blocks.OfType<MdMathBlock>().Any(), "方括号独占一行 -> 块级公式");
        Check(MathParser.LastError == null, "解析没有走兜底路径");

        var spanDoc = MarkdownParser.Parse(@"若 \(q=0\) 则");
        Check(spanDoc.Blocks.OfType<MdParagraph>().SelectMany(p => p.Inlines).OfType<MdMathSpan>().Any(), "圆括号公式变成行内公式");

        var mdMath = MarkdownParser.Parse(@"$$\frac{a}{b}$$");
        Check(mdMath.Blocks.OfType<MdMathBlock>().Any() || mdMath.Blocks.OfType<MdParagraph>().SelectMany(p => p.Inlines).OfType<MdMathSpan>().Any(),
              "$$ ... $$ 还是公式");

        Check(MarkdownParser.HasMarkup("$x^2$"), "成对的 $ -> 走解析器");
        Check(!MarkdownParser.HasMarkup("这本书 $5"), "单独一个 $ 不算公式(纯文本快路径)");
        Check(MarkdownParser.HasMarkup(@"\[x\]"), "方括号公式 -> 走解析器");
        Check(!MarkdownParser.HasMarkup("今天下午三点开会"), "普通文字仍旧走快路径");

        var fenceDoc = MarkdownParser.Parse("```" + Environment.NewLine + @"\[ x \]" + Environment.NewLine + "```");
        Check(!fenceDoc.Blocks.OfType<MdMathBlock>().Any(), "代码块里的 LaTeX 不会被当成公式");
        Check(fenceDoc.Blocks.OfType<MdCodeBlock>().Any(), "代码块还是代码块");

        var plain = MarkdownParser.ToPlainText(@"\[
\frac{a}{b}
\]");
        Check(plain.Contains("a/b") && !plain.Contains('\\'), "通知用的纯文本把公式化成可读文字: " + plain);

        // 真实消息里出现过"只有 \r 换行"的写法(从别处粘过来就是这种), 也要认
        var crOnly = "因此四个根为\r" + @"\[" + "\r" + @"\boxed{" + "\rx_{1,2}=1\r}\r" + @"\]" + "\r" + "的任一根";
        var crDoc = MarkdownParser.Parse(crOnly);
        Check(crDoc.Blocks.OfType<MdMathBlock>().Any(), "只有 CR 换行时也认得出方括号公式");
        var crDollar = MarkdownParser.Parse("前缀\r$$\r" + @"\boxed{x=1}" + "\r$$\r后缀");
        Check(crDollar.Blocks.OfType<MdMathBlock>().Any(), "只有 CR 换行时也认得出美元公式");

        var message = "一般四次方程" + Environment.NewLine + @"\[" + Environment.NewLine +
                      @"ax^4+bx^3+cx^2+dx+e=0\qquad(a\ne0)" + Environment.NewLine + @"\]" + Environment.NewLine +
                      "的根式解如下。" + Environment.NewLine + Environment.NewLine +
                      @"令 \(u=y^2\) 得" + Environment.NewLine + @"\[" + Environment.NewLine +
                      "u^2+pu+r=0." + Environment.NewLine + @"\]";
        var messageDoc = MarkdownParser.Parse(message);
        int blocks = messageDoc.Blocks.OfType<MdMathBlock>().Count();
        int spans = messageDoc.Blocks.OfType<MdParagraph>().SelectMany(p => p.Inlines).OfType<MdMathSpan>().Count();
        Check(blocks == 2 && spans == 1, "一条样例消息里认出 " + blocks + " 个块级 + " + spans + " 个行内公式");

        Console.WriteLine();
        Console.WriteLine("公式部分: 通过 " + _pass + " 项, 失败 " + _fail + " 项");
        return _fail;
    }
}
