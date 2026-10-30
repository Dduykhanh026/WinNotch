using System.Windows.Threading;
using WinNotch.Models;

namespace WinNotch.Engine;

public sealed class NotchStateMachine
{
    public static readonly NotchDimensions DimCollapsedIdle = new(132, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedIdleNet = new(168, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedTimer = new(106, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedMusic = new(228, 32, 7.5, 12.5);
    public static readonly NotchDimensions DimCollapsedDualActive = new(276, 32, 8.0, 12.5);
    public static readonly NotchDimensions DimHudPop = new(376, 48, 9.5, 18.0);
    public static readonly NotchDimensions DimFaceIdHud = new(156, 148, 11.0, 36.0);
    public static readonly NotchDimensions DimDragReady = new(444, 118, 11.5, 26.0);
    public static readonly NotchDimensions DimExpanded = new(684, 272, 13.5, 30.0);
    public static readonly NotchDimensions DimHidden = new(110, 0, 0, 0);

    private readonly DispatcherTimer _hoverExpandTimer;
    private readonly DispatcherTimer _leaveCollapseTimer;
    private readonly DispatcherTimer _hudDismissTimer;

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
    private bool _isFaceIdSquareHud;

    public NotchVisualState State { get; private set; } = NotchVisualState.CollapsedIdle;

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
                    _hudDismissTimer.Start();
                }
                else
                {
                    TransitionTo(GetCollapsedBaseline());
                }
            }
        };
    }

    public void ApplySettings(AppSettings settings)
    {
        _hoverExpandTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.HoverExpandDelayMs, 50, 800));
        _leaveCollapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.MouseLeaveCollapseDelayMs, 80, 1200));

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

    public NotchDimensions CurrentTargetDimensions => GetDimensionsForState(State);

    public void SetBackgroundActivity(bool hasActivity, CollapsedContentKind contentKind, int privacyDotCount)
    {
        int clampedDots = Math.Clamp(privacyDotCount, 0, 2);
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
        _hoverExpandTimer.Stop();

        if (State is NotchVisualState.CollapsedIdle or NotchVisualState.CollapsedActive)
        {
            // Relax back from hover micro-swell
            HoverBreatheNudged?.Invoke(GetDimensionsForState(State), true);
        }
        else if (State == NotchVisualState.Expanded && !_isPinned && !_isDraggingSlider)
        {
            ScheduleCollapse();
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
        if (tab.HasValue) RequestTabSwitch?.Invoke(tab.Value);
        _isPinned = true;
        PinnedChanged?.Invoke(true);
        TransitionTo(NotchVisualState.Expanded);
    }

    public void CollapseImmediately()
    {
        _hoverExpandTimer.Stop();
        _leaveCollapseTimer.Stop();
        _isPointerInsideNotch = false;

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

    public void TriggerHudPop(bool isFaceIdSquare = false, double durationSeconds = 2.0, bool forcePreemptExpanded = false)
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
        _isFaceIdSquareHud = isFaceIdSquare;
        _hudDismissTimer.Stop();
        _hudDismissTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 1.0, 5.0));

        NotchDimensions nextDims = GetDimensionsForState(NotchVisualState.HudPop);
        if (State != NotchVisualState.HudPop || prevDims != nextDims)
        {
            bool isCollapsing = nextDims.Height < prevDims.Height && nextDims.Width < prevDims.Width;
            State = NotchVisualState.HudPop;
            StateChanged?.Invoke(State, nextDims, isCollapsing);
        }

        _hudDismissTimer.Start();
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
            NotchVisualState.HudPop => _isFaceIdSquareHud ? DimFaceIdHud : DimHudPop,
            NotchVisualState.DragReady => DimDragReady,
            NotchVisualState.Expanded => DimExpanded,
            NotchVisualState.Hidden => DimHidden,
            _ => DimCollapsedIdle
        };
    }

    private NotchDimensions ResolveCollapsedDimensions()
    {
        NotchDimensions baseDim = _collapsedKind switch
        {
            CollapsedContentKind.TimerOnly => DimCollapsedTimer,
            CollapsedContentKind.MusicOnly => DimCollapsedMusic,
            CollapsedContentKind.MusicAndTimer => DimCollapsedDualActive,
            _ => _showIdleNetworkSpeed ? DimCollapsedIdleNet : DimCollapsedIdle
        };

        if (_privacyDotCount <= 0) return baseDim;
        double extra = _privacyDotCount == 1 ? 15.0 : 27.0;
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
