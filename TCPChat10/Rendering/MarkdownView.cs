using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TCPChat10.Services;
using Windows.UI;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;   // 和 Windows.UI.Text 里的同名类区分开

namespace TCPChat10.Rendering;

/// <summary>markdown 渲染用的配色/字号(由气泡的正文颜色推出来)。</summary>
public sealed class MarkdownStyle
{
    public Brush Foreground = new SolidColorBrush(Colors.Black);
    public Brush Muted = new SolidColorBrush(Colors.Gray);
    public Brush Accent = new SolidColorBrush(Colors.SteelBlue);
    public Brush CodeBack = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));
    public Brush CodeEdge = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));
    public Brush CodeText = new SolidColorBrush(Colors.Black);
    public Brush Keyword = new SolidColorBrush(Color.FromArgb(255, 0x7A, 0x3E, 0xD8));
    public Brush Str = new SolidColorBrush(Color.FromArgb(255, 0xB0, 0x5A, 0x00));
    public Brush Comment = new SolidColorBrush(Color.FromArgb(255, 0x55, 0x80, 0x55));
    public Brush Number = new SolidColorBrush(Color.FromArgb(255, 0x1C, 0x6E, 0xA8));
    public Brush QuoteBar = new SolidColorBrush(Colors.SteelBlue);
    public Brush QuoteBack = new SolidColorBrush(Color.FromArgb(18, 0, 0, 0));
    public Brush Rule = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
    public Brush TableEdge = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0));
    public Brush TableHead = new SolidColorBrush(Color.FromArgb(22, 0, 0, 0));
    /// <summary>11.4: 代码块里 lines=6-9 那种高亮行的底色。</summary>
    public Brush HighlightBack = new SolidColorBrush(Color.FromArgb(40, 0x2B, 0x6C, 0xB0));
    public double FontSize = 14;
}

/// <summary>
/// MdDocument -> WinUI 元素(10.7)。
/// 这里只负责"画"; 支持哪些语法由 MarkdownParser 决定, 那部分是纯逻辑, 能离线测。
/// </summary>
public static class MarkdownView
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>纯文本快路径: 没有 markdown 标记的消息直接一个 TextBlock, 不进解析器。</summary>
    public static TextBlock BuildPlain(string text, MarkdownStyle style)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = style.FontSize,
            IsTextSelectionEnabled = true,
            Foreground = style.Foreground,
        };
        UiFont.ApplyToText(tb);
        return tb;
    }

    public static UIElement Build(MdDocument doc, MarkdownStyle style)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var block in doc.Blocks) AddBlock(panel, block, style, 0);
        if (panel.Children.Count == 0) return BuildPlain(doc.Source, style);

        // 只有一段纯文字是最常见的情况: 摊平, 少一层布局
        if (panel.Children.Count == 1 && panel.Children[0] is RichTextBlock only)
        {
            panel.Children.RemoveAt(0);
            return only;
        }
        return panel;
    }

    private static void AddBlock(Panel host, MdBlock block, MarkdownStyle style, int depth)
    {
        switch (block)
        {
            case MdParagraph p:
                AddParagraph(host, p.Inlines, style, depth);
                break;
            case MdHeading h:
                AddParagraph(host, h.Inlines, style, depth, heading: h.Level);
                break;
            case MdCodeBlock c:
                host.Children.Add(CodeBlock(c, style));
                break;
            case MdMathBlock m:
                // 11.4: 真的排版 LaTeX(分数/根号/矩阵环境), 排版不出来才退回等宽原文
                host.Children.Add(MathView.BuildDisplay(m.Code, style));
                break;
            case MdRule:
                host.Children.Add(new Border { Height = 1, Background = style.Rule, Margin = new Thickness(0, 4, 0, 4) });
                break;
            case MdQuote q:
                host.Children.Add(Quote(q, style, depth));
                break;
            case MdList l:
                host.Children.Add(List(l, style, depth));
                break;
            case MdTable t:
                host.Children.Add(Table(t, style));
                break;
            case MdContainer c:
                host.Children.Add(Container(c, style, depth));
                break;
            case MdDefinitionList dl:
                host.Children.Add(DefinitionList(dl, style, depth));
                break;
            case MdHtmlBlock html:
                host.Children.Add(CodeBlock(html.Text, "html", style));
                break;
            case MdFootnotes notes:
                host.Children.Add(Footnotes(notes, style, depth));
                break;
        }
    }

    // ---------- 段落 / 标题 ----------

    private static void AddParagraph(Panel host, List<MdInline> inlines, MarkdownStyle style,
                                     int depth, int heading = 0)
    {
        var images = new List<MdImage>();
        var text = BuildText(inlines, style, images, out bool hasText);
        if (hasText) host.Children.Add(text);
        foreach (var img in images) host.Children.Add(ImageBox(img, style));

        if (heading > 0 && hasText)
        {
            text.FontSize = HeadingSize(heading, style.FontSize);
            text.FontWeight = FontWeights.SemiBold;
            if (heading <= 2)
            {
                text.Margin = new Thickness(0, 2, 0, 0);
                host.Children.Add(new Border
                {
                    Height = 1,
                    Background = style.Rule,
                    Margin = new Thickness(0, 0, 0, 2),
                });
            }
        }
    }

    private static double HeadingSize(int level, double baseSize) => level switch
    {
        1 => baseSize + 6,
        2 => baseSize + 4,
        3 => baseSize + 2.5,
        4 => baseSize + 1.5,
        5 => baseSize + 1,
        _ => baseSize + 0.5,
    };

    /// <summary>把一行内元素画成 RichTextBlock(顺带把图片挑出来, 图片单独成块)。</summary>
    private static RichTextBlock BuildText(List<MdInline> inlines, MarkdownStyle style,
                                          List<MdImage> images, out bool hasText)
    {
        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = style.FontSize,
            IsTextSelectionEnabled = true,
            Foreground = style.Foreground,
        };
        UiFont.ApplyToText(rtb);

        var para = new Paragraph();
        var fmt = new Fmt();
        AddInlines(para.Inlines, inlines, style, fmt, images);
        hasText = para.Inlines.Count > 0;
        if (hasText) rtb.Blocks.Add(para);
        return rtb;
    }

    private struct Fmt
    {
        public bool Bold, Italic, Strike, Sub, Sup, Mark;
    }

    private static void AddInlines(InlineCollection target, List<MdInline> inlines, MarkdownStyle style,
                                   Fmt fmt, List<MdImage> images)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText t:
                    target.Add(TextRun(t.Text, style, fmt));
                    break;

                case MdBreak:
                    target.Add(new LineBreak());
                    break;

                case MdCodeSpan code:
                {
                    // 行内代码: 等宽 + 换个颜色(Run 没有背景色, 只能靠颜色区分)
                    var run = TextRun(code.Text, style, fmt);
                    run.FontFamily = UiFont.Mono;
                    run.Foreground = style.Accent;
                    target.Add(run);
                    break;
                }

                case MdStyle s:
                {
                    var inner = fmt;
                    switch (s.Kind)
                    {
                        case MdStyleKind.Bold: inner.Bold = true; break;
                        case MdStyleKind.Italic: inner.Italic = true; break;
                        case MdStyleKind.Strike: inner.Strike = true; break;
                        case MdStyleKind.Sub: inner.Sub = true; break;
                        case MdStyleKind.Sup: inner.Sup = true; break;
                        case MdStyleKind.Mark: inner.Mark = true; break;
                    }
                    AddInlines(target, s.Children, style, inner, images);
                    break;
                }

                case MdLink l:
                {
                    var link = new Hyperlink { Foreground = style.Accent };
                    var inner = fmt;
                    AddInlines(link.Inlines, l.Children, style, inner, images);
                    if (link.Inlines.Count == 0) link.Inlines.Add(TextRun(l.Url, style, fmt));
                    var url = l.Url;
                    if (!string.IsNullOrWhiteSpace(l.Title))
                        ToolTipService.SetToolTip(link, l.Title);
                    link.Click += (_, _) => OpenLink(url);
                    target.Add(link);
                    break;
                }

                case MdImage img:
                    images.Add(img);
                    break;

                case MdMathSpan m:
                    // 11.4: 行内公式跟着文字走(排不出来时内部会退回等宽原文)
                    target.Add(MathView.BuildInline(m.Text, style));
                    break;

                case MdFootnoteRef f:
                {
                    var run = TextRun("[" + f.Label + "]", style, fmt);
                    run.FontSize = style.FontSize * 0.78;
                    run.Foreground = style.Accent;
                    target.Add(run);
                    break;
                }
            }
        }
    }

    private static Run TextRun(string text, MarkdownStyle style, Fmt fmt)
    {
        var run = new Run { Text = text, FontFamily = UiFont.Family };
        run.FontWeight = fmt.Bold ? FontWeights.Bold : FontWeights.Normal;
        run.FontStyle = fmt.Italic ? FontStyle.Italic : FontStyle.Normal;
        if (fmt.Strike) run.TextDecorations = TextDecorations.Strikethrough;
        if (fmt.Sub || fmt.Sup) run.FontSize = style.FontSize * 0.78;
        if (fmt.Mark) run.Foreground = style.Accent;
        return run;
    }

    private static void OpenLink(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!url.Contains("://", StringComparison.Ordinal) &&
                !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch { }
    }

    // ---------- 代码块 ----------

    /// <summary>代码块(11.4 起支持洛谷的 line-numbers / lines=6-9 参数)。</summary>
    private static FrameworkElement CodeBlock(MdCodeBlock block, MarkdownStyle style)
        => CodeBlock(block.Code, block.Language, style, block.LineNumbers, block.HasHighlight ? block.IsHighlighted : null);

    private static FrameworkElement CodeBlock(string code, string? language, MarkdownStyle style,
                                              bool lineNumbers = false, Func<int, bool>? highlight = null)
    {
        code = (code ?? "").TrimEnd('\n', '\r');
        var inner = new StackPanel { Spacing = 4 };

        if (!string.IsNullOrWhiteSpace(language) && language != "math")
        {
            var label = new TextBlock
            {
                Text = language,
                FontSize = style.FontSize - 3,
                Foreground = style.Muted,
                Opacity = 0.85,
            };
            UiFont.ApplyToText(label, mono: true);
            inner.Children.Add(label);
        }

        if (lineNumbers || highlight != null)
        {
            inner.Children.Add(CodeWithLines(code, language, style, lineNumbers, highlight));
        }
        else
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                FontSize = style.FontSize - 1,
                IsTextSelectionEnabled = true,
                Foreground = style.CodeText,
            };
            UiFont.ApplyToText(tb, mono: true);
            Highlighter.Fill(tb, code, language, style);
            inner.Children.Add(tb);
        }

        var scroller = new ScrollViewer
        {
            Content = inner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };

        return new Border
        {
            Background = style.CodeBack,
            BorderBrush = style.CodeEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = scroller,
        };
    }

    /// <summary>逐行画(要行号或要高亮时用): 左边一列行号, 右边一列代码。</summary>
    private static FrameworkElement CodeWithLines(string code, string? language, MarkdownStyle style,
                                                  bool lineNumbers, Func<int, bool>? highlight)
    {
        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var grid = new Grid { ColumnSpacing = 10 };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        double numberWidth = Math.Max(2, lines.Length.ToString().Length) * (style.FontSize - 1) * 0.62;

        for (int i = 0; i < lines.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int number = i + 1;

            if (highlight?.Invoke(number) == true)
            {
                var back = new Border { Background = style.HighlightBack, CornerRadius = new CornerRadius(3) };
                Grid.SetRow(back, i);
                Grid.SetColumn(back, 0);
                Grid.SetColumnSpan(back, 2);
                grid.Children.Add(back);
            }

            if (lineNumbers)
            {
                var num = new TextBlock
                {
                    Text = number.ToString(),
                    FontSize = style.FontSize - 2,
                    Foreground = style.Muted,
                    TextAlignment = TextAlignment.Right,
                    MinWidth = numberWidth,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                UiFont.ApplyToText(num, mono: true);
                Grid.SetRow(num, i);
                Grid.SetColumn(num, 0);
                grid.Children.Add(num);
            }

            var line = new TextBlock
            {
                TextWrapping = TextWrapping.NoWrap,
                FontSize = style.FontSize - 1,
                Foreground = style.CodeText,
                IsTextSelectionEnabled = true,
            };
            UiFont.ApplyToText(line, mono: true);
            Highlighter.Fill(line, lines[i], language, style);
            Grid.SetRow(line, i);
            Grid.SetColumn(line, 1);
            grid.Children.Add(line);
        }
        return grid;
    }

    // ---------- 引用 / 提示块 ----------

    private static FrameworkElement Quote(MdQuote q, MarkdownStyle style, int depth)
    {
        var body = new StackPanel { Spacing = 4 };
        foreach (var b in q.Blocks) AddBlock(body, b, style, depth + 1);

        Brush bar = style.QuoteBar;
        Brush? back = style.QuoteBack;
        string? badge = null;
        if (!string.IsNullOrWhiteSpace(q.Alert))
        {
            var kind = q.Alert!.Trim().ToUpperInvariant();
            badge = kind switch
            {
                "NOTE" => "ℹ️ 说明",
                "TIP" => "💡 提示",
                "IMPORTANT" => "❗ 重要",
                "WARNING" => "⚠️ 警告",
                "CAUTION" => "🛑 注意",
                _ => "ℹ️ " + kind,
            };
            bar = new SolidColorBrush(kind switch
            {
                "TIP" => Color.FromArgb(255, 0x2E, 0x9E, 0x5B),
                "IMPORTANT" => Color.FromArgb(255, 0x8B, 0x5C, 0xF6),
                "WARNING" => Color.FromArgb(255, 0xD9, 0x8A, 0x1E),
                "CAUTION" => Color.FromArgb(255, 0xD1, 0x3A, 0x3A),
                _ => Color.FromArgb(255, 0x2B, 0x7C, 0xD6),
            });
        }

        var content = new StackPanel { Spacing = 4 };
        if (badge != null)
        {
            var label = new TextBlock { Text = badge, FontSize = style.FontSize - 2, FontWeight = FontWeights.SemiBold, Foreground = bar };
            UiFont.ApplyToText(label);
            content.Children.Add(label);
        }
        content.Children.Add(body);

        return new Border
        {
            BorderBrush = bar,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = back,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(10, 6, 8, 6),
            Child = content,
        };
    }

    // ---------- 列表 ----------

    private static FrameworkElement List(MdList list, MarkdownStyle style, int depth)
    {
        var panel = new StackPanel { Spacing = 3 };
        int index = list.Start;
        foreach (var item in list.Items)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            string marker = item.IsTask
                ? (item.Checked ? "☑" : "☐")
                : list.Ordered ? index + "." : Bullet(depth);

            var mk = new TextBlock
            {
                Text = marker,
                FontSize = style.FontSize,
                Foreground = item.IsTask && item.Checked ? style.Muted : style.Foreground,
                MinWidth = list.Ordered ? 18 : 12,
                TextAlignment = list.Ordered ? TextAlignment.Right : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            UiFont.ApplyToText(mk);
            Grid.SetColumn(mk, 0);
            row.Children.Add(mk);

            var content = new StackPanel { Spacing = 3 };
            foreach (var b in item.Blocks) AddBlock(content, b, style, depth + 1);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            panel.Children.Add(row);
            index++;
        }
        return panel;
    }

    private static string Bullet(int depth) => (depth % 3) switch
    {
        0 => "•",
        1 => "◦",
        _ => "▪",
    };

    // ---------- 表格 ----------

    private static FrameworkElement Table(MdTable table, MarkdownStyle style)
    {
        int cols = Math.Max(1, table.Columns);
        var rows = table.Grid();                       // 表头是第一行
        var grid = new Grid();
        for (int i = 0; i < cols; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Count && c < cols; c++)
            {
                var cell = rows[r][c];
                if (cell.Hidden) continue;             // 11.4: ^ / < 合并掉的格子不画

                var el = TableCell(cell, style, c, r, isHeader: r == 0 && table.Headers.Count > 0,
                                   lastRow: r == rows.Count - 1, lastCol: c == cols - 1, tuack: table.Tuack);
                Grid.SetRow(el, r);
                Grid.SetColumn(el, c);
                if (cell.RowSpan > 1) Grid.SetRowSpan(el, cell.RowSpan);
                if (cell.ColSpan > 1) Grid.SetColumnSpan(el, cell.ColSpan);
                grid.Children.Add(el);
            }
        }

        var scroller = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };

        return new Border
        {
            BorderBrush = style.TableEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = scroller,
        };
    }

    private static FrameworkElement TableCell(MdTableCell? cell, MarkdownStyle style, int col, int row,
                                              bool isHeader, bool lastRow, bool lastCol, bool tuack = false)
    {
        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = style.FontSize - 1,
            IsTextSelectionEnabled = true,
            Foreground = style.Foreground,
        };
        UiFont.ApplyToText(rtb);

        var para = new Paragraph();
        var fmt = new Fmt { Bold = isHeader };
        AddInlines(para.Inlines, cell?.Inlines ?? new List<MdInline>(), style, fmt, new List<MdImage>());
        rtb.Blocks.Add(para);

        var host = new Border
        {
            Child = rtb,
            Padding = new Thickness(8, 5, 8, 5),
            Background = isHeader ? style.TableHead : null,
            BorderBrush = style.TableEdge,
            BorderThickness = new Thickness(0, 0, lastCol ? 0 : 1, lastRow ? 0 : 1),
        };

        // 11.4: ::cute-table{tuack} 的表一律居中(和洛谷一致)
        var align = (tuack ? "Center" : cell?.Align ?? "Left").ToLowerInvariant();
        switch (align)
        {
            case "center":
                rtb.TextAlignment = TextAlignment.Center;
                host.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;
            case "right":
                rtb.TextAlignment = TextAlignment.Right;
                host.HorizontalAlignment = HorizontalAlignment.Stretch;
                break;
        }

        Grid.SetColumn(host, col);
        Grid.SetRow(host, row);
        return host;
    }

    // ---------- 自定义容器 / 定义列表 / 脚注 ----------

    /// <summary>
    /// ::: 容器(11.4 按洛谷那套语法渲染):
    /// info / success / warning / error = 折叠框(标题可写公式, {open} 默认展开),
    /// align{center|right|left} = 对齐, epigraph[——作者] = 引言, 其余按带边框的普通容器画。
    /// </summary>
    private static FrameworkElement Container(MdContainer c, MarkdownStyle style, int depth)
    {
        var body = new StackPanel { Spacing = 4 };
        foreach (var b in c.Blocks) AddBlock(body, b, style, depth + 1);

        // 对齐
        if (c.Align is { Length: > 0 } align)
        {
            var host = new StackPanel { Spacing = 4 };
            foreach (var b in c.Blocks) AddBlock(host, b, style, depth);
            host.HorizontalAlignment = align switch
            {
                "center" => HorizontalAlignment.Center,
                "right" => HorizontalAlignment.Right,
                "left" => HorizontalAlignment.Left,
                _ => HorizontalAlignment.Stretch,
            };
            return host;
        }

        // 引言: 内容缩进 + 右下角的署名
        if (c.Label == "epigraph")
        {
            var quote = new StackPanel { Spacing = 6, Margin = new Thickness(14, 2, 6, 2) };
            foreach (var b in c.Blocks) AddBlock(quote, b, style, depth + 1);
            if (c.Title.Count > 0)
            {
                var attr = BuildText(c.Title, style, new List<MdImage>(), out bool hasAttr);
                if (hasAttr)
                {
                    attr.TextAlignment = TextAlignment.Right;
                    attr.Opacity = 0.85;
                    quote.Children.Add(attr);
                }
            }
            return quote;
        }

        // 折叠框
        if (c.IsCallout)
        {
            var (icon, name, color) = CalloutStyle(c.Label!);
            var accent = new SolidColorBrush(color);

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var title = new TextBlock
            {
                Text = icon + " " + name,
                FontSize = style.FontSize - 1,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            UiFont.ApplyToText(title);
            header.Children.Add(title);

            if (c.Title.Count > 0)
            {
                var custom = BuildText(c.Title, style, new List<MdImage>(), out bool hasCustom);
                if (hasCustom) header.Children.Add(custom);
            }

            var expander = new Expander
            {
                Header = header,
                Content = body,
                IsExpanded = c.Open,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
            };
            UiFont.ApplyTo(expander);

            return new Border
            {
                BorderBrush = accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background = style.QuoteBack,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 8, 6),
                Child = expander,
            };
        }

        // 普通容器
        var panel = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(c.Label))
        {
            var label = new TextBlock
            {
                Text = c.Label,
                FontSize = style.FontSize - 2,
                FontWeight = FontWeights.SemiBold,
                Foreground = style.Muted,
            };
            UiFont.ApplyToText(label);
            panel.Children.Add(label);
        }
        foreach (var b in c.Blocks) AddBlock(panel, b, style, depth);

        if (string.IsNullOrWhiteSpace(c.Label) && c.Blocks.Count == 1 && panel.Children.Count == 1)
            return (FrameworkElement)panel.Children[0];

        return new Border
        {
            BorderBrush = style.TableEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Child = panel,
        };
    }

    /// <summary>洛谷那四种折叠框的样子(图标 / 默认标题 / 颜色)。</summary>
    private static (string Icon, string Name, Color Color) CalloutStyle(string label) => label switch
    {
        "success" => ("✅", "成功", Color.FromArgb(255, 0x2E, 0x9E, 0x5B)),
        "warning" => ("⚠️", "警告", Color.FromArgb(255, 0xD9, 0x8A, 0x1E)),
        "error" => ("🛑", "错误", Color.FromArgb(255, 0xD1, 0x3A, 0x3A)),
        "tip" => ("💡", "提示", Color.FromArgb(255, 0x2E, 0x9E, 0x5B)),
        "important" => ("❗", "重要", Color.FromArgb(255, 0x8B, 0x5C, 0xF6)),
        "caution" => ("🛑", "注意", Color.FromArgb(255, 0xD1, 0x3A, 0x3A)),
        "note" => ("ℹ️", "说明", Color.FromArgb(255, 0x2B, 0x7C, 0xD6)),
        _ => ("ℹ️", "说明", Color.FromArgb(255, 0x2B, 0x7C, 0xD6)),
    };

    private static FrameworkElement DefinitionList(MdDefinitionList dl, MarkdownStyle style, int depth)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var item in dl.Items)
        {
            var term = BuildText(item.Term, style, new List<MdImage>(), out bool hasTerm);
            if (hasTerm)
            {
                term.FontWeight = FontWeights.SemiBold;
                panel.Children.Add(term);
            }
            var body = new StackPanel { Spacing = 3, Margin = new Thickness(14, 0, 0, 0) };
            foreach (var b in item.Blocks) AddBlock(body, b, style, depth + 1);
            panel.Children.Add(body);
        }
        return panel;
    }

    private static FrameworkElement Footnotes(MdFootnotes notes, MarkdownStyle style, int depth)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new Border { Height = 1, Background = style.Rule, Margin = new Thickness(0, 2, 0, 4) });
        foreach (var n in notes.Items)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock { Text = "[" + n.Order + "]", FontSize = style.FontSize - 3, Foreground = style.Accent };
            UiFont.ApplyToText(label);
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var body = new StackPanel { Spacing = 2 };
            foreach (var b in n.Blocks) AddBlock(body, b, style, depth);
            foreach (var child in body.Children)
                if (child is RichTextBlock rtb) rtb.FontSize = style.FontSize - 1;
            Grid.SetColumn(body, 1);
            row.Children.Add(body);

            panel.Children.Add(row);
        }
        return panel;
    }

    // ---------- 图片 ----------

    private static bool IsRemote(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static FrameworkElement ImageBox(MdImage img, MarkdownStyle style)
    {
        var panel = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };

        var placeholder = new TextBlock
        {
            Text = "🖼 " + (string.IsNullOrWhiteSpace(img.Alt) ? img.Url : img.Alt),
            FontSize = style.FontSize - 1,
            Foreground = style.Muted,
            TextWrapping = TextWrapping.Wrap,
        };
        UiFont.ApplyToText(placeholder);
        panel.Children.Add(placeholder);

        if (!IsRemote(img.Url))
        {
            // 本地路径/相对路径不去读文件(消息里的图片一律走附件), 只显示成这样
            placeholder.TextDecorations = TextDecorations.Underline;
            return panel;
        }

        var image = new Image
        {
            MaxWidth = 320,
            MaxHeight = 240,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(image);
        _ = LoadImageAsync(image, placeholder, img.Url);
        return panel;
    }

    /// <summary>网图: 下载到缓存目录再显示(同一条消息滚动时不会重复下载)。</summary>
    private static async Task LoadImageAsync(Image image, TextBlock placeholder, string url)
    {
        try
        {
            var dir = Path.Combine(AppSettings.CacheDir, "mdimg");
            Directory.CreateDirectory(dir);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".img";
            var path = Path.Combine(dir, hash + ext);

            if (!File.Exists(path))
            {
                var bytes = await Http.GetByteArrayAsync(url);
                if (bytes.Length == 0 || bytes.Length > 12 * 1024 * 1024) return;
                await File.WriteAllBytesAsync(path, bytes);
            }

            if (MessageVmShuttingDown) return;

            var bmp = new BitmapImage();
            using (var fs = File.OpenRead(path))
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());

            image.DispatcherQueue?.TryEnqueue(() =>
            {
                if (MessageVmShuttingDown) return;
                image.Source = bmp;
                image.Visibility = Visibility.Visible;
                placeholder.Visibility = Visibility.Collapsed;
            });
        }
        catch
        {
            // 下载失败就保留 "🖼 说明文字" 那行, 不弹错误
        }
    }

    /// <summary>窗口正在关闭时不要再碰 XAML 对象(MessageVm.ShuttingDown 的只读镜像)。</summary>
    private static bool MessageVmShuttingDown => ViewModels.MessageVm.ShuttingDown;
}

/// <summary>
/// 很轻量的代码着色: 注释 / 字符串 / 数字 / 关键字。目的是"看起来像代码", 不做完整词法分析。
/// 手写扫描而不是正则, 免得一堆转义把源码搞得没法读。
/// </summary>
internal static class Highlighter
{
    private static readonly HashSet<string> Langs = new(StringComparer.OrdinalIgnoreCase)
    {
        "c", "h", "cpp", "c++", "cc", "hpp", "cs", "csharp", "java", "js", "javascript", "ts", "typescript",
        "jsx", "tsx", "json", "jsonc", "xml", "html", "xaml", "css", "scss", "less", "sql", "py", "python",
        "ps1", "powershell", "sh", "bash", "shell", "zsh", "go", "golang", "rust", "rs", "kt", "kotlin",
        "swift", "php", "rb", "ruby", "yaml", "yml", "toml", "ini", "bat", "cmd", "vue", "dart", "lua", "r",
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C 家族
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "dynamic", "else", "enum",
        "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "get", "global",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "let", "lock", "long", "namespace",
        "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "record", "ref", "return", "sealed", "set", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "when", "where", "while", "with",
        "yield", "function", "export", "import", "from", "of", "type", "extends", "implements", "instanceof",
        "typeof", "delete", "super", "undefined", "arguments", "console",
        // Python
        "and", "assert", "def", "del", "elif", "except", "exec", "from", "global", "lambda", "None", "nonlocal",
        "not", "or", "pass", "print", "raise", "self", "True", "False", "with", "yield",
        // SQL
        "select", "from", "where", "insert", "into", "values", "update", "delete", "create", "table", "index",
        "join", "left", "right", "inner", "outer", "group", "by", "order", "having", "limit", "offset", "distinct",
        "SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "DELETE", "CREATE", "TABLE", "JOIN",
        // 标记/配置
        "true", "false", "null", "nil", "end", "then", "begin", "do", "module", "require", "package", "func",
        "struct", "impl", "match", "pub", "mut", "fn", "use", "mod", "trait", "go", "defer", "chan", "select",
    };

    public static void Fill(TextBlock tb, string? code, string? language, MarkdownStyle style)
    {
        code ??= "";
        if (string.IsNullOrWhiteSpace(language) || !Langs.Contains(language!) || code.Length > 20000)
        {
            tb.Inlines.Add(new Run { Text = code, Foreground = style.CodeText });
            return;
        }

        var plain = new StringBuilder();
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];

            // 行注释: // 或 #
            if ((c == '/' && i + 1 < code.Length && code[i + 1] == '/') || c == '#')
            {
                Flush(tb, plain, style.CodeText);
                int e = code.IndexOf('\n', i);
                if (e < 0) e = code.Length;
                Add(tb, code[i..e], style.Comment);
                i = e;
                continue;
            }

            // 块注释
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                Flush(tb, plain, style.CodeText);
                int e = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? code.Length : e + 2;
                Add(tb, code[i..e], style.Comment);
                i = e;
                continue;
            }

            // 字符串
            if (c == '"' || c == '\'' || c == '`')
            {
                Flush(tb, plain, style.CodeText);
                int j = i + 1;
                while (j < code.Length && code[j] != c)
                {
                    if (code[j] == '\\') j++;
                    j++;
                }
                if (j < code.Length) j++;
                Add(tb, code[i..j], style.Str);
                i = j;
                continue;
            }

            // 数字
            if (char.IsDigit(c) && (i == 0 || !(char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_')))
            {
                Flush(tb, plain, style.CodeText);
                int j = i;
                while (j < code.Length && (char.IsLetterOrDigit(code[j]) || code[j] == '.' || code[j] == '_')) j++;
                Add(tb, code[i..j], style.Number);
                i = j;
                continue;
            }

            // 标识符 / 关键字
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < code.Length && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j++;
                var word = code[i..j];
                Flush(tb, plain, style.CodeText);
                Add(tb, word, Keywords.Contains(word) ? style.Keyword : null);
                i = j;
                continue;
            }

            plain.Append(c);
            i++;
        }
        Flush(tb, plain, style.CodeText);
    }

    private static void Flush(TextBlock tb, StringBuilder sb, Brush color)
    {
        if (sb.Length == 0) return;
        tb.Inlines.Add(new Run { Text = sb.ToString(), Foreground = color });
        sb.Clear();
    }

    private static void Add(TextBlock tb, string text, Brush? color)
    {
        if (text.Length == 0) return;
        var run = new Run { Text = text };
        if (color != null) run.Foreground = color;
        tb.Inlines.Add(run);
    }
}
