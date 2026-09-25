namespace TCPChat10.Rendering;

/// <summary>
/// 字体度量接口: 界面层用真实的字体测量实现, 离线测试用确定的假数据实现。
/// 这样"排版算得对不对"可以在没有界面的情况下测。
/// </summary>
public interface IMathMetrics
{
    /// <summary>一段文字的宽度。</summary>
    double Width(string text, MathFontKind font, bool italic, double size);

    /// <summary>一段文字相对基线的高度: (基线以上, 基线以下)。</summary>
    (double Ascent, double Descent) Vertical(string text, MathFontKind font, double size);
}

// ================= 排好版的盒子(纯几何) =================

/// <summary>排好版的一个盒子: 位置(相对父盒子基线)由父盒子记, 这里只有自己的大小。</summary>
public abstract class MathBox
{
    public double Width { get; set; }
    /// <summary>基线以上的高度。</summary>
    public double Height { get; set; }
    /// <summary>基线以下的高度(下标、分母这些会伸下去)。</summary>
    public double Depth { get; set; }
    public double TotalHeight => Height + Depth;
}

/// <summary>一段文字(画成 TextBlock)。</summary>
public sealed class MathGlyphBox : MathBox
{
    public string Text { get; set; } = "";
    public MathFontKind Font { get; set; } = MathFontKind.Math;
    public bool Italic { get; set; }
    public double Size { get; set; }
}

/// <summary>实心细条(分数线 / 上划线 / 根号顶上的横线)。</summary>
public sealed class MathRuleBox : MathBox { }

/// <summary>根号左边那一笔(界面层画成折线)。</summary>
public sealed class MathSurdBox : MathBox { }

/// <summary>把若干盒子摆到相对自己基线的位置上。</summary>
public sealed class MathStackBox : MathBox
{
    public List<(MathBox Box, double X, double Y)> Items { get; } = new();
    /// <summary>是否给整个盒子套一个方框(\boxed)。</summary>
    public bool Frame { get; set; }
    /// <summary>方框与内容之间的留白。</summary>
    public double FramePad { get; set; }

    /// <summary>按子盒子算出自己的大小(位置都是相对基线的)。</summary>
    public MathStackBox Finish()
    {
        double w = 0, h = 0, d = 0;
        foreach (var (box, x, y) in Items)
        {
            w = Math.Max(w, x + box.Width);
            h = Math.Max(h, y + box.Height);
            d = Math.Max(d, -y + box.Depth);
        }
        if (Frame)
        {
            w += FramePad * 2;
            h += FramePad;
            d += FramePad;
        }
        Width = w; Height = h; Depth = d;
        return this;
    }
}

/// <summary>
/// 公式排版: MathNode 树 -&gt; MathBox 树。
/// 尺寸全是几何量, 界面层照着画, 所以"排得对不对"能离线测(用假的度量)。
/// </summary>
public static class MathLayout
{
    // 各种间距都按 em(相对字号)算, 这样公式跟着字体大小一起缩放
    private const double Axis = 0.25;            // 分数线的高度(相对基线)
    private const double RuleThickness = 0.055;  // 细条粗细
    private const double FracGap = 0.13;         // 分子/分母与分数线之间的留白
    private const double FracPad = 0.14;         // 分数线左右多出来的部分
    private const double ScriptScale = 0.7;      // 上下标字号
    private const double SupShift = 0.45;        // 上标抬高
    private const double SubShift = 0.2;         // 下标压低
    private const double SupTuck = 0.12;         // 上标可以稍微压下来一点(TeX 也是这样)
    private const double SubTuck = 0.05;         // 下标底板高的地方可以贴紧一点
    private const double LimitGap = 0.16;        // 大运算符与上下限之间的留白
    private const double BigScale = 1.25;        // 块级公式里大运算符放大
    private const double SqrtPad = 0.12;         // 根号里内容与横线的留白
    private const double SurdWidth = 0.62;       // 根号那一笔的宽度
    private const double DelimPad = 0.08;        // 括号与内容之间的留白
    private const double BinSpace = 0.22;        // 二元运算符两侧
    private const double RelSpace = 0.28;        // 关系符两侧
    private const double PunctSpace = 0.17;      // 逗号后面的空格
    private const double OpSpace = 0.12;         // 函数名/大运算符两侧
    private const double RowGap = 0.4;           // 多行公式的行距
    private const double ColGap = 1.0;           // aligned 里两组之间的间距
    private const double FramePad = 0.28;        // \boxed 的留白
    private const double AccentGap = 0.1;        // 重音与字母之间的留白
    private const double AccentScale = 0.75;     // 重音符的字号

    /// <summary>排版一个公式; size = 正文字号, display = 块级(分数更大、大运算符上下限摆正上下)。</summary>
    public static MathBox Build(MathNode node, double size, IMathMetrics metrics, bool display)
    {
        var fmt = new Fmt(MathFontKind.Math, null, false);
        return BuildNode(node, size, metrics, display, fmt);
    }

    /// <summary>字体覆盖(\mathbf \text 之类往下传)。</summary>
    private readonly record struct Fmt(MathFontKind? Font, bool? Italic, bool Bold);

    private static MathBox BuildNode(MathNode node, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        switch (node)
        {
            case MathSym sym: return Glyph(sym.Text, sym.Font, sym.Italic, size, m, fmt);
            case MathRow row: return BuildRow(row, size, m, display, fmt);
            case MathFrac frac: return BuildFrac(frac, size, m, display, fmt);
            case MathSqrt sqrt: return BuildSqrt(sqrt, size, m, display, fmt);
            case MathScript script: return BuildScript(script, size, m, display, fmt);
            case MathDelim delim: return BuildDelim(delim, size, m, display, fmt);
            case MathStyled styled:
                return BuildNode(styled.Body, size, m, display,
                                 new Fmt(styled.Font, styled.Italic, styled.Font == MathFontKind.MathBold));
            case MathAccent accent: return BuildAccent(accent, size, m, display, fmt);
            case MathFrame frame: return BuildFrame(frame, size, m, display, fmt);
            case MathEnv env: return BuildEnv(env, size, m, display, fmt);
            case MathSpace space: return new MathStackBox { Width = space.Em * size }.Finish();
            default: return new MathStackBox().Finish();
        }
    }

    private static MathBox Glyph(string text, MathFontKind font, bool italic, double size, IMathMetrics m, Fmt fmt)
    {
        var effectiveFont = font;
        var effectiveItalic = italic;

        // 外层 \mathbf \mathrm 之类的覆盖只作用于"数学字体"的字符;
        // \text{...} 与 \mathtt{...} 保持自己的字体(中文要用界面字体才显示得出来)
        if (font is MathFontKind.Math or MathFontKind.MathUpright or MathFontKind.MathBold)
        {
            if (fmt.Font.HasValue) effectiveFont = fmt.Font.Value;
            if (fmt.Bold) effectiveFont = MathFontKind.MathBold;
            if (fmt.Italic.HasValue) effectiveItalic = fmt.Italic.Value;
            if (effectiveFont == MathFontKind.MathBold) effectiveItalic = false;
        }

        var (asc, desc) = m.Vertical(text, effectiveFont, size);
        return new MathGlyphBox
        {
            Text = text,
            Font = effectiveFont,
            Italic = effectiveItalic,
            Size = size,
            Width = m.Width(text, effectiveFont, effectiveItalic, size),
            Height = asc,
            Depth = desc,
        };
    }

    private static MathBox BuildRow(MathRow row, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        var stack = new MathStackBox();
        double x = 0;
        MathKind? prev = null;
        foreach (var item in row.Items)
        {
            var box = BuildNode(item, size, m, display, fmt);
            if (box.Width <= 0 && box.Height <= 0 && box.Depth <= 0) continue;      // 空盒子

            var kind = EffectiveKind(item, prev);
            if (prev.HasValue) x += SpaceBetween(prev.Value, kind, size);
            stack.Items.Add((box, x, 0));
            x += box.Width;
            prev = kind;
        }
        return stack.Finish();
    }

    /// <summary>行首的 +- 号按普通符号处理(TeX 的老规矩)。</summary>
    private static MathKind EffectiveKind(MathNode node, MathKind? prev)
    {
        var kind = KindOf(node);
        if (kind == MathKind.Bin && (prev == null || prev == MathKind.Bin || prev == MathKind.Rel || prev == MathKind.Open))
            return MathKind.Ord;
        return kind;
    }

    private static MathKind KindOf(MathNode node) => node switch
    {
        MathSym s => s.Kind,
        MathScript s => KindOf(s.Base),
        MathStyled s => KindOf(s.Body),
        MathAccent a => KindOf(a.Base),
        MathDelim => MathKind.Ord,
        MathSqrt => MathKind.Ord,
        MathFrac => MathKind.Ord,
        _ => MathKind.Ord,
    };

    private static double SpaceBetween(MathKind left, MathKind right, double size)
    {
        if (left == MathKind.Rel || right == MathKind.Rel) return RelSpace * size;
        if (left == MathKind.Bin || right == MathKind.Bin) return BinSpace * size;
        if (left == MathKind.Punct) return PunctSpace * size;
        if (left == MathKind.Big || right == MathKind.Big) return OpSpace * size;
        if (left == MathKind.Op || right == MathKind.Op) return OpSpace * size;
        if (left == MathKind.Open || right == MathKind.Close) return 0;
        return 0;
    }

    private static MathBox BuildFrac(MathFrac frac, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        double scale = display ? 1.0 : 0.88;
        var num = BuildNode(frac.Num, size * scale, m, display, fmt);
        var den = BuildNode(frac.Den, size * scale, m, display, fmt);

        double axis = Axis * size, t = RuleThickness * size, gap = FracGap * size, pad = FracPad * size;
        double width = Math.Max(num.Width, den.Width) + pad * 2;

        // 注意盒子的坐标约定: 细条的纵向范围是 [Y, Y+厚度](相对基线),
        // 所以想让分数线"骑"在轴线上, Y 要取 axis - t/2, 不是 axis + t/2。
        var stack = new MathStackBox();
        stack.Items.Add((num, (width - num.Width) / 2, axis + t / 2 + gap + num.Depth));
        stack.Items.Add((new MathRuleBox { Width = width, Height = t }, 0, axis - t / 2));
        stack.Items.Add((den, (width - den.Width) / 2, -(axis - t / 2 - gap) - den.Height));
        return stack.Finish();
    }

    private static MathBox BuildSqrt(MathSqrt sqrt, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        var body = BuildNode(sqrt.Radicand, size, m, display, fmt);
        var stack = new MathStackBox();

        double t = RuleThickness * size;                    // 顶上那道横线
        double pad = SqrtPad * size;
        // 根号那一笔跟着内容高度变宽一点: 内容很高时(比如里面是分数)才不会缩成一条竖线,
        // 这也是真正的数学字体对待大根号的做法(用更宽的变体)
        double stretch = Math.Clamp(1 + 0.22 * (body.TotalHeight / size - 1), 1.0, 1.8);
        double surdW = SurdWidth * size * stretch;

        MathBox? index = null;
        double indexShift = 0;
        if (sqrt.Index != null)
        {
            index = BuildNode(sqrt.Index, size * ScriptScale * 0.85, m, false, fmt);
            indexShift = index.Width * 0.6;
        }

        double contentX = surdW + indexShift;
        double topY = body.Height + pad;                    // 横线的位置(相对基线)
        // 根号那一笔: 从横线上面一点一直包到内容底部
        var surd = new MathSurdBox
        {
            Width = surdW + indexShift,
            Height = topY + t,
            Depth = body.Depth,
        };
        stack.Items.Add((surd, 0, 0));
        stack.Items.Add((body, contentX, 0));
        // 横线要在"根号那一笔的顶端"这条线上: 根号那条折线的收尾正好落在 Y+厚度/2,
        // 所以这里取 topY + t/2; 左边多搭 t/2, 免得两段之间出现一道缝。
        stack.Items.Add((new MathRuleBox { Width = body.Width + pad + t / 2, Height = t },
                         contentX - t / 2, topY + t / 2));
        if (index != null) stack.Items.Add((index, 0, topY - index.Depth + t));
        return stack.Finish();
    }

    private static MathBox BuildScript(MathScript script, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        var b = BuildNode(script.Base, size, m, display, fmt);
        bool big = script.Base is MathSym { Kind: MathKind.Big } s && s.Limits;
        double scriptSize = size * ScriptScale;

        var sub = script.Sub != null ? BuildNode(script.Sub, scriptSize, m, false, fmt) : null;
        var sup = script.Sup != null ? BuildNode(script.Sup, scriptSize, m, false, fmt) : null;

        var stack = new MathStackBox();

        if (big && display && (sub != null || sup != null))
        {
            // \sum_{i=1}^{n} 这种: 上下限摆在正上/正下
            double gap = LimitGap * size;
            double width = Math.Max(b.Width, Math.Max(sub?.Width ?? 0, sup?.Width ?? 0));
            stack.Items.Add((b, (width - b.Width) / 2, 0));
            if (sup != null)
                stack.Items.Add((sup, (width - sup.Width) / 2, b.Height + gap + sup.Depth));
            if (sub != null)
                stack.Items.Add((sub, (width - sub.Width) / 2, -(b.Depth + gap) - sub.Height));
            return stack.Finish();
        }

        double kern = 0.04 * size;
        stack.Items.Add((b, 0, 0));
        double x = b.Width + kern;
        if (sup != null)
            stack.Items.Add((sup, x, Math.Max(SupShift * size, b.Height - sup.Depth - SupTuck * size)));
        if (sub != null)
            stack.Items.Add((sub, x, -Math.Max(SubShift * size, b.Depth - SubTuck * size)));
        return stack.Finish();
    }

    private static MathBox BuildDelim(MathDelim delim, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        var body = BuildNode(delim.Body, size, m, display, fmt);
        var stack = new MathStackBox();

        double want = body.TotalHeight + DelimPad * 2 * size;
        double x = 0;

        if (!string.IsNullOrEmpty(delim.Left))
        {
            var box = ScaledGlyph(delim.Left!, size, want, m, fmt, delim.SizeFactor);
            double y = (body.Height - body.Depth) / 2 - (box.Height - box.Depth) / 2;
            stack.Items.Add((box, 0, y));
            x = box.Width;
        }

        stack.Items.Add((body, x, 0));
        x += body.Width;

        if (!string.IsNullOrEmpty(delim.Right))
        {
            var box = ScaledGlyph(delim.Right!, size, want, m, fmt, delim.SizeFactor);
            double y = (body.Height - body.Depth) / 2 - (box.Height - box.Depth) / 2;
            stack.Items.Add((box, x, y));
        }
        return stack.Finish();
    }

    /// <summary>把定界符放大到能包住 contentH 那么高(\left( \right) 的效果)。</summary>
    private static MathBox ScaledGlyph(string text, double size, double contentH, IMathMetrics m, Fmt fmt, double factor)
    {
        var (asc, desc) = m.Vertical(text, MathFontKind.MathUpright, size);
        double natural = Math.Max(0.1, asc + desc);
        double want = contentH * (factor > 1 ? factor : 1);
        double scale = Math.Clamp(want / natural, 1.0, 4.0);
        double fontSize = size * scale;
        var (a2, d2) = m.Vertical(text, MathFontKind.MathUpright, fontSize);
        return new MathGlyphBox
        {
            Text = text,
            Font = MathFontKind.MathUpright,
            Italic = false,
            Size = fontSize,
            Width = m.Width(text, MathFontKind.MathUpright, false, fontSize),
            Height = a2,
            Depth = d2,
        };
    }

    private static MathBox BuildAccent(MathAccent accent, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        var b = BuildNode(accent.Base, size, m, display, fmt);
        var stack = new MathStackBox();
        double t = RuleThickness * size;
        double gap = AccentGap * size;

        switch (accent.Kind)
        {
            case MathAccentKind.Overline:
                stack.Items.Add((b, 0, 0));
                stack.Items.Add((new MathRuleBox { Width = b.Width, Height = t }, 0, b.Height + gap));
                return stack.Finish();

            case MathAccentKind.Underline:
                stack.Items.Add((b, 0, 0));
                stack.Items.Add((new MathRuleBox { Width = b.Width, Height = t }, 0, -(b.Depth + gap) - t));
                return stack.Finish();
        }

        string mark = accent.Kind switch
        {
            MathAccentKind.Hat or MathAccentKind.WideHat => "^",
            MathAccentKind.Bar => "¯",
            MathAccentKind.Vec or MathAccentKind.OverRightArrow => "→",
            MathAccentKind.Dot => "˙",
            MathAccentKind.Ddot => "¨",
            _ => "˜",
        };

        var accentBox = Glyph(mark, MathFontKind.MathUpright, false, size * AccentScale, m,
                             new Fmt(MathFontKind.MathUpright, false, false));
        double width = Math.Max(b.Width, accentBox.Width);
        stack.Items.Add((b, (width - b.Width) / 2, 0));
        double markY = b.Height + gap + accentBox.Depth * 0.4;
        stack.Items.Add((accentBox, (width - accentBox.Width) / 2, markY));
        return stack.Finish();
    }

    private static MathBox BuildFrame(MathFrame frame, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        var body = BuildNode(frame.Body, size, m, display, fmt);
        double pad = FramePad * size;
        var stack = new MathStackBox { Frame = true, FramePad = pad };
        stack.Items.Add((body, pad, 0));
        return stack.Finish();
    }

    private static MathBox BuildEnv(MathEnv env, double size, IMathMetrics m, bool display, Fmt fmt)
    {
        var rows = new List<List<MathBox>>();
        foreach (var row in env.Rows)
        {
            var built = new List<MathBox>();
            foreach (var cell in row) built.Add(BuildNode(cell, size, m, display, fmt));
            rows.Add(built);
        }
        // 末尾多出来的空行(\begin{aligned}a=b\\\end{aligned} 这种)不算
        while (rows.Count > 1 && rows[^1].All(b => b.Width <= 0 && b.Height <= 0 && b.Depth <= 0))
            rows.RemoveAt(rows.Count - 1);
        if (rows.Count == 0) return new MathStackBox().Finish();

        int cols = 0;
        foreach (var r in rows) cols = Math.Max(cols, r.Count);
        if (cols == 0) return new MathStackBox().Finish();

        // 列宽 / 列对齐
        var widths = new double[cols];
        var align = new Align[cols];
        for (int c = 0; c < cols; c++)
        {
            align[c] = AlignmentOf(env, c);
            foreach (var r in rows)
                if (c < r.Count) widths[c] = Math.Max(widths[c], r[c].Width);
        }

        double colGap = ColGap * size;
        double rowGap = RowGap * size;

        // 行高/行深
        int n = rows.Count;
        var heights = new double[n];
        var depths = new double[n];
        for (int i = 0; i < n; i++)
            foreach (var cell in rows[i])
            {
                heights[i] = Math.Max(heights[i], cell.Height);
                depths[i] = Math.Max(depths[i], cell.Depth);
            }

        double blockH = 0;
        for (int i = 0; i < n; i++) blockH += heights[i] + depths[i];
        blockH += rowGap * (n - 1);

        double width = 0;
        for (int c = 0; c < cols; c++)
        {
            width += widths[c];
            if (c < cols - 1) width += GapAfter(env, c, colGap);
        }

        var stack = new MathStackBox();
        double top = Axis * size + blockH / 2;
        double y = top;
        for (int i = 0; i < n; i++)
        {
            y -= heights[i];
            double x = 0;
            for (int c = 0; c < cols; c++)
            {
                if (c < rows[i].Count)
                {
                    var cell = rows[i][c];
                    double offset = align[c] switch
                    {
                        Align.Right => widths[c] - cell.Width,
                        Align.Center => (widths[c] - cell.Width) / 2,
                        _ => 0,
                    };
                    stack.Items.Add((cell, x + offset, y));
                }
                x += widths[c];
                if (c < cols - 1) x += GapAfter(env, c, colGap);
            }
            y -= depths[i] + rowGap;
        }

        var result = stack.Finish();

        // 矩阵/cases 的左右定界符
        string? left = env.LeftDelim;
        string? right = env.RightDelim;
        if (env.Kind == MathEnvKind.Cases) left = "{";

        if (left == null && right == null) return result;

        var outer = new MathStackBox();
        double bodyWant = result.TotalHeight + DelimPad * 2 * size;
        double ox = 0;
        if (left != null)
        {
            var box = ScaledGlyph(left, size, bodyWant, m, fmt, 1);
            outer.Items.Add((box, 0, (result.Height - result.Depth) / 2 - (box.Height - box.Depth) / 2));
            ox = box.Width;
        }
        outer.Items.Add((result, ox, 0));
        ox += result.Width;
        if (right != null)
        {
            var box = ScaledGlyph(right, size, bodyWant, m, fmt, 1);
            outer.Items.Add((box, ox, (result.Height - result.Depth) / 2 - (box.Height - box.Depth) / 2));
        }
        return outer.Finish();
    }

    private enum Align { Left, Center, Right }

    private static Align AlignmentOf(MathEnv env, int col)
    {
        switch (env.Kind)
        {
            case MathEnvKind.Aligned:
                // & 对齐点: 奇数列右对齐、偶数列左对齐(和 LaTeX 一样)
                return col % 2 == 0 ? Align.Right : Align.Left;
            case MathEnvKind.Gathered:
                return Align.Center;
            case MathEnvKind.Matrix:
                if (!string.IsNullOrEmpty(env.ColumnSpec) && col < env.ColumnSpec!.Length)
                    return SpecAlign(env.ColumnSpec![col]);
                return Align.Center;
            case MathEnvKind.Array:
                if (!string.IsNullOrEmpty(env.ColumnSpec) && col < env.ColumnSpec!.Length)
                    return SpecAlign(env.ColumnSpec![col]);
                return Align.Left;
            case MathEnvKind.Cases:
                return col == 0 ? Align.Left : Align.Left;
            default:
                return Align.Left;
        }
    }

    private static Align SpecAlign(char c) => c switch
    {
        'r' => Align.Right,
        'c' => Align.Center,
        _ => Align.Left,
    };

    private static double GapAfter(MathEnv env, int col, double colGap) => env.Kind switch
    {
        // aligned: 一组内部(& 两边)不留缝, 组与组之间留
        MathEnvKind.Aligned => col % 2 == 1 ? colGap : 0,
        MathEnvKind.Cases => colGap * 0.6,
        _ => colGap * 0.6,
    };
}
