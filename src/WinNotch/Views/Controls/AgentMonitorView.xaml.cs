using System.Windows.Controls;
using System.Windows.Input;

namespace WinNotch.Views.Controls;

public partial class AgentMonitorView : UserControl
{
    public AgentMonitorView()
    {
        InitializeComponent();
    }

    private void OnAgentStreamWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - (e.Delta * 0.45));
            e.Handled = true;
        }
    }
}
