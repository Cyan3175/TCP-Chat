using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TCPChat10.Services;

namespace TCPChat10.Controls;

/// <summary>
/// 苹果那种细轨道滑杆(液态玻璃版): 玻璃轨道 + 染色已填充段 + 白色圆钮, 拖动时圆钮放大。
/// </summary>
public sealed class GlassSlider : UserControl
{
    private const double TrackHeight = 6;
    private const double KnobSize = 26;
    private const double KnobDragSize = 32;

    private readonly Border _track;
    private readonly Border _fill;
    private readonly Ellipse _knob;
    private double _value;
    private bool _dragging;

    public GlassSlider()
    {
        Height = 33;
        MinWidth = 120;

        _track = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = LiquidGlass.ControlTrack(LiquidGlass.Quality, false),
            BorderThickness = new Thickness(1),
            BorderBrush = LiquidGlass.ControlEdge(LiquidGlass.Quality),
        };
        _fill = new Border
        {
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = LiquidGlass.ControlFill(LiquidGlass.Quality),
        };
        _knob = new Ellipse
        {
            Width = KnobSize,
            Height = KnobSize,
            Fill = new SolidColorBrush(ThemeLookup.Color("ControlKnobColor")),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        try
        {
            _knob.Shadow = new ThemeShadow();
            _knob.Translation = new System.Numerics.Vector3(0, 0, 24);
        }
        catch { }

        var root = new Grid { Children = { _track, _fill, _knob } };
        Content = root;

        root.PointerPressed += OnPointerPressed;
        root.PointerMoved += OnPointerMoved;
        root.PointerReleased += OnPointerReleased;
        root.PointerCanceled += OnPointerReleased;
        root.PointerCaptureLost += OnPointerReleased;
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) =>
        {
            ApplyMaterial();
            GlassRuntime.MaterialChanged += OnMaterialChanged;
        };
        Unloaded += (_, _) =>
        {
            GlassRuntime.MaterialChanged -= OnMaterialChanged;
            GlassRuntime.Detach(_track);
        };
    }

    public double Minimum { get; set; } = 1;
    public double Maximum { get; set; } = 10;
    public double StepFrequency { get; set; } = 1;

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(v - _value) < 1e-9) return;
            _value = v;
            Layout();
            ValueChanged?.Invoke(this, _value);
        }
    }

    public event EventHandler<double>? ValueChanged;

    private void OnMaterialChanged(object? sender, EventArgs e) => ApplyMaterial();

    public void ApplyMaterial()
    {
        _track.Background = LiquidGlass.ControlTrack(LiquidGlass.Quality, false);
        _track.BorderBrush = LiquidGlass.ControlEdge(LiquidGlass.Quality);
        _fill.Background = LiquidGlass.ControlFill(LiquidGlass.Quality);
        if (LiquidGlass.Enabled) GlassRuntime.Attach(_track, LiquidGlass.Quality);
        else GlassRuntime.Detach(_track);
    }

    private double TrackWidth => Math.Max(1, ActualWidth - KnobSize);

    private void Layout()
    {
        var t = Maximum > Minimum ? (_value - Minimum) / (Maximum - Minimum) : 0;
        var x = t * TrackWidth;
        _fill.Width = Math.Max(0, x + KnobSize / 2 - (ActualWidth - TrackWidth) / 2);
        _knob.Margin = new Thickness(x, 0, 0, 0);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _knob.Width = _knob.Height = KnobDragSize;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        SetFromPoint(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        SetFromPoint(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _knob.Width = _knob.Height = KnobSize;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        Layout();
        e.Handled = true;
    }

    private void SetFromPoint(double x)
    {
        var t = Math.Clamp((x - KnobSize / 2) / TrackWidth, 0, 1);
        var raw = Minimum + t * (Maximum - Minimum);
        var stepped = StepFrequency > 0 ? Math.Round(raw / StepFrequency) * StepFrequency : raw;
        Value = Math.Clamp(stepped, Minimum, Maximum);
        Layout();
    }
}
