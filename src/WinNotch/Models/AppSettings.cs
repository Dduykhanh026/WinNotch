namespace WinNotch.Models;

public sealed class AppSettings
{
    public string TargetMonitorDeviceName { get; set; } = string.Empty;
    public int HoverExpandDelayMs { get; set; } = 160;
    public int MouseLeaveCollapseDelayMs { get; set; } = 250;
    public string SpringPreset { get; set; } = "Balanced"; // Balanced | Snappy | Bouncy
    public string ExpandedSizePreset { get; set; } = "Compact"; // "Compact" | "Spacious"

    public bool ReplaceSystemVolumeOsd { get; set; } = true;
    public bool EnableCtrlGhostPassthrough { get; set; } = true;
    public bool EnableFullscreenHide { get; set; } = true;
    public bool EnableFftVisualizer { get; set; } = true;
    public bool EnablePrivacyDots { get; set; } = true;
    public bool EnableBatteryHud { get; set; } = true;
    public bool EnableTrackChangeHud { get; set; } = true;
    public bool EnableUnlockFaceIdHud { get; set; } = true;
    public bool EnableNotificationHud { get; set; } = true;
    public bool HideNotificationPreviewText { get; set; } = false;
    public bool RespectWindowsDoNotDisturb { get; set; } = false;
    public bool EnableCollapsedScrollGestures { get; set; } = true;
    public bool EnableAudioDeviceHud { get; set; } = true;
    public bool EnableCapsLockHud { get; set; } = true;
    public bool EnableAgentMonitor { get; set; } = true;
    public bool EnableAgentApprovalHud { get; set; } = true;
    public bool DefaultOpenAgentsTab { get; set; } = true;
    public bool ShowCollapsedNetworkSpeed { get; set; } = false;
    public bool AutoRemoveShelfItemAfterDrag { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;

    public string LastMediaAppHint { get; set; } = string.Empty;
    public string LastMediaAppDisplayName { get; set; } = string.Empty;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
