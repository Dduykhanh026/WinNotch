using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinNotch.ViewModels;

namespace WinNotch.Views.Controls;

public partial class MediaOrQuickControlView : UserControl
{
    private bool _isDraggingThumb;
    private MainNotchViewModel? _boundVm;
    private ImageSource? _lastKnownArt;

    public MediaOrQuickControlView()
    {
        InitializeComponent();
        DataContextChanged += OnControlDataContextChanged;
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;
    }

    private void OnControlDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (e.NewValue is MainNotchViewModel vm)
        {
            AttachViewModel(vm);
        }
    }

    private void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (_boundVm == null && DataContext is MainNotchViewModel vm)
        {
            AttachViewModel(vm);
        }
    }

    private void OnControlUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();
    }

    private void AttachViewModel(MainNotchViewModel vm)
    {
        _boundVm = vm;
        _boundVm.PropertyChanged += OnViewModelPropertyChanged;

        // Apply initial playback state and artwork without animation
        ApplyPlaybackState(vm.IsMediaPlaying, animate: false);
        _lastKnownArt = vm.AlbumArt;

        if (CurrentAlbumArtBorder != null)
        {
            CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, null);
            CurrentAlbumArtBorder.Opacity = 1.0;
        }

        if (PreviousAlbumArtBorder != null)
        {
            PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
        }
        if (PreviousAlbumArtBrush != null)
        {
            PreviousAlbumArtBrush.ImageSource = null;
        }
    }

    private void DetachViewModel()
    {
        if (_boundVm != null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
            _boundVm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_boundVm == null) return;

        if (e.PropertyName == nameof(MainNotchViewModel.IsMediaPlaying))
        {
            ApplyPlaybackState(_boundVm.IsMediaPlaying, animate: true);
        }
        else if (e.PropertyName == nameof(MainNotchViewModel.AlbumArt))
        {
            TransitionAlbumArt(_boundVm.AlbumArt);
        }
    }

    private void ApplyPlaybackState(bool isPlaying, bool animate)
    {
        if (AlbumArtScale == null || AlbumArtContainer == null)
            return;

        double targetScale = isPlaying ? 1.0 : 0.90;
        double targetOpacity = isPlaying ? 1.0 : 0.84;

        if (!animate)
        {
            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AlbumArtContainer.BeginAnimation(OpacityProperty, null);

            AlbumArtScale.ScaleX = targetScale;
            AlbumArtScale.ScaleY = targetScale;
            AlbumArtContainer.Opacity = targetOpacity;
            return;
        }

        if (isPlaying)
        {
            // Pop out to 1.0 (Opacity 1.0) with BackEase Amplitude="0.32"
            var scaleAnim = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 }
            };
            var opacityAnim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            scaleAnim.Completed += (_, _) =>
            {
                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                AlbumArtScale.ScaleX = targetScale;
                AlbumArtScale.ScaleY = targetScale;
            };

            opacityAnim.Completed += (_, _) =>
            {
                AlbumArtContainer.BeginAnimation(OpacityProperty, null);
                AlbumArtContainer.Opacity = targetOpacity;
            };

            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            AlbumArtContainer.BeginAnimation(OpacityProperty, opacityAnim);
        }
        else
        {
            // Recess gently to 0.90 (Opacity = 0.84) over 200ms CubicEase
            var scaleAnim = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var opacityAnim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            scaleAnim.Completed += (_, _) =>
            {
                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                AlbumArtScale.ScaleX = targetScale;
                AlbumArtScale.ScaleY = targetScale;
            };

            opacityAnim.Completed += (_, _) =>
            {
                AlbumArtContainer.BeginAnimation(OpacityProperty, null);
                AlbumArtContainer.Opacity = targetOpacity;
            };

            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            AlbumArtContainer.BeginAnimation(OpacityProperty, opacityAnim);
        }
    }

    private void TransitionAlbumArt(ImageSource? newArt)
    {
        if (CurrentAlbumArtBorder == null || PreviousAlbumArtBorder == null ||
            CurrentAlbumArtBrush == null || PreviousAlbumArtBrush == null)
            return;

        if (PreviousAlbumArtBorder.Visibility == Visibility.Visible ||
            PreviousAlbumArtBrush.ImageSource != null ||
            CurrentAlbumArtBorder.HasAnimatedProperties)
        {
            CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, null);
            CurrentAlbumArtBorder.Opacity = 1.0;
            PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
            PreviousAlbumArtBrush.ImageSource = null;
        }

        ImageSource? oldArt = _lastKnownArt;
        _lastKnownArt = newArt;

        if (ReferenceEquals(oldArt, newArt))
            return;

        if (oldArt != null && newArt != null)
        {
            // Smooth 180ms cross-fade between old and new album cover art
            PreviousAlbumArtBrush.ImageSource = oldArt;
            PreviousAlbumArtBorder.Visibility = Visibility.Visible;
            PreviousAlbumArtBorder.Opacity = 1.0;

            var crossFadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            crossFadeAnim.Completed += (_, _) =>
            {
                if (ReferenceEquals(_lastKnownArt, newArt))
                {
                    PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
                    PreviousAlbumArtBrush.ImageSource = null;
                    CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, null);
                    CurrentAlbumArtBorder.Opacity = 1.0;
                }
            };

            CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, crossFadeAnim);
        }
        else if (newArt != null && oldArt == null)
        {
            // Smooth 180ms fade in from null artwork
            PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
            PreviousAlbumArtBrush.ImageSource = null;

            var fadeInAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            fadeInAnim.Completed += (_, _) =>
            {
                if (ReferenceEquals(_lastKnownArt, newArt))
                {
                    CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, null);
                    CurrentAlbumArtBorder.Opacity = 1.0;
                }
            };

            CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, fadeInAnim);
        }
        else
        {
            // Artwork became null (fallback icon is shown via NullToInvVis)
            PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
            PreviousAlbumArtBrush.ImageSource = null;
            CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, null);
            CurrentAlbumArtBorder.Opacity = 1.0;
        }
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        _isDraggingThumb = true;
        if (DataContext is MainNotchViewModel vm)
        {
            vm.StateMachine.SetSliderInteraction(true);
            vm.BeginSeek();
        }
    }

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDraggingThumb = false;
        if (DataContext is MainNotchViewModel vm)
        {
            vm.StateMachine.SetSliderInteraction(false);
            vm.CommitSeek(MediaSeekSlider.Value);
        }
    }

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm)
        {
            vm.StateMachine.SetSliderInteraction(true);
            vm.BeginSeek();
        }
    }

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingThumb && DataContext is MainNotchViewModel vm)
        {
            vm.StateMachine.SetSliderInteraction(false);
            vm.CommitSeek(MediaSeekSlider.Value);
        }
    }
}
