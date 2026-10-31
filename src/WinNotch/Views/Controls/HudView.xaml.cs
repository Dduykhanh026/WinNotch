using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using WinNotch.ViewModels;

namespace WinNotch.Views.Controls;

public partial class HudView : UserControl
{
    private MainNotchViewModel? _boundVm;

    public HudView()
    {
        InitializeComponent();
        DataContextChanged += OnHudDataContextChanged;
        Unloaded += (_, _) => DetachViewModel();
    }

    private void OnHudDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (e.NewValue is MainNotchViewModel vm)
        {
            _boundVm = vm;
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void DetachViewModel()
    {
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm = null;
        }

        // Cleanly cancel and detach any active animation clocks when unbinding/unloading
        StatusVisualScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        StatusVisualScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        SliderIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        SliderIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        StatusTextTranslate?.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        StatusTextContainer?.BeginAnimation(OpacityProperty, null);

        NotifIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        NotifIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        NotifTextTranslate?.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        NotifTextContainer?.BeginAnimation(OpacityProperty, null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainNotchViewModel.NotificationPulseToken))
        {
            PlayNotificationArrivalChoreography();
        }
        else if (e.PropertyName == nameof(MainNotchViewModel.HudArrivalPulseToken))
        {
            PlayHudArrivalPulse();
        }
    }

    private void PlayHudArrivalPulse()
    {
        // 1. Elastic Icon / Swatch / Album Art Scale Pulse: 0.74 -> 1.0 in 280ms
        var arrivalScale = new DoubleAnimation(0.74, 1.0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
        };

        arrivalScale.Completed += (_, _) =>
        {
            StatusVisualScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            StatusVisualScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            if (StatusVisualScale != null)
            {
                StatusVisualScale.ScaleX = 1.0;
                StatusVisualScale.ScaleY = 1.0;
            }

            SliderIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            SliderIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            if (SliderIconScale != null)
            {
                SliderIconScale.ScaleX = 1.0;
                SliderIconScale.ScaleY = 1.0;
            }
        };

        StatusVisualScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, arrivalScale);
        StatusVisualScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, arrivalScale);

        SliderIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, arrivalScale);
        SliderIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, arrivalScale);

        // 2. Subtle Text Settle & Bloom: 4.0px slide, 0.35 -> 1.0 opacity in 220ms
        if (StatusTextTranslate != null && StatusTextContainer != null)
        {
            var textSlide = new DoubleAnimation(4.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var textFade = new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            textSlide.Completed += (_, _) =>
            {
                StatusTextTranslate?.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                if (StatusTextTranslate != null)
                {
                    StatusTextTranslate.X = 0.0;
                }
            };

            textFade.Completed += (_, _) =>
            {
                StatusTextContainer?.BeginAnimation(OpacityProperty, null);
                if (StatusTextContainer != null)
                {
                    StatusTextContainer.Opacity = 1.0;
                }
            };

            StatusTextTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, textSlide);
            StatusTextContainer.BeginAnimation(OpacityProperty, textFade);
        }
    }

    private void PlayNotificationArrivalChoreography()
    {
        // 1. Elastic App Icon Pop (0.76 -> 1.04 -> 1.0)
        var iconPop = new DoubleAnimation(0.76, 1.0, TimeSpan.FromMilliseconds(330))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.38 }
        };

        iconPop.Completed += (_, _) =>
        {
            NotifIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            NotifIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            if (NotifIconScale != null)
            {
                NotifIconScale.ScaleX = 1.0;
                NotifIconScale.ScaleY = 1.0;
            }
        };

        NotifIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, iconPop);
        NotifIconScale?.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, iconPop);

        // 2. Crisp Text Upward Settle & Bloom
        if (NotifTextTranslate != null && NotifTextContainer != null)
        {
            var textSlide = new DoubleAnimation(5.0, 0.0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var textFade = new DoubleAnimation(0.28, 1.0, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            textSlide.Completed += (_, _) =>
            {
                NotifTextTranslate?.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                if (NotifTextTranslate != null)
                {
                    NotifTextTranslate.Y = 0.0;
                }
            };

            textFade.Completed += (_, _) =>
            {
                NotifTextContainer?.BeginAnimation(OpacityProperty, null);
                if (NotifTextContainer != null)
                {
                    NotifTextContainer.Opacity = 1.0;
                }
            };

            NotifTextTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, textSlide);
            NotifTextContainer.BeginAnimation(OpacityProperty, textFade);
        }
    }

    private void OnNotificationBannerLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && IsInsideButton(dep))
            return;

        if (DataContext is MainNotchViewModel vm && vm.ActivateNotificationHudCommand.CanExecute(null))
        {
            vm.ActivateNotificationHudCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnNotificationBannerRightClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm && vm.DismissNotificationHudCommand.CanExecute(null))
        {
            vm.DismissNotificationHudCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDismissNotificationButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm && vm.DismissNotificationHudCommand.CanExecute(null))
        {
            vm.DismissNotificationHudCommand.Execute(null);
        }
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is Button) return true;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnHudMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm && vm.ActiveHud.IsInteractiveSlider)
        {
            vm.AdjustActiveHudByWheel(e.Delta);
            e.Handled = true;
        }
    }

    private void OnSliderDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm)
        {
            vm.StateMachine.SetSliderInteraction(true);
        }
    }

    private void OnSliderUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm)
        {
            vm.StateMachine.SetSliderInteraction(false);
        }
    }
}
