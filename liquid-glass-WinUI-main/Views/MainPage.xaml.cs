using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Microsoft.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;
using Windows.Foundation;
using System;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace GlassEffectApp.Views;

/// <summary>
/// A page that demonstrates liquid glass effects with customizable properties.
/// </summary>
public partial class MainPage : Page
{
    private Color _currentTintColor = Color.FromArgb(255, 220, 240, 255); // Subtle blue tint instead of pure white
    private Color _currentShadowColor = Colors.Black; // Changed to black for better shadow visibility
    private double _currentTintOpacity = 0.0; // Zero opacity for transparent glass by default
    private double _currentShadowBlur = 20;
    private double _currentShadowSpread = -5;
    private double _currentFrostBlur = 2;
    private double _currentNoiseFreq = 0.008;
    private double _currentDistortionStrength = 77;
    private double _dpiScale = 1.0;

    // Dragging state
    private bool _isDragging = false;
    private Windows.Foundation.Point _lastPointerPosition;

    // Win2D resources for liquid glass effect
    private CanvasBitmap? _backgroundBitmap;
    private ICanvasEffect? _liquidGlassEffect;
    private bool _resourcesLoaded = false;

    public MainPage()
    {
        this.InitializeComponent();
        InitializeGlassEffect();
        InitializeDpiAwareness();
        // Disable composition clipping - it's causing visual tree walk crashes
        // SetupCompositionClipping();
    }

    private void SetupCompositionClipping()
    {
        try
        {
            // Set up composition-level clipping for perfect rectangular bounds
            // This gives us browser-like automatic clipping behavior
            this.Loaded += (s, e) =>
            {
                try
                {
                    // Find the correct canvas control name from XAML
                    var canvas = this.FindName("LiquidGlassCanvas") as CanvasControl;
                    if (canvas == null)
                    {
                        return;
                    }
                    
                    // Get the composition visual for the canvas
                    var visual = ElementCompositionPreview.GetElementVisual(canvas);
                    if (visual == null)
                    {
                        return;
                    }
                    
                    var compositor = visual.Compositor;
                    if (compositor == null)
                    {
                        return;
                    }
                    
                    // Create a rounded rectangle clip geometry
                    var clipGeometry = compositor.CreateRoundedRectangleGeometry();
                    clipGeometry.Size = new Vector2(300, 200);
                    clipGeometry.CornerRadius = new Vector2(28, 28);
                    
                    // Apply the clip to the visual
                    visual.Clip = compositor.CreateGeometricClip(clipGeometry);
                }
                catch
                {
                    // Silent fallback
                }
            };
        }
        catch
        {
            // Silent fallback
        }
    }

    private void InitializeGlassEffect()
    {
        // Set initial glass effect
        UpdateGlassEffect();
    }

    private void InitializeDpiAwareness()
    {
        // Get current DPI scale - use XamlRoot as primary source
        if (XamlRoot != null)
        {
            _dpiScale = XamlRoot.RasterizationScale;
            // Subscribe to DPI changes
            XamlRoot.Changed += OnXamlRootChanged;
        }
        else
        {
            // Default to 1.0 if XamlRoot not available yet
            // Will be updated when XamlRoot becomes available
            _dpiScale = 1.0;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // Update DPI scale when it changes
        var newDpiScale = sender.RasterizationScale;
        if (Math.Abs(newDpiScale - _dpiScale) > 0.001)
        {
            _dpiScale = newDpiScale;
            UpdateGlassEffect(); // Recreate effects with new DPI scale
            
            // Invalidate canvas to redraw with new scale
            var canvas = this.FindName("LiquidGlassCanvas") as CanvasControl;
            canvas?.Invalidate();
        }
    }

    #region Win2D Glass Effect Methods
    private void OnCreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        // Update DPI scale when canvas resources are created
        // This ensures we have the correct DPI even if XamlRoot wasn't available during construction
        if (XamlRoot != null && Math.Abs(XamlRoot.RasterizationScale - _dpiScale) > 0.001)
        {
            _dpiScale = XamlRoot.RasterizationScale;
        }
        
        // Force the canvas to use 96 DPI to avoid scaling issues
        // This will ensure 1:1 pixel mapping for reflections
        sender.DpiScale = 1.0f;
        
        args.TrackAsyncAction(CreateResourcesAsync(sender).AsAsyncAction());
    }

    private async Task CreateResourcesAsync(CanvasControl canvas)
    {
        try
        {
            // Add a small delay to ensure Win2D is fully initialized
            await Task.Delay(100);
            
            // Load background image for sampling
            _backgroundBitmap = await CanvasBitmap.LoadAsync(canvas, new Uri("ms-appx:///Assets/home.jpg"));
            
            // Create the liquid glass effect chain
            CreateLiquidGlassEffect(canvas);
            
            _resourcesLoaded = true;
        }
        catch
        {
            // Win2D failed, will use fallback rendering in draw method
            _resourcesLoaded = false;
        }
    }

    private void CreateLiquidGlassEffect(CanvasControl canvas)
    {
        try
        {
            // Start with a simple tint effect for fallback
            var tintColor = _currentTintColor;
            tintColor.A = (byte)(255 * _currentTintOpacity);
            _liquidGlassEffect = new ColorSourceEffect { Color = tintColor };

            if (_backgroundBitmap != null)
            {
                // 1. Background source (will be replaced with actual background during draw)
                var backgroundSource = new ColorSourceEffect { Color = Colors.Transparent };

                // 2. Apply Gaussian blur for frosted glass effect
                var backgroundBlur = new GaussianBlurEffect
                {
                    Source = backgroundSource,
                    BlurAmount = (float)(_currentFrostBlur * Math.Min(_dpiScale, 1.5)),
                    BorderMode = EffectBorderMode.Soft
                };

                // 3. Create turbulence for liquid distortion
                var turbulence = new TurbulenceEffect
                {
                    Frequency = new Vector2((float)_currentNoiseFreq, (float)_currentNoiseFreq),
                    Octaves = 3,
                    Seed = 92
                };

                // 4. Blur turbulence for organic look
                var turbulenceBlur = new GaussianBlurEffect
                {
                    Source = turbulence,
                    BlurAmount = 2.0f * (float)Math.Min(_dpiScale, 1.5),
                    BorderMode = EffectBorderMode.Soft
                };

                // 5. Create radial mask for edge fade
                ICanvasImage radialMaskImage;
                {
                    var maskCommandList = new CanvasCommandList(canvas);
                    using (var ds = maskCommandList.CreateDrawingSession())
                    {
                        var center = new Vector2((float)canvas.Size.Width / 2, (float)canvas.Size.Height / 2);
                        var radius = Math.Min(canvas.Size.Width, canvas.Size.Height) * 0.5f;
                        var gradientStops = new CanvasGradientStop[]
                        {
                            new CanvasGradientStop { Position = 0.0f, Color = Colors.White },
                            new CanvasGradientStop { Position = 0.7f, Color = Colors.White },
                            new CanvasGradientStop { Position = 1.0f, Color = Colors.Transparent }
                        };
                        var brush = new CanvasRadialGradientBrush(ds, gradientStops);
                        brush.Center = center;
                        brush.RadiusX = (float)radius;
                        brush.RadiusY = (float)radius;
                        ds.FillRectangle(new Rect(0, 0, canvas.Size.Width, canvas.Size.Height), brush);
                    }
                    radialMaskImage = maskCommandList;
                }

                // 6. Mask turbulence with radial mask
                var maskedTurbulence = new ArithmeticCompositeEffect
                {
                    Source1 = turbulenceBlur,
                    Source2 = radialMaskImage,
                    MultiplyAmount = 1.0f,
                    Source1Amount = 0.0f,
                    Source2Amount = 0.0f,
                    Offset = 0.0f
                };

                // 7. Displacement map with masked turbulence
                var displacement = new DisplacementMapEffect
                {
                    Source = backgroundBlur,
                    Displacement = maskedTurbulence,
                    Amount = (float)(_currentDistortionStrength * 0.7f),
                    XChannelSelect = EffectChannelSelect.Red,
                    YChannelSelect = EffectChannelSelect.Green
                };

                // 8. Tint overlay
                var tintOverlay = new ColorSourceEffect { Color = tintColor };

                // 9. Blend displaced background with tint
                var blendedGlass = new BlendEffect
                {
                    Background = displacement,
                    Foreground = tintOverlay,
                    Mode = BlendEffectMode.Overlay
                };

                // 10. Saturation and contrast for vibrancy
                var saturatedGlass = new SaturationEffect
                {
                    Source = blendedGlass,
                    Saturation = 1.08f // Lower saturation for more natural look
                };

                var contrastGlass = new ContrastEffect
                {
                    Source = saturatedGlass,
                    Contrast = 0.08f // Lower contrast for better detail
                };

                _liquidGlassEffect = contrastGlass;
            }
        }
        catch
        {
            var tintColor = _currentTintColor;
            tintColor.A = (byte)(255 * _currentTintOpacity);
            _liquidGlassEffect = new ColorSourceEffect { Color = tintColor };
        }
    }

    private void OnLiquidGlassDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            var session = args.DrawingSession;
            var canvasSize = sender.Size;
            var glassRect = new Rect(0, 0, canvasSize.Width, canvasSize.Height);
        
            try
            {
                using (var rect = CanvasGeometry.CreateRectangle(session, glassRect))
                {
                    // 1. Outer shadow removed for borderless glass

                    // 2. Draw glass effect with background sampling
                    if (_backgroundBitmap != null && _liquidGlassEffect != null)
                    {
                        // Get the actual position of the glass panel relative to the background container
                        var canvasLeft = Canvas.GetLeft(sender);
                        var canvasTop = Canvas.GetTop(sender);
                        
                        // Find the background container to get proper coordinates
                        var backgroundContainer = this.FindName("BackgroundContainer") as Border;
                        if (backgroundContainer != null)
                        {
                            try
                            {
                                // Get the transform from the glass canvas to the background container
                                var glassCanvas = this.FindName("GlassCanvas") as Canvas;
                                if (glassCanvas != null)
                                {
                                    // Get the position of the glass panel relative to the background
                                    var glassToBackground = sender.TransformToVisual(backgroundContainer);
                                    var glassPosition = glassToBackground.TransformPoint(new Windows.Foundation.Point(0, 0));
                                    
                                    // Get the size of the background container
                                    var backgroundSize = new Windows.Foundation.Size(
                                        backgroundContainer.ActualWidth, 
                                        backgroundContainer.ActualHeight
                                    );
                                    
                                    // Create background sample that exactly matches glass panel size (no overscan)
                                    // This mimics how backdrop-filter works in browsers - exact element size
                                    using (var backgroundSample = new CanvasCommandList(session))
                                    {
                                        using (var bgSession = backgroundSample.CreateDrawingSession())
                                        {
                                            // Calculate exact pixel mapping but sample a larger area
                                            // This ensures we have enough pixels for distortion without losing rectangular shape
                                            var pixelScaleX = _backgroundBitmap.SizeInPixels.Width / backgroundSize.Width;
                                            var pixelScaleY = _backgroundBitmap.SizeInPixels.Height / backgroundSize.Height;
                                            
                                            // Create overscan to handle distortion displacement
                                            var overscan = 50; // Extra pixels around the glass panel
                                            
                                            // Source rect with overscan - sample more than glass panel size
                                            var sourceRect = new Rect(
                                                Math.Max(0, (glassPosition.X - overscan) * pixelScaleX),
                                                Math.Max(0, (glassPosition.Y - overscan) * pixelScaleY),
                                                Math.Min(_backgroundBitmap.SizeInPixels.Width, (canvasSize.Width + 2 * overscan) * pixelScaleX),
                                                Math.Min(_backgroundBitmap.SizeInPixels.Height, (canvasSize.Height + 2 * overscan) * pixelScaleY)
                                            );
                                            
                                            // Destination with overscan - draw larger than canvas
                                            var destRect = new Rect(-overscan, -overscan, 
                                                canvasSize.Width + 2 * overscan, 
                                                canvasSize.Height + 2 * overscan);
                                            
                                            // Draw background with overscan to handle distortion displacement
                                            bgSession.DrawImage(_backgroundBitmap, destRect, sourceRect);
                                        }

                                        // Update the glass effect with the correct background sample
                                        if (_liquidGlassEffect is ContrastEffect contrastEffect &&
                                            contrastEffect.Source is SaturationEffect satEffect &&
                                            satEffect.Source is BlendEffect blendEffect &&
                                            blendEffect.Background is DisplacementMapEffect dispEffect &&
                                            dispEffect.Source is GaussianBlurEffect blurEffect)
                                        {
                                            blurEffect.Source = backgroundSample;
                                        }

                                        // Use strict geometric clipping to ensure rectangular bounds
                                        // This mimics browser backdrop-filter behavior exactly
                                        using (var clipGeometry = CanvasGeometry.CreateRectangle(session, 
                                            new Rect(0, 0, canvasSize.Width, canvasSize.Height)))
                                        {
                                            using (session.CreateLayer(1.0f, clipGeometry))
                                            {
                                                // Draw the glass effect with strict rectangular clipping
                                                // The overscan background and reduced distortion ensure no white spaces
                                                session.DrawImage(_liquidGlassEffect);
                                            }
                                        }
                                        
                                        // Draw inner shadow
                                        var shadowGeometry = CanvasGeometry.CreateRectangle(session, 
                                            new Rect(0, 0, canvasSize.Width, canvasSize.Height));
                                        DrawInnerShadow(session, shadowGeometry);
                                        
                                        // Subtle border removed for clean glass look
                                    }
                                }
                            }
                            catch
                            {
                                // Fall back to simple glass effect
                                DrawSimpleGlassEffect(session, rect);
                            }
                        }
                        else
                        {
                            // Fallback if background container not found
                            DrawSimpleGlassEffect(session, rect);
                        }
                    }
                    else
                    {
                        // Improved fallback glass effect - less washed out
                        var tintColor = _currentTintColor;
                        tintColor.A = (byte)(255 * _currentTintOpacity);
                        
                        // Single subtle tint layer instead of multiple washed out layers
                        session.FillGeometry(rect, tintColor);

                        // Reduced highlight for more subtle effect
                        var highlightColor = Color.FromArgb((byte)(255 * 0.1), 255, 255, 255);
                        session.FillGeometry(rect, highlightColor);
                    }

                    // 3. Border removed for clean glass look

                    // 4. Inner shadow (remove dark border)
                    // Optionally, you can comment out or further soften the inner shadow if needed
                }
            }
            catch
            {
                DrawFallbackGlass(session, sender);
            }
        }
        catch
        {
            // Silent fallback
        }
    }

    private void DrawInnerShadow(CanvasDrawingSession session, CanvasGeometry geometry)
    {
    // Inner shadow removed for borderless glass panel
    }

    private void DrawFallbackGlass(CanvasDrawingSession session, CanvasControl canvas)
    {
        var glassRect = new Rect(0, 0, canvas.Size.Width, canvas.Size.Height);
        
        using (var rect = CanvasGeometry.CreateRectangle(session, glassRect))
        {
            DrawSimpleGlassEffect(session, rect);
        }
    }

    private void DrawSimpleGlassEffect(CanvasDrawingSession session, CanvasGeometry rect)
    {
        // Improved simple glass effect fallback
        var tintColor = _currentTintColor;
        tintColor.A = (byte)(255 * _currentTintOpacity);

        // Single subtle tint layer
        session.FillGeometry(rect, tintColor);

        // Minimal highlight for glass effect
        var highlightColor = Color.FromArgb((byte)(255 * 0.08), 255, 255, 255);
        session.FillGeometry(rect, highlightColor);
    }

    private void UpdateLiquidGlassEffect()
    {
        if (_resourcesLoaded)
        {
            // Find the Win2D canvas and update it
            var canvas = this.FindName("LiquidGlassCanvas") as CanvasControl;
            if (canvas != null)
            {
                CreateLiquidGlassEffect(canvas);
                canvas.Invalidate(); // Force redraw
            }
        }
    }
    #endregion

    #region Pointer Events for Dragging
    private void OnGlassPanelPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var element = sender as FrameworkElement;
        if (element != null)
        {
            _isDragging = true;
            var glassCanvas = this.FindName("GlassCanvas") as Canvas;
            if (glassCanvas != null)
            {
                _lastPointerPosition = e.GetCurrentPoint(glassCanvas).Position;
            }
            element.CapturePointer(e.Pointer);
        }
    }

    private void OnGlassPanelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            var glassCanvas = this.FindName("GlassCanvas") as Canvas;
            var liquidGlassCanvas = this.FindName("LiquidGlassCanvas") as CanvasControl;
            
            if (glassCanvas != null && liquidGlassCanvas != null)
            {
                var currentPosition = e.GetCurrentPoint(glassCanvas).Position;
                var deltaX = currentPosition.X - _lastPointerPosition.X;
                var deltaY = currentPosition.Y - _lastPointerPosition.Y;

                // Move the Win2D canvas
                Canvas.SetLeft(liquidGlassCanvas, Canvas.GetLeft(liquidGlassCanvas) + deltaX);
                Canvas.SetTop(liquidGlassCanvas, Canvas.GetTop(liquidGlassCanvas) + deltaY);

                _lastPointerPosition = currentPosition;
                
                // Force redraw to update background sampling with new position
                liquidGlassCanvas.Invalidate();
            }
        }
    }

    private void OnGlassPanelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            var element = sender as FrameworkElement;
            element?.ReleasePointerCapture(e.Pointer);
        }
    }
    #endregion

    #region Color Pickers
    private async void OnShadowColorClick(object sender, RoutedEventArgs e)
    {
        var colorPicker = new ContentDialog
        {
            Title = "Select Inner Shadow Color",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            Content = new ColorPicker { Color = _currentShadowColor }
        };

        colorPicker.XamlRoot = XamlRoot;

        var result = await colorPicker.ShowAsync();
        if (result == ContentDialogResult.Primary && colorPicker.Content is ColorPicker picker)
        {
            _currentShadowColor = picker.Color;
            UpdateGlassEffect();
        }
    }

    private async void OnTintColorClick(object sender, RoutedEventArgs e)
    {
        var colorPicker = new ContentDialog
        {
            Title = "Select Tint Color",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            Content = new ColorPicker { Color = _currentTintColor }
        };

        colorPicker.XamlRoot = XamlRoot;

        var result = await colorPicker.ShowAsync();
        if (result == ContentDialogResult.Primary && colorPicker.Content is ColorPicker picker)
        {
            _currentTintColor = picker.Color;
            UpdateGlassEffect();
        }
    }
    #endregion

    #region Slider Events
    private void OnShadowBlurChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _currentShadowBlur = e.NewValue;
        var textBlock = this.FindName("ShadowBlurText") as TextBlock;
        if (textBlock != null)
            textBlock.Text = $"{e.NewValue:F0}px";
        UpdateGlassEffect();
    }

    private void OnShadowSpreadChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _currentShadowSpread = e.NewValue;
        var textBlock = this.FindName("ShadowSpreadText") as TextBlock;
        if (textBlock != null)
            textBlock.Text = $"{e.NewValue:F0}px";
        UpdateGlassEffect();
    }

    private void OnTintOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _currentTintOpacity = e.NewValue / 100.0; // Convert percentage to decimal
        var textBlock = this.FindName("TintOpacityText") as TextBlock;
        if (textBlock != null)
            textBlock.Text = $"{e.NewValue:F0}%";
        UpdateGlassEffect();
    }

    private void OnFrostBlurChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _currentFrostBlur = e.NewValue;
        var textBlock = this.FindName("FrostBlurText") as TextBlock;
        if (textBlock != null)
            textBlock.Text = $"{e.NewValue:F0}px";
        UpdateGlassEffect();
    }

    private void OnNoiseFreqChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _currentNoiseFreq = e.NewValue;
        var textBlock = this.FindName("NoiseFreqText") as TextBlock;
        if (textBlock != null)
            textBlock.Text = e.NewValue.ToString("F3");
        UpdateGlassEffect();
    }

    private void OnDistortionStrengthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _currentDistortionStrength = e.NewValue;
        var textBlock = this.FindName("DistortionStrengthText") as TextBlock;
        if (textBlock != null)
            textBlock.Text = e.NewValue.ToString("F0");
        UpdateGlassEffect();
    }
    #endregion

    #region Background Management
    private void OnBackgroundUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyBackgroundFromUrl();
        }
    }

    private void OnApplyBackgroundClick(object sender, RoutedEventArgs e)
    {
        ApplyBackgroundFromUrl();
    }

    private void OnResetBackgroundClick(object sender, RoutedEventArgs e)
    {
        // Reset to local image - Win2D will reload the background
        UpdateGlassEffect();
    }

    private async void ApplyBackgroundFromUrl()
    {
        // For Win2D implementation, we'd need to load the URL into a CanvasBitmap
        // For now, this is a placeholder for the URL functionality
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Info",
                Content = "URL background loading will be implemented with the Win2D canvas.",
                CloseButtonText = "OK"
            };
            dialog.XamlRoot = XamlRoot;
            await dialog.ShowAsync();
        }
        catch
        {
            // Handle error silently
        }
    }
    #endregion

    #region Glass Effect Updates
    private void UpdateGlassEffect()
    {
        // Update the Win2D glass effect
        UpdateLiquidGlassEffect();
    }

    private void UpdateShadowEffect()
    {
        // Shadow effects are now handled in Win2D rendering
        UpdateLiquidGlassEffect();
    }
    #endregion
}
