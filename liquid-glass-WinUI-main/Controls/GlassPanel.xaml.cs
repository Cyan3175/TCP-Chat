using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace GlassEffectApp.Controls;

public sealed partial class GlassPanel : UserControl
{
    private Compositor? _compositor;
    private Visual? _visual;

    public static readonly DependencyProperty TintColorProperty =
        DependencyProperty.Register(nameof(TintColor), typeof(Color), typeof(GlassPanel), 
            new PropertyMetadata(Microsoft.UI.Colors.White));

    public static readonly DependencyProperty TintOpacityProperty =
        DependencyProperty.Register(nameof(TintOpacity), typeof(double), typeof(GlassPanel), 
            new PropertyMetadata(0.4));

    public static readonly DependencyProperty BlurRadiusProperty =
        DependencyProperty.Register(nameof(BlurRadius), typeof(double), typeof(GlassPanel), 
            new PropertyMetadata(2.0));

    public static readonly DependencyProperty InnerShadowColorProperty =
        DependencyProperty.Register(nameof(InnerShadowColor), typeof(Color), typeof(GlassPanel), 
            new PropertyMetadata(Microsoft.UI.Colors.White));

    public Color TintColor
    {
        get => (Color)GetValue(TintColorProperty);
        set => SetValue(TintColorProperty, value);
    }

    public double TintOpacity
    {
        get => (double)GetValue(TintOpacityProperty);
        set => SetValue(TintOpacityProperty, value);
    }

    public double BlurRadius
    {
        get => (double)GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    public Color InnerShadowColor
    {
        get => (Color)GetValue(InnerShadowColorProperty);
        set => SetValue(InnerShadowColorProperty, value);
    }

    public GlassPanel()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeComposition();
    }

    private void InitializeComposition()
    {
        try
        {
            _visual = ElementCompositionPreview.GetElementVisual(GlassBorder);
            _compositor = _visual.Compositor;

            // Create a simple backdrop effect
            var backdropBrush = _compositor.CreateBackdropBrush();
            var spriteVisual = _compositor.CreateSpriteVisual();
            spriteVisual.Brush = backdropBrush;
            spriteVisual.Size = new Vector2(300, 200);

            ElementCompositionPreview.SetElementChildVisual(GlassBorder, spriteVisual);
        }
        catch
        {
            // Fallback if composition is not available
        }
    }

    
}
