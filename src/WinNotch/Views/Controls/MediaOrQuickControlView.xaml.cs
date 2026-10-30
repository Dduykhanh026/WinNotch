using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WinNotch.ViewModels;

namespace WinNotch.Views.Controls;

public partial class MediaOrQuickControlView : UserControl
{
    private bool _isDraggingThumb;

    public MediaOrQuickControlView()
    {
        InitializeComponent();
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
