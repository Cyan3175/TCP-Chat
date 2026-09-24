using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TCPChat10.Services;

/// <summary>
/// 按"当前实际主题"取资源。
///
/// XAML 里写 {ThemeResource ...} 会自动跟随主题, 但代码里
/// Application.Current.Resources[key] 只认应用级主题 —— 窗口上强制浅色/深色时拿到的还是系统那套,
/// 半透明气泡和玻璃面板就会配错色。所以代码里统一走这里: 按根元素的实际主题去对应的
/// ThemeDictionaries 里取。
/// </summary>
public static class ThemeLookup
{
    /// <summary>窗口根元素当前的实际主题(Default = 跟随系统)。</summary>
    public static ElementTheme Current { get; set; } = ElementTheme.Default;

    private static bool Dark => Current switch
    {
        ElementTheme.Dark => true,
        ElementTheme.Light => false,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    private static ResourceDictionary Dict =>
        (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[Dark ? "Dark" : "Light"];

    public static Brush Brush(string key) => (Brush)Dict[key];

    public static Color Color(string key) => (Color)Dict[key];
}
