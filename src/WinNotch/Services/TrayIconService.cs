using System.IO;
using System.Windows;
using WinNotch.Engine;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotchStateMachine _stateMachine;
    private readonly Action _openSettingsAction;
    private readonly Action _exitAppAction;

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Drawing.Icon? _appIcon;

    public TrayIconService(
        NotchStateMachine stateMachine,
        Action openSettingsAction,
        Action exitAppAction)
    {
        _stateMachine = stateMachine;
        _openSettingsAction = openSettingsAction;
        _exitAppAction = exitAppAction;
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

        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Cài đặt WinNotch...");
        settingsItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(_openSettingsAction);

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Thoát WinNotch");
        exitItem.Click += (_, _) => Application.Current?.Dispatcher.BeginInvoke(_exitAppAction);

        menu.Items.Add(toggleItem);
        menu.Items.Add(systemItem);
        menu.Items.Add(shelfItem);
        menu.Items.Add(hideItem);
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
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _appIcon?.Dispose();
    }
}
