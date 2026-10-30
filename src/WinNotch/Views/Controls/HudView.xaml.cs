using System.Windows.Controls;
using System.Windows.Input;
using WinNotch.ViewModels;

namespace WinNotch.Views.Controls;

public partial class HudView : UserControl
{
    public HudView()
    {
        InitializeComponent();
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
