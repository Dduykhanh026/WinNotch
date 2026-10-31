using System.Windows;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;
using WinNotch.Views;

namespace WinNotch;

public partial class App : Application
{
    private SettingsService? _settingsService;
    private NotchStateMachine? _stateMachine;
    private InputHookService? _inputHookService;
    private FullscreenService? _fullscreenService;
    private AudioService? _audioService;
    private DisplayPowerService? _displayPowerService;
    private MediaService? _mediaService;
    private FileShelfService? _fileShelfService;
    private TimerService? _timerService;
    private PrivacyService? _privacyService;
    private SystemMonitorService? _systemMonitorService;
    private NotificationService? _notificationService;
    private ColorPickerService? _colorPickerService;
    private AgentMonitorService? _agentMonitorService;
    private TrayIconService? _trayIconService;

    private NotchWindow? _notchWindow;
    private MainNotchViewModel? _mainViewModel;
    private SettingsWindow? _settingsWindow;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWaitRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settingsService = new SettingsService();
        MonitorDescriptor targetMonitor = WindowStyles.ResolveTargetMonitor(_settingsService.Current.TargetMonitorDeviceName);

        _stateMachine = new NotchStateMachine(_settingsService.Current);
        _inputHookService = new InputHookService(_settingsService, targetMonitor);
        _fullscreenService = new FullscreenService(_settingsService, targetMonitor);
        _audioService = new AudioService(_settingsService);
        _displayPowerService = new DisplayPowerService(_settingsService, targetMonitor);
        _mediaService = new MediaService(_settingsService);
        _fileShelfService = new FileShelfService(_settingsService);
        _timerService = new TimerService();
        _privacyService = new PrivacyService(_settingsService);
        _systemMonitorService = new SystemMonitorService(_settingsService);
        _notificationService = new NotificationService(_settingsService);
        _colorPickerService = new ColorPickerService(_stateMachine);
        _agentMonitorService = new AgentMonitorService(_settingsService);

        var mainVm = new MainNotchViewModel(
            _settingsService,
            _stateMachine,
            _audioService,
            _displayPowerService,
            _mediaService,
            _fileShelfService,
            _timerService,
            _privacyService,
            _systemMonitorService,
            _notificationService,
            _colorPickerService,
            _agentMonitorService,
            OpenSettingsWindow);

        _mainViewModel = mainVm;
        _notchWindow = new NotchWindow(
            _settingsService,
            _stateMachine,
            mainVm,
            _notificationService,
            targetMonitor,
            () => _fullscreenService?.RefreshNow());
        _notchWindow.Show();

        // Wire global hooks & detectors
        _inputHookService.ToggleHotkeyPressed += () => _stateMachine.ToggleExpandViaHotkey();
        _inputHookService.MicMuteHotkeyPressed += () => _audioService.ToggleMicrophoneMute(showHud: true);
        _inputHookService.ColorPickerHotkeyPressed += () => _colorPickerService.StartColorPicker();
        _inputHookService.CapsLockToggled += isOn => mainVm.EmitCapsLockHud(isOn);
        _inputHookService.GhostModeChanged += active => _stateMachine.SetGhostPassthrough(active);
        _inputHookService.DragSensorZoneChanged += active => _stateMachine.SetDragZoneActive(active);
        _inputHookService.VolumeKeyIntercepted += (delta, mute) => _audioService.StepVolume(delta, mute);

        _fullscreenService.FullscreenStateChanged += isFs => _stateMachine.SetFullscreenHidden(isFs);

        _settingsService.SettingsChanged += OnSettingsChanged;

        // Start background engines
        _audioService.Start();
        _displayPowerService.Start();
        _mediaService.Start();
        _privacyService.Start();
        _systemMonitorService.Start();
        _notificationService.Start(_notchWindow.WindowHandle);
        _agentMonitorService.Start();
        _inputHookService.Start();
        _fullscreenService.Start(_notchWindow.WindowHandle);

        _trayIconService = new TrayIconService(
            _stateMachine,
            _displayPowerService,
            _audioService,
            _colorPickerService,
            OpenSettingsWindow,
            ShutdownApplication);
        _trayIconService.Start();

        // Listen for second-launch signal to unhide & expand Notch immediately
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateInstanceEventName);
        _activateWaitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (_stateMachine != null)
                {
                    _stateMachine.IsManuallyHidden = false;
                    _stateMachine.ToggleExpandViaHotkey();
                }
            }),
            null,
            -1,
            executeOnlyOnce: false);
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        MonitorDescriptor monitor = WindowStyles.ResolveTargetMonitor(settings.TargetMonitorDeviceName);
        _notchWindow?.RepositionToMonitor(monitor);
        _inputHookService?.UpdateTargetMonitor(monitor);
        _fullscreenService?.UpdateTargetMonitor(monitor);
        _displayPowerService?.UpdateTargetMonitor(monitor);
    }

    private void OpenSettingsWindow()
    {
        if (_settingsService == null) return;

        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settingsService,
            previewFaceIdAction: () =>
            {
                _fullscreenService?.RefreshNow();
                _mainViewModel?.TriggerFaceIdUnlockHud();
            },
            previewNotificationAction: hidePreview =>
            {
                _fullscreenService?.RefreshNow();
                _mainViewModel?.TriggerPreviewNotificationHud(hidePreview);
            },
            previewAgentHudAction: () =>
            {
                _fullscreenService?.RefreshNow();
                _mainViewModel?.TriggerPreviewAgentHud();
            });
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShutdownApplication()
    {
        _activateWaitRegistration?.Unregister(null);
        _activateEvent?.Dispose();
        _trayIconService?.Dispose();
        _inputHookService?.Dispose();
        _fullscreenService?.Dispose();
        _mediaService?.Dispose();
        _audioService?.Dispose();
        _displayPowerService?.Dispose();
        _timerService?.Dispose();
        _privacyService?.Dispose();
        _systemMonitorService?.Dispose();
        _notificationService?.Dispose();
        _colorPickerService?.Dispose();
        _agentMonitorService?.Dispose();

        _settingsWindow?.Close();
        _notchWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
        _inputHookService?.Dispose();
        _fullscreenService?.Dispose();
        _mediaService?.Dispose();
        _audioService?.Dispose();
        _displayPowerService?.Dispose();
        _timerService?.Dispose();
        _privacyService?.Dispose();
        _systemMonitorService?.Dispose();
        _notificationService?.Dispose();
        _colorPickerService?.Dispose();
        _agentMonitorService?.Dispose();
        base.OnExit(e);
    }
}
