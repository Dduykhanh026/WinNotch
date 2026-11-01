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

    [ObservableProperty] private int _selectedCategoryIndex;
    [ObservableProperty] private string _selectedMonitorDevice = string.Empty;
    [ObservableProperty] private string _selectedExpandedSizePreset = "Compact";
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

    public bool IsCat0AppearanceSelected => SelectedCategoryIndex == 0;
    public bool IsCat1AgentsSelected => SelectedCategoryIndex == 1;
    public bool IsCat2InteractionSelected => SelectedCategoryIndex == 2;
    public bool IsCat3HudSelected => SelectedCategoryIndex == 3;
    public bool IsCat4SystemShelfSelected => SelectedCategoryIndex == 4;

    public string CurrentCategoryTitle => SelectedCategoryIndex switch
    {
        1 => "Trung tâm AI Agents",
        2 => "Tương tác & Cử chỉ",
        3 => "Thông báo & Dynamic HUD",
        4 => "Hệ thống & Khay giữ file",
        _ => "Giao diện & Vật lý chuyển động"
    };

    public string CurrentCategorySubtitle => SelectedCategoryIndex switch
    {
        1 => "Theo dõi thời gian thực tiến độ dự án, Subagents và thông báo phê duyệt (0% CPU khi rảnh)",
        2 => "Tùy chỉnh Ghost Mode bấm xuyên qua Notch, cử chỉ cuộn chuột và ẩn tự động Fullscreen",
        3 => "Quản lý thẻ thông báo Dynamic Island đen tuyền #050507, Face ID, Pin, Tai nghe và Caps Lock",
        4 => "Cấu hình sóng nhạc FFT, chấm bảo mật Camera/Micro, tốc độ mạng và hành vi Khay giữ file",
        _ => "Chọn kích thước khung mở rộng, độ nảy lò xo 240Hz, màn hình đích và độ trễ chống lướt nhầm"
    };

    public bool IsCompactSizeSelected => !string.Equals(SelectedExpandedSizePreset, "Spacious", StringComparison.OrdinalIgnoreCase);
    public bool IsSpaciousSizeSelected => string.Equals(SelectedExpandedSizePreset, "Spacious", StringComparison.OrdinalIgnoreCase);

    public bool IsBalancedSpringSelected => string.Equals(SelectedSpringPreset, "Balanced", StringComparison.OrdinalIgnoreCase);
    public bool IsSnappySpringSelected => string.Equals(SelectedSpringPreset, "Snappy", StringComparison.OrdinalIgnoreCase);
    public bool IsBouncySpringSelected => string.Equals(SelectedSpringPreset, "Bouncy", StringComparison.OrdinalIgnoreCase);

    public string StoragePathDisplay => _settingsService.DataDirectory;
    public string ModeBadgeText => _settingsService.IsPortableMode ? "Portable • 100% Offline" : "Chuẩn • 100% Offline";

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

    partial void OnSelectedCategoryIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCat0AppearanceSelected));
        OnPropertyChanged(nameof(IsCat1AgentsSelected));
        OnPropertyChanged(nameof(IsCat2InteractionSelected));
        OnPropertyChanged(nameof(IsCat3HudSelected));
        OnPropertyChanged(nameof(IsCat4SystemShelfSelected));
        OnPropertyChanged(nameof(CurrentCategoryTitle));
        OnPropertyChanged(nameof(CurrentCategorySubtitle));
    }

    partial void OnSelectedExpandedSizePresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsCompactSizeSelected));
        OnPropertyChanged(nameof(IsSpaciousSizeSelected));
    }

    partial void OnSelectedSpringPresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsBalancedSpringSelected));
        OnPropertyChanged(nameof(IsSnappySpringSelected));
        OnPropertyChanged(nameof(IsBouncySpringSelected));
    }

    [RelayCommand]
    private void SelectCategory(string? indexText)
    {
        if (int.TryParse(indexText, out int idx))
        {
            SelectedCategoryIndex = Math.Clamp(idx, 0, 4);
        }
    }

    [RelayCommand]
    private void SelectExpandedSize(string? preset)
    {
        SelectedExpandedSizePreset = string.Equals(preset, "Spacious", StringComparison.OrdinalIgnoreCase)
            ? "Spacious"
            : "Compact";
    }

    [RelayCommand]
    private void SelectSpringPresetCard(string? preset)
    {
        if (string.Equals(preset, "Snappy", StringComparison.OrdinalIgnoreCase))
            SelectedSpringPreset = "Snappy";
        else if (string.Equals(preset, "Bouncy", StringComparison.OrdinalIgnoreCase))
            SelectedSpringPreset = "Bouncy";
        else
            SelectedSpringPreset = "Balanced";
    }

    private void LoadFromCurrentSettings()
    {
        AppSettings s = _settingsService.Current;
        SelectedMonitorDevice = Monitors.Any(m => m.DeviceName == s.TargetMonitorDeviceName)
            ? s.TargetMonitorDeviceName
            : string.Empty;
        SelectedExpandedSizePreset = string.Equals(s.ExpandedSizePreset, "Spacious", StringComparison.OrdinalIgnoreCase)
            ? "Spacious"
            : "Compact";
        HoverExpandDelayMs = s.HoverExpandDelayMs;
        MouseLeaveCollapseDelayMs = s.MouseLeaveCollapseDelayMs;
        SelectedSpringPreset = string.IsNullOrWhiteSpace(s.SpringPreset) ? "Balanced" : s.SpringPreset;
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
        updated.ExpandedSizePreset = SelectedExpandedSizePreset;
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
