using System.Text;

namespace TCPChat10.Rendering;

/// <summary>
/// LaTeX 数学公式 -&gt; MathNode 树(聊天里够用的一个子集)。
///
/// 支持: 上下标 _ ^ ' / 分数 \frac \dfrac \tfrac / 根号 \sqrt \sqrt[n]{} /
/// \left(...\right) 自动放大括号 / \sum \int \lim 等大运算符(块级时上下限摆在正上下) /
/// \begin{aligned} \begin{cases} \begin{pmatrix} 等环境(&amp; 对齐, \\ 换行) /
/// \boxed / \vec \hat \bar \overline 等重音 / \text \mathrm \mathbf 换字体 /
/// \quad \qquad \, 等空白 / 常用希腊字母与运算符符号表。
///
/// 不认识的命令按原样显示(不会悄悄吞掉), 解析出意外也退回"原文显示"。
/// 纯逻辑, 不碰界面类型, 离线测试能直接跑。
/// </summary>
public static class MathParser
{
    /// <summary>上次解析失败的原因(正常是 null)。</summary>
    public static string? LastError { get; private set; }

    /// <summary>解析公式(display = 块级公式, 分数/根号更大, 大运算符上下限摆正上下)。</summary>
    public static MathNode Parse(string? latex)
    {
        LastError = null;
        if (string.IsNullOrWhiteSpace(latex)) return new MathRow();
        try
        {
            var p = new P(latex!);
            return p.ParseDocument();
        }
        catch (Exception ex)
        {
            // 公式写错/遇到没见过的语法: 原样显示, 不能让整条消息消失
            LastError = ex.GetType().Name + ": " + ex.Message;
            var row = new MathRow();
            row.Items.Add(new MathSym(latex!.Trim()) { Font = MathFontKind.MathUpright });
            return row;
        }
    }

    /// <summary>公式的纯文本形式(系统通知、引用预览用)。</summary>
    public static string ToPlainText(string? latex) => MathPlain.Render(Parse(latex));

    // ================= 解析 =================

    private sealed class P
    {
        private readonly string _s;
        private int _i;

        public P(string s) { _s = s; }

        private bool Eof => _i >= _s.Length;
        private char Cur => _i < _s.Length ? _s[_i] : '\0';
        private char Peek(int n = 1) => _i + n < _s.Length ? _s[_i + n] : '\0';
        private void Advance() => _i++;
        private bool Looking(string text) => string.CompareOrdinal(_s, _i, text, 0, text.Length) == 0 && _i + text.Length <= _s.Length;
        private void SkipSpaces() { while (!Eof && (Cur == ' ' || Cur == '\t' || Cur == '\r' || Cur == '\n')) Advance(); }

        // ---------- 顶层 ----------

        public MathNode ParseDocument()
        {
            SkipSpaces();
            var first = ParseRow();
            SkipSpaces();
            if (!(Cur == '&' || (Cur == '\\' && Peek(1) == '\\'))) return first;

            // 顶层就写了 & 或 \\: 当成一个"多行公式"处理(居中或按 &amp; 对齐)
            var env = new MathEnv { Kind = MathEnvKind.Gathered };
            var cells = new List<MathNode> { first };
            bool sawAmp = false;
            while (!Eof)
            {
                SkipSpaces();
                if (Cur == '&') { sawAmp = true; Advance(); cells.Add(ParseRow()); continue; }
                if (Cur == '\\' && Peek(1) == '\\')
                {
                    Advance(); Advance(); SkipRowSize();
                    env.Rows.Add(cells);
                    cells = new List<MathNode> { ParseRow() };
                    continue;
                }
                break;
            }
            env.Rows.Add(cells);
            if (sawAmp) env.Kind = MathEnvKind.Aligned;
            return env;
        }

        /// <summary>解析一行, 遇到 } &amp; \\ \right \end 就停(stopAtBracket 时 ] 也停)。</summary>
        private MathRow ParseRow(bool stopAtBracket = false)
        {
            var row = new MathRow();
            while (true)
            {
                SkipSpaces();
                if (Eof) break;
                char c = Cur;
                if (c == '}') break;
                if (c == ']' && stopAtBracket) break;
                if (c == '&') break;
                if (c == '\\' && Peek(1) == '\\') break;
                if (c == '\\' && (Looking("\\right") || Looking("\\end"))) break;

                var before = _i;
                var item = ParseItem();
                if (item != null) row.Items.Add(item);
                if (_i == before) Advance();          // 兜底: 保证一定往前走, 不会死循环
            }
            return row;
        }

        // ---------- 一个"带上下标的原子" ----------

        private MathNode? ParseItem()
        {
            var atom = ParseAtom();
            if (atom == null) return null;

            MathNode? sub = null, sup = null;
            while (true)
            {
                SkipSpaces();
                if (Cur == '\\' && Looking("\\limits")) { Advance(); _i += 6; continue; }
                if (Cur == '\\' && Looking("\\nolimits")) { Advance(); _i += 8; continue; }
                if (Cur == '^') { Advance(); sup = ParseScriptArg(); continue; }
                if (Cur == '_') { Advance(); sub = ParseScriptArg(); continue; }
                if (Cur == '\'') { Advance(); sup = AppendPrime(sup); continue; }
                break;
            }
            if (sub == null && sup == null) return atom;

            bool limits = atom is MathSym { Kind: MathKind.Big, Limits: true };
            return new MathScript { Base = atom, Sub = sub, Sup = sup, Limits = limits };
        }

        private static MathNode AppendPrime(MathNode? sup)
        {
            var prime = new MathSym("′") { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
            if (sup == null) return prime;
            var row = new MathRow();
            row.Items.Add(sup);
            row.Items.Add(prime);
            return row;
        }

        /// <summary>上下标的内容: {..} 或单个原子。</summary>
        private MathNode ParseScriptArg()
        {
            SkipSpaces();
            if (Eof) return new MathRow();
            if (Cur == '{')
            {
                Advance();
                var body = ParseRow();
                SkipSpaces();
                if (Cur == '}') Advance();
                return body;
            }
            var atom = ParseAtom();
            return atom ?? new MathRow();
        }

        // ---------- 原子 ----------

        private MathNode? ParseAtom()
        {
            if (Eof) return null;
            char c = Cur;

            if (c == '{')
            {
                Advance();
                var body = ParseRow();
                SkipSpaces();
                if (Cur == '}') Advance();
                return body;
            }
            if (c == '}') return null;
            if (c == '\\') return ParseCommand();

            // 单个字符
            Advance();
            return CharAtom(c);
        }

        private static MathNode CharAtom(char c)
        {
            switch (c)
            {
                case '(': case '[': case '{':
                    return new MathSym(c.ToString()) { Kind = MathKind.Open, Font = MathFontKind.MathUpright };
                case ')': case ']': case '}':
                    return new MathSym(c.ToString()) { Kind = MathKind.Close, Font = MathFontKind.MathUpright };
                case '|': case '/':
                    return new MathSym(c.ToString()) { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
                case '+':
                    return new MathSym("+") { Kind = MathKind.Bin, Font = MathFontKind.MathUpright };
                case '-':
                    return new MathSym("−") { Kind = MathKind.Bin, Font = MathFontKind.MathUpright };
                case '*':
                    return new MathSym("∗") { Kind = MathKind.Bin, Font = MathFontKind.MathUpright };
                case '=':
                    return new MathSym("=") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright };
                case '<': case '>':
                    return new MathSym(c.ToString()) { Kind = MathKind.Rel, Font = MathFontKind.MathUpright };
                case ',':
                    return new MathSym(",") { Kind = MathKind.Punct, Font = MathFontKind.MathUpright };
                case ';':
                    return new MathSym(";") { Kind = MathKind.Punct, Font = MathFontKind.MathUpright };
                case ':':
                    return new MathSym(":") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright };
                case '!': case '?':
                    return new MathSym(c.ToString()) { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
                case '~':
                    return new MathSpace(0.35);
                case '\u00a0': case '\u3000':
                    return new MathSpace(0.5);
                case '\'':
                    return new MathSym("′") { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
            }

            if (c >= '0' && c <= '9')
                return new MathSym(c.ToString()) { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };

            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
            {
                // 变量用斜体(和 TeX 一样), 单字母之外的连着写也不拆
                return new MathSym(c.ToString()) { Kind = MathKind.Ord, Italic = true };
            }

            // 中文/其它字符: 直立显示, 不做斜体
            return new MathSym(c.ToString()) { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
        }

        // ---------- 命令 ----------

        private MathNode? ParseCommand()
        {
            _i++;                                  // 吃掉反斜杠
            if (Eof) return new MathSym("\\") { Font = MathFontKind.MathUpright };

            char c = Cur;
            // 反斜杠 + 单个符号: 转义(\{ \} \_ \% ...) 或空白/换行
            if (!char.IsLetter(c))
            {
                Advance();
                switch (c)
                {
                    case '{': return new MathSym("{") { Kind = MathKind.Open, Font = MathFontKind.MathUpright };
                    case '}': return new MathSym("}") { Kind = MathKind.Close, Font = MathFontKind.MathUpright };
                    case '_': case '%': case '&': case '#': case '$':
                        return new MathSym(c.ToString()) { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
                    case ' ': case '\t': case '\n': case '\r':
                        return new MathSpace(0.35);
                    case ',': return new MathSpace(0.17);
                    case ':': return new MathSpace(0.22);
                    case ';': return new MathSpace(0.28);
                    case '!': return new MathSpace(-0.17);
                    case '/': return new MathSpace(0.05);
                    case '-': return new MathSpace(0.1);
                    default:
                        return new MathSym(c.ToString()) { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
                }
            }

            var name = ReadName();
            return NamedCommand(name);
        }

        private string ReadName()
        {
            var start = _i;
            while (!Eof && char.IsLetter(Cur)) Advance();
            return _s[start.._i];
        }

        private MathNode? NamedCommand(string name)
        {
            switch (name)
            {
                case "frac": case "dfrac": case "tfrac": case "cfrac":
                {
                    var num = ParseScriptArg();
                    var den = ParseScriptArg();
                    return new MathFrac { Num = num, Den = den, Display = name != "tfrac" };
                }

                case "sqrt":
                {
                    SkipSpaces();
                    MathNode? index = null;
                    if (Cur == '[')
                    {
                        Advance();
                        index = ParseRow(stopAtBracket: true);
                        SkipSpaces();
                        if (Cur == ']') Advance();
                    }
                    var radicand = ParseScriptArg();
                    return new MathSqrt { Radicand = radicand, Index = index };
                }

                case "text": case "textrm": case "textnormal": case "mbox": case "hbox":
                    return new MathSym(ReadRawGroup()) { Font = MathFontKind.Text, Kind = MathKind.Ord };

                case "operatorname":
                {
                    var t = ReadRawGroup();
                    if (string.IsNullOrEmpty(t))
                    {
                        // \operatorname*{lim} 这种带星号的写法
                        if (Cur == '*') Advance();
                        t = ReadRawGroup();
                    }
                    return new MathSym(t) { Font = MathFontKind.MathUpright, Kind = MathKind.Op };
                }

                case "mathrm": case "mathsf": case "textup":
                    return new MathStyled { Body = StyleArg(), Font = MathFontKind.MathUpright };
                case "mathbf": case "boldsymbol": case "bm": case "textbf":
                    return new MathStyled { Body = StyleArg(), Font = MathFontKind.MathBold };
                case "mathit": case "textit":
                    return new MathStyled { Body = StyleArg(), Font = MathFontKind.Math, Italic = true };
                case "mathtt": case "texttt":
                    return new MathStyled { Body = StyleArg(), Font = MathFontKind.Mono };
                case "mathcal": case "mathscr":
                    return new MathStyled { Body = StyleArg(), Font = MathFontKind.Math, Italic = false };
                case "mathbb": case "Bbb":
                    return new MathStyled { Body = StyleArg(), Font = MathFontKind.Math, Italic = false };

                case "boxed": case "fbox":
                    return new MathFrame { Body = StyleArg() };

                case "overline": case "bar":
                    return new MathAccent { Base = StyleArg(), Kind = name == "bar" ? MathAccentKind.Bar : MathAccentKind.Overline };
                case "underline":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.Underline };
                case "vec":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.Vec };
                case "hat":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.Hat };
                case "widehat":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.WideHat };
                case "tilde":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.Tilde };
                case "widetilde":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.WideTilde };
                case "dot":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.Dot };
                case "ddot":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.Ddot };
                case "overrightarrow":
                    return new MathAccent { Base = StyleArg(), Kind = MathAccentKind.OverRightArrow };

                case "left":
                    return ParseLeftRight();
                case "right":
                    return null;                    // 落单的 \right: 忽略

                case "begin":
                    return ParseEnvironment();

                case "quad": return new MathSpace(1.0);
                case "qquad": return new MathSpace(2.0);
                case "enspace": return new MathSpace(0.5);
                case "thinspace": return new MathSpace(0.17);
                case "hspace": case "kern": case "mkern": case "mskip": case "hskip": case "phantom": case "vphantom": case "hphantom":
                    ReadRawGroup();
                    return null;
                case "limits": case "nolimits": case "displaystyle": case "textstyle":
                case "scriptstyle": case "scriptscriptstyle": case "nonumber": case "notag":
                case "allowbreak": case "relax": case "mathstrut":
                    return null;
                case "label": case "tag": case "ref": case "eqref":
                    ReadRawGroup();
                    return null;

                case "pmod":
                {
                    var arg = StyleArg();
                    var row = new MathRow();
                    row.Items.Add(new MathSpace(0.5));
                    row.Items.Add(new MathSym("(") { Kind = MathKind.Open, Font = MathFontKind.MathUpright });
                    row.Items.Add(new MathSym("mod") { Kind = MathKind.Op, Font = MathFontKind.MathUpright });
                    row.Items.Add(new MathSpace(0.3));
                    row.Items.Add(arg);
                    row.Items.Add(new MathSym(")") { Kind = MathKind.Close, Font = MathFontKind.MathUpright });
                    return row;
                }

                case "not":
                    return ParseNot();

                // 大运算符 / 函数名: 走符号表(下面 Symbols 里有)
            }

            if (Symbols.TryGetValue(name, out var sym))
            {
                var node = new MathSym(sym.Text) { Kind = sym.Kind, Font = sym.Font, Limits = sym.Limits };
                node.Italic = sym.Italic;
                return node;
            }

            // 不认识的命令: 原样显示, 免得内容凭空消失
            return new MathSym("\\" + name) { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
        }

        private MathNode StyleArg()
        {
            SkipSpaces();
            if (Cur == '{')
            {
                Advance();
                var body = ParseRow();
                SkipSpaces();
                if (Cur == '}') Advance();
                return body;
            }
            var atom = ParseAtom();
            return atom ?? new MathRow();
        }

        /// <summary>读 { ... } 里的原始文字(不解析公式, 空格保留)。没有花括号就取一个原子。</summary>
        private string ReadRawGroup()
        {
            SkipSpaces();
            if (Cur != '{')
            {
                var single = ParseAtom();
                return single is MathSym s ? s.Text : "";
            }
            Advance();
            var sb = new StringBuilder();
            int depth = 1;
            while (!Eof)
            {
                char c = Cur;
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { Advance(); break; }
                }
                else if (c == '\\' && Peek(1) == '\\')     // \\{ \\} 之类的转义
                {
                    char n = Peek(1);
                    if (n == '{' || n == '}' || n == '_' || n == '%' || n == '&' || n == '#' || n == '$')
                    {
                        sb.Append(n);
                        Advance(); Advance();
                        continue;
                    }
                }
                sb.Append(c);
                Advance();
            }
            return sb.ToString();
        }

        private MathNode ParseLeftRight()
        {
            var left = ReadDelimiter();
            var body = ParseRow();
            SkipSpaces();
            string? right = null;
            if (Looking("\\right"))
            {
                _i += 6;
                right = ReadDelimiter();
            }
            return new MathDelim { Left = left, Right = right, Body = body, Auto = true };
        }

        private string? ReadDelimiter()
        {
            SkipSpaces();
            if (Eof) return null;
            char c = Cur;
            if (c == '\\')
            {
                _i++;
                if (Eof) return null;
                if (char.IsLetter(Cur))
                {
                    var name = ReadName();
                    return name switch
                    {
                        "langle" => "⟨", "rangle" => "⟩",
                        "lceil" => "⌈", "rceil" => "⌉",
                        "lfloor" => "⌊", "rfloor" => "⌋",
                        "lvert" => "|", "rvert" => "|",
                        "lVert" => "‖", "rVert" => "‖",
                        "vert" => "|", "Vert" => "‖",
                        "uparrow" => "↑", "downarrow" => "↓", "updownarrow" => "↕",
                        "backslash" => "\\",
                        _ => null,
                    };
                }
                char e = Cur;
                Advance();
                return e switch
                {
                    '{' => "{", '}' => "}", '|' => "‖", '.' => null,
                    _ => e.ToString(),
                };
            }
            if (c == '.' ) { Advance(); return null; }
            Advance();
            return c.ToString();
        }

        private MathNode ParseNot()
        {
            SkipSpaces();
            if (Cur == '=')
            {
                Advance();
                return new MathSym("≠") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright };
            }
            if (Cur == '\\')
            {
                var save = _i;
                var next = ParseAtom();
                if (next is MathSym s)
                {
                    return s.Text switch
                    {
                        "∈" => new MathSym("∉") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright },
                        "∋" => new MathSym("∌") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright },
                        "⊂" => new MathSym("⊄") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright },
                        "⊃" => new MathSym("⊅") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright },
                        "≡" => new MathSym("≢") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright },
                        "≤" => new MathSym("≰") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright },
                        "≥" => new MathSym("≱") { Kind = MathKind.Rel, Font = MathFontKind.MathUpright },
                        _ => new MathSym("̸" + s.Text) { Kind = MathKind.Rel, Font = MathFontKind.MathUpright },
                    };
                }
                _i = save;
            }
            return new MathSym("̸") { Kind = MathKind.Ord, Font = MathFontKind.MathUpright };
        }

        /// <summary>\\begin{xxx} ... \\end{xxx}</summary>
        private MathNode ParseEnvironment()
        {
            var name = ReadRawGroup().Trim();
            string? spec = null;
            if (name is "array" or "matrix*" or "pmatrix*" or "bmatrix*" or "Bmatrix*" or "vmatrix*" or "Vmatrix*")
            {
                SkipSpaces();
                if (Cur == '{') spec = ReadRawGroup();
            }

            var env = new MathEnv();
            switch (name)
            {
                case "aligned": case "align": case "align*": case "alignedat": case "split": case "flalign": case "flalign*":
                    env.Kind = MathEnvKind.Aligned;
                    break;
                case "gathered": case "gather": case "gather*": case "multline": case "multline*":
                    env.Kind = MathEnvKind.Gathered;
                    break;
                case "cases": case "dcases": case "rcases":
                    env.Kind = MathEnvKind.Cases;
                    break;
                case "array":
                    env.Kind = MathEnvKind.Array;
                    env.ColumnSpec = spec;
                    break;
                case "pmatrix": case "pmatrix*":
                    env.Kind = MathEnvKind.Matrix; env.LeftDelim = "("; env.RightDelim = ")"; break;
                case "bmatrix": case "bmatrix*":
                    env.Kind = MathEnvKind.Matrix; env.LeftDelim = "["; env.RightDelim = "]"; break;
                case "Bmatrix": case "Bmatrix*":
                    env.Kind = MathEnvKind.Matrix; env.LeftDelim = "{"; env.RightDelim = "}"; break;
                case "vmatrix": case "vmatrix*":
                    env.Kind = MathEnvKind.Matrix; env.LeftDelim = "|"; env.RightDelim = "|"; break;
                case "Vmatrix": case "Vmatrix*":
                    env.Kind = MathEnvKind.Matrix; env.LeftDelim = "‖"; env.RightDelim = "‖"; break;
                case "matrix": case "smallmatrix":
                    env.Kind = MathEnvKind.Matrix; break;
                case "matrix*":
                    env.Kind = MathEnvKind.Matrix; env.ColumnSpec = spec; break;
                default:
                    env.Kind = MathEnvKind.Aligned;
                    break;
            }

            // 一行一行地读, & 分列, \\ 换行, 直到 \end{name}
            var cells = new List<MathNode> { ParseRow() };
            while (true)
            {
                SkipSpaces();
                if (Eof) break;
                if (Cur == '&') { Advance(); cells.Add(ParseRow()); continue; }
                if (Cur == '\\' && Peek(1) == '\\')
                {
                    Advance(); Advance(); SkipRowSize();
                    env.Rows.Add(cells);
                    cells = new List<MathNode> { ParseRow() };
                    continue;
                }
                break;
            }
            env.Rows.Add(cells);

            SkipSpaces();
            if (Looking("\\end"))
            {
                _i += 4;
                ReadRawGroup();                      // 环境名, 不校验(宽容一点)
            }
            return env;
        }

        /// <summary>\\ 后面的可选尺寸 [2pt] —— 读掉不用。</summary>
        private void SkipRowSize()
        {
            SkipSpaces();
            if (Cur != '[') return;
            var save = _i;
            Advance();
            var sb = new StringBuilder();
            while (!Eof && Cur != ']' && sb.Length < 32) { sb.Append(Cur); Advance(); }
            if (Cur == ']')
            {
                var text = sb.ToString().Trim();
                if (text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '-' || text[0] == '+')) { Advance(); return; }
            }
            _i = save;
        }
    }

    // ================= 符号表 =================

    private readonly record struct Sym(string Text, MathKind Kind, MathFontKind Font, bool Italic, bool Limits);

    private static Sym Ord(string t, bool italic = false) => new(t, MathKind.Ord, italic ? MathFontKind.Math : MathFontKind.MathUpright, italic, false);
    private static Sym Bin(string t) => new(t, MathKind.Bin, MathFontKind.MathUpright, false, false);
    private static Sym Rel(string t) => new(t, MathKind.Rel, MathFontKind.MathUpright, false, false);
    private static Sym Big(string t, bool limits, MathKind kind = MathKind.Big) => new(t, kind, MathFontKind.MathUpright, false, limits);
    private static Sym Op(string t) => new(t, MathKind.Op, MathFontKind.MathUpright, false, false);

    private static readonly Dictionary<string, Sym> Symbols = BuildSymbols();

    private static Dictionary<string, Sym> BuildSymbols()
    {
        var d = new Dictionary<string, Sym>(StringComparer.Ordinal);

        void Add(Sym s, params string[] names) { foreach (var n in names) d[n] = s; }

        // 小写希腊字母(TeX 里默认斜体)
        Add(Ord("α", true), "alpha");
        Add(Ord("β", true), "beta");
        Add(Ord("γ", true), "gamma");
        Add(Ord("δ", true), "delta");
        Add(Ord("ε", true), "varepsilon", "epsilon");
        Add(Ord("ϵ"), "epsilon2");
        Add(Ord("ζ", true), "zeta");
        Add(Ord("η", true), "eta");
        Add(Ord("θ", true), "theta");
        Add(Ord("ϑ"), "vartheta");
        Add(Ord("ι", true), "iota");
        Add(Ord("κ", true), "kappa");
        Add(Ord("λ", true), "lambda");
        Add(Ord("μ", true), "mu");
        Add(Ord("ν", true), "nu");
        Add(Ord("ξ", true), "xi");
        Add(Ord("π", true), "pi");
        Add(Ord("ϖ"), "varpi");
        Add(Ord("ρ", true), "rho");
        Add(Ord("ϱ"), "varrho");
        Add(Ord("σ", true), "sigma");
        Add(Ord("ς"), "varsigma");
        Add(Ord("τ", true), "tau");
        Add(Ord("υ", true), "upsilon");
        Add(Ord("φ", true), "varphi");
        Add(Ord("ϕ"), "phi");
        Add(Ord("χ", true), "chi");
        Add(Ord("ψ", true), "psi");
        Add(Ord("ω", true), "omega");

        // 大写希腊字母
        Add(Ord("Γ"), "Gamma");
        Add(Ord("Δ"), "Delta");
        Add(Ord("Θ"), "Theta");
        Add(Ord("Λ"), "Lambda");
        Add(Ord("Ξ"), "Xi");
        Add(Ord("Π"), "Pi");
        Add(Ord("Σ"), "Sigma");
        Add(Ord("Υ"), "Upsilon");
        Add(Ord("Φ"), "Phi");
        Add(Ord("Ψ"), "Psi");
        Add(Ord("Ω"), "Omega");

        // 常用普通符号
        Add(Ord("∞"), "infty");
        Add(Ord("∂"), "partial");
        Add(Ord("∇"), "nabla");
        Add(Ord("∀"), "forall");
        Add(Ord("∃"), "exists");
        Add(Ord("∄"), "nexists");
        Add(Ord("∅"), "emptyset", "varnothing");
        Add(Ord("∠"), "angle");
        Add(Ord("△"), "triangle");
        Add(Ord("□"), "square");
        Add(Ord("ℏ"), "hbar");
        Add(Ord("ℓ"), "ell");
        Add(Ord("ℜ"), "Re");
        Add(Ord("ℑ"), "Im");
        Add(Ord("℘"), "wp");
        Add(Ord("…"), "ldots", "dots", "dotsc");
        Add(Ord("⋯"), "cdots");
        Add(Ord("⋮"), "vdots");
        Add(Ord("⋱"), "ddots");
        Add(Ord("′"), "prime");
        Add(Ord("″"), "dprime");
        Add(Ord("°"), "degree", "circ");
        Add(Ord("•"), "bullet");
        Add(Ord("★"), "bigstar");
        Add(Ord("♠"), "spadesuit");
        Add(Ord("♣"), "clubsuit");
        Add(Ord("♥"), "heartsuit");
        Add(Ord("♦"), "diamondsuit");
        Add(Ord("ℵ"), "aleph");
        Add(Ord("ℶ"), "beth");
        Add(Ord("⌊"), "lfloor");
        Add(Ord("⌋"), "rfloor");
        Add(Ord("⌈"), "lceil");
        Add(Ord("⌉"), "rceil");
        Add(Ord("⟨"), "langle");
        Add(Ord("⟩"), "rangle");
        Add(Ord("‖"), "Vert", "lVert", "rVert");
        Add(Ord("\u00a0"), "nobreakspace");

        // 二元运算符
        Add(Bin("±"), "pm");
        Add(Bin("∓"), "mp");
        Add(Bin("×"), "times");
        Add(Bin("÷"), "div");
        Add(Bin("⋅"), "cdot");
        Add(Bin("∗"), "ast");
        Add(Bin("⋆"), "star");
        Add(Bin("∘"), "circ2");
        Add(Bin("∪"), "cup");
        Add(Bin("∩"), "cap");
        Add(Bin("∖"), "setminus");
        Add(Bin("⊕"), "oplus");
        Add(Bin("⊖"), "ominus");
        Add(Bin("⊗"), "otimes");
        Add(Bin("⊘"), "oslash");
        Add(Bin("⊙"), "odot");
        Add(Bin("⊎"), "uplus");
        Add(Bin("⊔"), "sqcup");
        Add(Bin("⊓"), "sqcap");
        Add(Bin("∨"), "vee", "lor");
        Add(Bin("∧"), "wedge", "land");
        Add(Bin("†"), "dagger");
        Add(Bin("‡"), "ddagger");
        Add(Bin("⋄"), "diamond");
        Add(Bin("△"), "bigtriangleup");
        Add(Bin("▽"), "bigtriangledown");
        Add(Bin("◁"), "triangleleft");
        Add(Bin("▷"), "triangleright");
        Add(Bin("⋉"), "ltimes");
        Add(Bin("⋊"), "rtimes");
        Add(Bin("≀"), "wr");
        Add(Bin("∐"), "coprod2");

        // 关系符
        Add(Rel("="), "eq");
        Add(Rel("≠"), "ne", "neq");
        Add(Rel("≈"), "approx");
        Add(Rel("≃"), "simeq");
        Add(Rel("≅"), "cong");
        Add(Rel("≡"), "equiv");
        Add(Rel("∼"), "sim");
        Add(Rel("∽"), "backsim");
        Add(Rel("∝"), "propto");
        Add(Rel("≤"), "le", "leq");
        Add(Rel("≥"), "ge", "geq");
        Add(Rel("≪"), "ll");
        Add(Rel("≫"), "gg");
        Add(Rel("⊂"), "subset");
        Add(Rel("⊆"), "subseteq");
        Add(Rel("⊃"), "supset");
        Add(Rel("⊇"), "supseteq");
        Add(Rel("⊊"), "subsetneq");
        Add(Rel("∈"), "in");
        Add(Rel("∋"), "ni", "owns");
        Add(Rel("∉"), "notin");
        Add(Rel("∌"), "notni");
        Add(Rel("⊥"), "perp");
        Add(Rel("∥"), "parallel");
        Add(Rel("∦"), "nparallel");
        Add(Rel("∣"), "mid");
        Add(Rel("∤"), "nmid");
        Add(Rel("≺"), "prec");
        Add(Rel("≻"), "succ");
        Add(Rel("≼"), "preceq");
        Add(Rel("≽"), "succeq");
        Add(Rel("≍"), "asymp");
        Add(Rel("≐"), "doteq");
        Add(Rel("⊨"), "models");
        Add(Rel("⊢"), "vdash");
        Add(Rel("⊣"), "dashv");
        Add(Rel("⌣"), "smile");
        Add(Rel("⌢"), "frown");
        Add(Rel("∴"), "therefore");
        Add(Rel("∵"), "because");
        Add(Rel("≟"), "questeq");
        Add(Rel("≐"), "coloneq");

        // 箭头
        Add(Rel("→"), "to", "rightarrow");
        Add(Rel("←"), "leftarrow", "gets");
        Add(Rel("↔"), "leftrightarrow");
        Add(Rel("⇒"), "Rightarrow", "implies");
        Add(Rel("⇐"), "Leftarrow");
        Add(Rel("⇔"), "Leftrightarrow", "iff");
        Add(Rel("↦"), "mapsto");
        Add(Rel("↑"), "uparrow");
        Add(Rel("↓"), "downarrow");
        Add(Rel("↕"), "updownarrow");
        Add(Rel("⟶"), "longrightarrow");
        Add(Rel("⟵"), "longleftarrow");
        Add(Rel("⟹"), "Longrightarrow");
        Add(Rel("⟸"), "Longleftarrow");
        Add(Rel("⟺"), "Longleftrightarrow");
        Add(Rel("↪"), "hookrightarrow");
        Add(Rel("↩"), "hookleftarrow");
        Add(Rel("↗"), "nearrow");
        Add(Rel("↘"), "searrow");
        Add(Rel("↙"), "swarrow");
        Add(Rel("↖"), "nwarrow");

        // 大运算符(块级公式里上下标摆正上下)
        Add(Big("∑", true), "sum");
        Add(Big("∏", true), "prod");
        Add(Big("∐", true), "coprod");
        Add(Big("⋃", true), "bigcup");
        Add(Big("⋂", true), "bigcap");
        Add(Big("⨁", true), "bigoplus");
        Add(Big("⨂", true), "bigotimes");
        Add(Big("⋁", true), "bigvee");
        Add(Big("⋀", true), "bigwedge");
        Add(Big("⨆", true), "bigsqcup");
        Add(Big("∫", false), "int");
        Add(Big("∬", false), "iint");
        Add(Big("∭", false), "iiint");
        Add(Big("∮", false), "oint");
        Add(Big("∯", false), "oiint");
        Add(Big("lim", true, MathKind.Op), "lim");
        Add(Big("lim sup", true, MathKind.Op), "limsup");
        Add(Big("lim inf", true, MathKind.Op), "liminf");
        Add(Big("max", true, MathKind.Op), "max");
        Add(Big("min", true, MathKind.Op), "min");
        Add(Big("sup", true, MathKind.Op), "sup");
        Add(Big("inf", true, MathKind.Op), "inf");
        Add(Big("det", true, MathKind.Op), "det");
        Add(Big("gcd", true, MathKind.Op), "gcd");
        Add(Big("Pr", true, MathKind.Op), "Pr");

        // 函数名(直立)
        foreach (var f in new[]
        {
            "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan",
            "sinh", "cosh", "tanh", "coth", "log", "ln", "lg", "exp", "deg", "dim",
            "ker", "hom", "arg", "mod", "bmod", "div2",
        })
        {
            d[f] = Op(f switch { "div2" => "div", "bmod" => "mod", _ => f });
        }
        return d;
    }
}
