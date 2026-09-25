using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace TCPChat10.Controls;

/// <summary>
/// 液态玻璃开关(11.0): 玻璃药丸轨道 + 玻璃圆钮, 和主界面的玻璃用同一套效果链。
/// 点一下切换, 键盘空格/回车也能切。
/// </summary>
public sealed class GlassSwitch : GlassControlBase
{
    private const double TrackWidth = 58;
    private const double TrackHeight = 30;
    private const double KnobInset = 3;

    private static readonly Color AccentOn = Color.FromArgb(255, 0x2B, 0x6C, 0xB0);

    private bool _isOn;
    private double _knobPos;          // 0 = 关, 1 = 开(带一点过渡动画)

    public event Action? Toggled;

    public GlassSwitch()
    {
        Width = TrackWidth;
        Height = TrackHeight;
        MinWidth = TrackWidth;
        MinHeight = TrackHeight;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        ManipulationMode = ManipulationModes.None;

        Tapped += (_, _) => Toggle();
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Space || e.Key == Windows.System.VirtualKey.Enter)
            {
                Toggle();
                e.Handled = true;
            }
        };

        // 底下的圆角矩形亮边(玻璃本身由画布画, 这层只是让控件有边界感)
        Redraw();
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            StartAnim();
            Redraw();
        }
    }

    public void SetOnWithoutNotify(bool value)
    {
        _isOn = value;
        _knobPos = value ? 1 : 0;
        Redraw();
    }

    private void Toggle()
    {
        IsOn = !IsOn;
        Toggled?.Invoke();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _anim;
    private void StartAnim()
    {
        _anim?.Stop();
        _anim = DispatcherQueue.CreateTimer();
        _anim.Interval = TimeSpan.FromMilliseconds(16);
        _anim.Tick += (_, _) =>
        {
            var target = _isOn ? 1.0 : 0.0;
            _knobPos += (target - _knobPos) * 0.35;
            if (Math.Abs(target - _knobPos) < 0.02) { _knobPos = target; _anim?.Stop(); }
            Redraw();
        };
        _anim.Start();
    }

    protected override void OnDraw(CanvasDrawingSession ds, Size size)
    {
        var radius = TrackHeight / 2;
        var track = new Rect(0.5, 0.5, TrackWidth - 1, TrackHeight - 1);

        var knob = TrackHeight - KnobInset * 2;
        var x = KnobInset + _knobPos * (TrackWidth - knob - KnobInset * 2);
        var knobRect = new Rect(x, KnobInset, knob, knob);

        if (!GlassLook)
        {
            // 关掉液态玻璃: 画成普通开关(中性灰 / 主题蓝轨道 + 白钮 + 描边), 白底上也看得清
            FillRound(ds, track, radius, _isOn ? AccentOn : NeutralTrack);
            Edge(ds, track, radius, TrackEdge);
            FillRound(ds, knobRect, knob / 2, KnobSolid);
            Edge(ds, knobRect, knob / 2, KnobEdge);
            return;
        }

        // 开着玻璃: 轨道开 = 蓝玻璃, 关 = 白/黑玻璃; 再描一圈边, 免得白玻璃压在浅色卡片上看不出形状
        var trackTint = _isOn
            ? AccentOn
            : Dark ? Color.FromArgb(255, 22, 24, 30) : Color.FromArgb(255, 255, 255, 255);
        var trackOpacity = _isOn ? 0.78 : Dark ? 0.72 : 0.80;
        var fullTrack = new Rect(0, 0, TrackWidth, TrackHeight);
        DrawGlass(ds, size, fullTrack, radius, trackTint, trackOpacity);
        Edge(ds, track, radius, TrackEdge);

        var knobTint = Dark ? Color.FromArgb(255, 0xE8, 0xEC, 0xF4) : Color.FromArgb(255, 255, 255, 255);
        DrawGlass(ds, size, knobRect, knob / 2, knobTint, 0.92);
        Edge(ds, knobRect, knob / 2, KnobEdge);
    }
}

/// <summary>
/// 液态玻璃滑块(11.0): 玻璃轨道 + 已选中的蓝色玻璃段 + 玻璃圆钮, 拖动即时生效。
/// </summary>
public sealed class GlassSlider : GlassControlBase
{
    private const double TrackHeight = 12;
    private const double Knob = 26;
    private const double Height_ = 30;

    private static readonly Color Accent = Color.FromArgb(255, 0x2B, 0x6C, 0xB0);

    private double _minimum;
    private double _maximum = 100;
    private double _value;
    private bool _dragging;

    public event Action<double>? ValueChanged;

    public GlassSlider()
    {
        Height = Height_;
        MinHeight = Height_;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ManipulationMode = ManipulationModes.TranslateX;

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        SizeChanged += (_, _) => Redraw();
    }

    public double Minimum { get => _minimum; set { _minimum = value; Redraw(); } }
    public double Maximum { get => _maximum; set { _maximum = value; Redraw(); } }

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, _minimum, _maximum);
            if (Math.Abs(v - _value) < 1e-9) return;
            _value = v;
            Redraw();
        }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        CapturePointer(e.Pointer);
        ApplyPoint(e.GetCurrentPoint(this).Position.X, notify: true);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        ApplyPoint(e.GetCurrentPoint(this).Position.X, notify: true);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
    }

    private void ApplyPoint(double x, bool notify)
    {
        var usable = Math.Max(1, ActualWidth - Knob);
        var ratio = Math.Clamp((x - Knob / 2) / usable, 0, 1);
        var v = _minimum + ratio * (_maximum - _minimum);
        v = Math.Round(v);
        if (Math.Abs(v - _value) < 1e-9) return;
        _value = v;
        Redraw();
        if (notify) ValueChanged?.Invoke(v);
    }

    protected override void OnDraw(CanvasDrawingSession ds, Size size)
    {
        var w = size.Width;
        if (w < Knob + 4) return;

        var trackY = (Height_ - TrackHeight) / 2;
        var radius = TrackHeight / 2;
        var track = new Rect(0.5, trackY + 0.5, w - 1, TrackHeight - 1);

        var ratio = _maximum > _minimum ? (_value - _minimum) / (_maximum - _minimum) : 0;
        var knobX = ratio * (w - Knob);
        var fillWidth = Math.Max(TrackHeight, knobX + Knob / 2);
        var fill = new Rect(0.5, trackY + 0.5, Math.Max(TrackHeight, fillWidth) - 1, TrackHeight - 1);
        var knobRect = new Rect(knobX + 0.5, 1.5, Knob - 1, Knob - 1);

        if (!GlassLook)
        {
            // 关掉液态玻璃: 普通滑块(灰色轨道 + 主题蓝已选段 + 白钮 + 描边)
            FillRound(ds, track, radius, NeutralTrack);
            Edge(ds, track, radius, TrackEdge);
            FillRound(ds, fill, radius, Accent);
            FillRound(ds, knobRect, Knob / 2, KnobSolid);
            Edge(ds, knobRect, Knob / 2, KnobEdge);
            return;
        }

        var trackTint = Dark ? Color.FromArgb(255, 22, 24, 30) : Color.FromArgb(255, 255, 255, 255);
        DrawGlass(ds, size, new Rect(0, trackY, w, TrackHeight), radius, trackTint, Dark ? 0.72 : 0.80);
        Edge(ds, track, radius, TrackEdge);
        DrawGlass(ds, size, new Rect(0, trackY, fillWidth, TrackHeight), radius, Accent, 0.80);

        // 圆钮: 浅色主题下白钮压在白色玻璃轨道上会看不见, 所以给一点冷灰蓝
        var knobTint = Dark ? Color.FromArgb(255, 0xE8, 0xEC, 0xF4) : Color.FromArgb(255, 0xC6, 0xD6, 0xEA);
        DrawGlass(ds, size, new Rect(knobX, 2, Knob, Knob), Knob / 2, knobTint, 0.95);
        Edge(ds, knobRect, Knob / 2, KnobEdge);
    }
}
