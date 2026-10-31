using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;

namespace WinNotch.ViewModels;

public sealed record MonitorOption(string DeviceName, string Label);
public sealed record SpringPresetOption(string Key, string Label);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly Action _closeWindowAction;
    private readonly Action? _previewFaceIdAction;
    private readonly Action<bool>? _previewNotificationAction;
    private readonly Action? _previewAgentHudAction;

    public ObservableCollection<MonitorOption> Monitors { get; } = [];
    public ObservableCollection<SpringPresetOption> SpringPresets { get; } =
    [
        new("Balanced", "Cân bằng tự nhiên (Khuyên dùng - 380/26)"),
        new("Snappy", "Nhanh & Sắc nét (520/34)"),
        new("Bouncy", "Đàn hồi mềm mại (320/19)")
    ];

    [ObservableProperty] private string _selectedMonitorDevice = string.Empty;
    [ObservableProperty] private int _hoverExpandDelayMs = 160;
    [ObservableProperty] private int _mouseLeaveCollapseDelayMs = 250;
    [ObservableProperty] private string _selectedSpringPreset = "Balanced";

    [ObservableProperty] private bool _replaceSystemVolumeOsd = true;
    [ObservableProperty] private bool _enableCtrlGhostPassthrough = true;
    [ObservableProperty] private bool _enableFullscreenHide = true;
    [ObservableProperty] private bool _enableFftVisualizer = true;
    [ObservableProperty] private bool _enablePrivacyDots = true;
    [ObservableProperty] private bool _enableBatteryHud = true;
    [ObservableProperty] private bool _enableTrackChangeHud = true;
    [ObservableProperty] private bool _enableUnlockFaceIdHud = true;
    [ObservableProperty] private bool _enableNotificationHud = true;
    [ObservableProperty] private bool _hideNotificationPreviewText;
    [ObservableProperty] private bool _respectWindowsDoNotDisturb;
    [ObservableProperty] private bool _showCollapsedNetworkSpeed;
    [ObservableProperty] private bool _autoRemoveShelfItemAfterDrag;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _enableCollapsedScrollGestures = true;
    [ObservableProperty] private bool _enableAudioDeviceHud = true;
    [ObservableProperty] private bool _enableCapsLockHud = true;
    [ObservableProperty] private bool _enableAgentMonitor = true;
    [ObservableProperty] private bool _enableAgentApprovalHud = true;
    [ObservableProperty] private bool _defaultOpenAgentsTab = true;

    public string StoragePathDisplay => _settingsService.DataDirectory;
    public string ModeBadgeText => _settingsService.IsPortableMode ? "Chế độ Portable (100% Offline)" : "Bản cài đặt chuẩn (100% Offline)";

    public SettingsViewModel(
        SettingsService settingsService,
        Action closeWindowAction,
        Action? previewFaceIdAction = null,
        Action<bool>? previewNotificationAction = null,
        Action? previewAgentHudAction = null)
    {
        _settingsService = settingsService;
        _closeWindowAction = closeWindowAction;
        _previewFaceIdAction = previewFaceIdAction;
        _previewNotificationAction = previewNotificationAction;
        _previewAgentHudAction = previewAgentHudAction;

        Monitors.Add(new MonitorOption(string.Empty, "Tự động theo Màn hình chính (Primary)"));
        foreach (MonitorDescriptor m in WindowStyles.GetMonitors())
        {
            Monitors.Add(new MonitorOption(m.DeviceName, m.FriendlyLabel));
        }

        LoadFromCurrentSettings();
    }

    private void LoadFromCurrentSettings()
    {
        AppSettings s = _settingsService.Current;
        SelectedMonitorDevice = Monitors.Any(m => m.DeviceName == s.TargetMonitorDeviceName)
            ? s.TargetMonitorDeviceName
            : string.Empty;
        HoverExpandDelayMs = s.HoverExpandDelayMs;
        MouseLeaveCollapseDelayMs = s.MouseLeaveCollapseDelayMs;
        SelectedSpringPreset = s.SpringPreset;
        ReplaceSystemVolumeOsd = s.ReplaceSystemVolumeOsd;
        EnableCtrlGhostPassthrough = s.EnableCtrlGhostPassthrough;
        EnableFullscreenHide = s.EnableFullscreenHide;
        EnableFftVisualizer = s.EnableFftVisualizer;
        EnablePrivacyDots = s.EnablePrivacyDots;
        EnableBatteryHud = s.EnableBatteryHud;
        EnableTrackChangeHud = s.EnableTrackChangeHud;
        EnableUnlockFaceIdHud = s.EnableUnlockFaceIdHud;
        EnableNotificationHud = s.EnableNotificationHud;
        HideNotificationPreviewText = s.HideNotificationPreviewText;
        RespectWindowsDoNotDisturb = s.RespectWindowsDoNotDisturb;
        ShowCollapsedNetworkSpeed = s.ShowCollapsedNetworkSpeed;
        AutoRemoveShelfItemAfterDrag = s.AutoRemoveShelfItemAfterDrag;
        StartWithWindows = s.StartWithWindows;
        EnableCollapsedScrollGestures = s.EnableCollapsedScrollGestures;
        EnableAudioDeviceHud = s.EnableAudioDeviceHud;
        EnableCapsLockHud = s.EnableCapsLockHud;
        EnableAgentMonitor = s.EnableAgentMonitor;
        EnableAgentApprovalHud = s.EnableAgentApprovalHud;
        DefaultOpenAgentsTab = s.DefaultOpenAgentsTab;
    }

    [RelayCommand]
    private void SaveAndClose()
    {
        AppSettings updated = _settingsService.Current.Clone();
        updated.TargetMonitorDeviceName = SelectedMonitorDevice;
        updated.HoverExpandDelayMs = Math.Clamp(HoverExpandDelayMs, 50, 800);
        updated.MouseLeaveCollapseDelayMs = Math.Clamp(MouseLeaveCollapseDelayMs, 80, 1200);
        updated.SpringPreset = SelectedSpringPreset;
        updated.ReplaceSystemVolumeOsd = ReplaceSystemVolumeOsd;
        updated.EnableCtrlGhostPassthrough = EnableCtrlGhostPassthrough;
        updated.EnableFullscreenHide = EnableFullscreenHide;
        updated.EnableFftVisualizer = EnableFftVisualizer;
        updated.EnablePrivacyDots = EnablePrivacyDots;
        updated.EnableBatteryHud = EnableBatteryHud;
        updated.EnableTrackChangeHud = EnableTrackChangeHud;
        updated.EnableUnlockFaceIdHud = EnableUnlockFaceIdHud;
        updated.EnableNotificationHud = EnableNotificationHud;
        updated.HideNotificationPreviewText = HideNotificationPreviewText;
        updated.RespectWindowsDoNotDisturb = RespectWindowsDoNotDisturb;
        updated.ShowCollapsedNetworkSpeed = ShowCollapsedNetworkSpeed;
        updated.AutoRemoveShelfItemAfterDrag = AutoRemoveShelfItemAfterDrag;
        updated.StartWithWindows = StartWithWindows;
        updated.EnableCollapsedScrollGestures = EnableCollapsedScrollGestures;
        updated.EnableAudioDeviceHud = EnableAudioDeviceHud;
        updated.EnableCapsLockHud = EnableCapsLockHud;
        updated.EnableAgentMonitor = EnableAgentMonitor;
        updated.EnableAgentApprovalHud = EnableAgentApprovalHud;
        updated.DefaultOpenAgentsTab = DefaultOpenAgentsTab;

        _settingsService.Save(updated);
        _closeWindowAction();
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        var defaults = new AppSettings
        {
            LastMediaAppHint = _settingsService.Current.LastMediaAppHint,
            LastMediaAppDisplayName = _settingsService.Current.LastMediaAppDisplayName
        };
        _settingsService.Save(defaults);
        LoadFromCurrentSettings();
    }

    [RelayCommand]
    private void PreviewFaceId() => _previewFaceIdAction?.Invoke();

    [RelayCommand]
    private void PreviewNotification() => _previewNotificationAction?.Invoke(HideNotificationPreviewText);

    [RelayCommand]
    private void PreviewAgentHud() => _previewAgentHudAction?.Invoke();

    [RelayCommand]
    private void Cancel() => _closeWindowAction();
}
