using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TCPChat10.Services;
using Windows.UI;

namespace TCPChat10.Controls;

/// <summary>
/// 苹果那种药丸开关(液态玻璃版): 玻璃轨道 + 白色圆钮, 打开时轨道染成绿色, 圆钮弹过去。
/// 轨道交给 GlassRuntime 垫玻璃层(真玻璃后端会真的糊背景)。
/// </summary>
public sealed class GlassSwitch : UserControl
{
    private const double TrackWidth = 51;
    private const double TrackHeight = 31;
    private const double KnobSize = 27;
    private const double KnobMargin = 2;

    private readonly Border _track;
    private readonly Ellipse _knob;
    private readonly TranslateTransform _shift = new();
    private bool _on;

    public GlassSwitch()
    {
        Width = TrackWidth;
        Height = TrackHeight;

        _track = new Border
        {
            Width = TrackWidth,
            Height = TrackHeight,
            CornerRadius = new CornerRadius(TrackHeight / 2),
            BorderThickness = new Thickness(1),
            Background = LiquidGlass.ControlTrack(LiquidGlass.Quality, false),
            BorderBrush = LiquidGlass.ControlEdge(LiquidGlass.Quality),
        };

        _knob = new Ellipse
        {
            Width = KnobSize,
            Height = KnobSize,
            Fill = new SolidColorBrush(ThemeLookup.Color("ControlKnobColor")),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(KnobMargin, 0, 0, 0),
            RenderTransform = _shift,
        };
        try
        {
            _knob.Shadow = new ThemeShadow();
            _knob.Translation = new System.Numerics.Vector3(0, 0, 16);
        }
        catch { }

        Content = new Grid { Children = { _track, _knob } };
        Tapped += (_, e) => { Toggle(); e.Handled = true; };
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

    /// <summary>开关状态。</summary>
    public bool IsOn
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            UpdateVisual(animate: IsLoaded);
            IsOnChanged?.Invoke(this, _on);
        }
    }

    public event EventHandler<bool>? IsOnChanged;

    public void Toggle() => IsOn = !IsOn;

    /// <summary>玻璃设置变了(开关/质量/后端)时重新上一遍材质。</summary>
    private void OnMaterialChanged(object? sender, EventArgs e) => ApplyMaterial();

    public void ApplyMaterial()
    {
        _track.Background = LiquidGlass.ControlTrack(LiquidGlass.Quality, _on);
        _track.BorderBrush = LiquidGlass.ControlEdge(LiquidGlass.Quality);
        if (LiquidGlass.Enabled) GlassRuntime.Attach(_track, LiquidGlass.Quality);
        else GlassRuntime.Detach(_track);
    }

    private void UpdateVisual(bool animate)
    {
        var to = _on ? TrackWidth - KnobSize - KnobMargin * 2 : 0;
        if (animate)
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(anim, _shift);
            Storyboard.SetTargetProperty(anim, "X");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }
        else
        {
            _shift.X = to;
        }
        _track.Background = LiquidGlass.ControlTrack(LiquidGlass.Quality, _on);
    }
}
