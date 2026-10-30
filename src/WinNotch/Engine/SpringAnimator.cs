using System.Diagnostics;
using System.Windows.Media;
using WinNotch.Models;

namespace WinNotch.Engine;

public readonly record struct NotchDimensions(double Width, double Height, double EarRadius, double BottomRadius);

public readonly record struct NotchRenderFrame(
    NotchDimensions Dimensions,
    double CollapsedOpacity,
    double HudOpacity,
    double DragOpacity,
    double ExpandedOpacity,
    double ExpandedProgress);

public sealed class SpringAnimator
{
    private struct SpringChannel
    {
        public double Value;
        public double Velocity;
        public double Target;

        public bool Step(double dt, double stiffness, double damping, double clampMin)
        {
            double displacement = Value - Target;
            double accel = (-stiffness * displacement) - (damping * Velocity);
            Velocity += accel * dt;
            Value += Velocity * dt;

            if (Value < clampMin)
            {
                Value = clampMin;
                if (Velocity < 0) Velocity = 0;
            }

            if (Math.Abs(Value - Target) < 0.03 && Math.Abs(Velocity) < 0.12)
            {
                Value = Target;
                Velocity = 0.0;
                return false;
            }

            return true;
        }
    }

    private struct AlphaChannel
    {
        public double Value;
        public double Target;

        public bool Step(double dt)
        {
            if (Math.Abs(Value - Target) < 0.004)
            {
                Value = Target;
                return false;
            }

            // Outgoing layers dissolve out briskly; incoming layers bloom smoothly
            double speed = Target < Value ? 19.0 : 12.5;
            double factor = 1.0 - Math.Exp(-speed * dt);
            Value += (Target - Value) * factor;

            if (Math.Abs(Value - Target) < 0.004)
            {
                Value = Target;
                return false;
            }
            return true;
        }
    }

    private SpringChannel _width;
    private SpringChannel _height;
    private SpringChannel _earRadius;
    private SpringChannel _bottomRadius;

    private AlphaChannel _collapsedAlpha = new() { Value = 1.0, Target = 1.0 };
    private AlphaChannel _hudAlpha;
    private AlphaChannel _dragAlpha;
    private AlphaChannel _expandedAlpha;

    private double _stiffness = 265.0;
    private double _damping = 24.8;
    private string _preset = "Balanced";
    private bool _isRunning;
    private long _lastTimestamp;

    public event Action<NotchRenderFrame>? FrameUpdated;

    public NotchDimensions CurrentDimensions => new(_width.Value, _height.Value, _earRadius.Value, _bottomRadius.Value);

    public SpringAnimator(NotchDimensions initial)
    {
        _width = new SpringChannel { Value = initial.Width, Target = initial.Width };
        _height = new SpringChannel { Value = initial.Height, Target = initial.Height };
        _earRadius = new SpringChannel { Value = initial.EarRadius, Target = initial.EarRadius };
        _bottomRadius = new SpringChannel { Value = initial.BottomRadius, Target = initial.BottomRadius };
    }

    public void SetPreset(string? preset)
    {
        _preset = string.IsNullOrWhiteSpace(preset) ? "Balanced" : preset;
    }

    public void AnimateTo(NotchVisualState targetState, NotchDimensions target, bool isCollapsing)
    {
        (_stiffness, _damping) = ResolvePhysics(_preset, isCollapsing);

        _width.Target = target.Width;
        _height.Target = target.Height;
        _earRadius.Target = target.EarRadius;
        _bottomRadius.Target = target.BottomRadius;

        bool showCollapsed = targetState is NotchVisualState.CollapsedIdle
            or NotchVisualState.CollapsedActive
            or NotchVisualState.GhostPassthrough;

        _collapsedAlpha.Target = showCollapsed ? 1.0 : 0.0;
        _hudAlpha.Target = targetState == NotchVisualState.HudPop ? 1.0 : 0.0;
        _dragAlpha.Target = targetState == NotchVisualState.DragReady ? 1.0 : 0.0;
        _expandedAlpha.Target = targetState == NotchVisualState.Expanded ? 1.0 : 0.0;

        EnsureLoopRunning();
    }

    public void NudgeDimensions(NotchDimensions target, bool isShrinking)
    {
        (_stiffness, _damping) = ResolvePhysics(_preset, isShrinking);
        _width.Target = target.Width;
        _height.Target = target.Height;
        _earRadius.Target = target.EarRadius;
        _bottomRadius.Target = target.BottomRadius;
        EnsureLoopRunning();
    }

    private void EnsureLoopRunning()
    {
        if (!_isRunning)
        {
            _isRunning = true;
            _lastTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnCompositionRendering;
        }
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (double)(now - _lastTimestamp) / Stopwatch.Frequency;
        _lastTimestamp = now;

        if (dt <= 0.0) return;
        if (dt > 0.033) dt = 0.033;

        // 4x sub-stepping per frame for ultra-smooth, jitter-free integration at 60-240Hz
        const int steps = 4;
        double subDt = dt / steps;
        bool springMoving = false;

        for (int i = 0; i < steps; i++)
        {
            bool m1 = _width.Step(subDt, _stiffness, _damping, 40.0);
            bool m2 = _height.Step(subDt, _stiffness, _damping, 0.0);
            bool m3 = _earRadius.Step(subDt, _stiffness, _damping, 0.0);
            bool m4 = _bottomRadius.Step(subDt, _stiffness, _damping, 0.0);
            springMoving = m1 || m2 || m3 || m4;
        }

        bool a1 = _collapsedAlpha.Step(dt);
        bool a2 = _hudAlpha.Step(dt);
        bool a3 = _dragAlpha.Step(dt);
        bool a4 = _expandedAlpha.Step(dt);

        double baseH = NotchStateMachine.DimCollapsedIdle.Height;
        double fullH = NotchStateMachine.DimExpanded.Height;
        double expProgress = Math.Clamp((_height.Value - baseH) / Math.Max(1.0, fullH - baseH), 0.0, 1.12);

        FrameUpdated?.Invoke(new NotchRenderFrame(
            CurrentDimensions,
            _collapsedAlpha.Value,
            _hudAlpha.Value,
            _dragAlpha.Value,
            _expandedAlpha.Value,
            expProgress));

        if (!springMoving && !a1 && !a2 && !a3 && !a4)
        {
            StopLoop();
        }
    }

    private void StopLoop()
    {
        if (_isRunning)
        {
            _isRunning = false;
            CompositionTarget.Rendering -= OnCompositionRendering;
        }
    }

    private static (double Stiffness, double Damping) ResolvePhysics(string preset, bool isCollapsing)
    {
        return preset switch
        {
            "Snappy" => isCollapsing ? (420.0, 41.0) : (370.0, 31.5),
            "Bouncy" => isCollapsing ? (315.0, 34.0) : (235.0, 18.0),
            // Apple CASpringAnimation liquid feel: silky organic expand, critically-damped zero-bounce collapse
            _ => isCollapsing ? (315.0, 34.8) : (265.0, 24.8)
        };
    }
}
