using System.Windows;
using WinNotch.Services;
using WinNotch.ViewModels;

namespace WinNotch.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(
        SettingsService settingsService,
        Action? previewFaceIdAction = null,
        Action<bool>? previewNotificationAction = null,
        Action? previewAgentHudAction = null)
    {
        InitializeComponent();
        DataContext = new SettingsViewModel(
            settingsService,
            Close,
            previewFaceIdAction,
            previewNotificationAction,
            previewAgentHudAction);
    }
}
