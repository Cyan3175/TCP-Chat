using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using TCPChat10.Services;
using Windows.UI;

namespace TCPChat10.Rendering;

/// <summary>
/// 按气泡底色给出 markdown 的配色(自己的气泡是蓝色, 别人的跟随主题)。
/// 主题或字体一变就 Version++, 让已经画好的消息重建一次(颜色是算好的, 不会自己跟着主题变)。
/// </summary>
public static class MarkdownStyles
{
    public static int Version { get; private set; }

    public static void Invalidate() => Version++;

    public static MarkdownStyle For(bool self, bool decryptFailed)
    {
        // 11.4: 缩放(Ctrl +/-)就是把这个基准字号乘一下 —— 正文/代码块/表格/公式都按它算
        var s = new MarkdownStyle { FontSize = UiZoom.BaseFontSize * UiZoom.Level };

        if (decryptFailed)
        {
            s.Foreground = ThemeLookup.Brush("MetaOtherBrush");
            s.Muted = ThemeLookup.Brush("MetaOtherBrush");
            s.Accent = ThemeLookup.Brush("AccentBrush");
            s.CodeText = ThemeLookup.Brush("MetaOtherBrush");
            s.CodeBack = ThemeLookup.Brush("QuoteBrush");
            s.CodeEdge = ThemeLookup.Brush("LineBrush");
            s.QuoteBar = ThemeLookup.Brush("LineBrush");
            s.Rule = ThemeLookup.Brush("LineBrush");
            s.TableEdge = ThemeLookup.Brush("LineBrush");
            s.TableHead = ThemeLookup.Brush("QuoteBrush");
            return s;
        }

        if (self)
        {
            // 自己的气泡: 蓝底白字, 代码块用更深的半透明底
            s.Foreground = new SolidColorBrush(Colors.White);
            s.Muted = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
            s.Accent = new SolidColorBrush(Color.FromArgb(255, 0xD6, 0xEC, 0xFF));
            s.CodeBack = new SolidColorBrush(Color.FromArgb(48, 0, 0, 0));
            s.CodeEdge = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            s.CodeText = new SolidColorBrush(Colors.White);
            s.Keyword = new SolidColorBrush(Color.FromArgb(255, 0xCB, 0xE8, 0xFF));
            s.Str = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xE3, 0xA3));
            s.Comment = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255));
            s.Number = new SolidColorBrush(Color.FromArgb(255, 0xC3, 0xF0, 0xD8));
            s.QuoteBar = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            s.QuoteBack = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255));
            s.HighlightBack = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255));   // 代码块高亮行
            s.Rule = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            s.TableEdge = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255));
            s.TableHead = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            return s;
        }

        // 别人的气泡: 全部跟随主题
        s.Foreground = ThemeLookup.Brush("BodyOtherBrush");
        s.Muted = ThemeLookup.Brush("MetaOtherBrush");
        s.Accent = ThemeLookup.Brush("AccentBrush");
        s.CodeText = ThemeLookup.Brush("BodyOtherBrush");
        s.CodeBack = ThemeLookup.Brush("QuoteBrush");
        s.CodeEdge = ThemeLookup.Brush("LineBrush");
        s.QuoteBar = ThemeLookup.Brush("AccentBrush");
        s.QuoteBack = ThemeLookup.Brush("QuoteBrush");
        s.Rule = ThemeLookup.Brush("LineBrush");
        s.TableEdge = ThemeLookup.Brush("LineBrush");
        s.TableHead = ThemeLookup.Brush("QuoteBrush");
        s.Keyword = new SolidColorBrush(Color.FromArgb(255, 0x7A, 0x3E, 0xD8));
        s.Str = new SolidColorBrush(Color.FromArgb(255, 0xB0, 0x5A, 0x00));
        s.Comment = ThemeLookup.Brush("MetaOtherBrush");
        s.Number = new SolidColorBrush(Color.FromArgb(255, 0x1C, 0x6E, 0xA8));
        return s;
    }
}
