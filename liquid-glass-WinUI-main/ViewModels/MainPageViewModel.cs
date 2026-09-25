using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI;

namespace GlassEffectApp.ViewModels;

public class MainPageViewModel : INotifyPropertyChanged
{
    private Color _tintColor = Colors.White;
    private double _tintOpacity = 0.4;
    private double _blurRadius = 2.0;
    private Color _innerShadowColor = Colors.White;
    private double _shadowBlur = 20.0;
    private double _shadowSpread = -5.0;
    private double _noiseFrequency = 0.008;
    private double _distortionStrength = 77.0;
    private string _backgroundImageUrl = "https://images.unsplash.com/photo-1618221195710-dd6b41faaea6?q=80&w=2000&auto=format&fit=crop";

    public Color TintColor
    {
        get => _tintColor;
        set
        {
            if (_tintColor != value)
            {
                _tintColor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TintColorBrush));
            }
        }
    }

    public double TintOpacity
    {
        get => _tintOpacity;
        set
        {
            if (Math.Abs(_tintOpacity - value) > 0.001)
            {
                _tintOpacity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TintOpacityPercent));
            }
        }
    }

    public double BlurRadius
    {
        get => _blurRadius;
        set
        {
            if (Math.Abs(_blurRadius - value) > 0.001)
            {
                _blurRadius = value;
                OnPropertyChanged();
            }
        }
    }

    public Color InnerShadowColor
    {
        get => _innerShadowColor;
        set
        {
            if (_innerShadowColor != value)
            {
                _innerShadowColor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(InnerShadowColorBrush));
            }
        }
    }

    public double ShadowBlur
    {
        get => _shadowBlur;
        set
        {
            if (Math.Abs(_shadowBlur - value) > 0.001)
            {
                _shadowBlur = value;
                OnPropertyChanged();
            }
        }
    }

    public double ShadowSpread
    {
        get => _shadowSpread;
        set
        {
            if (Math.Abs(_shadowSpread - value) > 0.001)
            {
                _shadowSpread = value;
                OnPropertyChanged();
            }
        }
    }

    public double NoiseFrequency
    {
        get => _noiseFrequency;
        set
        {
            if (Math.Abs(_noiseFrequency - value) > 0.001)
            {
                _noiseFrequency = value;
                OnPropertyChanged();
            }
        }
    }

    public double DistortionStrength
    {
        get => _distortionStrength;
        set
        {
            if (Math.Abs(_distortionStrength - value) > 0.001)
            {
                _distortionStrength = value;
                OnPropertyChanged();
            }
        }
    }

    public string BackgroundImageUrl
    {
        get => _backgroundImageUrl;
        set
        {
            if (_backgroundImageUrl != value)
            {
                _backgroundImageUrl = value;
                OnPropertyChanged();
            }
        }
    }

    // Computed properties for UI binding
    public Brush TintColorBrush => new SolidColorBrush(TintColor);
    public Brush InnerShadowColorBrush => new SolidColorBrush(InnerShadowColor);
    public string TintOpacityPercent => $"{TintOpacity * 100:F0}%";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
