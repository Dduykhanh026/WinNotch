using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinNotch.Models;
using WinNotch.ViewModels;

namespace WinNotch.Views.Controls;

public partial class ShelfView : UserControl
{
    private static readonly Brush CardNormalBgBrush = CreateFrozenBrush("#18FFFFFF");
    private static readonly Brush CardNormalBorderBrush = CreateFrozenBrush("#26FFFFFF");
    private static readonly Brush CardHoverBgBrush = CreateFrozenBrush("#22FFFFFF");
    private static readonly Brush CardHoverBorderBrush = CreateFrozenBrush("#3EFFFFFF");

    private Point _dragStartPoint;
    private ShelfItem? _pendingDragItem;
    private bool _pendingDragAll;

    public ShelfView()
    {
        InitializeComponent();
    }

    private void OnShelfMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ShelfScroller.ScrollToHorizontalOffset(ShelfScroller.HorizontalOffset - (e.Delta * 0.6));
        e.Handled = true;
    }

    private void OnItemCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SquircleBorder card || card.DataContext is not ShelfItem item)
            return;

        // Play tactile BackEase entrance pop (0.88 -> 1.0, 220ms) only on newly added/pasted cards when onscreen
        if (PresentationSource.FromVisual(card) == null || (DateTime.UtcNow - item.AddedAtUtc).TotalMilliseconds > 750.0)
            return;

        if (TryGetCardTransforms(card, out ScaleTransform? scale, out _))
        {
            var popAnim = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
                FillBehavior = FillBehavior.Stop
            };
            scale!.ScaleX = 1.0;
            scale.ScaleY = 1.0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, popAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, popAnim);
        }
    }

    private void OnItemCardMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not SquircleBorder card)
            return;

        card.Background = CardHoverBgBrush;
        card.BorderBrush = CardHoverBorderBrush;

        if (TryGetCardTransforms(card, out _, out TranslateTransform? translate))
        {
            if (PresentationSource.FromVisual(card) == null)
            {
                translate!.Y = -2.0;
                return;
            }

            var liftAnim = new DoubleAnimation(translate!.Y, -2.0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            translate.Y = -2.0;
            translate.BeginAnimation(TranslateTransform.YProperty, liftAnim);
        }
    }

    private void OnItemCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not SquircleBorder card)
            return;

        card.Background = CardNormalBgBrush;
        card.BorderBrush = CardNormalBorderBrush;

        if (TryGetCardTransforms(card, out _, out TranslateTransform? translate))
        {
            if (PresentationSource.FromVisual(card) == null)
            {
                translate!.Y = 0.0;
                return;
            }

            var dropAnim = new DoubleAnimation(translate!.Y, 0.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            translate.Y = 0.0;
            translate.BeginAnimation(TranslateTransform.YProperty, dropAnim);
        }
    }

    private static bool TryGetCardTransforms(SquircleBorder card, out ScaleTransform? scale, out TranslateTransform? translate)
    {
        scale = null;
        translate = null;

        if (card.RenderTransform is TransformGroup group && group.Children.Count >= 2)
        {
            scale = group.Children[0] as ScaleTransform;
            translate = group.Children[1] as TranslateTransform;
            return scale != null && translate != null;
        }

        return false;
    }

    private void OnItemCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShelfItem item)
        {
            if (e.ClickCount == 2)
            {
                _pendingDragItem = null;
                _pendingDragAll = false;
                if (DataContext is MainNotchViewModel vm)
                {
                    vm.OpenShelfItemCommand.Execute(item);
                    e.Handled = true;
                    return;
                }
            }

            _dragStartPoint = e.GetPosition(this);
            _pendingDragItem = item;
            _pendingDragAll = false;
        }
    }

    private void OnItemCardMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pendingDragItem == null)
        {
            return;
        }

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            ShelfItem item = _pendingDragItem;
            _pendingDragItem = null;

            if (DataContext is MainNotchViewModel vm && sender is DependencyObject source)
            {
                vm.StateMachine.SetSliderInteraction(true);
                try
                {
                    vm.FileShelf.DragOutSingle(source, item);
                }
                finally
                {
                    vm.StateMachine.SetSliderInteraction(false);
                }
            }
        }
    }

    private void OnDragAllMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        _pendingDragAll = true;
        _pendingDragItem = null;
    }

    private void OnDragAllMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_pendingDragAll)
        {
            return;
        }

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _pendingDragAll = false;
            if (DataContext is MainNotchViewModel vm && sender is DependencyObject source)
            {
                vm.StateMachine.SetSliderInteraction(true);
                try
                {
                    vm.FileShelf.DragOutAll(source);
                }
                finally
                {
                    vm.StateMachine.SetSliderInteraction(false);
                }
            }
        }
    }

    private void OnShelfPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (DataContext is MainNotchViewModel vm)
            {
                vm.PasteShelfFromClipboardCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
