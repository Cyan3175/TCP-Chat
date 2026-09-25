using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TCPChat10.Services;
using Windows.Foundation;
using Windows.UI.Text;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace TCPChat10.Rendering;

/// <summary>
/// 排好版的公式 -&gt; WinUI 元素(11.4)。
///
/// 公式画在一张 Canvas 上: 位置由 MathLayout 算好(每个盒子都是"基线 + 宽度/高度/深度"),
/// 这里只负责把它们摆上去。文字用支持数学符号的字体(优先 Cambria Math),
/// 分数线/根号/方框用矩形和折线画, 所以放到多大都清晰。
/// </summary>
public static class MathView
{
    /// <summary>公式字体: 优先 Cambria Math(Windows 自带, 数学符号最全)。</summary>
    private static readonly Lazy<FontFamily> MathFamily = new(() =>
    {
        foreach (var name in new[] { "Cambria Math", "Cambria", "Segoe UI Symbol", "Times New Roman" })
        {
            try { if (FontList.Exists(name)) return new FontFamily(name); } catch { }
        }
        return FontFamily.XamlAutoFontFamily;
    });

    private static readonly XamlMathMetrics Metrics = new();

    /// <summary>块级公式(自己占一行, 居中; 太宽了可以横向滚动)。</summary>
    public static FrameworkElement BuildDisplay(string latex, MarkdownStyle style)
    {
        var node = MathParser.Parse(latex);
        if (MathParser.LastError != null) return Raw(latex, style);

        try
        {
            var size = style.FontSize * 1.06;
            var box = MathLayout.Build(node, size, Metrics, display: true);
            if (box.Width <= 0 || box.TotalHeight <= 0) return Raw(latex, style);

            var canvas = Render(box, style.Foreground);
            var host = new ScrollViewer
            {
                Content = canvas,
                HorizontalAlignment = HorizontalAlignment.Center,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(2, 2, 2, 2),
            };
            return host;
        }
        catch
        {
            // 画不出来也不能让消息消失: 退回等宽原文
            return Raw(latex, style);
        }
    }

    /// <summary>行内公式(跟着文字走)。</summary>
    public static Inline BuildInline(string latex, MarkdownStyle style)
    {
        var node = MathParser.Parse(latex);
        if (MathParser.LastError != null) return RawRun(latex, style);

        try
        {
            var size = style.FontSize;
            var box = MathLayout.Build(node, size, Metrics, display: false);
            if (box.Width <= 0 || box.TotalHeight <= 0) return RawRun(latex, style);

            var canvas = Render(box, style.Foreground);
            // 行内元素按"底边贴在基线上"摆放, 所以深度那部分要用负外边距让出来
            canvas.Margin = new Thickness(1, 0, 1, -box.Depth);
            return new InlineUIContainer { Child = canvas };
        }
        catch
        {
            return RawRun(latex, style);
        }
    }

    /// <summary>画不出来时的兜底: 等宽显示 LaTeX 原文。</summary>
    private static TextBlock Raw(string latex, MarkdownStyle style)
    {
        var tb = new TextBlock
        {
            Text = latex.Trim(),
            FontSize = style.FontSize,
            Foreground = style.CodeText,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        UiFont.ApplyToText(tb, mono: true);
        return tb;
    }

    private static Run RawRun(string latex, MarkdownStyle style) => new()
    {
        Text = latex,
        FontFamily = UiFont.Mono,
        FontStyle = FontStyle.Italic,
    };

    // ---------- 画 ----------

    private static Canvas Render(MathBox root, Brush foreground)
    {
        var canvas = new Canvas
        {
            Width = Math.Max(1, root.Width),
            Height = Math.Max(1, root.TotalHeight),
            IsHitTestVisible = false,
        };
        // 基线画在"高度"这个位置: 盒子往上 Height, 往下 Depth
        Add(canvas, root, 0, root.Height, foreground);
        return canvas;
    }

    private static void Add(Canvas canvas, MathBox box, double x, double baseline, Brush foreground)
    {
        switch (box)
        {
            case MathGlyphBox glyph:
            {
                var tb = new TextBlock
                {
                    Text = glyph.Text,
                    FontSize = glyph.Size,
                    FontFamily = FamilyFor(glyph.Font),
                    Foreground = foreground,
                    IsTextSelectionEnabled = false,
                    TextWrapping = TextWrapping.NoWrap,
                };
                if (glyph.Italic) tb.FontStyle = FontStyle.Italic;
                if (glyph.Font == MathFontKind.MathBold) tb.FontWeight = FontWeights.SemiBold;
                Canvas.SetLeft(tb, x);
                Canvas.SetTop(tb, baseline - glyph.Height);
                canvas.Children.Add(tb);
                break;
            }

            case MathRuleBox rule:
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(0.5, rule.Width),
                    Height = Math.Max(0.6, rule.Height),
                    Fill = foreground,
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, baseline - rule.Height);
                canvas.Children.Add(rect);
                break;
            }

            case MathSurdBox surd:
                canvas.Children.Add(Surd(x, baseline, surd, foreground));
                break;

            case MathStackBox stack:
            {
                foreach (var (child, cx, cy) in stack.Items)
                    Add(canvas, child, x + cx, baseline - cy, foreground);

                if (stack.Frame)
                {
                    double t = 1;
                    var frame = new Rectangle
                    {
                        Width = Math.Max(1, stack.Width - t),
                        Height = Math.Max(1, stack.TotalHeight - t),
                        Stroke = foreground,
                        StrokeThickness = t,
                        RadiusX = 2,
                        RadiusY = 2,
                    };
                    Canvas.SetLeft(frame, x + t / 2);
                    Canvas.SetTop(frame, baseline - stack.Height + t / 2);
                    canvas.Children.Add(frame);
                }
                break;
            }
        }
    }

    /// <summary>根号那一笔: 一条从下往上、带个尖角的折线(和排版里留的宽度一致)。</summary>
    private static Microsoft.UI.Xaml.Shapes.Path Surd(double x, double baseline, MathSurdBox box, Brush foreground)
    {
        double top = baseline - box.Height;
        double bottom = baseline + box.Depth;
        double h = Math.Max(1, bottom - top);
        double w = Math.Max(1, box.Width);
        double stroke = Math.Max(0.9, h * 0.05);
        // 顶上那道横线的中线就是本盒子的顶边(排版那边把横线摆成以顶边为中心),
        // 所以收尾要正好画在 top 上 —— 之前又多加了半个线宽, 于是横线和根号错开半个线宽。
        double line = top;

        var figure = new PathFigure
        {
            StartPoint = new Point(x, line + h * 0.5),
            IsClosed = false,
            IsFilled = false,
        };
        figure.Segments.Add(new LineSegment { Point = new Point(x + w * 0.22, line + h * 0.72) });
        figure.Segments.Add(new LineSegment { Point = new Point(x + w * 0.44, bottom - stroke / 2) });
        figure.Segments.Add(new LineSegment { Point = new Point(x + w * 0.72, line) });
        figure.Segments.Add(new LineSegment { Point = new Point(x + w, line) });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = foreground,
            StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }

    private static FontFamily FamilyFor(MathFontKind font) => font switch
    {
        MathFontKind.Text => UiFont.Family,
        MathFontKind.Mono => UiFont.Mono,
        _ => MathFamily.Value,
    };

    // ---------- 字体度量 ----------

    /// <summary>
    /// 文字量宽: 直接用真的 TextBlock 量(XAML 怎么画就怎么量, 不会和实际差一截);
    /// 基线高低: 问 DirectWrite(Win2D), 拿不到就按常见比例估。
    /// </summary>
    private sealed class XamlMathMetrics : IMathMetrics
    {
        /// <summary>有下伸部分的字符(基线下还有笔画)。别的字符不留深度, 分数线和方框才贴得紧。</summary>
        private const string Descenders = "gjpqy()[]{}|/,;@_βγζημξρσςφχψцщд";

        private readonly Dictionary<(MathFontKind Font, int Size), (double Ascent, double Descent)> _lines = new();

        public double Width(string text, MathFontKind font, bool italic, double size)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            try
            {
                var tb = MakeText(text, font, italic, size);
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return tb.DesiredSize.Width;
            }
            catch
            {
                return text.Length * size * 0.5;      // 兜底: 估个宽度, 别让公式塌成 0
            }
        }

        public (double Ascent, double Descent) Vertical(string text, MathFontKind font, double size)
        {
            var key = (font, (int)Math.Round(size * 4));
            if (!_lines.TryGetValue(key, out var line))
            {
                line = MeasureLine(font, size);
                _lines[key] = line;
            }

            bool descends = false;
            foreach (var ch in text)
            {
                if (Descenders.IndexOf(ch) >= 0) { descends = true; break; }
            }
            return (line.Ascent, descends ? line.Descent : 0);
        }

        /// <summary>一行字的"基线以上/以下"高度: 高度问 XAML, 基线位置问 DirectWrite(Win2D)。</summary>
        private static (double Ascent, double Descent) MeasureLine(MathFontKind font, double size)
        {
            double lineHeight = size * 1.2;
            try
            {
                var tb = MakeText("Hxg", font, false, size);
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                lineHeight = tb.DesiredSize.Height;
            }
            catch { }

            double ascent = lineHeight * 0.8;
            try
            {
                var direct = DirectWriteAscent(font, size);
                if (direct > 0 && direct < lineHeight + size) ascent = direct;
            }
            catch { }

            return (ascent, Math.Max(0, lineHeight - ascent));
        }

        /// <summary>DirectWrite 给的"行顶到基线"的距离(Win2D 的 CanvasLineMetrics.Baseline)。</summary>
        private static double DirectWriteAscent(MathFontKind font, double size)
        {
            try
            {
                var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
                var format = new CanvasTextFormat
                {
                    FontFamily = FamilyFor(font).Source,
                    FontSize = (float)size,
                    WordWrapping = CanvasWordWrapping.NoWrap,
                };
                using (format)
                {
                    var layout = new CanvasTextLayout(device, "Hxg", format, 0, 0);
                    using (layout)
                    {
                        var lines = layout.LineMetrics;
                        if (lines is { Length: > 0 }) return lines[0].Baseline;
                    }
                }
            }
            catch { }
            return 0;
        }

        private static TextBlock MakeText(string text, MathFontKind font, bool italic, double size)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontFamily = FamilyFor(font),
                TextWrapping = TextWrapping.NoWrap,
            };
            if (italic) tb.FontStyle = FontStyle.Italic;
            if (font == MathFontKind.MathBold) tb.FontWeight = FontWeights.SemiBold;
            return tb;
        }
    }
}
