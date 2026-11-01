using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinNotch.Services;
using WinNotch.ViewModels;

namespace WinNotch.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(
        SettingsService settingsService,
        Action? previewFaceIdAction = null,
        Action<bool>? previewNotificationAction = null,
        Action? previewAgentHudAction = null)
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel(
            settingsService,
            Close,
            previewFaceIdAction,
            previewNotificationAction,
            previewAgentHudAction);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch { }
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.SelectedCategoryIndex))
            return;

        CategoryScrollViewer?.ScrollToTop();

        if (CategoryContentHost == null || PresentationSource.FromVisual(this) == null)
            return;

        var fadeAnim = new DoubleAnimation(0.42, 1.0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        CategoryContentHost.Opacity = 1.0;
        CategoryContentHost.BeginAnimation(OpacityProperty, fadeAnim);

        if (CategoryContentHost.RenderTransform is TranslateTransform tt)
        {
            var slideAnim = new DoubleAnimation(5.5, 0.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            tt.Y = 0.0;
            tt.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }
    }
}
