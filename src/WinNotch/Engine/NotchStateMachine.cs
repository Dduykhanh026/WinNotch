using System.Diagnostics;
using System.Windows.Threading;
using WinNotch.Models;

namespace WinNotch.Engine;

public sealed class NotchStateMachine
{
    public static readonly NotchDimensions DimCollapsedIdle = new(132, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedIdleNet = new(168, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedTimer = new(106, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedAgent = new(196, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedMusic = new(228, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedDualActive = new(276, 32, 8.0, 12.5);
    public static readonly NotchDimensions DimHudPop = new(376, 48, 9.5, 18.0);
    public static readonly NotchDimensions DimFaceIdHud = new(156, 148, 11.0, 36.0);
    public static readonly NotchDimensions DimNotificationHud = new(424, 80, 10.5, 24.0);
    public static readonly NotchDimensions DimDragReady = new(444, 118, 11.5, 26.0);
    public static readonly NotchDimensions DimExpandedCompact = new(684, 272, 13.5, 30.0);
    public static readonly NotchDimensions DimExpandedSpacious = new(744, 296, 14.0, 31.0);
    public static readonly NotchDimensions DimExpanded = DimExpandedCompact;
    public static readonly NotchDimensions DimHidden = new(110, 0, 0, 0);

    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly DispatcherTimer _leaveCollapseTimer;
    private readonly DispatcherTimer _hudDismissTimer;

    private string _expandedSizePreset = "Compact";
    private NotchTab _activeTab = NotchTab.Overview;
    private double _desiredAgentsHeight;
    private double _maxScreenTwoThirdsDipHeight;
    private bool _hasBackgroundActivity;
    private CollapsedContentKind _collapsedKind = CollapsedContentKind.Idle;
    private int _privacyDotCount;
    private bool _showIdleNetworkSpeed;
    private bool _isFullscreenHidden;
    private bool _isManuallyHidden;
    private bool _isGhostPassthrough;
    private bool _isPinned;
    private bool _isPointerInsideNotch;
    private bool _isDraggingSlider;
    private HudShapeKind _activeHudShape = HudShapeKind.CompactPill;
    private bool _hudTimerExpiredWhileHovered;
    private long _suppressHoverExpandUntilTicks;

    public NotchVisualState State { get; private set; } = NotchVisualState.CollapsedIdle;

    public string ExpandedSizePreset => _expandedSizePreset;
    public NotchDimensions ActiveExpandedDimensions =>
        string.Equals(_expandedSizePreset, "Spacious", StringComparison.OrdinalIgnoreCase)
            ? DimExpandedSpacious
            : DimExpandedCompact;
    public NotchDimensions CurrentExpandedDimensions => ResolveExpandedDimensions();

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PinnedChanged?.Invoke(_isPinned);
            if (!_isPinned && !_isPointerInsideNotch && State == NotchVisualState.Expanded)
            {
                ScheduleCollapse();
            }
        }
    }

    public bool IsManuallyHidden
    {
        get => _isManuallyHidden;
        set
        {
            if (_isManuallyHidden == value) return;
            _isManuallyHidden = value;
            ReevaluateState();
        }
    }

    public event Action<NotchVisualState, NotchDimensions, bool>? StateChanged;
    public event Action<NotchDimensions, bool>? HoverBreatheNudged;
    public event Action<bool>? PinnedChanged;
    public event Action<NotchTab>? RequestTabSwitch;

    public NotchStateMachine(AppSettings settings)
    {
        _expandedSizePreset = string.IsNullOrWhiteSpace(settings.ExpandedSizePreset) ? "Compact" : settings.ExpandedSizePreset;
        _activeTab = settings.DefaultOpenAgentsTab ? NotchTab.Agents : NotchTab.Overview;
        _showIdleNetworkSpeed = settings.ShowCollapsedNetworkSpeed;

        _hoverExpandTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.HoverExpandDelayMs, 50, 800))
        };
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (_isPointerInsideNotch && !_isGhostPassthrough && !_isFullscreenHidden && !_isManuallyHidden)
            {
                TransitionTo(NotchVisualState.Expanded);
            }
        };

        _leaveCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.MouseLeaveCollapseDelayMs, 80, 1200))
        };
        _leaveCollapseTimer.Tick += (_, _) =>
        {
            _leaveCollapseTimer.Stop();
            if (!_isPointerInsideNotch && !_isPinned && !_isDraggingSlider && State == NotchVisualState.Expanded)
            {
                TransitionTo(GetCollapsedBaseline());
            }
        };

        _hudDismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.0)
        };
        _hudDismissTimer.Tick += (_, _) =>
        {
            _hudDismissTimer.Stop();
            if (State == NotchVisualState.HudPop)
            {
                if (_isPointerInsideNotch || _isDraggingSlider)
                {
                    _hudTimerExpiredWhileHovered = true;
                }
                else
                {
                    _hudTimerExpiredWhileHovered = false;
                    TransitionTo(GetCollapsedBaseline());
                }
            }
        };
    }

    public void ApplySettings(AppSettings settings)
    {
        _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.HoverExpandDelayMs, 50, 800));
        _leaveCollapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.MouseLeaveCollapseDelayMs, 80, 1200));

        string newPreset = string.IsNullOrWhiteSpace(settings.ExpandedSizePreset) ? "Compact" : settings.ExpandedSizePreset;
        if (!string.Equals(_expandedSizePreset, newPreset, StringComparison.OrdinalIgnoreCase))
        {
            NotchDimensions prevDims = GetDimensionsForState(State);
            _expandedSizePreset = newPreset;
            if (State == NotchVisualState.Expanded)
            {
                NotchDimensions nextDims = GetDimensionsForState(State);
                if (nextDims != prevDims)
                {
                    bool isShrinking = nextDims.Width < prevDims.Width || nextDims.Height < prevDims.Height;
                    StateChanged?.Invoke(State, nextDims, isShrinking);
                }
            }
        }

        if (_showIdleNetworkSpeed != settings.ShowCollapsedNetworkSpeed)
        {
            double prevW = CurrentTargetDimensions.Width;
            _showIdleNetworkSpeed = settings.ShowCollapsedNetworkSpeed;
            if (State is NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive)
            {
                NotchDimensions nextDims = GetDimensionsForState(State);
                StateChanged?.Invoke(State, nextDims, nextDims.Width < prevW);
            }
        }
    }

    public void SetActiveTabAndAgentsHeight(NotchTab activeTab, double desiredAgentsHeight, double maxScreenTwoThirdsDipHeight)
    {
        NotchDimensions prevDims = GetDimensionsForState(State);
        _activeTab = activeTab;
        _desiredAgentsHeight = desiredAgentsHeight;
        _maxScreenTwoThirdsDipHeight = maxScreenTwoThirdsDipHeight;

        if (State == NotchVisualState.Expanded)
        {
            NotchDimensions nextDims = GetDimensionsForState(State);
            if (nextDims != prevDims)
            {
                bool isShrinking = nextDims.Height < prevDims.Height || nextDims.Width < prevDims.Width;
                StateChanged?.Invoke(State, nextDims, isShrinking);
            }
        }
    }

    public NotchDimensions CurrentTargetDimensions => GetDimensionsForState(State);

    public void SetBackgroundActivity(bool hasActivity, CollapsedContentKind contentKind, int privacyDotCount)
    {
        int clampedDots = Math.Clamp(privacyDotCount, 0, 3);
        if (_hasBackgroundActivity == hasActivity && _collapsedKind == contentKind && _privacyDotCount == clampedDots)
        {
            return;
        }

        double prevWidth = CurrentTargetDimensions.Width;
        _hasBackgroundActivity = hasActivity;
        _collapsedKind = contentKind;
        _privacyDotCount = clampedDots;

        if (State is NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive)
        {
            NotchVisualState next = GetCollapsedBaseline();
            NotchDimensions nextDims = GetDimensionsForState(next);
            bool shrinking = nextDims.Width < prevWidth;
            State = next;
            StateChanged?.Invoke(State, nextDims, shrinking);
        }
    }

    public void SetFullscreenHidden(bool isFullscreen)
    {
        if (_isFullscreenHidden == isFullscreen) return;
        _isFullscreenHidden = isFullscreen;
        ReevaluateState();
    }

    public void SetGhostPassthrough(bool active)
    {
        if (_isGhostPassthrough == active) return;
        _isGhostPassthrough = active;

        if (_isGhostPassthrough)
        {
            _hoverExpandTimer.Stop();
            if (State is not NotchVisualState.Hidden)
            {
                TransitionTo(NotchVisualState.GhostPassthrough);
            }
        }
        else
        {
            ReevaluateState();
        }
    }

    public void NotifyPointerEntered()
    {
        _isPointerInsideNotch = true;
        _leaveCollapseTimer.Stop();

        if (_isGhostPassthrough || _isFullscreenHidden || _isManuallyHidden) return;

        if (_suppressHoverExpandUntilTicks != 0 && Stopwatch.GetTimestamp() < _suppressHoverExpandUntilTicks)
        {
            return;
        }
        _suppressHoverExpandUntilTicks = 0;

        if (State is NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive)
        {
            // Immediate Apple Dynamic Island tactile micro-swell on touch while dwell timer runs
            NotchDimensions baseDim = GetDimensionsForState(State);
            HoverBreatheNudged?.Invoke(
                new NotchDimensions(baseDim.Width + 12.0, baseDim.Height + 2.0, baseDim.EarRadius + 0.5, baseDim.BottomRadius + 1.0),
                false);

            _hoverExpandTimer.Stop();
            _hoverExpandTimer.Start();
        }
    }

    public void NotifyPointerLeft()
    {
        _isPointerInsideNotch = false;
        if (_suppressHoverExpandUntilTicks != 0 && Stopwatch.GetTimestamp() >= _suppressHoverExpandUntilTicks)
        {
            _suppressHoverExpandUntilTicks = 0;
        }
        _hoverExpandTimer.Stop();

        if (State is NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive)
        {
            // Relax back from hover micro-swell
            HoverBreatheNudged?.Invoke(GetDimensionsForState(State), true);
        }
        else if (State == NotchVisualState.HudPop && !_isDraggingSlider)
        {
            if (_hudTimerExpiredWhileHovered || _activeHudShape == HudShapeKind.NotificationBanner)
            {
                _hudDismissTimer.Stop();
                _hudDismissTimer.Interval = TimeSpan.FromMilliseconds(_hudTimerExpiredWhileHovered ? 620 : 1200);
                _hudTimerExpiredWhileHovered = false;
                _hudDismissTimer.Start();
            }
        }
        else if (State == NotchVisualState.Expanded && !_isPinned && !_isDraggingSlider)
        {
            ScheduleCollapse();
        }
    }

    /// <summary>
    /// Suppresses pending hover expansion caused by pointer dwell over the collapsed notch.
    /// Halts the hover timer, relaxes the tactile micro-swell breathe back to resting dimensions,
    /// and establishes an 850ms suppression window to facilitate continuous wheel scrolling and middle-click gestures.
    /// </summary>
    public void SuppressPendingHoverExpand()
    {
        _hoverExpandTimer.Stop();
        _suppressHoverExpandUntilTicks = Stopwatch.GetTimestamp() + (long)(0.85 * Stopwatch.Frequency);

        if (State is NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive)
        {
            HoverBreatheNudged?.Invoke(GetDimensionsForState(State), true);
        }
    }

    public void SetSliderInteraction(bool dragging)
    {
        _isDraggingSlider = dragging;
        if (!dragging && !_isPointerInsideNotch && State == NotchVisualState.Expanded && !_isPinned)
        {
            ScheduleCollapse();
        }
        else if (State == NotchVisualState.HudPop)
        {
            _hudDismissTimer.Stop();
            _hudDismissTimer.Start();
        }
    }

    public void ToggleExpandViaHotkey()
    {
        if (_isFullscreenHidden || _isManuallyHidden) return;

        _hoverExpandTimer.Stop();
        _leaveCollapseTimer.Stop();

        if (State == NotchVisualState.Expanded)
        {
            _isPinned = false;
            PinnedChanged?.Invoke(false);
            TransitionTo(GetCollapsedBaseline());
        }
        else
        {
            TransitionTo(NotchVisualState.Expanded);
        }
    }

    public void ExpandAndPin(NotchTab? tab = null)
    {
        if (_isManuallyHidden) _isManuallyHidden = false;
        if (tab.HasValue)
        {
            _activeTab = tab.Value;
            RequestTabSwitch?.Invoke(tab.Value);
        }
        _isPinned = true;
        PinnedChanged?.Invoke(true);
        TransitionTo(NotchVisualState.Expanded);
    }

    public void CollapseImmediately()
    {
        _hoverExpandTimer.Stop();
        _leaveCollapseTimer.Stop();
        _isPointerInsideNotch = false;
        _suppressHoverExpandUntilTicks = Stopwatch.GetTimestamp() + (long)(0.65 * Stopwatch.Frequency);

        if (_isPinned)
        {
            _isPinned = false;
            PinnedChanged?.Invoke(false);
        }

        if (State is NotchVisualState.Expanded or NotchVisualState.HudPop or NotchVisualState.DragReady)
        {
            TransitionTo(GetCollapsedBaseline());
        }
    }

    public void TriggerHudPop(
        HudShapeKind shapeKind = HudShapeKind.CompactPill,
        double durationSeconds = 2.0,
        bool forcePreemptExpanded = false)
    {
        if (_isFullscreenHidden || _isManuallyHidden || _isGhostPassthrough) return;

        if (!forcePreemptExpanded && State is NotchVisualState.Expanded or NotchVisualState.DragReady)
        {
            return;
        }

        if (forcePreemptExpanded && _isPinned)
        {
            _isPinned = false;
            PinnedChanged?.Invoke(false);
        }

        NotchDimensions prevDims = CurrentTargetDimensions;
        _activeHudShape = shapeKind;
        _hudTimerExpiredWhileHovered = false;
        _hudDismissTimer.Stop();
        _hudDismissTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1.0, 8.0));

        NotchDimensions nextDims = GetDimensionsForState(NotchVisualState.HudPop);
        if (State != NotchVisualState.HudPop || prevDims != nextDims)
        {
            bool isCollapsing = nextDims.Height < prevDims.Height && nextDims.Width < prevDims.Width;
            State = NotchVisualState.HudPop;
            StateChanged?.Invoke(State, nextDims, isCollapsing);
        }

        _hudDismissTimer.Start();
    }

    public void DismissActiveHud()
    {
        if (State != NotchVisualState.HudPop) return;
        _hudDismissTimer.Stop();
        _hudTimerExpiredWhileHovered = false;
        _isPointerInsideNotch = false;
        _suppressHoverExpandUntilTicks = Stopwatch.GetTimestamp() + (long)(0.65 * Stopwatch.Frequency);
        TransitionTo(GetCollapsedBaseline());
    }

    public void ResetHudTimer()
    {
        if (State == NotchVisualState.HudPop)
        {
            _hudDismissTimer.Stop();
            _hudDismissTimer.Start();
        }
    }

    public void SetDragZoneActive(bool active)
    {
        if (_isFullscreenHidden || _isManuallyHidden || _isGhostPassthrough) return;

        if (active)
        {
            _hoverExpandTimer.Stop();
            _leaveCollapseTimer.Stop();
            if (State != NotchVisualState.Expanded && State != NotchVisualState.DragReady)
            {
                TransitionTo(NotchVisualState.DragReady);
            }
        }
        else if (State == NotchVisualState.DragReady)
        {
            TransitionTo(GetCollapsedBaseline());
        }
    }

    public void CompleteDropToFileShelf()
    {
        RequestTabSwitch?.Invoke(NotchTab.FileShelf);
        TransitionTo(NotchVisualState.Expanded);
        if (!_isPointerInsideNotch && !_isPinned)
        {
            ScheduleCollapse();
        }
    }

    private void ScheduleCollapse()
    {
        _leaveCollapseTimer.Stop();
        _leaveCollapseTimer.Start();
    }

    private void ReevaluateState()
    {
        if (_isFullscreenHidden || _isManuallyHidden)
        {
            _hoverExpandTimer.Stop();
            _leaveCollapseTimer.Stop();
            _hudDismissTimer.Stop();
            TransitionTo(NotchVisualState.Hidden);
            return;
        }

        if (_isGhostPassthrough)
        {
            TransitionTo(NotchVisualState.GhostPassthrough);
            return;
        }

        if (_isPinned)
        {
            TransitionTo(NotchVisualState.Expanded);
            return;
        }

        TransitionTo(GetCollapsedBaseline());
    }

    private NotchVisualState GetCollapsedBaseline() =>
        _hasBackgroundActivity ? NotchVisualState.CollapsedActive : NotchVisualState.CollapsedIdle;

    public NotchDimensions GetDimensionsForState(NotchVisualState state)
    {
        NotchDimensions collapsedDims = ResolveCollapsedDimensions();
        return state switch
        {
            NotchVisualState.CollapsedIdle => collapsedDims,
            NotchVisualState.CollapsedActive => collapsedDims,
            NotchVisualState.GhostPassthrough => collapsedDims,
            NotchVisualState.HudPop => _activeHudShape switch
            {
                HudShapeKind.FaceIdSquare => DimFaceIdHud,
                HudShapeKind.NotificationBanner => DimNotificationHud,
                _ => DimHudPop
            },
            NotchVisualState.DragReady => DimDragReady,
            NotchVisualState.Expanded => ResolveExpandedDimensions(),
            NotchVisualState.Hidden => DimHidden,
            _ => DimCollapsedIdle
        };
    }

    private NotchDimensions ResolveExpandedDimensions()
    {
        NotchDimensions baseDims = ActiveExpandedDimensions;
        if (_activeTab != NotchTab.Agents || _desiredAgentsHeight <= 0.0)
        {
            return baseDims;
        }

        double minH = baseDims.Height;
        double maxH = _maxScreenTwoThirdsDipHeight > minH ? _maxScreenTwoThirdsDipHeight : minH;
        double targetH = Math.Clamp(_desiredAgentsHeight, minH, maxH);
        return baseDims with { Height = targetH };
    }

    private NotchDimensions ResolveCollapsedDimensions()
    {
        NotchDimensions baseDim = _collapsedKind switch
        {
            CollapsedContentKind.TimerOnly => DimCollapsedTimer,
            CollapsedContentKind.AgentOnly => DimCollapsedAgent,
            CollapsedContentKind.MusicOnly => DimCollapsedMusic,
            CollapsedContentKind.MusicAndTimer => DimCollapsedDualActive,
            _ => _showIdleNetworkSpeed ? DimCollapsedIdleNet : DimCollapsedIdle
        };

        if (_privacyDotCount <= 0) return baseDim;
        double extra = _privacyDotCount switch
        {
            1 => 15.0,
            2 => 27.0,
            _ => 39.0
        };
        return baseDim with { Width = baseDim.Width + extra };
    }

    private void TransitionTo(NotchVisualState next)
    {
        NotchDimensions prevDims = GetDimensionsForState(State);
        NotchDimensions nextDims = GetDimensionsForState(next);

        if (State == next && prevDims == nextDims) return;

        bool isCollapsing = nextDims.Height < prevDims.Height || nextDims.Width < prevDims.Width;
        State = next;
        StateChanged?.Invoke(State, nextDims, isCollapsing);
    }
}
