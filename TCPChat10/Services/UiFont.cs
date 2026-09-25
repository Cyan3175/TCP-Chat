using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace TCPChat10.Services;

/// <summary>
/// 10.7: 把设置里选的字体应用到"所有界面"。
///
/// 只给消息列表和输入框设 FontFamily 是不够的 —— 顶栏、按钮、状态栏、翻出来的
/// 右键菜单、对话框、下拉框各有各的宿主(弹出层根本不在窗口可视树里, 继承不到),
/// 所以这里两条腿走路:
///   1. 覆盖应用级字体资源(ThemeResource 查找会命中), 管住以后新建的弹出层;
///   2. 递归刷一遍可视树上的每个文字控件, 管住已经在界面上的东西。
/// 图标字体(Segoe MDL2 Assets / Fluent Icons)一律不碰, 否则图标会变成方框。
/// </summary>
public static class UiFont
{
    /// <summary>当前字体名, 空 = 跟随系统默认。</summary>
    public static string FamilyName { get; private set; } = "";

    public static FontFamily Family => string.IsNullOrWhiteSpace(FamilyName)
        ? FontFamily.XamlAutoFontFamily
        : new FontFamily(FamilyName);

    /// <summary>字体留空时, markdown 代码块仍然用等宽字体。</summary>
    public static FontFamily Mono => string.IsNullOrWhiteSpace(FamilyName)
        ? new FontFamily("Consolas")
        : Family;

    /// <summary>设置里写的字体本机没有(卸载了/名字变了)时记在这里, 由界面提示一句。</summary>
    public static string MissingFont { get; private set; } = "";

    /// <summary>
    /// 设置当前字体: 先更新应用级资源, 再刷可视树。
    /// 字体本机没有就退回系统默认 —— 否则界面会"悄悄"变成默认字体, 用户不知道为什么。
    /// </summary>
    public static void Use(string? name)
    {
        var wanted = name?.Trim() ?? "";
        MissingFont = "";
        if (wanted.Length > 0 && !FontList.Exists(wanted))
        {
            MissingFont = wanted;
            wanted = "";
        }
        FamilyName = wanted;
        ApplyResources();
    }

    private static readonly string[] FontKeys =
    {
        "ContentControlThemeFontFamily",   // 控件默认字体
        "XamlAutoFontFamily",              // TextBlock 默认字体
        "BodyFontFamily",
        "CaptionFontFamily",
    };

    private static void ApplyResources()
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res == null) return;
            foreach (var key in FontKeys) res[key] = Family;

            // 主题字典(Light/Dark/HighContrast)里也放一份, 免得弹出层按主题查找时漏掉
            if (res.ThemeDictionaries != null)
                foreach (var value in res.ThemeDictionaries.Values)
                    if (value is ResourceDictionary dict)
                        foreach (var key in FontKeys) dict[key] = Family;
        }
        catch { /* 资源被占用等异常不影响主流程 */ }
    }

    /// <summary>把字体刷到整棵可视树(包括弹出层自己那棵树)。</summary>
    public static void Apply(DependencyObject? root)
    {
        if (root == null) return;
        ApplyOne(root);

        int count;
        try { count = VisualTreeHelper.GetChildrenCount(root); }
        catch { return; }
        for (int i = 0; i < count; i++)
        {
            DependencyObject? child;
            try { child = VisualTreeHelper.GetChild(root, i); }
            catch { continue; }
            Apply(child);
        }
    }

    private static void ApplyOne(DependencyObject o)
    {
        // 图标字体不能动: FontIcon 用的是 Segoe MDL2 Assets / Segoe Fluent Icons
        if (o is IconElement) return;
        if (o is FontIcon) return;

        var family = Family;
        switch (o)
        {
            case TextBlock tb: tb.FontFamily = family; break;
            case RichTextBlock rtb: rtb.FontFamily = family; break;
            case Control c: c.FontFamily = family; break;
            case Hyperlink h: h.FontFamily = family; break;
        }
    }

    /// <summary>给 markdown 里新建的文本元素用。</summary>
    public static void ApplyToText(TextBlock tb, bool mono = false)
    {
        try { tb.FontFamily = mono ? Mono : Family; } catch { }
    }

    /// <summary>markdown 里的行内元素(Run 之类)也跟着设置走。</summary>
    public static void ApplyToText(RichTextBlock rtb)
    {
        try { rtb.FontFamily = Family; } catch { }
    }

    /// <summary>对话框/菜单这类弹出层: 建好后、显示前刷一次。</summary>
    public static void ApplyTo(FrameworkElement? popup)
    {
        if (popup == null) return;
        if (popup is Control c) c.FontFamily = Family;
        Apply(popup);
    }
}
