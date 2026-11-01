using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;

namespace WinNotch.Views;

public partial class NotchWindow : Window
{
    private struct ScalarSpring
    {
        public double Value;
        public double Velocity;
        public double Target;

        public void Snap(double v)
        {
            Value = v;
            Target = v;
            Velocity = 0.0;
        }

        public bool Step(double dt, double stiffness, double damping)
        {
            double disp = Value - Target;
            double accel = (-stiffness * disp) - (damping * Velocity);
            Velocity += accel * dt;
            Value += Velocity * dt;

            if (Math.Abs(Value - Target) < 0.03 && Math.Abs(Velocity) < 0.12)
            {
                Value = Target;
                Velocity = 0.0;
                return false;
            }
            return true;
        }
    }

    private readonly SettingsService _settingsService;
    private readonly NotchStateMachine _stateMachine;
    private readonly SpringAnimator _springAnimator;
    private readonly MainNotchViewModel _viewModel;
    private readonly NotificationService _notificationService;
    private readonly Action? _onSessionUnlockedRefreshAction;
    private readonly DispatcherTimer _unlockSettleTimer;

    private IntPtr _hwnd;
    private MonitorDescriptor _currentMonitor;
    private long _lastUnlockTimestamp;
    private bool _wtsRegistered;
    private bool _shellHookRegistered;
    private int _shellHookMsgId;

    private const int TabCount = 4;

    // 60-240Hz Spring Tab Switcher & Sliding Segmented Pill State
    private int _activeTabIndex;
    private bool _hasInitialPillSync;
    private bool _isTabAnimRunning;
    private long _lastTabAnimTimestamp;
    private ScalarSpring _pillXSpring;
    private ScalarSpring _pillWSpring = new() { Value = 96.0, Target = 96.0 };
    private readonly ScalarSpring[] _tabXSprings = new ScalarSpring[TabCount];
    private readonly double[] _tabAlphas = [1.0, 0.0, 0.0, 0.0];
    private readonly double[] _tabAlphaTargets = [1.0, 0.0, 0.0, 0.0];

    private enum CollapsedDisplayMode { Idle, Timer, Agent, Music, Dual }
    private CollapsedDisplayMode _lastCollapsedDisplayMode = CollapsedDisplayMode.Idle;

    public IntPtr WindowHandle => _hwnd;

    public NotchWindow(
        SettingsService settingsService,
        NotchStateMachine stateMachine,
        MainNotchViewModel viewModel,
        NotificationService notificationService,
        MonitorDescriptor initialMonitor,
        Action? onSessionUnlockedRefreshAction = null)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _stateMachine = stateMachine;
        _viewModel = viewModel;
        _notificationService = notificationService;
        _currentMonitor = initialMonitor;
        _onSessionUnlockedRefreshAction = onSessionUnlockedRefreshAction;

        _unlockSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        _unlockSettleTimer.Tick += (_, _) =>
        {
            _unlockSettleTimer.Stop();
            MonitorDescriptor refreshed = WindowStyles.ResolveTargetMonitor(_settingsService.Current.TargetMonitorDeviceName);
            RepositionToMonitor(refreshed);
            _onSessionUnlockedRefreshAction?.Invoke();

            if (_settingsService.Current.EnableUnlockFaceIdHud)
            {
                _viewModel.TriggerFaceIdUnlockHud();
            }
        };

        DataContext = _viewModel;
        _activeTabIndex = Math.Clamp((int)_viewModel.SelectedTab, 0, TabCount - 1);
        SnapTabsToActiveIndex();

        NotchDimensions initialDims = _stateMachine.CurrentTargetDimensions;
        _springAnimator = new SpringAnimator(initialDims);
        _springAnimator.SetPreset(_settingsService.Current.SpringPreset);
        _springAnimator.FrameUpdated += ApplyRenderFrame;

        ApplyRenderFrame(new NotchRenderFrame(initialDims, 1.0, 0.0, 0.0, 0.0, 0.0));
        UpdateHitTestTargets(_stateMachine.State);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _stateMachine.StateChanged += OnNotchStateChanged;
        _stateMachine.HoverBreatheNudged += (nudgeDims, shrinking) => _springAnimator.NudgeDimensions(nudgeDims, shrinking);
        _settingsService.SettingsChanged += OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;

        SourceInitialized += OnSourceInitialized;
        Closed += OnWindowClosed;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Loaded += (_, _) => UpdateAuraPresentedState();
        IsVisibleChanged += (_, _) => UpdateAuraPresentedState();
        LocationChanged += (_, _) => UpdateAuraPresentedState();

        _lastCollapsedDisplayMode = DetermineCurrentCollapsedMode();
        SyncAgentsDynamicHeight();
    }

    public void RepositionToMonitor(MonitorDescriptor monitor)
    {
        _currentMonitor = monitor;
        if (_hwnd == IntPtr.Zero) return;

        double scaleX = monitor.DpiScaleX > 0 ? monitor.DpiScaleX : 1.0;
        double scaleY = monitor.DpiScaleY > 0 ? monitor.DpiScaleY : 1.0;

        double dipHeight = monitor.PhysicalBounds.Height > 0 ? monitor.PhysicalBounds.Height / scaleY : 1080.0;
        double canvasHeight = ConcaveNotchPath.CalculateHostCanvasHeight(dipHeight);

        Width = ConcaveNotchPath.HostCanvasWidth;
        Height = canvasHeight;
        RootHost.Width = ConcaveNotchPath.HostCanvasWidth;
        RootHost.Height = canvasHeight;

        int physW = (int)Math.Round(ConcaveNotchPath.HostCanvasWidth * scaleX);
        int physH = (int)Math.Round(canvasHeight * scaleY);
        int physX = monitor.PhysicalBounds.Left + ((monitor.PhysicalBounds.Width - physW) / 2);
        int physY = monitor.PhysicalBounds.Top;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            physX,
            physY,
            physW,
            physH,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        SyncAgentsDynamicHeight();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;

        WindowStyles.ApplyNotchOverlayStyles(_hwnd);

        HwndSource? source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        try
        {
            _wtsRegistered = NativeMethods.WTSRegisterSessionNotification(_hwnd, NativeMethods.NOTIFY_FOR_THIS_SESSION);
        }
        catch { }

        try
        {
            uint msgId = NativeMethods.RegisterWindowMessageW("SHELLHOOK");
            if (msgId != 0)
            {
                _shellHookMsgId = (int)msgId;
                _shellHookRegistered = NativeMethods.RegisterShellHookWindow(_hwnd);
            }
        }
        catch { }

        RepositionToMonitor(_currentMonitor);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }

        if (_shellHookMsgId != 0 && msg == _shellHookMsgId)
        {
            int code = wParam.ToInt32() & 0xFFFF;
            if (code == NativeMethods.HSHELL_FLASH && lParam != IntPtr.Zero)
            {
                _notificationService.NotifyWindowFlashed(lParam);
            }
        }

        if (msg == NativeMethods.WM_WTSSESSION_CHANGE)
        {
            int reason = wParam.ToInt32();
            if (reason == NativeMethods.WTS_SESSION_LOCK)
            {
                _unlockSettleTimer.Stop();
                _notificationService.SetSessionLocked(true);
                _stateMachine.CollapseImmediately();
            }
            else if (reason == NativeMethods.WTS_SESSION_UNLOCK)
            {
                HandleSessionUnlocked();
            }
        }

        if (msg is NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(() =>
            {
                MonitorDescriptor refreshed = WindowStyles.ResolveTargetMonitor(_settingsService.Current.TargetMonitorDeviceName);
                RepositionToMonitor(refreshed);
            });
        }

        return IntPtr.Zero;
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _unlockSettleTimer.Stop();
                _notificationService.SetSessionLocked(true);
                _stateMachine.CollapseImmediately();
            });
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            Dispatcher.BeginInvoke(HandleSessionUnlocked);
        }
    }

    private void HandleSessionUnlocked()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastUnlockTimestamp != 0 &&
            ((double)(now - _lastUnlockTimestamp) / Stopwatch.Frequency) < 2.0)
        {
            return;
        }

        _lastUnlockTimestamp = now;
        _notificationService.SetSessionLocked(false);
        _unlockSettleTimer.Stop();
        _unlockSettleTimer.Start();
    }

    private void OnNotchStateChanged(NotchVisualState state, NotchDimensions targetDims, bool isCollapsing)
    {
        bool isGhost = state == NotchVisualState.GhostPassthrough;
        bool isHidden = state == NotchVisualState.Hidden;

        WindowStyles.SetClickThrough(_hwnd, isGhost || isHidden);
        RootHost.Opacity = isGhost ? 0.20 : (isHidden ? 0.0 : 1.0);

        UpdateHitTestTargets(state);
        _springAnimator.AnimateTo(state, targetDims, isCollapsing);

        if (state == NotchVisualState.Expanded)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (!_isTabAnimRunning)
                {
                    UpdatePillTargetForIndex(_activeTabIndex, snap: true);
                }
            });
        }
    }

    private void UpdateHitTestTargets(NotchVisualState activeState)
    {
        CollapsedLayer.IsHitTestVisible = activeState is NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive;
        HudLayer.IsHitTestVisible = activeState == NotchVisualState.HudPop;
        DragReadyLayer.IsHitTestVisible = activeState == NotchVisualState.DragReady;
        ExpandedLayer.IsHitTestVisible = activeState == NotchVisualState.Expanded;
    }

    private void ApplyRenderFrame(NotchRenderFrame frame)
    {
        NotchDimensions dims = frame.Dimensions;
        StreamGeometry fillGeom = ConcaveNotchPath.BuildFillGeometry(dims.Width, dims.Height, dims.EarRadius, dims.BottomRadius);
        StreamGeometry rimGeom = ConcaveNotchPath.BuildRimStrokeGeometry(dims.Width, dims.Height, dims.EarRadius, dims.BottomRadius);

        NotchFillPath.Data = fillGeom;
        NotchRimPath.Data = rimGeom;
        NotchAmbientHaloPath.Data = rimGeom;
        NotchContactShadowPath.Data = rimGeom;

        ContentViewport.Width = Math.Max(0.0, dims.Width);
        ContentViewport.Height = Math.Max(0.0, dims.Height);

        // 1. Collapsed Pill continuous dissolve & micro-scale
        ApplyLayerVisual(CollapsedLayer, frame.CollapsedOpacity);
        double colScale = 0.94 + (0.06 * frame.CollapsedOpacity);
        CollapsedScale.ScaleX = colScale;
        CollapsedScale.ScaleY = colScale;

        // 2. HUD Popup dissolve & top-anchored scale
        ApplyLayerVisual(HudLayer, frame.HudOpacity);
        double hudScale = 0.92 + (0.08 * frame.HudOpacity);
        HudScale.ScaleX = hudScale;
        HudScale.ScaleY = hudScale;

        // 3. Drag-Ready Drop Zone dissolve & top-anchored scale
        ApplyLayerVisual(DragReadyLayer, frame.DragOpacity);
        double dragScale = 0.90 + (0.10 * frame.DragOpacity);
        DragScale.ScaleX = dragScale;
        DragScale.ScaleY = dragScale;

        // 4. Expanded Workspace liquid unfold from top bezel (no abrupt Frame-0 disappearance!)
        ApplyLayerVisual(ExpandedLayer, frame.ExpandedOpacity);
        ExpandedLayer.SetCurrentValue(FrameworkElement.WidthProperty, _stateMachine.ActiveExpandedDimensions.Width);
        ExpandedLayer.SetCurrentValue(FrameworkElement.HeightProperty, Math.Max(_stateMachine.ActiveExpandedDimensions.Height, dims.Height));
        double clampedProg = Math.Clamp(frame.ExpandedProgress, 0.0, 1.03);
        double expScale = 0.88 + (0.12 * clampedProg);
        ExpandedScale.ScaleX = expScale;
        ExpandedScale.ScaleY = expScale;
        ExpandedTranslate.Y = -10.0 * (1.0 - Math.Min(1.0, clampedProg));
    }

    private static void ApplyLayerVisual(UIElement element, double opacity)
    {
        if (opacity <= 0.006)
        {
            if (element.Visibility != Visibility.Collapsed)
            {
                element.Visibility = Visibility.Collapsed;
            }
            element.Opacity = 0.0;
        }
        else
        {
            if (element.Visibility != Visibility.Visible)
            {
                element.Visibility = Visibility.Visible;
            }
            element.Opacity = Math.Clamp(opacity, 0.0, 1.0);
        }
    }

    private void OnNotchSurfaceMouseEnter(object sender, MouseEventArgs e)
    {
        _stateMachine.NotifyPointerEntered();
    }

    private void OnNotchSurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        if (!NotchFillPath.IsMouseOver && !ContentViewport.IsMouseOver)
        {
            _stateMachine.NotifyPointerLeft();
        }
    }

    private void OnNotchSurfaceMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_stateMachine.State is not (NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive))
        {
            return;
        }

        if (!_settingsService.Current.EnableCollapsedScrollGestures)
        {
            return;
        }

        _stateMachine.SuppressPendingHoverExpand();

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _viewModel.StepBrightness(e.Delta > 0 ? 4 : -4);
        }
        else
        {
            _viewModel.StepVolume(e.Delta > 0 ? 2 : -2);
        }

        e.Handled = true;
    }

    private void OnNotchSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_stateMachine.State is not (NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive))
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            _stateMachine.SuppressPendingHoverExpand();

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                _viewModel.NextTrackCommand.Execute(null);
            }
            else
            {
                _viewModel.TogglePlayPauseCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_viewModel.IsFileShelfTabSelected && _viewModel.IsExpandedVisible)
            {
                _viewModel.PasteShelfFromClipboardCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        if (_viewModel.FileShelf.CanAcceptDataObject(e.Data))
        {
            e.Effects = DragDropEffects.Copy;
            _stateMachine.SetDragZoneActive(true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _viewModel.FileShelf.CanAcceptDataObject(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _stateMachine.SetDragZoneActive(false);
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        int count = _viewModel.FileShelf.AcceptDrop(e.Data);
        if (count > 0)
        {
            _stateMachine.CompleteDropToFileShelf();
        }
        else
        {
            _stateMachine.SetDragZoneActive(false);
        }
        e.Handled = true;
    }

    private void OnSettingsChanged(AppSettings updated)
    {
        _springAnimator.SetPreset(updated.SpringPreset);
        _stateMachine.ApplySettings(updated);
        SyncAgentsDynamicHeight();
    }

    private void OnSystemDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MonitorDescriptor refreshed = WindowStyles.ResolveTargetMonitor(_settingsService.Current.TargetMonitorDeviceName);
            RepositionToMonitor(refreshed);
        });
    }

    private void OnTabButtonsPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool shouldSnap = !_hasInitialPillSync ||
                          _stateMachine.State != NotchVisualState.Expanded ||
                          ExpandedLayer.Opacity <= 0.05;

        if (UpdatePillTargetForIndex(_activeTabIndex, snap: shouldSnap))
        {
            _hasInitialPillSync = true;
            if (!shouldSnap)
            {
                EnsureTabAnimationLoop();
            }
        }
    }

    private void SyncAgentsDynamicHeight()
    {
        double scaleY = _currentMonitor.DpiScaleY > 0 ? _currentMonitor.DpiScaleY : 1.0;
        double monitorDipHeight = _currentMonitor.PhysicalBounds.Height > 0
            ? _currentMonitor.PhysicalBounds.Height / scaleY
            : 1080.0;
        double maxTwoThirds = Math.Floor(monitorDipHeight * (2.0 / 3.0));

        _stateMachine.SetActiveTabAndAgentsHeight(
            _viewModel.SelectedTab,
            _viewModel.DesiredAgentsHeight,
            maxTwoThirds);
    }

    private CollapsedDisplayMode DetermineCurrentCollapsedMode()
    {
        if (_viewModel.IsCollapsedDualMode) return CollapsedDisplayMode.Dual;
        if (_viewModel.IsCollapsedMusicMode) return CollapsedDisplayMode.Music;
        if (_viewModel.IsCollapsedTimerOnlyMode) return CollapsedDisplayMode.Timer;
        if (_viewModel.IsCollapsedAgentOnlyMode) return CollapsedDisplayMode.Agent;
        return CollapsedDisplayMode.Idle;
    }

    private void CheckAndAnimateCollapsedModeChange()
    {
        CollapsedDisplayMode currentMode = DetermineCurrentCollapsedMode();
        if (currentMode == _lastCollapsedDisplayMode)
            return;

        _lastCollapsedDisplayMode = currentMode;
        if (_stateMachine.State is NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive)
        {
            PlayCollapsedModeTransition();
        }
    }

    public static readonly DependencyProperty IsAuraPresentedProperty =
        DependencyProperty.Register(
            nameof(IsAuraPresented),
            typeof(bool),
            typeof(NotchWindow),
            new PropertyMetadata(false));

    public bool IsAuraPresented
    {
        get => (bool)GetValue(IsAuraPresentedProperty);
        set => SetValue(IsAuraPresentedProperty, value);
    }

    private void UpdateAuraPresentedState()
    {
        bool presented = IsLoaded && IsVisible &&
                         PresentationSource.FromVisual(this) != null &&
                         Left >= -1000.0 && Top >= -1000.0;
        if (IsAuraPresented != presented)
        {
            IsAuraPresented = presented;
            if (!presented)
            {
                DetachAgentBadgeAuraClocks();
            }
        }
    }

    private int _collapsedTransitionGeneration;
    private long _lastCollapsedTransitionTimestamp;
    private FrameworkElement? _cachedAgentBadgeAuraBorder;
    private Storyboard? _cachedAgentBadgeAuraStoryboard;

    private FrameworkElement? FindAgentBadgeAuraBorder()
    {
        if (_cachedAgentBadgeAuraBorder != null)
            return _cachedAgentBadgeAuraBorder;

        if (AgentBadgeAuraBorder != null)
        {
            _cachedAgentBadgeAuraBorder = AgentBadgeAuraBorder;
            return _cachedAgentBadgeAuraBorder;
        }

        if (CollapsedContentHost == null || AgentBadgeAuraScale == null)
            return null;

        _cachedAgentBadgeAuraBorder = FindElementWithTransform(CollapsedContentHost, AgentBadgeAuraScale);
        return _cachedAgentBadgeAuraBorder;
    }

    private Storyboard? FindAgentBadgeAuraStoryboard()
    {
        if (_cachedAgentBadgeAuraStoryboard != null)
            return _cachedAgentBadgeAuraStoryboard;

        var auraBorder = FindAgentBadgeAuraBorder();
        if (auraBorder?.Style?.Triggers != null)
        {
            foreach (var trigger in auraBorder.Style.Triggers)
            {
                if (trigger is MultiDataTrigger mdt)
                {
                    foreach (var action in mdt.EnterActions)
                    {
                        if (action is BeginStoryboard bs && bs.Storyboard != null)
                        {
                            _cachedAgentBadgeAuraStoryboard = bs.Storyboard;
                            return _cachedAgentBadgeAuraStoryboard;
                        }
                    }
                }
                else if (trigger is DataTrigger dt)
                {
                    foreach (var action in dt.EnterActions)
                    {
                        if (action is BeginStoryboard bs && bs.Storyboard != null)
                        {
                            _cachedAgentBadgeAuraStoryboard = bs.Storyboard;
                            return _cachedAgentBadgeAuraStoryboard;
                        }
                    }
                }
            }
        }
        return null;
    }

    private static FrameworkElement? FindElementWithTransform(DependencyObject parent, Transform targetTransform)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is FrameworkElement fe)
            {
                if (ReferenceEquals(fe.RenderTransform, targetTransform))
                    return fe;
                FrameworkElement? found = FindElementWithTransform(fe, targetTransform);
                if (found != null) return found;
            }
        }
        if (parent is Visual visualParent)
        {
            int count = VisualTreeHelper.GetChildrenCount(visualParent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(visualParent, i);
                if (child is FrameworkElement fe)
                {
                    if (ReferenceEquals(fe.RenderTransform, targetTransform))
                        return fe;
                    FrameworkElement? found = FindElementWithTransform(fe, targetTransform);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }

    private void DetachAgentBadgeAuraClocks()
    {
        var auraBorder = FindAgentBadgeAuraBorder();
        if (auraBorder != null)
        {
            var sb = FindAgentBadgeAuraStoryboard();
            if (sb != null)
            {
                try
                {
                    sb.Stop(auraBorder);
                    sb.Remove(auraBorder);
                }
                catch { }
            }

            auraBorder.BeginAnimation(UIElement.OpacityProperty, null);
            auraBorder.Opacity = 0.0;
        }

        AgentBadgeAuraScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AgentBadgeAuraScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (AgentBadgeAuraScale != null)
        {
            AgentBadgeAuraScale.ScaleX = 1.0;
            AgentBadgeAuraScale.ScaleY = 1.0;
        }
    }

    private void PlayCollapsedModeTransition()
    {
        if (CollapsedContentHost == null || CollapsedContentTranslate == null || CollapsedContentScale == null)
            return;

        // Synchronously detach in-flight animation clocks and reset transforms before starting new transition
        CollapsedContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CollapsedContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CollapsedContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CollapsedContentHost.BeginAnimation(OpacityProperty, null);

        CollapsedContentTranslate.Y = 0.0;
        CollapsedContentScale.ScaleX = 1.0;
        CollapsedContentScale.ScaleY = 1.0;
        CollapsedContentHost.Opacity = 1.0;

        // High-Frequency Sequential Transition Guard:
        // Evaluated BEFORE presentation guard so timestamp is always recorded and rapid bursts
        // are debounced across all execution contexts (headless, offscreen, and onscreen).
        long now = Stopwatch.GetTimestamp();
        double elapsedMs = _lastCollapsedTransitionTimestamp == 0
            ? double.MaxValue
            : (double)(now - _lastCollapsedTransitionTimestamp) * 1000.0 / Stopwatch.Frequency;
        _lastCollapsedTransitionTimestamp = now;

        if (elapsedMs < 150.0)
        {
            DetachAgentBadgeAuraClocks();
            return;
        }

        // Offscreen / headless test harness presentation guard:
        // When the window is not loaded, not visible, lacks an active HwndSource presentation source,
        // or is located offscreen in test harnesses (Left/Top < -1000), starting compositor DoubleAnimations
        // continuously drives DispatcherPriority.Render ticks that starve nested DispatcherPriority.Background pumps.
        // Resting baseline values are already asserted above; return early without launching animations.
        if (!IsLoaded || !IsVisible || PresentationSource.FromVisual(this) == null || Left < -1000.0 || Top < -1000.0)
        {
            DetachAgentBadgeAuraClocks();
            return;
        }

        int generation = ++_collapsedTransitionGeneration;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(210);

        var slideAnim = new DoubleAnimation(5.5, 0.0, duration) { EasingFunction = ease };
        var scaleXAnim = new DoubleAnimation(0.96, 1.0, duration) { EasingFunction = ease };
        var scaleYAnim = new DoubleAnimation(0.96, 1.0, duration) { EasingFunction = ease };
        var fadeAnim = new DoubleAnimation(0.18, 1.0, duration) { EasingFunction = ease };

        slideAnim.Completed += (_, _) =>
        {
            if (_collapsedTransitionGeneration != generation)
                return;

            CollapsedContentTranslate?.BeginAnimation(TranslateTransform.YProperty, null);
            CollapsedContentScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CollapsedContentScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CollapsedContentHost?.BeginAnimation(OpacityProperty, null);

            if (CollapsedContentTranslate != null) CollapsedContentTranslate.Y = 0.0;
            if (CollapsedContentScale != null)
            {
                CollapsedContentScale.ScaleX = 1.0;
                CollapsedContentScale.ScaleY = 1.0;
            }
            if (CollapsedContentHost != null) CollapsedContentHost.Opacity = 1.0;
        };

        CollapsedContentTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        CollapsedContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        CollapsedContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        CollapsedContentHost.BeginAnimation(OpacityProperty, fadeAnim);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainNotchViewModel.SelectedTab) ||
            e.PropertyName == nameof(MainNotchViewModel.DesiredAgentsHeight))
        {
            SyncAgentsDynamicHeight();
        }

        if (e.PropertyName == nameof(MainNotchViewModel.HasActiveOrWaitingAgents))
        {
            UpdateAuraPresentedState();
            if (!_viewModel.HasActiveOrWaitingAgents || !IsAuraPresented)
            {
                DetachAgentBadgeAuraClocks();
            }
        }

        if (e.PropertyName is
            nameof(MainNotchViewModel.IsMediaPlaying) or
            nameof(MainNotchViewModel.HasActiveTimer) or
            nameof(MainNotchViewModel.IsTimerAlarmRinging) or
            nameof(MainNotchViewModel.HasActiveOrWaitingAgents) or
            nameof(MainNotchViewModel.ShowCollapsedNetworkSpeed) or
            nameof(MainNotchViewModel.ShowCollapsedTimerPill) or
            nameof(MainNotchViewModel.IsCollapsedDualMode) or
            nameof(MainNotchViewModel.IsCollapsedMusicMode) or
            nameof(MainNotchViewModel.IsCollapsedTimerOnlyMode) or
            nameof(MainNotchViewModel.IsCollapsedAgentOnlyMode) or
            nameof(MainNotchViewModel.IsCollapsedIdleMode))
        {
            CheckAndAnimateCollapsedModeChange();
        }

        if (e.PropertyName != nameof(MainNotchViewModel.SelectedTab))
            return;

        int nextIdx = Math.Clamp((int)_viewModel.SelectedTab, 0, TabCount - 1);
        if (nextIdx == _activeTabIndex)
            return;

        int direction = nextIdx > _activeTabIndex ? 1 : -1;
        _activeTabIndex = nextIdx;
        ApplyTabHitTestTargets(nextIdx);

        bool shouldAnimate = _stateMachine.State == NotchVisualState.Expanded &&
                             ExpandedLayer.Visibility == Visibility.Visible &&
                             ExpandedLayer.Opacity > 0.05;

        if (!shouldAnimate)
        {
            SnapTabsToActiveIndex();
            return;
        }

        UpdatePillTargetForIndex(nextIdx, snap: false);

        for (int i = 0; i < TabCount; i++)
        {
            Grid page = GetTabPage(i);
            if (i == nextIdx)
            {
                if (page.Visibility != Visibility.Visible)
                {
                    page.Visibility = Visibility.Visible;
                }

                if (_tabAlphas[i] < 0.05)
                {
                    _tabXSprings[i].Value = direction * 20.0;
                    _tabXSprings[i].Velocity = 0.0;
                }

                _tabXSprings[i].Target = 0.0;
                _tabAlphaTargets[i] = 1.0;
            }
            else
            {
                if (_tabAlphas[i] > 0.01)
                {
                    _tabXSprings[i].Target = -direction * 14.0;
                }
                _tabAlphaTargets[i] = 0.0;
            }
        }

        EnsureTabAnimationLoop();
    }

    private void SnapTabsToActiveIndex()
    {
        StopTabAnimationLoop();
        ApplyTabHitTestTargets(_activeTabIndex);
        UpdatePillTargetForIndex(_activeTabIndex, snap: true);

        for (int i = 0; i < TabCount; i++)
        {
            bool active = i == _activeTabIndex;
            _tabAlphas[i] = active ? 1.0 : 0.0;
            _tabAlphaTargets[i] = active ? 1.0 : 0.0;
            _tabXSprings[i].Snap(0.0);

            Grid page = GetTabPage(i);
            ScaleTransform scale = GetTabScale(i);
            TranslateTransform trans = GetTabTranslate(i);

            page.Opacity = _tabAlphas[i];
            page.Visibility = active ? Visibility.Visible : Visibility.Hidden;
            double s = active ? 1.0 : 0.985;
            scale.ScaleX = s;
            scale.ScaleY = s;
            trans.X = 0.0;
        }
    }

    private void ApplyTabHitTestTargets(int activeIdx)
    {
        TabPageAgents.IsHitTestVisible = activeIdx == 0;
        TabPageOverview.IsHitTestVisible = activeIdx == 1;
        TabPageSystem.IsHitTestVisible = activeIdx == 2;
        TabPageShelf.IsHitTestVisible = activeIdx == 3;
    }

    private bool UpdatePillTargetForIndex(int index, bool snap)
    {
        Button btn = GetTabButton(index);
        if (btn.ActualWidth <= 1.0 || TabButtonsPanel.ActualWidth <= 1.0)
            return false;

        double targetX = btn.TranslatePoint(new Point(0, 0), TabButtonsPanel).X;
        double targetW = btn.ActualWidth;

        if (snap)
        {
            _pillXSpring.Snap(targetX);
            _pillWSpring.Snap(targetW);
            ActiveTabPillTranslate.X = targetX;
            ActiveTabPill.Width = targetW;
        }
        else
        {
            _pillXSpring.Target = targetX;
            _pillWSpring.Target = targetW;
        }

        return true;
    }

    private void EnsureTabAnimationLoop()
    {
        if (!_isTabAnimRunning)
        {
            _isTabAnimRunning = true;
            _lastTabAnimTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnTabAnimationRendering;
        }
    }

    private void StopTabAnimationLoop()
    {
        if (_isTabAnimRunning)
        {
            _isTabAnimRunning = false;
            CompositionTarget.Rendering -= OnTabAnimationRendering;
        }
    }

    private void OnTabAnimationRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (double)(now - _lastTabAnimTimestamp) / Stopwatch.Frequency;
        _lastTabAnimTimestamp = now;

        if (dt <= 0.0) return;
        if (dt > 0.033) dt = 0.033;

        const int steps = 4;
        double subDt = dt / steps;
        bool anySpringMoving = false;

        for (int s = 0; s < steps; s++)
        {
            bool px = _pillXSpring.Step(subDt, 510.0, 42.0);
            bool pw = _pillWSpring.Step(subDt, 510.0, 42.0);
            bool t0 = _tabXSprings[0].Step(subDt, 460.0, 41.5);
            bool t1 = _tabXSprings[1].Step(subDt, 460.0, 41.5);
            bool t2 = _tabXSprings[2].Step(subDt, 460.0, 41.5);
            bool t3 = _tabXSprings[3].Step(subDt, 460.0, 41.5);
            anySpringMoving = px || pw || t0 || t1 || t2 || t3;
        }

        ActiveTabPillTranslate.X = _pillXSpring.Value;
        ActiveTabPill.Width = Math.Max(24.0, _pillWSpring.Value);

        bool anyAlphaMoving = false;
        for (int i = 0; i < TabCount; i++)
        {
            double cur = _tabAlphas[i];
            double tgt = _tabAlphaTargets[i];

            if (Math.Abs(cur - tgt) > 0.004)
            {
                double speed = tgt < cur ? 26.0 : 17.0;
                cur += (tgt - cur) * (1.0 - Math.Exp(-speed * dt));
                if (Math.Abs(cur - tgt) <= 0.004)
                {
                    cur = tgt;
                }
                else
                {
                    anyAlphaMoving = true;
                }
                _tabAlphas[i] = cur;
            }
            else
            {
                _tabAlphas[i] = tgt;
            }

            Grid page = GetTabPage(i);
            ScaleTransform scale = GetTabScale(i);
            TranslateTransform trans = GetTabTranslate(i);

            double alpha = _tabAlphas[i];
            if (alpha <= 0.004 && tgt == 0.0)
            {
                page.Opacity = 0.0;
                if (page.Visibility != Visibility.Hidden)
                {
                    page.Visibility = Visibility.Hidden;
                }
                _tabXSprings[i].Snap(0.0);
                trans.X = 0.0;
            }
            else
            {
                if (page.Visibility != Visibility.Visible)
                {
                    page.Visibility = Visibility.Visible;
                }
                page.Opacity = Math.Clamp(alpha, 0.0, 1.0);
                double sc = 0.985 + (0.015 * alpha);
                scale.ScaleX = sc;
                scale.ScaleY = sc;
                trans.X = _tabXSprings[i].Value;
            }
        }

        if (!anySpringMoving && !anyAlphaMoving)
        {
            StopTabAnimationLoop();
        }
    }

    private Button GetTabButton(int index) => index switch
    {
        1 => TabBtnOverview,
        2 => TabBtnSystem,
        3 => TabBtnShelf,
        _ => TabBtnAgents
    };

    private Grid GetTabPage(int index) => index switch
    {
        1 => TabPageOverview,
        2 => TabPageSystem,
        3 => TabPageShelf,
        _ => TabPageAgents
    };

    private ScaleTransform GetTabScale(int index) => index switch
    {
        1 => TabOverviewScale,
        2 => TabSystemScale,
        3 => TabShelfScale,
        _ => TabAgentsScale
    };

    private TranslateTransform GetTabTranslate(int index) => index switch
    {
        1 => TabOverviewTranslate,
        2 => TabSystemTranslate,
        3 => TabShelfTranslate,
        _ => TabAgentsTranslate
    };

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _unlockSettleTimer.Stop();
        StopTabAnimationLoop();
        CollapsedContentTranslate?.BeginAnimation(TranslateTransform.YProperty, null);
        CollapsedContentScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CollapsedContentScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CollapsedContentHost?.BeginAnimation(OpacityProperty, null);
        DetachAgentBadgeAuraClocks();
        if (_wtsRegistered && _hwnd != IntPtr.Zero)
        {
            try { NativeMethods.WTSUnRegisterSessionNotification(_hwnd); } catch { }
            _wtsRegistered = false;
        }
        if (_shellHookRegistered && _hwnd != IntPtr.Zero)
        {
            try { NativeMethods.DeregisterShellHookWindow(_hwnd); } catch { }
            _shellHookRegistered = false;
        }
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }
}
