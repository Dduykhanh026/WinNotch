using System.Windows.Controls;
using System.Windows.Input;
using WinNotch.ViewModels;

namespace WinNotch.Views.Controls;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void OnQuickSliderDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm)
        {
            vm.StateMachine.SetSliderInteraction(true);
        }
    }

    private void OnQuickSliderUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm)
        {
            vm.StateMachine.SetSliderInteraction(false);
        }
    }

    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm)
        {
            vm.QuickVolumePercent = Math.Clamp(vm.QuickVolumePercent + (e.Delta > 0 ? 2 : -2), 0, 100);
            e.Handled = true;
        }
    }

    private void OnBrightnessWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm && vm.IsBrightnessSupported)
        {
            vm.QuickBrightnessPercent = Math.Clamp(vm.QuickBrightnessPercent + (e.Delta > 0 ? 4 : -4), 0, 100);
            e.Handled = true;
        }
    }

    private void OnAudioDeviceRightClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm)
        {
            vm.OpenSoundOutputSettingsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTimerWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is MainNotchViewModel vm)
        {
            vm.StepTimerMinutes(e.Delta > 0 ? 1 : -1);
            e.Handled = true;
        }
    }
}
