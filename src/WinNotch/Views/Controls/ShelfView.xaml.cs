using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinNotch.Models;
using WinNotch.ViewModels;

namespace WinNotch.Views.Controls;

public partial class ShelfView : UserControl
{
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

    private void OnItemCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShelfItem item)
        {
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
}
