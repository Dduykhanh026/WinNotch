using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace WinNotch.Views.Controls;

public partial class SystemMonitorView : UserControl
{
    public static readonly DependencyProperty SmoothValueProperty = DependencyProperty.RegisterAttached(
        "SmoothValue",
        typeof(double),
        typeof(SystemMonitorView),
        new PropertyMetadata(0.0, OnSmoothValueChanged));

    public SystemMonitorView()
    {
        InitializeComponent();
    }

    public static double GetSmoothValue(DependencyObject obj) => (double)obj.GetValue(SmoothValueProperty);
    public static void SetSmoothValue(DependencyObject obj, double value) => obj.SetValue(SmoothValueProperty, value);

    private static void OnSmoothValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar bar)
            return;

        double target = Math.Clamp((double)e.NewValue, bar.Minimum, bar.Maximum);
        double current = bar.Value;

        if (!bar.IsVisible || PresentationSource.FromVisual(bar) == null || Math.Abs(target - current) < 0.25)
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null);
            bar.Value = target;
            return;
        }

        var anim = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        bar.Value = target;
        bar.BeginAnimation(RangeBase.ValueProperty, anim);
    }
}
