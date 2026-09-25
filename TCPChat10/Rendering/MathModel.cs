namespace TCPChat10.Rendering;

/// <summary>
/// 数学公式的中间模型: LaTeX 子集 -&gt; 一棵"有什么"的树。
/// 和 markdown 那套一样, 这里全是纯数据, 不碰任何界面类型, 所以离线测试能直接跑。
/// </summary>
public abstract class MathNode { }

/// <summary>水平排成一排的东西(公式的默认容器)。</summary>
public sealed class MathRow : MathNode
{
    public List<MathNode> Items { get; } = new();
}

/// <summary>符号类别: 决定间距(TeX 那套 Ord/Bin/Rel/... 的简化版)。</summary>
public enum MathKind
{
    /// <summary>普通符号: 字母、数字、希腊字母、infty ...</summary>
    Ord,
    /// <summary>二元运算符: + - \pm \times \cdot \cup ...</summary>
    Bin,
    /// <summary>关系符: = &lt; &gt; \le \ne \approx \in ...</summary>
    Rel,
    /// <summary>左括号类</summary>
    Open,
    /// <summary>右括号类</summary>
    Close,
    /// <summary>标点: , ; :</summary>
    Punct,
    /// <summary>大运算符(\sum \int \prod \lim), 上下标可以摆到正上下方</summary>
    Big,
    /// <summary>函数名(\sin \log \max), 直立显示</summary>
    Op,
}

/// <summary>数学字体的种类(具体用哪个字体由界面层决定)。</summary>
public enum MathFontKind
{
    /// <summary>公式主体(数学字体, 斜体由 Italic 决定)</summary>
    Math,
    /// <summary>公式里的直立体(数字/函数名/\mathrm)</summary>
    MathUpright,
    /// <summary>粗体(\mathbf)</summary>
    MathBold,
    /// <summary>\text{...} 里的普通文字(用界面字体, 中文也能显示)</summary>
    Text,
    /// <summary>等宽(\mathtt)</summary>
    Mono,
}

/// <summary>一段不会再拆开的文字(一个字符, 或 \text 里的一整句)。</summary>
public sealed class MathSym : MathNode
{
    public string Text { get; set; } = "";
    public MathKind Kind { get; set; } = MathKind.Ord;
    public MathFontKind Font { get; set; } = MathFontKind.Math;
    public bool Italic { get; set; }
    /// <summary>大运算符是否把上下标摆到正上/正下(\sum 是, \int 不是)。</summary>
    public bool Limits { get; set; }

    public MathSym() { }
    public MathSym(string text) { Text = text; }
}

/// <summary>分数(也用于 \dfrac \tfrac \cfrac)。</summary>
public sealed class MathFrac : MathNode
{
    public MathNode Num { get; set; } = new MathRow();
    public MathNode Den { get; set; } = new MathRow();
    /// <summary>行内公式要不要缩小一号(TeX 的 textstyle 行为)。</summary>
    public bool Display { get; set; }
}

/// <summary>根号(Index 非空时是 \sqrt[n]{x})。</summary>
public sealed class MathSqrt : MathNode
{
    public MathNode Radicand { get; set; } = new MathRow();
    public MathNode? Index { get; set; }
}

/// <summary>上下标(可能只有其中一个; Limits 时摆成正上下)。</summary>
public sealed class MathScript : MathNode
{
    public MathNode Base { get; set; } = new MathRow();
    public MathNode? Sub { get; set; }
    public MathNode? Sup { get; set; }
    /// <summary>上下标是否叠在正上/正下(\sum_{i=1}^n 的块级写法)。</summary>
    public bool Limits { get; set; }
}

/// <summary>带左右定界符的内容(\left( ... \right))或普通括号自动放大。</summary>
public sealed class MathDelim : MathNode
{
    public string? Left { get; set; }
    public string? Right { get; set; }
    public MathNode Body { get; set; } = new MathRow();
    /// <summary>人为放大倍率(\big \Big \bigg \Bigg 用), 1 = 自动按内容高度。</summary>
    public double SizeFactor { get; set; } = 1;
    public bool Auto { get; set; } = true;
}

/// <summary>整体换字体/字重(\mathbf \mathrm \mathit ...)。</summary>
public sealed class MathStyled : MathNode
{
    public MathNode Body { get; set; } = new MathRow();
    public MathFontKind Font { get; set; } = MathFontKind.MathUpright;
    public bool Italic { get; set; }
}

/// <summary>重音/上划线(\vec \hat \bar \overline \underline ...)。</summary>
public sealed class MathAccent : MathNode
{
    public MathNode Base { get; set; } = new MathRow();
    public MathAccentKind Kind { get; set; } = MathAccentKind.Hat;
}

public enum MathAccentKind { Hat, Bar, Vec, Dot, Ddot, Tilde, Overline, Underline, WideHat, WideTilde, OverRightArrow }

/// <summary>\boxed{...}: 套一个方框。</summary>
public sealed class MathFrame : MathNode
{
    public MathNode Body { get; set; } = new MathRow();
}

/// <summary>空白(\quad \qquad \, \; \! 空格)。</summary>
public sealed class MathSpace : MathNode
{
    public double Em { get; set; }
    public MathSpace() { }
    public MathSpace(double em) { Em = em; }
}

public enum MathEnvKind
{
    /// <summary>aligned/align/split: &amp; 对齐, 奇数列右对齐偶数列左对齐</summary>
    Aligned,
    /// <summary>gathered/gather: 每行居中</summary>
    Gathered,
    /// <summary>cases: 左边一个大括号 + 两列左对齐</summary>
    Cases,
    /// <summary>matrix/pmatrix/bmatrix/...: 网格居中, 看 Kind 加不同括号</summary>
    Matrix,
    /// <summary>array: 按列格式(lcr)对齐</summary>
    Array,
}

/// <summary>\begin{...}...\end{...} 环境。</summary>
public sealed class MathEnv : MathNode
{
    public MathEnvKind Kind { get; set; } = MathEnvKind.Aligned;
    /// <summary>每一行; 每行是若干单元格(&amp; 分隔)。</summary>
    public List<List<MathNode>> Rows { get; } = new();
    /// <summary>array 的列格式, 例如 "lcr"; 其它环境为空。</summary>
    public string? ColumnSpec { get; set; }
    /// <summary>矩阵类环境的左右定界符(空 = 不加)。</summary>
    public string? LeftDelim { get; set; }
    public string? RightDelim { get; set; }
}
