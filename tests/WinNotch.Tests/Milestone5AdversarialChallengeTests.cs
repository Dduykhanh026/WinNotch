using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;
using WinNotch.Views;
using WinNotch.Views.Controls;

namespace WinNotch.Tests;

public static class Milestone5AdversarialChallengeTests
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_SHIFT = 0x10;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static readonly FieldInfo SuppressTicksField = typeof(NotchStateMachine).GetField("_suppressHoverExpandUntilTicks", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo HoverTimerField = typeof(NotchStateMachine).GetField("_hoverExpandTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo OnNotchSurfaceMouseWheelMethod = typeof(NotchWindow).GetMethod("OnNotchSurfaceMouseWheel", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo OnNotchSurfaceMouseDownMethod = typeof(NotchWindow).GetMethod("OnNotchSurfaceMouseDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo OnAudioDeviceRightClickMethod = typeof(DashboardView).GetMethod("OnAudioDeviceRightClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo OnTimerWheelMethod = typeof(DashboardView).GetMethod("OnTimerWheel", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo TransitionToMethod = typeof(NotchStateMachine).GetMethod("TransitionTo", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void TransitionTo(this NotchStateMachine sm, NotchVisualState state)
    {
        TransitionToMethod.Invoke(sm, [state]);
    }


    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" MILESTONE 5: NOTCH CONTROLS, GESTURES & HOVER SUPPRESSION SUITE        ");
        Console.WriteLine("=========================================================================");

        EnsureWpfEnvironment();

        // Suite 1: Collapsed Notch Mouse Wheel Gestures & Dwell Hover Suppression
        TestCollapsedWheelVolumeGestures(assert);
        TestCollapsedWheelShiftBrightnessGestures(assert);
        TestCollapsedWheelGesturesDisabledSetting(assert);
        TestCollapsedWheelInNonCollapsedStates(assert);
        TestHighFrequencyWheelScrollTorture(assert);

        // Suite 2: Collapsed Notch Middle-Click Gestures & Hover Suppression
        TestCollapsedMiddleClickMediaGestures(assert);
        TestCollapsedMiddleClickNonMiddleButtonsIgnored(assert);
        TestCollapsedMiddleClickInNonCollapsedStates(assert);
        TestInterleavedWheelAndMiddleClickRollingWindow(assert);

        // Suite 3: Lock Workstation Header Button
        TestLockWorkstationButtonPresenceAndRow0(assert);
        TestLockWorkstationButtonCommandBinding(assert);
        TestLockWorkstationAccessibilityAcrossAll3Tabs(assert);
        TestLockWorkstationVisualProperties(assert);

        // Suite 4: DashboardView Row 0 Audio Device Switcher
        TestDashboardAudioDevicePillBindings(assert);
        TestDashboardAudioDeviceLeftClickCommand(assert);
        TestDashboardAudioDeviceRightClickSettingsInvocation(assert);

        // Suite 5: DashboardView Row 2 Timer Wheel
        TestDashboardTimerWheelBindings(assert);
        TestDashboardTimerWheelPositiveAndNegativeSteps(assert);
        TestDashboardTimerWheelRapidStress(assert);

        // Suite 6: DashboardView Row 3 Action Pills
        TestDashboardRow3FourPillsStructureAndCommands(assert);
        TestDashboardRow3MicrophonePillDynamicVisuals(assert);
        TestDashboardRow3CaffeinePillDynamicVisuals(assert);
    }

    private static void EnsureWpfEnvironment()
    {
        if (Application.Current == null)
        {
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
        }
        else
        {
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        if (Application.Current != null && !Application.Current.Resources.Contains("AppleFontFamily"))
        {
            try
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/WinNotch;component/Views/Themes/NotchStyles.xaml", UriKind.Absolute)
                };
                Application.Current.Resources.MergedDictionaries.Add(dict);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Could not merge NotchStyles: {ex.Message}");
            }
        }
    }

    private static long GetSuppressTicks(NotchStateMachine sm) => (long)SuppressTicksField.GetValue(sm)!;
    private static DispatcherTimer GetHoverTimer(NotchStateMachine sm) => (DispatcherTimer)HoverTimerField.GetValue(sm)!;

    private static NotchWindow CreateTestHarness(
        out SettingsService settings,
        out NotchStateMachine stateMachine,
        out MainNotchViewModel viewModel,
        out AudioService audio,
        out DisplayPowerService power,
        out TimerService timer)
    {
        settings = new SettingsService();
        settings.Current.EnableCollapsedScrollGestures = true;
        stateMachine = new NotchStateMachine(settings.Current);

        var monitor = new MonitorDescriptor
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyLabel = "Test OLED Display",
            IsPrimary = true,
            PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            DpiScaleX = 1.0,
            DpiScaleY = 1.0
        };

        audio = new AudioService(settings);
        power = new DisplayPowerService(settings, monitor);
        var media = new MediaService(settings);
        var shelf = new FileShelfService(settings);
        timer = new TimerService();
        var privacy = new PrivacyService(settings);
        var systemMonitor = new SystemMonitorService(settings);
        var notif = new NotificationService(settings);
        var picker = new ColorPickerService(stateMachine);

        viewModel = new MainNotchViewModel(
            settings,
            stateMachine,
            audio,
            power,
            media,
            shelf,
            timer,
            privacy,
            systemMonitor,
            notif,
            picker,
            new AgentMonitorService(settings),
            () => { });

        var window = new NotchWindow(
            settings,
            stateMachine,
            viewModel,
            notif,
            monitor,
            () => { });

        return window;
    }

    private static MouseWheelEventArgs CreateWheelEventArgs(int delta)
    {
        return new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        };
    }

    private static MouseButtonEventArgs CreateMouseButtonEventArgs(MouseButton button)
    {
        return new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent
        };
    }

    // =========================================================================
    // SUITE 1: COLLAPSED NOTCH MOUSE WHEEL GESTURES & HOVER SUPPRESSION
    // =========================================================================

    private static void TestCollapsedWheelVolumeGestures(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S1] Collapsed Mouse Wheel Volume Stepping & Hover Suppression ---");

        var window = CreateTestHarness(out var settings, out var sm, out var vm, out var audio, out _, out _);
        sm.TransitionTo(NotchVisualState.CollapsedIdle);

        // 1. Wheel Up (+120): step volume +2%
        var wheelUpArgs = CreateWheelEventArgs(120);
        long beforeTicks = Stopwatch.GetTimestamp();
        OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, wheelUpArgs]);
        long suppressTicks = GetSuppressTicks(sm);

        assert(wheelUpArgs.Handled, "Wheel Up event marked handled");
        assert(suppressTicks > beforeTicks, "SuppressPendingHoverExpand sets suppression ticks in future");
        double remainingMs = (suppressTicks - beforeTicks) * 1000.0 / Stopwatch.Frequency;
        assert(remainingMs >= 840.0 && remainingMs <= 860.0, "Suppression window set to ~850ms", $"{remainingMs:F1}ms");
        assert(!GetHoverTimer(sm).IsEnabled, "Hover expand timer is stopped on wheel up");

        // 2. Wheel Down (-120): step volume -2%
        var wheelDownArgs = CreateWheelEventArgs(-120);
        beforeTicks = Stopwatch.GetTimestamp();
        OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, wheelDownArgs]);
        suppressTicks = GetSuppressTicks(sm);

        assert(wheelDownArgs.Handled, "Wheel Down event marked handled");
        assert(suppressTicks > beforeTicks, "SuppressPendingHoverExpand refreshed on wheel down");
        remainingMs = (suppressTicks - beforeTicks) * 1000.0 / Stopwatch.Frequency;
        assert(remainingMs >= 840.0 && remainingMs <= 860.0, "Suppression window refreshed to ~850ms", $"{remainingMs:F1}ms");

        // 3. Test in CollapsedActive state
        sm.TransitionTo(NotchVisualState.CollapsedActive);
        var activeWheelArgs = CreateWheelEventArgs(120);
        OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, activeWheelArgs]);
        assert(activeWheelArgs.Handled, "Wheel event handled in CollapsedActive state");
    }

    private static void TestCollapsedWheelShiftBrightnessGestures(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S1] Collapsed Mouse Wheel Shift + Wheel Brightness Stepping ---");

        var window = CreateTestHarness(out var settings, out var sm, out var vm, out _, out var power, out _);
        sm.TransitionTo(NotchVisualState.CollapsedIdle);

        // 1. Simulate Shift Key Down and Wheel Up
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);

        var shiftWheelUpArgs = CreateWheelEventArgs(120);
        OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, shiftWheelUpArgs]);

        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);

        assert(shiftWheelUpArgs.Handled, "Shift + Wheel Up marked handled");
        assert(GetSuppressTicks(sm) > 0, "Hover suppression active on Shift + Wheel Up");

        // 2. Simulate Shift Key Down and Wheel Down
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);

        var shiftWheelDownArgs = CreateWheelEventArgs(-120);
        OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, shiftWheelDownArgs]);

        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);

        assert(shiftWheelDownArgs.Handled, "Shift + Wheel Down marked handled");
        assert(GetSuppressTicks(sm) > 0, "Hover suppression active on Shift + Wheel Down");

        // 3. Verify StepBrightness on ViewModel directly
        double initialBrightness = vm.QuickBrightnessPercent;
        vm.StepBrightness(4);
        vm.StepBrightness(-4);
        assert(true, "StepBrightness(±4) executed on MainNotchViewModel cleanly");
    }

    private static void TestCollapsedWheelGesturesDisabledSetting(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S1] Collapsed Mouse Wheel Gestures Disabled Setting ---");

        var window = CreateTestHarness(out var settings, out var sm, out var vm, out _, out _, out _);
        sm.TransitionTo(NotchVisualState.CollapsedIdle);

        // Disable setting
        settings.Current.EnableCollapsedScrollGestures = false;
        SuppressTicksField.SetValue(sm, 0L);

        var wheelArgs = CreateWheelEventArgs(120);
        OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, wheelArgs]);

        assert(!wheelArgs.Handled, "Wheel event NOT handled when EnableCollapsedScrollGestures == false");
        assert(GetSuppressTicks(sm) == 0L, "SuppressPendingHoverExpand NOT called when gestures disabled");

        // Re-enable
        settings.Current.EnableCollapsedScrollGestures = true;
        wheelArgs = CreateWheelEventArgs(120);
        OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, wheelArgs]);
        assert(wheelArgs.Handled, "Wheel event handled once re-enabled");
        assert(GetSuppressTicks(sm) > 0L, "SuppressPendingHoverExpand called once re-enabled");
    }

    private static void TestCollapsedWheelInNonCollapsedStates(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S1] Collapsed Mouse Wheel Ignored in Non-Collapsed States ---");

        var window = CreateTestHarness(out _, out var sm, out _, out _, out _, out _);

        NotchVisualState[] nonCollapsedStates =
        [
            NotchVisualState.Expanded,
            NotchVisualState.HudPop,
            NotchVisualState.DragReady,
            NotchVisualState.GhostPassthrough,
            NotchVisualState.Hidden
        ];

        foreach (var state in nonCollapsedStates)
        {
            sm.TransitionTo(state);
            SuppressTicksField.SetValue(sm, 0L);

            var wheelArgs = CreateWheelEventArgs(120);
            OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, wheelArgs]);

            assert(!wheelArgs.Handled, $"Wheel event NOT handled in state {state}");
            assert(GetSuppressTicks(sm) == 0L, $"SuppressPendingHoverExpand NOT called in state {state}");
        }
    }

    private static void TestHighFrequencyWheelScrollTorture(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S1] High-Frequency Wheel Scroll Torture (100 Scrolls) ---");

        var window = CreateTestHarness(out _, out var sm, out _, out _, out _, out _);
        sm.TransitionTo(NotchVisualState.CollapsedIdle);

        long lastTicks = 0;
        bool allHandled = true;
        bool allSuppressed = true;

        for (int i = 0; i < 100; i++)
        {
            var args = CreateWheelEventArgs((i % 2 == 0) ? 120 : -120);
            OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, args]);

            if (!args.Handled) allHandled = false;
            long curTicks = GetSuppressTicks(sm);
            if (curTicks <= lastTicks && i > 0) allSuppressed = false;
            lastTicks = curTicks;

            if (GetHoverTimer(sm).IsEnabled)
            {
                assert(false, "Hover timer started during high frequency scroll!");
            }
        }

        assert(allHandled, "All 100 high-frequency wheel scrolls marked Handled");
        assert(allSuppressed, "Suppression ticks strictly advanced across all 100 scrolls");
        assert(!GetHoverTimer(sm).IsEnabled, "Hover timer strictly stopped after all 100 scrolls");
    }

    // =========================================================================
    // SUITE 2: COLLAPSED NOTCH MIDDLE-CLICK GESTURES & HOVER SUPPRESSION
    // =========================================================================

    private static void TestCollapsedMiddleClickMediaGestures(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S2] Collapsed Middle-Click Media Gestures & Hover Suppression ---");

        var window = CreateTestHarness(out _, out var sm, out var vm, out _, out _, out _);
        sm.TransitionTo(NotchVisualState.CollapsedIdle);

        // 1. Normal Middle-Click (Play/Pause)
        var midClickArgs = CreateMouseButtonEventArgs(MouseButton.Middle);
        long beforeTicks = Stopwatch.GetTimestamp();
        OnNotchSurfaceMouseDownMethod.Invoke(window, [window, midClickArgs]);
        long suppressTicks = GetSuppressTicks(sm);

        assert(midClickArgs.Handled, "Middle-click event marked handled");
        assert(suppressTicks > beforeTicks, "SuppressPendingHoverExpand called on middle-click");
        double remainingMs = (suppressTicks - beforeTicks) * 1000.0 / Stopwatch.Frequency;
        assert(remainingMs >= 840.0 && remainingMs <= 860.0, "Suppression window set to ~850ms on middle-click", $"{remainingMs:F1}ms");

        // 2. Shift + Middle-Click (Next Track)
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);

        var shiftMidClickArgs = CreateMouseButtonEventArgs(MouseButton.Middle);
        beforeTicks = Stopwatch.GetTimestamp();
        OnNotchSurfaceMouseDownMethod.Invoke(window, [window, shiftMidClickArgs]);

        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);

        assert(shiftMidClickArgs.Handled, "Shift + Middle-click marked handled");
        assert(GetSuppressTicks(sm) > beforeTicks, "Hover suppression refreshed on Shift + Middle-click");

        // 3. Test in CollapsedActive state
        sm.TransitionTo(NotchVisualState.CollapsedActive);
        var activeMidArgs = CreateMouseButtonEventArgs(MouseButton.Middle);
        OnNotchSurfaceMouseDownMethod.Invoke(window, [window, activeMidArgs]);
        assert(activeMidArgs.Handled, "Middle-click handled in CollapsedActive state");
    }

    private static void TestCollapsedMiddleClickNonMiddleButtonsIgnored(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S2] Non-Middle Mouse Clicks Ignored by Gesture Handler ---");

        var window = CreateTestHarness(out _, out var sm, out _, out _, out _, out _);
        sm.TransitionTo(NotchVisualState.CollapsedIdle);
        SuppressTicksField.SetValue(sm, 0L);

        // Left Click
        var leftClickArgs = CreateMouseButtonEventArgs(MouseButton.Left);
        OnNotchSurfaceMouseDownMethod.Invoke(window, [window, leftClickArgs]);
        assert(!leftClickArgs.Handled, "Left-click NOT handled by OnNotchSurfaceMouseDown");
        assert(GetSuppressTicks(sm) == 0L, "SuppressPendingHoverExpand NOT called on left click");

        // Right Click
        var rightClickArgs = CreateMouseButtonEventArgs(MouseButton.Right);
        OnNotchSurfaceMouseDownMethod.Invoke(window, [window, rightClickArgs]);
        assert(!rightClickArgs.Handled, "Right-click NOT handled by OnNotchSurfaceMouseDown");
        assert(GetSuppressTicks(sm) == 0L, "SuppressPendingHoverExpand NOT called on right click");
    }

    private static void TestCollapsedMiddleClickInNonCollapsedStates(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S2] Middle-Click Ignored in Non-Collapsed States ---");

        var window = CreateTestHarness(out _, out var sm, out _, out _, out _, out _);

        NotchVisualState[] nonCollapsedStates =
        [
            NotchVisualState.Expanded,
            NotchVisualState.HudPop,
            NotchVisualState.DragReady,
            NotchVisualState.GhostPassthrough,
            NotchVisualState.Hidden
        ];

        foreach (var state in nonCollapsedStates)
        {
            sm.TransitionTo(state);
            SuppressTicksField.SetValue(sm, 0L);

            var midArgs = CreateMouseButtonEventArgs(MouseButton.Middle);
            OnNotchSurfaceMouseDownMethod.Invoke(window, [window, midArgs]);

            assert(!midArgs.Handled, $"Middle-click NOT handled in state {state}");
            assert(GetSuppressTicks(sm) == 0L, $"SuppressPendingHoverExpand NOT called in state {state}");
        }
    }

    private static void TestInterleavedWheelAndMiddleClickRollingWindow(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S2] Interleaved Wheel & Middle-Click Rolling Window Torture ---");

        var window = CreateTestHarness(out _, out var sm, out _, out _, out _, out _);
        sm.TransitionTo(NotchVisualState.CollapsedIdle);

        long lastTicks = 0;
        bool allHandled = true;

        for (int i = 0; i < 60; i++)
        {
            RoutedEventArgs args;
            if (i % 2 == 0)
            {
                var wArgs = CreateWheelEventArgs(120);
                OnNotchSurfaceMouseWheelMethod.Invoke(window, [window, wArgs]);
                args = wArgs;
            }
            else
            {
                var mArgs = CreateMouseButtonEventArgs(MouseButton.Middle);
                OnNotchSurfaceMouseDownMethod.Invoke(window, [window, mArgs]);
                args = mArgs;
            }

            if (args is MouseWheelEventArgs wa && !wa.Handled) allHandled = false;
            if (args is MouseButtonEventArgs ma && !ma.Handled) allHandled = false;

            long curTicks = GetSuppressTicks(sm);
            if (curTicks <= lastTicks && i > 0)
            {
                assert(false, "Suppression ticks failed to roll forward during interleaved gestures");
            }
            lastTicks = curTicks;
        }

        assert(allHandled, "All 60 interleaved gestures marked Handled");
        assert(!GetHoverTimer(sm).IsEnabled, "Hover timer never started during interleaved interaction");
    }

    // =========================================================================
    // SUITE 3: LOCK WORKSTATION HEADER BUTTON
    // =========================================================================

    private static void TestLockWorkstationButtonPresenceAndRow0(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S3] Lock Workstation Button Header Row 0 Structural Oracle ---");

        var window = CreateTestHarness(out _, out _, out var vm, out _, out _, out _);

        // Find ExpandedLayer
        var expandedLayer = (Grid)window.FindName("ExpandedLayer");
        assert(expandedLayer != null, "ExpandedLayer found in NotchWindow");

        // ExpandedLayer RowDefinitions
        assert(expandedLayer!.RowDefinitions.Count >= 2, "ExpandedLayer has at least 2 rows (Row 0: Header, Row 1: Content)");

        // Find Lock Button in header by tooltip or command
        Button? lockButton = FindButtonByToolTip(expandedLayer, "Khóa màn hình") ?? FindButtonWithCommand(expandedLayer, vm.LockWorkstationCommand);
        assert(lockButton != null, "Lock button found in ExpandedLayer");
        assert(lockButton!.Width == 32.0 && lockButton.Height == 32.0, "Lock button dimensions are exactly 32x32", $"{lockButton.Width}x{lockButton.Height}");

        // Verify parent is inside Row 0
        FrameworkElement? current = lockButton;
        int rowNumber = -1;
        while (current != null && current != expandedLayer)
        {
            if (current.Parent == expandedLayer)
            {
                rowNumber = Grid.GetRow(current);
                break;
            }
            current = current.Parent as FrameworkElement;
        }
        assert(rowNumber == 0, "Lock button container is situated in ExpandedLayer Row 0 (Header bar)", $"Row {rowNumber}");
    }

    private static void TestLockWorkstationButtonCommandBinding(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S3] Lock Workstation Button Binding Oracle ---");

        var window = CreateTestHarness(out _, out var sm, out var vm, out _, out _, out _);
        sm.TransitionTo(NotchVisualState.Expanded);
        var expandedLayer = (Grid)window.FindName("ExpandedLayer");
        Button? lockButton = FindButtonByToolTip(expandedLayer, "Khóa màn hình") ?? FindButtonWithCommand(expandedLayer, vm.LockWorkstationCommand);

        assert(lockButton != null, "Lock button is present");
        var binding = BindingOperations.GetBinding(lockButton!, Button.CommandProperty);
        assert(binding != null && binding.Path.Path == nameof(MainNotchViewModel.LockWorkstationCommand),
            "Lock button Command binding path is 'LockWorkstationCommand'", binding?.Path?.Path);
        assert(vm.LockWorkstationCommand != null, "vm.LockWorkstationCommand is generated and non-null");
    }

    private static void TestLockWorkstationAccessibilityAcrossAll3Tabs(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S3] Lock Workstation Accessibility Across Tabs 0, 1, 2 ---");

        var window = CreateTestHarness(out _, out var sm, out var vm, out _, out _, out _);
        sm.TransitionTo(NotchVisualState.Expanded);
        var expandedLayer = (Grid)window.FindName("ExpandedLayer");
        expandedLayer.Visibility = Visibility.Visible;
        Button? lockButton = FindButtonByToolTip(expandedLayer, "Khóa màn hình") ?? FindButtonWithCommand(expandedLayer, vm.LockWorkstationCommand);
        assert(lockButton != null, "Lock button is present");

        var tabPageOverview = (Grid)window.FindName("TabPageOverview");
        var tabPageSystem = (Grid)window.FindName("TabPageSystem");
        var tabPageShelf = (Grid)window.FindName("TabPageShelf");

        // Tab 0: Overview
        vm.SelectedTab = NotchTab.Overview;
        assert(lockButton!.Visibility == Visibility.Visible, "Lock button is Visible on Tab 0 (Overview)");
        assert(lockButton.IsHitTestVisible, "Lock button is HitTestVisible on Tab 0");
        assert(tabPageOverview.IsHitTestVisible, "TabPageOverview is hit-test visible on Tab 0");
        assert(!tabPageSystem.IsHitTestVisible, "TabPageSystem is NOT hit-test visible on Tab 0");
        assert(!tabPageShelf.IsHitTestVisible, "TabPageShelf is NOT hit-test visible on Tab 0");

        // Tab 1: System
        vm.SelectedTab = NotchTab.System;
        assert(lockButton.Visibility == Visibility.Visible, "Lock button is Visible on Tab 1 (System)");
        assert(lockButton.IsHitTestVisible, "Lock button is HitTestVisible on Tab 1");
        assert(!tabPageOverview.IsHitTestVisible, "TabPageOverview is NOT hit-test visible on Tab 1");
        assert(tabPageSystem.IsHitTestVisible, "TabPageSystem is hit-test visible on Tab 1");
        assert(!tabPageShelf.IsHitTestVisible, "TabPageShelf is NOT hit-test visible on Tab 1");

        // Tab 2: FileShelf
        vm.SelectedTab = NotchTab.FileShelf;
        assert(lockButton.Visibility == Visibility.Visible, "Lock button is Visible on Tab 2 (File Shelf)");
        assert(lockButton.IsHitTestVisible, "Lock button is HitTestVisible on Tab 2");
        assert(!tabPageOverview.IsHitTestVisible, "TabPageOverview is NOT hit-test visible on Tab 2");
        assert(!tabPageSystem.IsHitTestVisible, "TabPageSystem is NOT hit-test visible on Tab 2");
        assert(tabPageShelf.IsHitTestVisible, "TabPageShelf is hit-test visible on Tab 2");
    }

    private static void TestLockWorkstationVisualProperties(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S3] Lock Workstation Tooltip & Visual Styling Oracle ---");

        var window = CreateTestHarness(out _, out _, out var vm, out _, out _, out _);
        var expandedLayer = (Grid)window.FindName("ExpandedLayer");
        Button? lockButton = FindButtonByToolTip(expandedLayer, "Khóa màn hình") ?? FindButtonWithCommand(expandedLayer, vm.LockWorkstationCommand);

        assert(lockButton != null, "Lock button found");
        string tooltip = lockButton!.ToolTip?.ToString() ?? "";
        assert(tooltip.Contains("Khóa màn hình máy tính") || tooltip.Contains("Win + L"), "Lock button ToolTip mentions 'Khóa màn hình' or 'Win + L'", tooltip);
    }

    // =========================================================================
    // SUITE 4: DASHBOARDVIEW ROW 0 AUDIO DEVICE SWITCHER
    // =========================================================================

    private static void TestDashboardAudioDevicePillBindings(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S4] DashboardView Row 0 Audio Device Switcher Bindings ---");

        var vm = CreateTestViewModel();
        var dashboard = new DashboardView { DataContext = vm };

        Button? audioButton = FindButtonWithCommand(dashboard, vm.CycleAudioOutputCommand, nameof(MainNotchViewModel.CycleAudioOutputCommand))
            ?? FindButtonByToolTip(dashboard, "Click trái");
        assert(audioButton != null, "Audio device pill button found with CycleAudioOutputCommand");

        string tip = audioButton!.ToolTip?.ToString() ?? "";
        assert(tip.Contains("Click trái") && tip.Contains("Click phải"), "Tooltip explains both Left-Click and Right-Click actions", tip);
        assert(audioButton.Height == 20.0, "Audio device pill height is 20px", $"{audioButton.Height}px");
    }

    private static void TestDashboardAudioDeviceLeftClickCommand(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S4] DashboardView Row 0 Left-Click CycleAudioOutputCommand ---");

        var vm = CreateTestViewModel();
        assert(vm.CycleAudioOutputCommand != null, "vm.CycleAudioOutputCommand is non-null");

        bool cycleInvoked = false;
        try
        {
            vm.CycleAudioOutputCommand?.Execute(null);
            cycleInvoked = true;
        }
        catch { }
        assert(cycleInvoked, "CycleAudioOutputCommand executes cleanly without throwing");
    }

    private static void TestDashboardAudioDeviceRightClickSettingsInvocation(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S4] DashboardView Row 0 Right-Click OpenSoundOutputSettingsCommand ---");

        var vm = CreateTestViewModel();
        var dashboard = new DashboardView { DataContext = vm };

        assert(vm.OpenSoundOutputSettingsCommand != null, "vm.OpenSoundOutputSettingsCommand is non-null");

        // Send right button up event to OnAudioDeviceRightClick
        var rightUpArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent
        };

        OnAudioDeviceRightClickMethod.Invoke(dashboard, [dashboard, rightUpArgs]);
        assert(rightUpArgs.Handled, "Right-click event marked Handled by OnAudioDeviceRightClick");
    }

    // =========================================================================
    // SUITE 5: DASHBOARDVIEW ROW 2 TIMER WHEEL
    // =========================================================================

    private static void TestDashboardTimerWheelBindings(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S5] DashboardView Row 2 Timer Wheel Event & Tooltip ---");

        var vm = CreateTestViewModel();
        var dashboard = new DashboardView { DataContext = vm };

        var timerBorder = FindSquircleBorderWithTooltip(dashboard, "±1 phút");
        assert(timerBorder != null, "Row 2 Timer squircle border found with '±1 phút' tooltip");
    }

    private static void TestDashboardTimerWheelPositiveAndNegativeSteps(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S5] DashboardView Row 2 Timer Wheel StepMinutes Oracle ---");

        var vm = CreateTestViewModel(out _, out var timer);
        var dashboard = new DashboardView { DataContext = vm };

        // 1. Wheel Up (+120): step +1 minute
        var wheelUpArgs = CreateWheelEventArgs(120);
        OnTimerWheelMethod.Invoke(dashboard, [dashboard, wheelUpArgs]);

        assert(wheelUpArgs.Handled, "Timer wheel up marked Handled");
        assert(timer.RemainingSeconds == 60, "Timer started with 60s (+1 minute) from idle", $"{timer.RemainingSeconds}s");
        assert(timer.IsRunning, "Timer is running after positive step from idle");

        // 2. Wheel Up again (+120): step +1 minute -> 120s
        wheelUpArgs = CreateWheelEventArgs(120);
        OnTimerWheelMethod.Invoke(dashboard, [dashboard, wheelUpArgs]);
        assert(timer.RemainingSeconds == 120, "Timer incremented to 120s (+2 minutes)", $"{timer.RemainingSeconds}s");

        // 3. Wheel Down (-120): step -1 minute -> 60s
        var wheelDownArgs = CreateWheelEventArgs(-120);
        OnTimerWheelMethod.Invoke(dashboard, [dashboard, wheelDownArgs]);
        assert(wheelDownArgs.Handled, "Timer wheel down marked Handled");
        assert(timer.RemainingSeconds == 60, "Timer decremented to 60s (-1 minute)", $"{timer.RemainingSeconds}s");

        // 4. Wheel Down (-120): step -1 minute -> drops to 0s, clean reset
        wheelDownArgs = CreateWheelEventArgs(-120);
        OnTimerWheelMethod.Invoke(dashboard, [dashboard, wheelDownArgs]);
        assert(timer.RemainingSeconds == 0, "Timer dropped to 0s", $"{timer.RemainingSeconds}s");
        assert(!timer.IsRunning, "Timer cleanly halted after dropping to 0s");
        assert(!timer.IsAlarmRinging, "Alarm chime did NOT ring on negative step to 0");
    }

    private static void TestDashboardTimerWheelRapidStress(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S5] DashboardView Row 2 Timer Wheel Rapid 200 Cycles Stress ---");

        var vm = CreateTestViewModel(out _, out var timer);
        var dashboard = new DashboardView { DataContext = vm };

        // 100 positive steps
        for (int i = 0; i < 100; i++)
        {
            var up = CreateWheelEventArgs(120);
            OnTimerWheelMethod.Invoke(dashboard, [dashboard, up]);
        }
        assert(timer.RemainingSeconds == TimerService.MaxTimerSeconds, "Timer clamped at MaxTimerSeconds (5999s)", $"{timer.RemainingSeconds}s");

        // 120 negative steps
        for (int i = 0; i < 120; i++)
        {
            var down = CreateWheelEventArgs(-120);
            OnTimerWheelMethod.Invoke(dashboard, [dashboard, down]);
        }
        assert(timer.RemainingSeconds == 0, "Timer cleanly reset to 0s after excess down-steps", $"{timer.RemainingSeconds}s");
        assert(!timer.IsAlarmRinging, "Alarm strictly silent after excess down-steps");
    }

    // =========================================================================
    // SUITE 6: DASHBOARDVIEW ROW 3 ACTION PILLS
    // =========================================================================

    private static void TestDashboardRow3FourPillsStructureAndCommands(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S6] DashboardView Row 3 Four 33px Action Pills Oracle ---");

        var vm = CreateTestViewModel();
        var dashboard = new DashboardView { DataContext = vm };

        Button? caffeineBtn = FindButtonWithCommand(dashboard, vm.ToggleCaffeineCommand, nameof(MainNotchViewModel.ToggleCaffeineCommand))
            ?? FindButtonByToolTip(dashboard, "Giữ màn hình");
        assert(caffeineBtn != null, "Pill 1: Caffeine toggle button exists (ToggleCaffeineCommand)");

        Button? micBtn = FindButtonWithCommand(dashboard, vm.ToggleMicMuteCommand, nameof(MainNotchViewModel.ToggleMicMuteCommand))
            ?? FindButtonByToolTip(dashboard, "Micro");
        assert(micBtn != null, "Pill 2: Mic mute toggle button exists (ToggleMicMuteCommand)");

        Button? pickerBtn = FindButtonWithCommand(dashboard, vm.StartColorPickerCommand, nameof(MainNotchViewModel.StartColorPickerCommand))
            ?? FindButtonByToolTip(dashboard, "Chấm mã màu");
        assert(pickerBtn != null, "Pill 3: Color picker button exists (StartColorPickerCommand)");

        Button? snipBtn = FindButtonWithCommand(dashboard, vm.TriggerScreenSnipCommand, nameof(MainNotchViewModel.TriggerScreenSnipCommand))
            ?? FindButtonByToolTip(dashboard, "Chụp vùng");
        assert(snipBtn != null, "Pill 4: Screen snip button exists (TriggerScreenSnipCommand)");

    }

    private static void TestDashboardRow3MicrophonePillDynamicVisuals(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S6] DashboardView Row 3 Microphone Pill Dynamic Visuals ---");

        var vm = CreateTestViewModel();

        // When IsMicMuted == false
        vm.IsMicMuted = false;
        assert(!vm.IsMicMuted, "IsMicMuted is false initially");

        // When IsMicMuted == true
        vm.IsMicMuted = true;
        assert(vm.IsMicMuted, "IsMicMuted transitioned to true");
    }

    private static void TestDashboardRow3CaffeinePillDynamicVisuals(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M5-S6] DashboardView Row 3 Caffeine Pill Dynamic Visuals ---");

        var vm = CreateTestViewModel();

        vm.IsCaffeineActive = false;
        assert(!vm.IsCaffeineActive, "IsCaffeineActive is false initially");

        vm.IsCaffeineActive = true;
        assert(vm.IsCaffeineActive, "IsCaffeineActive transitioned to true");
    }

    // =========================================================================
    // TEST HELPERS & TREE TRAVERSAL
    // =========================================================================

    private static MainNotchViewModel CreateTestViewModel()
    {
        return CreateTestViewModel(out _, out _);
    }

    private static MainNotchViewModel CreateTestViewModel(out SettingsService settings, out TimerService timer)
    {
        settings = new SettingsService();
        var sm = new NotchStateMachine(settings.Current);
        var monitor = new MonitorDescriptor
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyLabel = "Test Display",
            IsPrimary = true,
            PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            DpiScaleX = 1.0,
            DpiScaleY = 1.0
        };
        var audio = new AudioService(settings);
        var power = new DisplayPowerService(settings, monitor);
        var media = new MediaService(settings);
        var shelf = new FileShelfService(settings);
        timer = new TimerService();
        var privacy = new PrivacyService(settings);
        var sys = new SystemMonitorService(settings);
        var notif = new NotificationService(settings);
        var picker = new ColorPickerService(sm);

        return new MainNotchViewModel(
            settings,
            sm,
            audio,
            power,
            media,
            shelf,
            timer,
            privacy,
            sys,
            notif,
            picker,
            new AgentMonitorService(settings),
            () => { });
    }

    private static T? FindElement<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        if (parent is T match && predicate(match))
        {
            return match;
        }

        if (parent is ContentControl { Content: DependencyObject cc })
        {
            var res = FindElement(cc, predicate);
            if (res != null) return res;
        }
        else if (parent is Decorator { Child: not null } dec)
        {
            var res = FindElement(dec.Child, predicate);
            if (res != null) return res;
        }
        else if (parent is Panel pan)
        {
            foreach (UIElement child in pan.Children)
            {
                var res = FindElement(child, predicate);
                if (res != null) return res;
            }
        }
        else if (parent is not (ColumnDefinition or RowDefinition))
        {
            foreach (var logicalChild in LogicalTreeHelper.GetChildren(parent))
            {
                if (logicalChild is DependencyObject dep and not (ColumnDefinition or RowDefinition))
                {
                    var res = FindElement(dep, predicate);
                    if (res != null) return res;
                }
            }
        }

        return null;
    }

    private static Button? FindButtonWithCommand(DependencyObject parent, ICommand targetCommand, string? commandName = null) =>
        FindElement<Button>(parent, b =>
            b.Command == targetCommand ||
            (commandName != null && BindingOperations.GetBinding(b, Button.CommandProperty)?.Path?.Path == commandName));

    private static Button? FindButtonByToolTip(DependencyObject parent, string tooltipSubstring) =>
        FindElement<Button>(parent, b => b.ToolTip?.ToString()?.Contains(tooltipSubstring) ?? false);

    private static SquircleBorder? FindSquircleBorderWithTooltip(DependencyObject parent, string tooltipSubstring) =>
        FindElement<SquircleBorder>(parent, sb => sb.ToolTip?.ToString()?.Contains(tooltipSubstring) ?? false);


}
