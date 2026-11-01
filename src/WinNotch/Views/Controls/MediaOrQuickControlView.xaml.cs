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
    private int _playbackStateGeneration;
    private System.Windows.Threading.DispatcherTimer? _crossFadeCleanupTimer;

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

        if (CurrentAlbumArtBrush != null && vm.AlbumArt != null)
        {
            CurrentAlbumArtBrush.ImageSource = vm.AlbumArt;
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

        _playbackStateGeneration++;
        _crossFadeCleanupTimer?.Stop();

        // Cleanly cancel and detach in-flight animation clocks upon unbind/unload
        AlbumArtScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AlbumArtScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AlbumArtContainer?.BeginAnimation(OpacityProperty, null);
        CurrentAlbumArtBorder?.BeginAnimation(OpacityProperty, null);

        if (PreviousAlbumArtBorder != null)
        {
            PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
        }
        if (PreviousAlbumArtBrush != null)
        {
            PreviousAlbumArtBrush.ImageSource = null;
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

        int generation = ++_playbackStateGeneration;

        double targetScale = isPlaying ? 1.0 : 0.90;
        double targetOpacity = isPlaying ? 1.0 : 0.84;

        // Synchronously detach in-flight animation clocks
        AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AlbumArtContainer.BeginAnimation(OpacityProperty, null);

        // Offscreen / headless test harness presentation guard:
        // When not animated, not loaded, not visible, or lacking an active HwndSource presentation source,
        // snap resting transforms and opacity immediately without scheduling compositor DoubleAnimations.
        if (!animate || !IsLoaded || !IsVisible || PresentationSource.FromVisual(this) == null)
        {
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
                if (_playbackStateGeneration != generation)
                    return;

                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                AlbumArtScale.ScaleX = targetScale;
                AlbumArtScale.ScaleY = targetScale;
            };

            opacityAnim.Completed += (_, _) =>
            {
                if (_playbackStateGeneration != generation)
                    return;

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
                if (_playbackStateGeneration != generation)
                    return;

                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                AlbumArtScale.ScaleX = targetScale;
                AlbumArtScale.ScaleY = targetScale;
            };

            opacityAnim.Completed += (_, _) =>
            {
                if (_playbackStateGeneration != generation)
                    return;

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

        ImageSource? oldArt = _lastKnownArt;
        if (ReferenceEquals(oldArt, newArt))
            return;

        _lastKnownArt = newArt;

        // Synchronously pre-detach prior in-flight animation clocks on CurrentAlbumArtBorder
        _crossFadeCleanupTimer?.Stop();
        CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, null);

        void FinalizeCrossFadeIfCurrent()
        {
            if (!ReferenceEquals(_lastKnownArt, newArt))
                return;

            _crossFadeCleanupTimer?.Stop();
            if (PreviousAlbumArtBorder != null)
                PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
            if (PreviousAlbumArtBrush != null)
                PreviousAlbumArtBrush.ImageSource = null;
            if (CurrentAlbumArtBorder != null)
            {
                CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, null);
                CurrentAlbumArtBorder.Opacity = 1.0;
            }
        }

        void ScheduleDeterministicClockDetach()
        {
            _crossFadeCleanupTimer ??= new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Send)
            {
                Interval = TimeSpan.FromMilliseconds(185)
            };
            _crossFadeCleanupTimer.Stop();
            _crossFadeCleanupTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(185),
                System.Windows.Threading.DispatcherPriority.Send,
                (_, _) => FinalizeCrossFadeIfCurrent(),
                Dispatcher);
            _crossFadeCleanupTimer.Start();
        }

        if (oldArt != null && newArt != null)
        {
            // 1. Synchronous state setup: outgoing artwork underlay + incoming artwork overlay
            PreviousAlbumArtBrush.ImageSource = oldArt;
            PreviousAlbumArtBorder.Visibility = Visibility.Visible;
            PreviousAlbumArtBorder.Opacity = 1.0;
            CurrentAlbumArtBrush.ImageSource = newArt;

            if (!IsLoaded || PresentationSource.FromVisual(this) == null)
            {
                FinalizeCrossFadeIfCurrent();
                return;
            }

            // 2. Smooth 180ms cross-fade animation between outgoing and incoming artwork
            CurrentAlbumArtBorder.Opacity = 0.0;
            var crossFadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            crossFadeAnim.Completed += (_, _) => FinalizeCrossFadeIfCurrent();

            CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, crossFadeAnim);
            ScheduleDeterministicClockDetach();
        }
        else if (newArt != null && oldArt == null)
        {
            // Fade in from null / empty artwork
            PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
            PreviousAlbumArtBrush.ImageSource = null;
            CurrentAlbumArtBrush.ImageSource = newArt;

            if (!IsLoaded || PresentationSource.FromVisual(this) == null)
            {
                FinalizeCrossFadeIfCurrent();
                return;
            }

            CurrentAlbumArtBorder.Opacity = 0.0;
            var fadeInAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            fadeInAnim.Completed += (_, _) => FinalizeCrossFadeIfCurrent();

            CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, fadeInAnim);
            ScheduleDeterministicClockDetach();
        }
        else
        {
            // Artwork became null (fallback icon is shown via NullToInvVis)
            PreviousAlbumArtBorder.Visibility = Visibility.Collapsed;
            PreviousAlbumArtBrush.ImageSource = null;
            CurrentAlbumArtBrush.ImageSource = null;
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
