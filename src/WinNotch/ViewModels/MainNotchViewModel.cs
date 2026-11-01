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
    private static readonly Brush AgentRunningAccentBrush = CreateFrozenBrush("#60A5FA");
    private static readonly Brush AgentRunningPillBgBrush = CreateFrozenBrush("#243B82F6");
    private static readonly Brush AgentWaitingAccentBrush = CreateFrozenBrush("#FBBF24");
    private static readonly Brush AgentWaitingPillBgBrush = CreateFrozenBrush("#2CF59E0B");

    private readonly SettingsService _settingsService;
    private readonly NotchStateMachine _stateMachine;
    private readonly AudioService _audioService;
    private readonly DisplayPowerService _displayPowerService;
    private readonly MediaService _mediaService;
    private readonly FileShelfService _fileShelfService;
    private readonly TimerService _timerService;
    private readonly PrivacyService _privacyService;
    private readonly SystemMonitorService _systemMonitorService;
    private readonly NotificationService _notificationService;
    private readonly ColorPickerService _colorPickerService;
    private readonly AgentMonitorService _agentMonitorService;
    private readonly Action _openSettingsAction;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _ipCopiedFeedbackTimer;
    private readonly DispatcherTimer _hookCopiedFeedbackTimer;
    private readonly HashSet<string> _collapsedAgentGroupIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isUserDraggingSeekbar;
    private bool _suppressSliderPush;
    private string _currentLocalIpv4 = "—";
    private bool _isIpCopiedFeedback;

    public ObservableCollection<ShelfItem> ShelfItems => _fileShelfService.Items;
    public ObservableCollection<AgentSessionItem> AgentSessions { get; } = [];
    public ObservableCollection<AgentSessionItem> FilteredAgentSessions { get; } = [];

    [ObservableProperty] private NotchVisualState _visualState = NotchVisualState.CollapsedIdle;
    [ObservableProperty] private NotchTab _selectedTab = NotchTab.Agents;
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
    [ObservableProperty] private int _notificationPulseToken;
    [ObservableProperty] private int _hudArrivalPulseToken;
    [ObservableProperty] private double _desiredAgentsHeight;

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
    [ObservableProperty] private string _batteryStatusDetail = "Nguồn trực tiếp";
    [ObservableProperty] private bool _isCaffeineActive;
    [ObservableProperty] private bool _isMicMuted;
    [ObservableProperty] private IconsaxKind _outputDeviceIcon = IconsaxKind.Headphone;

    partial void OnIsMicMutedChanged(bool value)
    {
        RefreshBackgroundActivityState();
    }

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
    [ObservableProperty] private bool _isRamOptimizing;
    [ObservableProperty] private string _ramOptimizedFeedbackText = string.Empty;

    public string RamOptimizedButtonText => IsRamOptimizing
        ? "Đang dọn..."
        : !string.IsNullOrEmpty(RamOptimizedFeedbackText)
            ? RamOptimizedFeedbackText
            : "Dọn RAM";

    public Brush RamOptimizedButtonBrush => !string.IsNullOrEmpty(RamOptimizedFeedbackText)
        ? RamWarnBrush
        : RamNormalBrush;

    private DispatcherTimer? _ramFeedbackTimer;
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

    // Real-Time AI Agent Monitor (Tab "Agents" & Collapsed Live Activity)
    [ObservableProperty] private AgentSessionItem? _primaryAgent;
    [ObservableProperty] private bool _hasAnyAgents;
    [ObservableProperty] private bool _hasFilteredAgentSessions;
    [ObservableProperty] private bool _showOnlyActiveAgents;
    [ObservableProperty] private bool _hasActiveOrWaitingAgents;
    [ObservableProperty] private bool _isAgentWaitingApproval;
    [ObservableProperty] private int _activeAndWaitingAgentCount;
    [ObservableProperty] private string _collapsedAgentText = string.Empty;
    [ObservableProperty] private IconsaxKind _collapsedAgentIcon = IconsaxKind.Bot;
    [ObservableProperty] private string _agentSwarmSummaryText = "Tự động theo dõi ~0% CPU • 100% Offline";
    [ObservableProperty] private string _agentStreamCountBadge = "0 phiên";
    [ObservableProperty] private string _agentHookButtonText = "Chép Hook";

    public bool IsAllAgentsFilterSelected => !ShowOnlyActiveAgents;
    public bool IsActiveAgentsFilterSelected => ShowOnlyActiveAgents;
    public bool ShowEmptyFilteredAgentsHint => HasAnyAgents && !HasFilteredAgentSessions;

    public bool IsCollapsedIdleVisible =>
        VisualState == NotchVisualState.CollapsedIdle ||
        (VisualState == NotchVisualState.GhostPassthrough && !ComputeHasBackgroundActivity());

    public bool IsCollapsedActiveVisible =>
        VisualState == NotchVisualState.CollapsedActive ||
        (VisualState == NotchVisualState.GhostPassthrough && ComputeHasBackgroundActivity());

    public bool IsHudVisible => VisualState == NotchVisualState.HudPop;
    public bool IsSliderHudVisible => ActiveHud.IsInteractiveSlider && ActiveHud.Kind is not (HudKind.FaceIdUnlock or HudKind.Notification or HudKind.AgentAlert);
    public bool IsStandardStatusHudVisible => !ActiveHud.IsInteractiveSlider && ActiveHud.Kind is not (HudKind.FaceIdUnlock or HudKind.Notification or HudKind.AgentAlert);
    public bool IsFaceIdHudVisible => ActiveHud.Kind == HudKind.FaceIdUnlock;
    public bool IsNotificationHudVisible => ActiveHud.Kind is HudKind.Notification or HudKind.AgentAlert;
    public string NotificationMetaText => string.IsNullOrWhiteSpace(ActiveHud.Subtitle)
        ? ActiveHud.TimestampBadge
        : $"{ActiveHud.Subtitle} · {ActiveHud.TimestampBadge}";
    public bool IsHudColorSwatchVisible => ActiveHud.Kind == HudKind.ColorPicked;
    public bool IsHudMiniMeterVisible => (ActiveHud.Kind is HudKind.AudioDevice or HudKind.Battery) && ActiveHud.NormalizedValue > 0;
    public double HudMiniMeterWidth => Math.Clamp(ActiveHud.NormalizedValue, 0.0, 1.0) * 42.0;
    public bool IsHudAlbumArtVisible => !IsHudColorSwatchVisible && ActiveHud.Thumbnail != null;
    public bool IsHudIconVisible => !IsHudColorSwatchVisible && ActiveHud.Thumbnail == null;
    public double ActiveExpandedWidth => _stateMachine.ActiveExpandedDimensions.Width;
    public double ActiveExpandedHeight => _stateMachine.ActiveExpandedDimensions.Height;
    public bool IsDragReadyVisible => VisualState == NotchVisualState.DragReady;
    public bool IsExpandedVisible => VisualState == NotchVisualState.Expanded;
    public bool IsAgentsTabSelected => SelectedTab == NotchTab.Agents;
    public bool IsOverviewTabSelected => SelectedTab == NotchTab.Overview;
    public bool IsSystemTabSelected => SelectedTab == NotchTab.System;
    public bool IsFileShelfTabSelected => SelectedTab == NotchTab.FileShelf;
    public bool HasShelfItems => ShelfCount > 0;
    public bool IsShelfEmpty => ShelfCount == 0;
    public string AgentsTabHeader => ActiveAndWaitingAgentCount > 0 ? $"Agents ({ActiveAndWaitingAgentCount})" : "Agents";
    public Brush CollapsedAgentAccentBrush => IsAgentWaitingApproval ? AgentWaitingAccentBrush : AgentRunningAccentBrush;
    public Brush CollapsedAgentPillBgBrush => IsAgentWaitingApproval ? AgentWaitingPillBgBrush : AgentRunningPillBgBrush;
    public string ShelfTabHeader => ShelfCount > 0 ? $"Khay giữ file ({ShelfCount})" : "Khay giữ file";
    public string DragAllButtonText => $"Kéo tất cả ({ShelfCount})";
    public IconsaxKind PlayPauseIcon => IsMediaPlaying ? IconsaxKind.Pause : IconsaxKind.Play;
    public IconsaxKind TimerToggleIcon => IsTimerRunning ? IconsaxKind.Pause : IconsaxKind.Play;

    // Camera-Free Center-Utilized Collapsed Pill Visibility Rules
    public bool HasAlbumArt => AlbumArt != null;
    public bool ShowCollapsedTimerPill => HasActiveTimer || IsTimerAlarmRinging;
    public bool IsCollapsedIdleMode => !IsMediaPlaying && !ShowCollapsedTimerPill && !HasActiveOrWaitingAgents;
    public bool IsCollapsedAgentOnlyMode => !IsMediaPlaying && !ShowCollapsedTimerPill && HasActiveOrWaitingAgents;
    public bool ShowTrailingAgentIndicator => (IsMediaPlaying || ShowCollapsedTimerPill) && HasActiveOrWaitingAgents;
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
        NotificationService notificationService,
        ColorPickerService colorPickerService,
        AgentMonitorService agentMonitorService,
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
        _notificationService = notificationService;
        _colorPickerService = colorPickerService;
        _agentMonitorService = agentMonitorService;
        _openSettingsAction = openSettingsAction;

        _selectedTab = _settingsService.Current.DefaultOpenAgentsTab ? NotchTab.Agents : NotchTab.Overview;

        _colorPickerService.ColorPickedHudRequested += payload => ShowHud(payload);
        _agentMonitorService.SnapshotUpdated += OnAgentSnapshotUpdated;
        _agentMonitorService.AgentHudRequested += payload => ShowHud(payload, forcePreemptExpanded: false);

        ShowCollapsedNetworkSpeed = _settingsService.Current.ShowCollapsedNetworkSpeed;

        _ipCopiedFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _ipCopiedFeedbackTimer.Tick += (_, _) =>
        {
            _ipCopiedFeedbackTimer.Stop();
            _isIpCopiedFeedback = false;
            RefreshIpButtonPresentation();
        };

        _hookCopiedFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _hookCopiedFeedbackTimer.Tick += (_, _) =>
        {
            _hookCopiedFeedbackTimer.Stop();
            AgentHookButtonText = "Chép Hook";
        };

        _stateMachine.StateChanged += (st, _, _) =>
        {
            VisualState = st;
            _systemMonitorService.SetDeepMonitoringActive(st == NotchVisualState.Expanded);
            OnPropertyChanged(nameof(ActiveExpandedWidth));
            OnPropertyChanged(nameof(ActiveExpandedHeight));
            NotifyVisibilityProperties();
        };
        _stateMachine.PinnedChanged += pinned => IsPinned = pinned;
        _stateMachine.RequestTabSwitch += tab => SelectTab(tab);

        _settingsService.SettingsChanged += _ =>
        {
            OnPropertyChanged(nameof(ActiveExpandedWidth));
            OnPropertyChanged(nameof(ActiveExpandedHeight));
            RebuildFilteredAgentSessions();
        };

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
        _notificationService.NotificationHudRequested += ShowHud;
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
        OnAgentSnapshotUpdated(_agentMonitorService.LatestSnapshot);
        RefreshLastMediaAppHint();
    }

    partial void OnActiveHudChanged(HudPayload value)
    {
        OnPropertyChanged(nameof(IsSliderHudVisible));
        OnPropertyChanged(nameof(IsStandardStatusHudVisible));
        OnPropertyChanged(nameof(IsFaceIdHudVisible));
        OnPropertyChanged(nameof(IsNotificationHudVisible));
        OnPropertyChanged(nameof(NotificationMetaText));
        OnPropertyChanged(nameof(IsHudColorSwatchVisible));
        OnPropertyChanged(nameof(IsHudMiniMeterVisible));
        OnPropertyChanged(nameof(HudMiniMeterWidth));
        OnPropertyChanged(nameof(IsHudAlbumArtVisible));
        OnPropertyChanged(nameof(IsHudIconVisible));
    }

    partial void OnIsPinnedChanged(bool value)
    {
        _stateMachine.IsPinned = value;
    }

    partial void OnSelectedTabChanged(NotchTab value)
    {
        OnPropertyChanged(nameof(IsAgentsTabSelected));
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
    private void SelectAgentsTab() => SelectTab(NotchTab.Agents);

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

    [RelayCommand]
    private void ToggleCaffeine() => _displayPowerService.ToggleCaffeine(showHud: true);

    [RelayCommand]
    private void ToggleMicMute() => _audioService.ToggleMicrophoneMute(showHud: true);

    [RelayCommand]
    private void CycleAudioOutput() => _audioService.CycleOutputDevice(showHud: true);

    public void StepVolume(int deltaPercent) => _audioService.StepVolume(deltaPercent, toggleMute: false);
    public void StepBrightness(int deltaPercent) => _displayPowerService.StepBrightness(deltaPercent);

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

    [RelayCommand]
    public void StepTimerMinutes(int deltaMinutes) => _timerService.StepMinutes(deltaMinutes);

    // Shelf Commands
    [RelayCommand]
    public void PasteShelfFromClipboard() => _fileShelfService.PasteFromClipboard();

    [RelayCommand]
    private void OpenShelfItem(ShelfItem? item) => _fileShelfService.OpenItem(item);

    [RelayCommand]
    private void RevealShelfItemInExplorer(ShelfItem? item) => _fileShelfService.RevealInExplorer(item);

    [RelayCommand]
    private void RemoveShelfItem(ShelfItem? item) => _fileShelfService.RemoveItem(item);

    [RelayCommand]
    private void CopyShelfItem(ShelfItem? item) => _fileShelfService.CopyItemToClipboard(item);

    [RelayCommand]
    private void ClearShelf() => _fileShelfService.ClearAll();

    [RelayCommand]
    public async Task OptimizeRamAsync()
    {
        if (IsRamOptimizing) return;
        IsRamOptimizing = true;
        OnPropertyChanged(nameof(RamOptimizedButtonText));
        OnPropertyChanged(nameof(RamOptimizedButtonBrush));

        try
        {
            long reclaimedBytes = await _systemMonitorService.OptimizeWorkingSetAsync();
            long reclaimedMb = reclaimedBytes / (1024L * 1024L);

            string feedback = reclaimedMb > 0 ? $"-{reclaimedMb} MB" : "Đã tối ưu";
            RamOptimizedFeedbackText = feedback;
            OnPropertyChanged(nameof(RamOptimizedButtonText));
            OnPropertyChanged(nameof(RamOptimizedButtonBrush));

            ShowHud(new HudPayload
            {
                Kind = HudKind.MemoryOptimized,
                Icon = IconsaxKind.Broom,
                Title = "Đã dọn dẹp RAM",
                ValueText = feedback,
                AccentHex = "#38BDF8"
            });

            _ramFeedbackTimer?.Stop();
            _ramFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _ramFeedbackTimer.Tick += (_, _) =>
            {
                _ramFeedbackTimer.Stop();
                RamOptimizedFeedbackText = string.Empty;
                OnPropertyChanged(nameof(RamOptimizedButtonText));
                OnPropertyChanged(nameof(RamOptimizedButtonBrush));
            };
            _ramFeedbackTimer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RamOptimizer] Failed: {ex.Message}");
        }
        finally
        {
            IsRamOptimizing = false;
            OnPropertyChanged(nameof(RamOptimizedButtonText));
            OnPropertyChanged(nameof(RamOptimizedButtonBrush));
        }
    }

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

    public void TriggerPreviewNotificationHud(bool? overrideHidePreview = null) =>
        _notificationService.TriggerPreviewNotification(overrideHidePreview);

    public void TriggerPreviewAgentHud() =>
        _agentMonitorService.TriggerPreviewAgentHud();

    [RelayCommand]
    private void FocusPrimaryAgent()
    {
        if (!IsPinned)
            _stateMachine.CollapseImmediately();
        _agentMonitorService.FocusAgentHostWindow(PrimaryAgent?.SessionId);
    }

    [RelayCommand]
    private void FocusAgentSession(string? sessionId)
    {
        if (!IsPinned)
            _stateMachine.CollapseImmediately();
        _agentMonitorService.FocusAgentHostWindow(sessionId);
    }

    [RelayCommand]
    private void CopyAgentHook()
    {
        if (_agentMonitorService.CopyHooksConfigToClipboard())
        {
            AgentHookButtonText = "Đã chép!";
            _hookCopiedFeedbackTimer.Stop();
            _hookCopiedFeedbackTimer.Start();
        }
    }

    [RelayCommand]
    private void ClearCompletedAgents() => _agentMonitorService.ClearCompletedSessions();

    [RelayCommand]
    private void SelectAllAgentsFilter() => ShowOnlyActiveAgents = false;

    [RelayCommand]
    private void SelectActiveAgentsFilter() => ShowOnlyActiveAgents = true;

    [RelayCommand]
    private void ToggleAgentGroupExpand(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return;

        if (!_collapsedAgentGroupIds.Add(groupId))
        {
            _collapsedAgentGroupIds.Remove(groupId);
        }

        RebuildFilteredAgentSessions();
    }

    partial void OnShowOnlyActiveAgentsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAllAgentsFilterSelected));
        OnPropertyChanged(nameof(IsActiveAgentsFilterSelected));
        RebuildFilteredAgentSessions();
    }

    partial void OnHasAnyAgentsChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyFilteredAgentsHint));
    partial void OnHasFilteredAgentSessionsChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyFilteredAgentsHint));

    [RelayCommand]
    private void ActivateNotificationHud()
    {
        if (ActiveHud.Kind == HudKind.AgentAlert)
        {
            HudPayload agentTarget = ActiveHud;
            _stateMachine.DismissActiveHud();
            _agentMonitorService.FocusAgentHostWindow(agentTarget.SourceAumid);
            return;
        }

        if (ActiveHud.Kind != HudKind.Notification)
            return;

        HudPayload target = ActiveHud;
        _stateMachine.DismissActiveHud();
        _notificationService.ActivateNotification(target);
    }

    [RelayCommand]
    private void DismissNotificationHud()
    {
        _stateMachine.DismissActiveHud();
    }

    [RelayCommand]
    private void StartColorPicker()
    {
        _colorPickerService.StartColorPicker();
    }

    public void EmitCapsLockHud(bool isOn)
    {
        ShowHud(new HudPayload
        {
            Kind = HudKind.CapsLock,
            Icon = IconsaxKind.Lock,
            Title = "Caps Lock",
            ValueText = isOn ? "Đang bật" : "Đã tắt",
            NormalizedValue = isOn ? 1.0 : 0.0,
            IsInteractiveSlider = false,
            AccentHex = isOn ? "#30D158" : "#8E98AE"
        });
    }

    private void ShowHud(HudPayload payload, bool forcePreemptExpanded = false)
    {
        HudShapeKind shape = payload.Kind switch
        {
            HudKind.FaceIdUnlock => HudShapeKind.FaceIdSquare,
            HudKind.Notification or HudKind.AgentAlert => HudShapeKind.NotificationBanner,
            _ => HudShapeKind.CompactPill
        };

        double durationSec = payload.Kind switch
        {
            HudKind.FaceIdUnlock => 2.18,
            HudKind.Notification => 4.35,
            HudKind.AgentAlert => 4.80,
            HudKind.ColorPicked => 2.1,
            HudKind.CapsLock => 1.35,
            _ => 2.0
        };

        _suppressSliderPush = true;
        try
        {
            ActiveHud = payload;
            HudSliderPercent = Math.Round(payload.NormalizedValue * 100.0);
            HudAccentBrush = ParseBrush(payload.AccentHex);
            HudArrivalPulseToken++;

            if (payload.Kind is HudKind.Notification or HudKind.AgentAlert)
            {
                NotificationPulseToken++;
            }
        }
        finally
        {
            _suppressSliderPush = false;
        }

        _stateMachine.TriggerHudPop(
            shapeKind: shape,
            durationSeconds: durationSec,
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
            OutputDeviceIcon = AudioService.IsHeadphoneDevice(OutputDeviceName) ? IconsaxKind.Headphone : IconsaxKind.VolumeHigh;
            IsMicMuted = _audioService.IsMicMuted;
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
            IsCaffeineActive = _displayPowerService.IsCaffeineActive;
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
        BatteryStatusDetail = _displayPowerService.BatteryStatusDetail;

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

    private void OnAgentSnapshotUpdated(AgentDashboardSnapshot snap)
    {
        AgentSessions.Clear();
        foreach (AgentSessionItem item in snap.Sessions)
        {
            AgentSessions.Add(item);
        }

        PrimaryAgent = snap.PrimarySession;
        HasAnyAgents = snap.HasAnySessions;
        HasActiveOrWaitingAgents = snap.HasActiveOrWaiting;
        IsAgentWaitingApproval = snap.IsCollapsedWaiting;
        ActiveAndWaitingAgentCount = snap.ActiveCount + snap.WaitingApprovalCount;
        CollapsedAgentText = FormatCollapsedAgentText(snap);
        CollapsedAgentIcon = snap.PrimarySession?.Provider == AgentProvider.Teamwork || snap.IsCollapsedWaiting
            ? IconsaxKind.Hierarchy
            : IconsaxKind.Bot;

        AgentStreamCountBadge = $"{snap.Sessions.Count} phiên";

        var parts = new List<string>(3);
        if (snap.WaitingApprovalCount > 0) parts.Add($"{snap.WaitingApprovalCount} chờ duyệt");
        if (snap.ActiveCount > 0) parts.Add($"{snap.ActiveCount} đang chạy");
        if (snap.CompletedRecentCount > 0) parts.Add($"{snap.CompletedRecentCount} hoàn tất");
        AgentSwarmSummaryText = parts.Count > 0
            ? string.Join(" • ", parts)
            : "Tự động theo dõi ~0% CPU • 100% Offline";

        OnPropertyChanged(nameof(AgentsTabHeader));
        OnPropertyChanged(nameof(CollapsedAgentAccentBrush));
        OnPropertyChanged(nameof(CollapsedAgentPillBgBrush));
        RebuildFilteredAgentSessions();
        RefreshBackgroundActivityState();
    }

    public void RebuildFilteredAgentSessions()
    {
        FilteredAgentSessions.Clear();

        if (AgentSessions.Count > 0)
        {
            var groups = new List<(string GroupId, AgentSessionItem? Parent, List<AgentSessionItem> Children)>();
            var groupIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (AgentSessionItem item in AgentSessions)
            {
                string gid = !string.IsNullOrWhiteSpace(item.GroupId)
                    ? item.GroupId
                    : (!string.IsNullOrWhiteSpace(item.ParentSessionId) ? item.ParentSessionId : item.SessionId);

                if (!groupIndexById.TryGetValue(gid, out int idx))
                {
                    idx = groups.Count;
                    groupIndexById[gid] = idx;
                    groups.Add((gid, null, []));
                }

                var current = groups[idx];
                if (!item.IsSubagent && current.Parent == null)
                {
                    groups[idx] = (gid, item, current.Children);
                }
                else
                {
                    current.Children.Add(item);
                }
            }

            foreach (var (gid, parent, children) in groups)
            {
                List<AgentSessionItem> visibleChildren = ShowOnlyActiveAgents
                    ? children.Where(c => c.IsActive).ToList()
                    : children;

                if (parent != null)
                {
                    if (ShowOnlyActiveAgents && !parent.IsActive && visibleChildren.Count == 0)
                        continue;

                    bool isExpanded = !_collapsedAgentGroupIds.Contains(gid);
                    FilteredAgentSessions.Add(parent.WithTreePresentation(isExpanded, visibleChildren.Count, gid));

                    if (isExpanded)
                    {
                        foreach (AgentSessionItem child in visibleChildren)
                        {
                            FilteredAgentSessions.Add(child.WithTreePresentation(true, 0, gid));
                        }
                    }
                }
                else
                {
                    foreach (AgentSessionItem child in visibleChildren)
                    {
                        FilteredAgentSessions.Add(child.WithTreePresentation(true, 0, gid));
                    }
                }
            }
        }

        HasFilteredAgentSessions = FilteredAgentSessions.Count > 0;

        double baseHeight = ActiveExpandedHeight;
        if (!HasAnyAgents || FilteredAgentSessions.Count == 0)
        {
            DesiredAgentsHeight = baseHeight;
        }
        else
        {
            double rowsHeight = 0.0;
            foreach (AgentSessionItem row in FilteredAgentSessions)
            {
                rowsHeight += row.IsSubagent ? 36.0 : 44.0;
            }

            DesiredAgentsHeight = Math.Max(baseHeight, Math.Ceiling(124.0 + rowsHeight));
        }
    }

    private static string FormatCollapsedAgentText(AgentDashboardSnapshot snap)
    {
        if (!snap.HasActiveOrWaiting) return string.Empty;

        string projectName = ExtractProjectName(snap.PrimarySession);

        if (snap.IsCollapsedWaiting)
        {
            return $"{projectName} · Chờ duyệt!";
        }

        if (!string.IsNullOrWhiteSpace(snap.CollapsedBadgeText))
        {
            int dotIdx = snap.CollapsedBadgeText.IndexOf("·", StringComparison.Ordinal);
            if (dotIdx >= 0)
            {
                string suffix = snap.CollapsedBadgeText.Substring(dotIdx).Trim();
                return $"{projectName} {suffix}";
            }
            return $"{projectName} · {snap.CollapsedBadgeText.Trim()}";
        }

        return $"{projectName} · Đang chạy";
    }

    private static string ExtractProjectName(AgentSessionItem? session)
    {
        if (session == null) return "WinNotch";
        if (!string.IsNullOrWhiteSpace(session.ProjectName))
            return session.ProjectName;
        if (!string.IsNullOrWhiteSpace(session.WorkspaceHint))
        {
            try
            {
                string trimmed = session.WorkspaceHint.TrimEnd('\\', '/');
                string? name = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
        }
        return !string.IsNullOrWhiteSpace(session.DisplayName) &&
               !session.DisplayName.StartsWith("Phiên ", StringComparison.Ordinal) &&
               session.DisplayName != "AI Agent"
            ? session.DisplayName
            : "WinNotch";
    }

    private bool ComputeHasBackgroundActivity() =>
        IsMediaPlaying || HasActiveTimer || IsTimerAlarmRinging || HasActiveOrWaitingAgents || IsCameraActive || IsMicrophoneActive;

    private void RefreshBackgroundActivityState()
    {
        bool hasActivity = ComputeHasBackgroundActivity();
        CollapsedContentKind kind = (IsMediaPlaying, ShowCollapsedTimerPill, HasActiveOrWaitingAgents) switch
        {
            (true, true, _) => CollapsedContentKind.MusicAndTimer,
            (true, false, _) => CollapsedContentKind.MusicOnly,
            (false, true, _) => CollapsedContentKind.TimerOnly,
            (false, false, true) => CollapsedContentKind.AgentOnly,
            _ => CollapsedContentKind.Idle
        };
        int privacyDots = (IsCameraActive ? 1 : 0) +
                          (IsMicrophoneActive ? 1 : 0) +
                          (IsMicMuted ? 1 : 0) +
                          (ShowTrailingAgentIndicator ? 1 : 0);

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
        OnPropertyChanged(nameof(IsCollapsedAgentOnlyMode));
        OnPropertyChanged(nameof(ShowTrailingAgentIndicator));
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
