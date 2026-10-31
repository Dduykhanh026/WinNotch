using System.IO;
using System.Windows;
using WinNotch.Engine;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotchStateMachine _stateMachine;
    private readonly DisplayPowerService? _displayPowerService;
    private readonly AudioService? _audioService;
    private readonly ColorPickerService? _colorPickerService;
    private readonly Action _openSettingsAction;
    private readonly Action _exitAppAction;

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _appIcon;
    private System.Windows.Forms.ToolStripMenuItem? _caffeineItem;
    private System.Windows.Forms.ToolStripMenuItem? _micMuteItem;

    public TrayIconService(
        NotchStateMachine stateMachine,
        Action openSettingsAction,
        Action exitAppAction)
        : this(stateMachine, null, null, null, openSettingsAction, exitAppAction)
    {
    }

    public TrayIconService(
        NotchStateMachine stateMachine,
        DisplayPowerService? displayPowerService,
        AudioService? audioService,
        ColorPickerService? colorPickerService,
        Action openSettingsAction,
        Action exitAppAction)
    {
        _stateMachine = stateMachine;
        _displayPowerService = displayPowerService;
        _audioService = audioService;
        _colorPickerService = colorPickerService;
        _openSettingsAction = openSettingsAction;
        _exitAppAction = exitAppAction;

        if (_displayPowerService != null)
        {
            _displayPowerService.StateChanged += OnDisplayPowerStateChanged;
        }
        if (_audioService != null)
        {
            _audioService.StateChanged += OnAudioStateChanged;
        }
    }

    public void Start()
    {
        _appIcon = LoadEmbeddedAppIcon();

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            BackColor = System.Drawing.Color.FromArgb(20, 23, 32),
            ForeColor = System.Drawing.Color.FromArgb(240, 242, 248),
            ShowImageMargin = false
        };

        var toggleItem = new System.Windows.Forms.ToolStripMenuItem("Mở / Thu gọn WinNotch (Alt + N)");
        toggleItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => _stateMachine.ToggleExpandViaHotkey());

        var agentsItem = new System.Windows.Forms.ToolStripMenuItem("Mở Giám sát AI Agents");
        agentsItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => _stateMachine.ExpandAndPin(NotchTab.Agents));

        var systemItem = new System.Windows.Forms.ToolStripMenuItem("Mở Giám sát hệ thống & Mạng");
        systemItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => _stateMachine.ExpandAndPin(NotchTab.System));

        var shelfItem = new System.Windows.Forms.ToolStripMenuItem("Mở Khay giữ file");
        shelfItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => _stateMachine.ExpandAndPin(NotchTab.FileShelf));

        var hideItem = new System.Windows.Forms.ToolStripMenuItem("Tạm ẩn WinNotch")
        {
            CheckOnClick = true
        };
        hideItem.CheckedChanged += (_, _) =>
        {
            bool hide = hideItem.Checked;
            Application.Current?.Dispatcher.BeginInvoke(() => _stateMachine.IsManuallyHidden = hide);
        };

        // Quick Tools & Toggles
        var caffeineItem = new System.Windows.Forms.ToolStripMenuItem("Giữ sáng màn hình (Caffeine)")
        {
            CheckOnClick = false
        };
        caffeineItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _displayPowerService?.ToggleCaffeine(showHud: true);
            if (_displayPowerService != null)
            {
                caffeineItem.Checked = _displayPowerService.IsCaffeineActive;
            }
        });
        _caffeineItem = caffeineItem;

        var micMuteItem = new System.Windows.Forms.ToolStripMenuItem("Khóa Micro toàn hệ thống (Alt + M)")
        {
            CheckOnClick = false
        };
        micMuteItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _audioService?.ToggleMicrophoneMute(showHud: true);
            if (_audioService != null)
            {
                micMuteItem.Checked = _audioService.IsMicMuted;
            }
        });
        _micMuteItem = micMuteItem;

        var colorPickerItem = new System.Windows.Forms.ToolStripMenuItem("Chấm mã màu màn hình (Alt + C)");
        colorPickerItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _colorPickerService?.StartColorPicker();
        });

        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Cài đặt WinNotch...");
        settingsItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(_openSettingsAction);

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Thoát WinNotch");
        exitItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(_exitAppAction);

        // Pre-display synchronization
        menu.Opening += (_, _) =>
        {
            if (_displayPowerService != null && _caffeineItem != null)
            {
                _caffeineItem.Checked = _displayPowerService.IsCaffeineActive;
            }
            if (_audioService != null && _micMuteItem != null)
            {
                _micMuteItem.Checked = _audioService.IsMicMuted;
            }
        };

        menu.Items.Add(toggleItem);
        menu.Items.Add(agentsItem);
        menu.Items.Add(systemItem);
        menu.Items.Add(shelfItem);
        menu.Items.Add(hideItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(caffeineItem);
        menu.Items.Add(micMuteItem);
        menu.Items.Add(colorPickerItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _appIcon ?? System.Drawing.SystemIcons.Application,
            Text = "WinNotch — Dynamic Notch (Alt + N)",
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(() => _stateMachine.ToggleExpandViaHotkey());
    }

    private void OnDisplayPowerStateChanged()
    {
        if (_displayPowerService == null || _caffeineItem == null) return;
        bool active = _displayPowerService.IsCaffeineActive;
        SyncMenuItemChecked(_caffeineItem, active);
    }

    private void OnAudioStateChanged()
    {
        if (_audioService == null || _micMuteItem == null) return;
        bool muted = _audioService.IsMicMuted;
        SyncMenuItemChecked(_micMuteItem, muted);
    }

    private static void SyncMenuItemChecked(System.Windows.Forms.ToolStripMenuItem item, bool isChecked)
    {
        if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => item.Checked = isChecked);
        }
        else
        {
            item.Checked = isChecked;
        }
    }

    private static System.Drawing.Icon? LoadEmbeddedAppIcon()
    {
        try
        {
            var resInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resInfo?.Stream != null)
            {
                using Stream s = resInfo.Stream;
                return new System.Drawing.Icon(s);
            }
        }
        catch { }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
        }
        catch { }

        return null;
    }

    public void Dispose()
    {
        if (_displayPowerService != null)
        {
            _displayPowerService.StateChanged -= OnDisplayPowerStateChanged;
        }
        if (_audioService != null)
        {
            _audioService.StateChanged -= OnAudioStateChanged;
        }

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _appIcon?.Dispose();
    }
}
