using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace WinNotch.Views.Controls;

/// <summary>
/// Authentic Apple Dynamic Island 5-bar center-symmetric capsule audio visualizer.
/// Features 4x sub-stepped per-bar spring physics at 60-240Hz, subtle inter-phrase organic breathing,
/// single-hue album-art tinting with [75%, 90%, 100%, 90%, 75%] depth opacity (Pearl White fallback
/// when no cover art is present), and collapses smoothly to 5 circular dots at 0% CPU when paused.
/// </summary>
public sealed class SmoothWaveVisualizer : FrameworkElement
{
    private const int BarCount = 5;

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(
            nameof(IsPlaying),
            typeof(bool),
            typeof(SmoothWaveVisualizer),
            new PropertyMetadata(false, OnPlaybackStateChanged));

    public static readonly DependencyProperty HasAlbumArtProperty =
        DependencyProperty.Register(
            nameof(HasAlbumArt),
            typeof(bool),
            typeof(SmoothWaveVisualizer),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnColorInputsChanged));

    public static readonly DependencyProperty BassLevelProperty =
        DependencyProperty.Register(
            nameof(BassLevel),
            typeof(double),
            typeof(SmoothWaveVisualizer),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty MidLevelProperty =
        DependencyProperty.Register(
            nameof(MidLevel),
            typeof(double),
            typeof(SmoothWaveVisualizer),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty TrebleLevelProperty =
        DependencyProperty.Register(
            nameof(TrebleLevel),
            typeof(double),
            typeof(SmoothWaveVisualizer),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty AccentColorProperty =
        DependencyProperty.Register(
            nameof(AccentColor),
            typeof(Color),
            typeof(SmoothWaveVisualizer),
            new FrameworkPropertyMetadata(
                Color.FromRgb(0xF5, 0xF7, 0xFC),
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnColorInputsChanged));

    public static readonly DependencyProperty BarWidthProperty =
        DependencyProperty.Register(
            nameof(BarWidth),
            typeof(double),
            typeof(SmoothWaveVisualizer),
            new FrameworkPropertyMetadata(
                2.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarGapProperty =
        DependencyProperty.Register(
            nameof(BarGap),
            typeof(double),
            typeof(SmoothWaveVisualizer),
            new FrameworkPropertyMetadata(
                2.2,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool HasAlbumArt
    {
        get => (bool)GetValue(HasAlbumArtProperty);
        set => SetValue(HasAlbumArtProperty, value);
    }

    public double BassLevel
    {
        get => (double)GetValue(BassLevelProperty);
        set => SetValue(BassLevelProperty, value);
    }

    public double MidLevel
    {
        get => (double)GetValue(MidLevelProperty);
        set => SetValue(MidLevelProperty, value);
    }

    public double TrebleLevel
    {
        get => (double)GetValue(TrebleLevelProperty);
        set => SetValue(TrebleLevelProperty, value);
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public double BarWidth
    {
        get => (double)GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    public double BarGap
    {
        get => (double)GetValue(BarGapProperty);
        set => SetValue(BarGapProperty, value);
    }

    private struct BarSpring
    {
        public double Value;
        public double Velocity;

        public bool Step(double target, double dt)
        {
            // Fast punchy upward attack, silky cushioned downward release
            double stiffness = target > Value ? 540.0 : 235.0;
            double damping = target > Value ? 35.0 : 25.5;

            double accel = (-stiffness * (Value - target)) - (damping * Velocity);
            Velocity += accel * dt;
            Value += Velocity * dt;

            if (Value < 0.0)
            {
                Value = 0.0;
                if (Velocity < 0.0) Velocity = 0.0;
            }
            else if (Value > 1.0)
            {
                Value = 1.0;
                if (Velocity > 0.0) Velocity = 0.0;
            }

            if (Math.Abs(Value - target) < 0.003 && Math.Abs(Velocity) < 0.015)
            {
                Value = target;
                Velocity = 0.0;
                return false;
            }

            return true;
        }
    }

    // Apple Dynamic Island arch profile + depth opacity [75%, 90%, 100%, 90%, 75%]
    private static readonly double[] ArchScale = [0.56, 0.85, 1.00, 0.83, 0.54];
    private static readonly byte[] DepthAlphas = [191, 230, 255, 230, 191];
    private static readonly double[] BreathFreqs = [3.1, 3.8, 4.3, 3.6, 3.3];
    private static readonly double[] BreathOffsets = [0.0, 1.35, 2.65, 4.05, 5.25];

    private readonly BarSpring[] _bars = new BarSpring[BarCount];
    private readonly Brush[] _barBrushes = new Brush[BarCount];

    private bool _isSubscribed;
    private long _lastTicks;
    private double _breathClock;

    public SmoothWaveVisualizer()
    {
        IsHitTestVisible = false;
        RebuildBrushes();

        IsVisibleChanged += (_, _) => UpdateRenderingSubscription();
        Loaded += (_, _) => UpdateRenderingSubscription();
        Unloaded += (_, _) => StopRenderingLoop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double bw = Math.Max(1.0, BarWidth);
        double gap = Math.Max(0.5, BarGap);
        double totalW = (BarCount * bw) + ((BarCount - 1) * gap);
        double desiredH = double.IsNaN(Height) || Height <= 0 ? 16.0 : Height;
        return new Size(totalW, desiredH);
    }

    private static void OnPlaybackStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SmoothWaveVisualizer vis)
        {
            vis.UpdateRenderingSubscription();
        }
    }

    private static void OnColorInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SmoothWaveVisualizer vis)
        {
            vis.RebuildBrushes();
        }
    }

    private void UpdateRenderingSubscription()
    {
        if (!IsVisible)
        {
            StopRenderingLoop();
            return;
        }

        if (IsPlaying || HasResidualBarMotion())
        {
            StartRenderingLoop();
        }
        else
        {
            InvalidateVisual();
        }
    }

    private bool HasResidualBarMotion()
    {
        for (int i = 0; i < BarCount; i++)
        {
            if (_bars[i].Value > 0.004 || Math.Abs(_bars[i].Velocity) > 0.01)
                return true;
        }
        return false;
    }

    private void StartRenderingLoop()
    {
        if (_isSubscribed) return;
        _isSubscribed = true;
        _lastTicks = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopRenderingLoop()
    {
        if (!_isSubscribed) return;
        _isSubscribed = false;
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (double)(now - _lastTicks) / Stopwatch.Frequency;
        _lastTicks = now;

        if (dt <= 0.0) return;
        if (dt > 0.04) dt = 0.04;

        bool active = IsPlaying && IsVisible;
        _breathClock += dt;
        if (_breathClock > 6283.1853) _breathClock -= 6283.1853;

        Span<double> targets = stackalloc double[BarCount];
        if (active)
        {
            double b = Math.Clamp(BassLevel, 0.0, 1.0);
            double m = Math.Clamp(MidLevel, 0.0, 1.0);
            double t = Math.Clamp(TrebleLevel, 0.0, 1.0);

            ReadOnlySpan<double> spectral =
            [
                (b * 0.78) + (m * 0.22),
                (b * 0.52) + (m * 0.48),
                (b * 0.42) + (m * 0.43) + (t * 0.15),
                (m * 0.56) + (t * 0.44),
                (m * 0.26) + (t * 0.74)
            ];

            for (int i = 0; i < BarCount; i++)
            {
                // Subtle micro-breathing floor keeps bars organically alive during brief vocal pauses
                double breath = 0.13 + (0.09 * (0.5 + (0.5 * Math.Sin((_breathClock * BreathFreqs[i]) + BreathOffsets[i]))));
                double combined = Math.Max(breath, (breath * 0.22) + (spectral[i] * 0.92));
                targets[i] = Math.Clamp(combined * ArchScale[i], 0.0, 1.0);
            }
        }
        else
        {
            targets.Clear();
        }

        // 3x sub-stepping for rock-solid spring integration across 60-240Hz monitors
        const int subSteps = 3;
        double subDt = dt / subSteps;
        bool anyMoving = false;

        for (int s = 0; s < subSteps; s++)
        {
            for (int i = 0; i < BarCount; i++)
            {
                if (_bars[i].Step(targets[i], subDt))
                {
                    anyMoving = true;
                }
            }
        }

        InvalidateVisual();

        if (!active && !anyMoving)
        {
            StopRenderingLoop();
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1.0 || h <= 1.0) return;

        double bw = Math.Max(1.0, BarWidth);
        double gap = Math.Max(0.5, BarGap);
        double clusterW = (BarCount * bw) + ((BarCount - 1) * gap);
        double startX = (w - clusterW) * 0.5;
        double cy = h * 0.5;
        double radius = bw * 0.5;
        double maxGrow = Math.Max(0.0, h - bw);

        for (int i = 0; i < BarCount; i++)
        {
            double barH = bw + (_bars[i].Value * maxGrow);
            double x = startX + (i * (bw + gap));
            double top = cy - (barH * 0.5);

            dc.DrawRoundedRectangle(
                _barBrushes[i],
                null,
                new Rect(x, top, bw, barH),
                radius,
                radius);
        }
    }

    private void RebuildBrushes()
    {
        Color resolved = ResolveUnifiedBarColor(AccentColor, HasAlbumArt);

        for (int i = 0; i < BarCount; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb(DepthAlphas[i], resolved.R, resolved.G, resolved.B));
            brush.Freeze();
            _barBrushes[i] = brush;
        }
    }

    private static Color ResolveUnifiedBarColor(Color raw, bool hasAlbumArt)
    {
        // Pearl White fallback when no cover art is available
        if (!hasAlbumArt)
        {
            return Color.FromRgb(0xF5, 0xF7, 0xFC);
        }

        RgbToHsl(raw.R, raw.G, raw.B, out double h, out double s, out double l);
        if (s < 0.12)
        {
            // Monochrome / grayscale cover art -> crisp Pearl White
            return Color.FromRgb(0xF5, 0xF7, 0xFC);
        }

        // Boost saturation and lightness so dark covers stay vibrant and legible on #050507
        s = Math.Clamp(Math.Max(s, 0.70), 0.0, 0.96);
        l = Math.Clamp(Math.Max(l, 0.66), 0.64, 0.78);
        return HslToRgb(h, s, l);
    }

    private static void RgbToHsl(byte rByte, byte gByte, byte bByte, out double h, out double s, out double l)
    {
        double r = rByte / 255.0, g = gByte / 255.0, b = bByte / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        l = (max + min) * 0.5;

        if (Math.Abs(max - min) < 0.0001)
        {
            h = 0.0;
            s = 0.0;
            return;
        }

        double d = max - min;
        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        if (Math.Abs(max - r) < 0.0001)
            h = ((g - b) / d) + (g < b ? 6.0 : 0.0);
        else if (Math.Abs(max - g) < 0.0001)
            h = ((b - r) / d) + 2.0;
        else
            h = ((r - g) / d) + 4.0;

        h *= 60.0;
    }

    private static Color HslToRgb(double h, double s, double l)
    {
        if (s <= 0.0001)
        {
            byte gray = (byte)Math.Clamp(Math.Round(l * 255.0), 0, 255);
            return Color.FromRgb(gray, gray, gray);
        }

        double q = l < 0.5 ? l * (1.0 + s) : l + s - (l * s);
        double p = (2.0 * l) - q;
        double hk = h / 360.0;

        byte r = (byte)Math.Clamp(Math.Round(HueToChannel(p, q, hk + (1.0 / 3.0)) * 255.0), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(HueToChannel(p, q, hk) * 255.0), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(HueToChannel(p, q, hk - (1.0 / 3.0)) * 255.0), 0, 255);
        return Color.FromRgb(r, g, b);
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0.0) t += 1.0;
        if (t > 1.0) t -= 1.0;
        if (t < 1.0 / 6.0) return p + ((q - p) * 6.0 * t);
        if (t < 0.5) return q;
        if (t < 2.0 / 3.0) return p + ((q - p) * ((2.0 / 3.0) - t) * 6.0);
        return p;
    }
}
