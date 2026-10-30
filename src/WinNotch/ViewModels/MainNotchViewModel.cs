using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;

namespace WinNotch.ViewModels;

public sealed partial class MainNotchViewModel : ObservableObject
{
    private static readonly Brush RamNormalBrush = CreateFrozenBrush("#38BDF8");
    private static readonly Brush RamWarnBrush = CreateFrozenBrush("#F59E0B");
    private static readonly Brush RamCritBrush = CreateFrozenBrush("#F87171");
    private static readonly Brush IpNormalAccentBrush = CreateFrozenBrush("#93C5FD");
    private static readonly Brush IpNormalTextBrush = CreateFrozenBrush("#F7F8FC");
    private static readonly Brush IpCopiedAccentBrush = CreateFrozenBrush("#34D399");
    private static readonly Brush IpCopiedTextBrush = CreateFrozenBrush("#6EE7B7");

    private readonly SettingsService _settingsService;
    private readonly NotchStateMachine _stateMachine;
    private readonly AudioService _audioService;
    private readonly DisplayPowerService _displayPowerService;
    private readonly MediaService _mediaService;
    private readonly FileShelfService _fileShelfService;
    private readonly TimerService _timerService;
    private readonly PrivacyService _privacyService;
    private readonly SystemMonitorService _systemMonitorService;
    private readonly Action _openSettingsAction;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _ipCopiedFeedbackTimer;
    private bool _isUserDraggingSeekbar;
    private bool _suppressSliderPush;
    private string _currentLocalIpv4 = "—";
    private bool _isIpCopiedFeedback;

    public ObservableCollection<ShelfItem> ShelfItems => _fileShelfService.Items;

    [ObservableProperty] private NotchVisualState _visualState = NotchVisualState.CollapsedIdle;
    [ObservableProperty] private NotchTab _selectedTab = NotchTab.Overview;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _clockText = DateTime.Now.ToString("HH:mm");
    [ObservableProperty] private string _shortDateBadgeText = string.Empty;
    [ObservableProperty] private bool _showCollapsedNetworkSpeed;
    [ObservableProperty] private string _collapsedNetSpeedText = "0 KB/s";

    // Collapsed & Privacy state
    [ObservableProperty] private bool _isCameraActive;
    [ObservableProperty] private string _cameraTooltip = "Camera đang bật";
    [ObservableProperty] private bool _isMicrophoneActive;
    [ObservableProperty] private string _microphoneTooltip = "Micro đang thu";

    // Normalized spectral wave levels (0.0 .. 1.0) driving SmoothWaveVisualizer
    [ObservableProperty] private double _waveBassLevel;
    [ObservableProperty] private double _waveMidLevel;
    [ObservableProperty] private double _waveTrebleLevel;

    // HUD state
    [ObservableProperty] private HudPayload _activeHud = new() { Kind = HudKind.Volume, Icon = IconsaxKind.VolumeHigh, ValueText = "50%", NormalizedValue = 0.5, IsInteractiveSlider = true };
    [ObservableProperty] private double _hudSliderPercent = 50.0;
    [ObservableProperty] private Brush _hudAccentBrush = Brushes.White;
    [ObservableProperty] private int _faceIdPulseToken;

    // Media / Quick Control state
    [ObservableProperty] private bool _hasMediaSession;
    [ObservableProperty] private bool _isMediaPlaying;
    [ObservableProperty] private string _mediaTitle = string.Empty;
    [ObservableProperty] private string _mediaArtist = string.Empty;
    [ObservableProperty] private string _mediaSourceName = string.Empty;
    [ObservableProperty] private ImageSource? _albumArt;
    [ObservableProperty] private Color _ambientGlowColor = Color.FromRgb(82, 130, 255);
    [ObservableProperty] private double _seekSeconds;
    [ObservableProperty] private double _durationSeconds = 1.0;
    [ObservableProperty] private bool _canSeek;
    [ObservableProperty] private string _positionLabel = "00:00";
    [ObservableProperty] private string _durationLabel = "00:00";

    // Control Center (Audio, Brightness, Quick Timer & Power)
    [ObservableProperty] private string _outputDeviceName = "Loa hệ thống";
    [ObservableProperty] private double _quickVolumePercent = 50.0;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private IconsaxKind _volumeIcon = IconsaxKind.VolumeHigh;
    [ObservableProperty] private bool _isBrightnessSupported = true;
    [ObservableProperty] private double _quickBrightnessPercent = 80.0;
    [ObservableProperty] private bool _hasLastMediaApp = true;
    [ObservableProperty] private string _lastMediaAppButtonText = "Mở Trình phát nhạc";
    [ObservableProperty] private bool _hasActiveTimer;
    [ObservableProperty] private bool _isTimerRunning;
    [ObservableProperty] private bool _isTimerAlarmRinging;
    [ObservableProperty] private string _timerCollapsedText = "00:00";
    [ObservableProperty] private string _timerDisplayText = "25:00";
    [ObservableProperty] private bool _hasBatteryDisplay;
    [ObservableProperty] private IconsaxKind _batteryIcon = IconsaxKind.BatteryFull;
    [ObservableProperty] private string _batteryPercentText = "100%";

    // File Shelf counters
    [ObservableProperty] private int _shelfCount;

    // Deep System & Live Network Telemetry (Tab "Hệ thống")
    [ObservableProperty] private string _sysCpuModelLabel = "CPU";
    [ObservableProperty] private string _sysCpuModelTooltip = "Bộ vi xử lý hệ thống";
    [ObservableProperty] private string _sysCpuPercentText = "0%";
    [ObservableProperty] private double[] _sysCpuHistory = [];
    [ObservableProperty] private string _sysGpuBadgeText = "GPU 0%";
    [ObservableProperty] private string _sysGpuTooltip = "Bộ xử lý đồ họa (GPU)";
    [ObservableProperty] private string _sysRamDetailText = "0.0 / 0.0 GB (0%)";
    [ObservableProperty] private int _sysRamPercent;
    [ObservableProperty] private Brush _sysRamBarBrush = RamNormalBrush;
    [ObservableProperty] private string _sysDriveLabelText = "Ổ C: trống — GB";
    [ObservableProperty] private string _sysDriveTooltip = "Ổ đĩa hệ thống";
    [ObservableProperty] private int _sysDriveUsedPercent;
    [ObservableProperty] private string _sysDiskRwText = "Đọc 0 • Ghi 0 MB/s";
    [ObservableProperty] private string _sysTopAppName = "Hệ thống rảnh";
    [ObservableProperty] private string _sysTopAppMetricText = "0% • 0 MB";
    [ObservableProperty] private IconsaxKind _sysNetIcon = IconsaxKind.Wifi;
    [ObservableProperty] private string _sysNetAdapterText = "Đang dò mạng...";
    [ObservableProperty] private string _sysNetSessionText = "↓ 0 MB  ↑ 0 MB";
    [ObservableProperty] private string _sysNetDownText = "0 KB/s";
    [ObservableProperty] private string _sysNetUpText = "0 KB/s";
    [ObservableProperty] private double[] _sysNetDownHistory = [];
    [ObservableProperty] private double[] _sysNetUpHistory = [];
    [ObservableProperty] private string _sysNetPeakLabel = "Đỉnh 64 KB/s";
    [ObservableProperty] private string _sysIpButtonText = "—";
    [ObservableProperty] private Brush _sysIpAccentBrush = IpNormalAccentBrush;
    [ObservableProperty] private Brush _sysIpTextBrush = IpNormalTextBrush;
    [ObservableProperty] private string _sysUptimeText = "00g 00p";

    public bool IsCollapsedIdleVisible =>
        VisualState == NotchVisualState.CollapsedIdle ||
        (VisualState == NotchVisualState.GhostPassthrough && !ComputeHasBackgroundActivity());

    public bool IsCollapsedActiveVisible =>
        VisualState == NotchVisualState.CollapsedActive ||
        (VisualState == NotchVisualState.GhostPassthrough && ComputeHasBackgroundActivity());

    public bool IsHudVisible => VisualState == NotchVisualState.HudPop;
    public bool IsSliderHudVisible => ActiveHud.IsInteractiveSlider && ActiveHud.Kind != HudKind.FaceIdUnlock;
    public bool IsStandardStatusHudVisible => !ActiveHud.IsInteractiveSlider && ActiveHud.Kind != HudKind.FaceIdUnlock;
    public bool IsFaceIdHudVisible => ActiveHud.Kind == HudKind.FaceIdUnlock;
    public bool IsDragReadyVisible => VisualState == NotchVisualState.DragReady;
    public bool IsExpandedVisible => VisualState == NotchVisualState.Expanded;
    public bool IsOverviewTabSelected => SelectedTab == NotchTab.Overview;
    public bool IsSystemTabSelected => SelectedTab == NotchTab.System;
    public bool IsFileShelfTabSelected => SelectedTab == NotchTab.FileShelf;
    public bool HasShelfItems => ShelfCount > 0;
    public bool IsShelfEmpty => ShelfCount == 0;
    public string ShelfTabHeader => ShelfCount > 0 ? $"Khay giữ file ({ShelfCount})" : "Khay giữ file";
    public string DragAllButtonText => $"Kéo tất cả ({ShelfCount})";
    public IconsaxKind PlayPauseIcon => IsMediaPlaying ? IconsaxKind.Pause : IconsaxKind.Play;
    public IconsaxKind TimerToggleIcon => IsTimerRunning ? IconsaxKind.Pause : IconsaxKind.Play;

    // Camera-Free Center-Utilized Collapsed Pill Visibility Rules
    public bool HasAlbumArt => AlbumArt != null;
    public bool ShowCollapsedTimerPill => HasActiveTimer || IsTimerAlarmRinging;
    public bool IsCollapsedIdleMode => !IsMediaPlaying && !ShowCollapsedTimerPill;
    public bool IsCollapsedMusicMode => IsMediaPlaying;
    public bool IsCollapsedTimerOnlyMode => !IsMediaPlaying && ShowCollapsedTimerPill;
    public bool IsCollapsedDualMode => IsMediaPlaying && ShowCollapsedTimerPill;
    public string CollapsedTrackTitle => string.IsNullOrWhiteSpace(MediaTitle) ? "Đang phát nhạc" : MediaTitle;

    public FileShelfService FileShelf => _fileShelfService;
    public NotchStateMachine StateMachine => _stateMachine;

    public MainNotchViewModel(
        SettingsService settingsService,
        NotchStateMachine stateMachine,
        AudioService audioService,
        DisplayPowerService displayPowerService,
        MediaService mediaService,
        FileShelfService fileShelfService,
        TimerService timerService,
        PrivacyService privacyService,
        SystemMonitorService systemMonitorService,
        Action openSettingsAction)
    {
        _settingsService = settingsService;
        _stateMachine = stateMachine;
        _audioService = audioService;
        _displayPowerService = displayPowerService;
        _mediaService = mediaService;
        _fileShelfService = fileShelfService;
        _timerService = timerService;
        _privacyService = privacyService;
        _systemMonitorService = systemMonitorService;
        _openSettingsAction = openSettingsAction;

        ShowCollapsedNetworkSpeed = _settingsService.Current.ShowCollapsedNetworkSpeed;

        _ipCopiedFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _ipCopiedFeedbackTimer.Tick += (_, _) =>
        {
            _ipCopiedFeedbackTimer.Stop();
            _isIpCopiedFeedback = false;
            RefreshIpButtonPresentation();
        };

        _stateMachine.StateChanged += (st, _, _) =>
        {
            VisualState = st;
            _systemMonitorService.SetDeepMonitoringActive(st == NotchVisualState.Expanded);
            NotifyVisibilityProperties();
        };
        _stateMachine.PinnedChanged += pinned => IsPinned = pinned;
        _stateMachine.RequestTabSwitch += tab => SelectTab(tab);

        _audioService.StateChanged += SyncAudioState;
        _audioService.VolumeHudRequested += ShowHud;
        _audioService.FftBarsUpdated += OnFftBarsUpdated;

        _displayPowerService.StateChanged += SyncDisplayPowerState;
        _displayPowerService.HudRequested += ShowHud;

        _mediaService.MediaUpdated += OnMediaSnapshotUpdated;
        _mediaService.TrackChangeHudRequested += ShowHud;

        _fileShelfService.ShelfChanged += SyncShelfCounters;
        _timerService.StateChanged += SyncTimerState;
        _privacyService.StateChanged += SyncPrivacyAndMetrics;
        _systemMonitorService.SnapshotUpdated += OnSystemTelemetryUpdated;
        _settingsService.SettingsChanged += s =>
        {
            ShowCollapsedNetworkSpeed = s.ShowCollapsedNetworkSpeed;
            RefreshLastMediaAppHint();
        };

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        UpdateClock();
        SyncAudioState();
        SyncDisplayPowerState();
        SyncShelfCounters();
        SyncTimerState();
        SyncPrivacyAndMetrics();
        OnSystemTelemetryUpdated(_systemMonitorService.LatestSnapshot);
        RefreshLastMediaAppHint();
    }

    partial void OnActiveHudChanged(HudPayload value)
    {
        OnPropertyChanged(nameof(IsSliderHudVisible));
        OnPropertyChanged(nameof(IsStandardStatusHudVisible));
        OnPropertyChanged(nameof(IsFaceIdHudVisible));
    }

    partial void OnIsPinnedChanged(bool value)
    {
        _stateMachine.IsPinned = value;
    }

    partial void OnSelectedTabChanged(NotchTab value)
    {
        OnPropertyChanged(nameof(IsOverviewTabSelected));
        OnPropertyChanged(nameof(IsSystemTabSelected));
        OnPropertyChanged(nameof(IsFileShelfTabSelected));
    }

    partial void OnIsMediaPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayPauseIcon));
    partial void OnIsTimerRunningChanged(bool value) => OnPropertyChanged(nameof(TimerToggleIcon));

    partial void OnShelfCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasShelfItems));
        OnPropertyChanged(nameof(IsShelfEmpty));
        OnPropertyChanged(nameof(ShelfTabHeader));
        OnPropertyChanged(nameof(DragAllButtonText));
    }

    partial void OnQuickVolumePercentChanged(double value)
    {
        if (_suppressSliderPush) return;
        _audioService.SetVolumePercent(value, showHud: false);
    }

    partial void OnQuickBrightnessPercentChanged(double value)
    {
        if (_suppressSliderPush) return;
        _displayPowerService.SetBrightnessPercent(value, showHud: false);
    }

    partial void OnHudSliderPercentChanged(double value)
    {
        if (_suppressSliderPush) return;
        if (ActiveHud.Kind == HudKind.Volume)
        {
            _audioService.SetVolumePercent(value, showHud: false);
            UpdateActiveHudInlineValue($"{Math.Round(value):0}%", value / 100.0);
        }
        else if (ActiveHud.Kind == HudKind.Brightness)
        {
            _displayPowerService.SetBrightnessPercent(value, showHud: false);
            UpdateActiveHudInlineValue($"{Math.Round(value):0}%", value / 100.0);
        }
        _stateMachine.ResetHudTimer();
    }

    public void AdjustActiveHudByWheel(int wheelDelta)
    {
        int step = wheelDelta > 0 ? +2 : -2;
        if (ActiveHud.Kind == HudKind.Brightness)
        {
            _displayPowerService.StepBrightness(step);
        }
        else
        {
            _audioService.StepVolume(step, toggleMute: false);
        }
        _stateMachine.ResetHudTimer();
    }

    public void BeginSeek() => _isUserDraggingSeekbar = true;

    public async void CommitSeek(double targetSeconds)
    {
        _isUserDraggingSeekbar = false;
        SeekSeconds = targetSeconds;
        PositionLabel = FormatTimeSpan(TimeSpan.FromSeconds(targetSeconds));
        await _mediaService.SeekToSecondsAsync(targetSeconds);
    }

    [RelayCommand]
    private void SelectOverviewTab() => SelectTab(NotchTab.Overview);

    [RelayCommand]
    private void SelectSystemTab() => SelectTab(NotchTab.System);

    [RelayCommand]
    private void SelectFileShelfTab() => SelectTab(NotchTab.FileShelf);

    private void SelectTab(NotchTab tab) => SelectedTab = tab;

    [RelayCommand]
    private void CopyLocalIp()
    {
        if (string.IsNullOrWhiteSpace(_currentLocalIpv4) || _currentLocalIpv4 == "—")
            return;

        try
        {
            Clipboard.SetText(_currentLocalIpv4);
            _isIpCopiedFeedback = true;
            RefreshIpButtonPresentation();
            _ipCopiedFeedbackTimer.Stop();
            _ipCopiedFeedbackTimer.Start();
        }
        catch { }
    }

    [RelayCommand]
    private void OpenTaskManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    [RelayCommand]
    private void OpenSettings() => _openSettingsAction();

    [RelayCommand]
    private async Task PreviousTrackAsync() => await _mediaService.PreviousTrackAsync();

    [RelayCommand]
    private async Task TogglePlayPauseAsync() => await _mediaService.TogglePlayPauseAsync();

    [RelayCommand]
    private async Task NextTrackAsync() => await _mediaService.NextTrackAsync();

    [RelayCommand]
    private void ToggleMute() => _audioService.ToggleMute(showHud: false);

    [RelayCommand]
    private void LaunchLastMediaApp() => _mediaService.TryLaunchLastMediaApp();

    // Control Center Quick Actions
    [RelayCommand]
    private void OpenSoundOutputSettings()
    {
        try
        {
            if (!IsPinned)
                _stateMachine.CollapseImmediately();
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task TriggerScreenSnipAsync()
    {
        try
        {
            _stateMachine.CollapseImmediately();
            await Task.Delay(170);
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void LockWorkstation()
    {
        try
        {
            _stateMachine.CollapseImmediately();
            NativeMethods.LockWorkStation();
        }
        catch { }
    }

    // Pomodoro / Timer Commands
    [RelayCommand]
    private void StartTimer5() => _timerService.StartPresetMinutes(5);

    [RelayCommand]
    private void StartTimer15() => _timerService.StartPresetMinutes(15);

    [RelayCommand]
    private void StartTimer25() => _timerService.StartPresetMinutes(25);

    [RelayCommand]
    private void AddTimer1Min() => _timerService.AddOneMinute();

    [RelayCommand]
    private void ToggleTimerPause() => _timerService.TogglePause();

    [RelayCommand]
    private void ResetTimer() => _timerService.Reset();

    // Shelf Commands
    [RelayCommand]
    private void RemoveShelfItem(ShelfItem? item) => _fileShelfService.RemoveItem(item);

    [RelayCommand]
    private void CopyShelfItem(ShelfItem? item) => _fileShelfService.CopyItemToClipboard(item);

    [RelayCommand]
    private void ClearShelf() => _fileShelfService.ClearAll();

    public void TriggerFaceIdUnlockHud()
    {
        FaceIdPulseToken++;

        ShowHud(new HudPayload
        {
            Kind = HudKind.FaceIdUnlock,
            Icon = IconsaxKind.Scan,
            IsInteractiveSlider = false,
            AccentHex = "#32D74B"
        }, forcePreemptExpanded: true);
    }

    private void ShowHud(HudPayload payload, bool forcePreemptExpanded = false)
    {
        bool isFaceId = payload.Kind == HudKind.FaceIdUnlock;

        _suppressSliderPush = true;
        try
        {
            ActiveHud = payload;
            HudSliderPercent = Math.Round(payload.NormalizedValue * 100.0);
            HudAccentBrush = ParseBrush(payload.AccentHex);
        }
        finally
        {
            _suppressSliderPush = false;
        }

        _stateMachine.TriggerHudPop(
            isFaceIdSquare: isFaceId,
            durationSeconds: isFaceId ? 2.18 : 2.0,
            forcePreemptExpanded: forcePreemptExpanded);
    }

    private void ShowHud(HudPayload payload) => ShowHud(payload, forcePreemptExpanded: false);

    private void UpdateActiveHudInlineValue(string valText, double norm)
    {
        IconsaxKind icon = ActiveHud.Icon;
        if (ActiveHud.Kind == HudKind.Volume)
        {
            icon = norm <= 0.005
                ? IconsaxKind.VolumeMute
                : norm < 0.50 ? IconsaxKind.VolumeLow : IconsaxKind.VolumeHigh;
        }

        ActiveHud = new HudPayload
        {
            Kind = ActiveHud.Kind,
            Icon = icon,
            Title = ActiveHud.Title,
            ValueText = valText,
            NormalizedValue = Math.Clamp(norm, 0.0, 1.0),
            IsInteractiveSlider = ActiveHud.IsInteractiveSlider,
            IsMuted = ActiveHud.IsMuted,
            AccentHex = ActiveHud.AccentHex,
            Thumbnail = ActiveHud.Thumbnail
        };
    }

    private void SyncAudioState()
    {
        _suppressSliderPush = true;
        try
        {
            OutputDeviceName = _audioService.OutputDeviceName;
            QuickVolumePercent = _audioService.VolumePercent;
            IsMuted = _audioService.IsMuted;
            VolumeIcon = IsMuted
                ? IconsaxKind.VolumeSlash
                : QuickVolumePercent <= 0.5
                    ? IconsaxKind.VolumeMute
                    : QuickVolumePercent < 50 ? IconsaxKind.VolumeLow : IconsaxKind.VolumeHigh;
        }
        finally
        {
            _suppressSliderPush = false;
        }
    }

    private void SyncDisplayPowerState()
    {
        _suppressSliderPush = true;
        try
        {
            IsBrightnessSupported = _displayPowerService.IsBrightnessSupported;
            QuickBrightnessPercent = _displayPowerService.BrightnessPercent;
        }
        finally
        {
            _suppressSliderPush = false;
        }
        UpdateMetricsBanner();
    }

    private void OnFftBarsUpdated(double b0, double b1, double b2, double b3)
    {
        WaveBassLevel = (b0 * 0.72) + (b1 * 0.28);
        WaveMidLevel = (b1 * 0.42) + (b2 * 0.58);
        WaveTrebleLevel = (b2 * 0.30) + (b3 * 0.70);
    }

    private void OnMediaSnapshotUpdated(MediaSnapshot snap)
    {
        HasMediaSession = snap.HasSession;
        IsMediaPlaying = snap.HasSession && snap.IsPlaying;
        MediaTitle = snap.Title;
        MediaArtist = snap.Artist;
        MediaSourceName = snap.SourceDisplayName;
        AlbumArt = snap.AlbumArt;
        AmbientGlowColor = snap.DominantColor;
        OnPropertyChanged(nameof(HasAlbumArt));
        OnPropertyChanged(nameof(CollapsedTrackTitle));

        double durSec = Math.Max(0.0, snap.Duration.TotalSeconds);
        CanSeek = durSec > 1.0;
        DurationSeconds = CanSeek ? durSec : 1.0;
        DurationLabel = CanSeek ? FormatTimeSpan(snap.Duration) : "--:--";

        if (!_isUserDraggingSeekbar)
        {
            SeekSeconds = CanSeek ? Math.Clamp(snap.Position.TotalSeconds, 0.0, durSec) : 0.0;
            PositionLabel = CanSeek ? FormatTimeSpan(snap.Position) : "00:00";
        }

        // Strictly gate WASAPI Loopback FFT on PlaybackStatus == Playing (SPEC 4.1)
        _audioService.SetMediaPlayingGate(IsMediaPlaying);
        RefreshLastMediaAppHint();
        RefreshBackgroundActivityState();
    }

    private void SyncShelfCounters()
    {
        ShelfCount = _fileShelfService.Items.Count;
    }

    private void SyncTimerState()
    {
        HasActiveTimer = _timerService.HasActiveTimer;
        IsTimerRunning = _timerService.IsRunning;
        IsTimerAlarmRinging = _timerService.IsAlarmRinging;
        TimerCollapsedText = _timerService.FormattedTime;
        TimerDisplayText = _timerService.HasActiveTimer ? _timerService.FormattedTime : "25:00";
        RefreshBackgroundActivityState();
    }

    private void SyncPrivacyAndMetrics()
    {
        IsCameraActive = _privacyService.IsCameraActive;
        CameraTooltip = string.IsNullOrWhiteSpace(_privacyService.CameraActiveApp)
            ? "Camera đang hoạt động"
            : $"Camera đang dùng bởi: {_privacyService.CameraActiveApp}";

        IsMicrophoneActive = _privacyService.IsMicrophoneActive;
        MicrophoneTooltip = string.IsNullOrWhiteSpace(_privacyService.MicrophoneActiveApp)
            ? "Micro đang thu âm"
            : $"Micro đang dùng bởi: {_privacyService.MicrophoneActiveApp}";

        UpdateMetricsBanner();
        RefreshBackgroundActivityState();
    }

    private void UpdateMetricsBanner()
    {
        HasBatteryDisplay = _displayPowerService.HasBattery;

        if (_displayPowerService.HasBattery)
        {
            BatteryIcon = _displayPowerService.IsCharging
                ? IconsaxKind.BatteryCharging
                : _displayPowerService.BatteryPercent <= 20
                    ? IconsaxKind.BatteryLow
                    : IconsaxKind.BatteryFull;
            BatteryPercentText = $"{_displayPowerService.BatteryPercent}%";
        }
    }

    private void OnSystemTelemetryUpdated(SystemTelemetrySnapshot snap)
    {
        CollapsedNetSpeedText = SystemMonitorService.FormatSpeed(snap.DownloadBytesPerSec, compact: true);

        SysCpuModelLabel = $"{snap.CpuName} • {snap.LogicalCores} luồng";
        SysCpuModelTooltip = $"{snap.CpuName} ({snap.LogicalCores} luồng xử lý logic)";
        SysCpuPercentText = $"{snap.CpuPercent}%";
        SysCpuHistory = snap.CpuHistory;

        SysGpuBadgeText = snap.IsGpuAvailable ? $"GPU {snap.GpuPercent}%" : "GPU —";
        SysGpuTooltip = string.IsNullOrWhiteSpace(snap.GpuVramLabel)
            ? $"{snap.GpuName} • Mức tải {snap.GpuPercent}%"
            : $"{snap.GpuName} ({snap.GpuVramLabel}) • Mức tải {snap.GpuPercent}%";

        SysRamPercent = snap.RamPercent;
        SysRamDetailText = snap.RamTotalGb > 0
            ? $"{snap.RamUsedGb:0.0} / {snap.RamTotalGb:0.0} GB ({snap.RamPercent}%)"
            : $"{snap.RamPercent}%";
        SysRamBarBrush = snap.RamPercent >= 92
            ? RamCritBrush
            : snap.RamPercent >= 80 ? RamWarnBrush : RamNormalBrush;

        SysDriveUsedPercent = snap.DriveUsedPercent;
        SysDriveLabelText = snap.DriveTotalGb > 0
            ? $"Ổ {snap.SystemDriveName} trống {snap.DriveFreeGb:0} GB"
            : $"Ổ {snap.SystemDriveName}";
        SysDriveTooltip = snap.DriveTotalGb > 0
            ? $"Ổ đĩa hệ thống {snap.SystemDriveName} — Còn trống {snap.DriveFreeGb:0.0} GB / Tổng {snap.DriveTotalGb:0.0} GB (Đã dùng {snap.DriveUsedPercent}%)"
            : "Ổ đĩa hệ thống";

        double rdMb = snap.DiskReadBytesPerSec / (1024.0 * 1024.0);
        double wrMb = snap.DiskWriteBytesPerSec / (1024.0 * 1024.0);
        SysDiskRwText = $"Đọc {FormatDiskRateMb(rdMb)} • Ghi {FormatDiskRateMb(wrMb)}";

        SysTopAppName = snap.TopAppName;
        string ramAppText = snap.TopAppRamMb >= 1024.0
            ? $"{snap.TopAppRamMb / 1024.0:0.0} GB"
            : $"{snap.TopAppRamMb:0} MB";
        SysTopAppMetricText = $"{snap.TopAppCpuPercent:0.#}% • {ramAppText}";

        SysNetIcon = snap.IsWifi ? IconsaxKind.Wifi : IconsaxKind.Global;
        SysNetAdapterText = snap.IsNetworkConnected ? snap.NetworkAdapterName : "Không có mạng";
        SysNetSessionText = $"↓ {SystemMonitorService.FormatBytesTotal(snap.SessionDownloadedBytes)}  ↑ {SystemMonitorService.FormatBytesTotal(snap.SessionUploadedBytes)}";
        SysNetDownText = SystemMonitorService.FormatSpeed(snap.DownloadBytesPerSec);
        SysNetUpText = SystemMonitorService.FormatSpeed(snap.UploadBytesPerSec);
        SysNetDownHistory = snap.DownloadHistory;
        SysNetUpHistory = snap.UploadHistory;

        double peakBps = 65536.0;
        for (int i = 0; i < snap.DownloadHistory.Length; i++)
            if (snap.DownloadHistory[i] > peakBps) peakBps = snap.DownloadHistory[i];
        for (int i = 0; i < snap.UploadHistory.Length; i++)
            if (snap.UploadHistory[i] > peakBps) peakBps = snap.UploadHistory[i];
        SysNetPeakLabel = $"Đỉnh {SystemMonitorService.FormatSpeed(peakBps)}";

        _currentLocalIpv4 = snap.LocalIpv4;
        RefreshIpButtonPresentation();

        SysUptimeText = SystemMonitorService.FormatUptime(snap.Uptime);
    }

    private void RefreshIpButtonPresentation()
    {
        if (_isIpCopiedFeedback)
        {
            SysIpButtonText = "Đã chép IP!";
            SysIpAccentBrush = IpCopiedAccentBrush;
            SysIpTextBrush = IpCopiedTextBrush;
        }
        else
        {
            SysIpButtonText = string.IsNullOrWhiteSpace(_currentLocalIpv4) || _currentLocalIpv4 == "—"
                ? "Chưa có IP"
                : _currentLocalIpv4;
            SysIpAccentBrush = IpNormalAccentBrush;
            SysIpTextBrush = IpNormalTextBrush;
        }
    }

    private static string FormatDiskRateMb(double mbPerSec)
    {
        if (mbPerSec < 0.05) return "0 MB/s";
        if (mbPerSec < 10.0) return $"{mbPerSec:0.1} MB/s";
        return $"{mbPerSec:0} MB/s";
    }

    private static Brush CreateFrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private void RefreshLastMediaAppHint()
    {
        string name = _settingsService.Current.LastMediaAppDisplayName;
        HasLastMediaApp = true;

        if (!string.IsNullOrWhiteSpace(name))
        {
            LastMediaAppButtonText = $"Mở {name}";
            return;
        }

        string spotifyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Spotify",
            "Spotify.exe");
        LastMediaAppButtonText = File.Exists(spotifyPath) ? "Mở Spotify" : "Mở Trình phát nhạc";
    }

    private bool ComputeHasBackgroundActivity() =>
        IsMediaPlaying || HasActiveTimer || IsTimerAlarmRinging || IsCameraActive || IsMicrophoneActive;

    private void RefreshBackgroundActivityState()
    {
        bool hasActivity = ComputeHasBackgroundActivity();
        CollapsedContentKind kind = (IsMediaPlaying, ShowCollapsedTimerPill) switch
        {
            (true, true) => CollapsedContentKind.MusicAndTimer,
            (true, false) => CollapsedContentKind.MusicOnly,
            (false, true) => CollapsedContentKind.TimerOnly,
            _ => CollapsedContentKind.Idle
        };
        int privacyDots = (IsCameraActive ? 1 : 0) + (IsMicrophoneActive ? 1 : 0);

        _stateMachine.SetBackgroundActivity(hasActivity, kind, privacyDots);
        NotifyVisibilityProperties();
    }

    private void NotifyVisibilityProperties()
    {
        OnPropertyChanged(nameof(IsCollapsedIdleVisible));
        OnPropertyChanged(nameof(IsCollapsedActiveVisible));
        OnPropertyChanged(nameof(IsHudVisible));
        OnPropertyChanged(nameof(IsDragReadyVisible));
        OnPropertyChanged(nameof(IsExpandedVisible));
        OnPropertyChanged(nameof(ShowCollapsedTimerPill));
        OnPropertyChanged(nameof(IsCollapsedIdleMode));
        OnPropertyChanged(nameof(IsCollapsedMusicMode));
        OnPropertyChanged(nameof(IsCollapsedTimerOnlyMode));
        OnPropertyChanged(nameof(IsCollapsedDualMode));
        OnPropertyChanged(nameof(CollapsedTrackTitle));
    }

    private void UpdateClock()
    {
        DateTime now = DateTime.Now;
        ClockText = now.ToString("HH:mm");
        ShortDateBadgeText = $"{FormatShortDayBadge(now.DayOfWeek)}, {now:dd}";
    }

    private static string FormatShortDayBadge(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday => "T2",
        DayOfWeek.Tuesday => "T3",
        DayOfWeek.Wednesday => "T4",
        DayOfWeek.Thursday => "T5",
        DayOfWeek.Friday => "T6",
        DayOfWeek.Saturday => "T7",
        _ => "CN"
    };

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1.0)
            return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}";
        return $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private static Brush ParseBrush(string hex)
    {
        try
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.White;
        }
    }
}
