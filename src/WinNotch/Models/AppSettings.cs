namespace WinNotch.Models;

public sealed class AppSettings
{
    public string TargetMonitorDeviceName { get; set; } = string.Empty;
    public int HoverExpandDelayMs { get; set; } = 160;
    public int MouseLeaveCollapseDelayMs { get; set; } = 250;
    public string SpringPreset { get; set; } = "Balanced"; // Balanced | Snappy | Bouncy

    public bool ReplaceSystemVolumeOsd { get; set; } = true;
    public bool EnableCtrlGhostPassthrough { get; set; } = true;
    public bool EnableFullscreenHide { get; set; } = true;
    public bool EnableFftVisualizer { get; set; } = true;
    public bool EnablePrivacyDots { get; set; } = true;
    public bool EnableBatteryHud { get; set; } = true;
    public bool EnableTrackChangeHud { get; set; } = true;
    public bool EnableUnlockFaceIdHud { get; set; } = true;
    public bool ShowCollapsedNetworkSpeed { get; set; } = false;
    public bool AutoRemoveShelfItemAfterDrag { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;

    public string LastMediaAppHint { get; set; } = string.Empty;
    public string LastMediaAppDisplayName { get; set; } = string.Empty;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
