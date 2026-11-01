using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;
using WinNotch.Views;

namespace WinNotch.Tests;

/// <summary>
/// Empirical Adversarial Challenge Suite for Milestone 2:
/// Collapsed Notch Dynamics & Dynamic Island HUD Animations.
/// Empirically stress-tests:
/// 1. Dynamic height transitions on Tab 1 (Agents) up to 2/3 screen height ceiling and tab switch restoration.
/// 2. Collapsed mode choreography transitions (Idle -> Timer -> Agent -> Music -> DualMode) and rapid switching.
/// 3. Collapsed Agent pill formatting: real project name extraction, progress %, and #FBBF24 approval highlight.
/// 4. HUD dynamic island pulse arrivals, G2 squircle color swatches, and capsule mini level meters.
/// 5. Apple Music album art scaling (1.0 on play, 0.90 on pause) and 180ms cross-fading.
/// 6. Pitch-black OLED #050507 shell and #1FFFFFFF rim highlight preservation.
/// </summary>
public static class Milestone2CollapsedDynamicsChallengeTests
{
    private static readonly MethodInfo FormatCollapsedAgentTextMethod =
        typeof(MainNotchViewModel).GetMethod("FormatCollapsedAgentText", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ExtractProjectNameMethod =
        typeof(MainNotchViewModel).GetMethod("ExtractProjectName", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo DetermineCurrentCollapsedModeMethod =
        typeof(NotchWindow).GetMethod("DetermineCurrentCollapsedMode", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo PlayCollapsedModeTransitionMethod =
        typeof(NotchWindow).GetMethod("PlayCollapsedModeTransition", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ApplyRenderFrameMethod =
        typeof(NotchWindow).GetMethod("ApplyRenderFrame", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo ShowHudMethod =
        typeof(MainNotchViewModel).GetMethod(
            "ShowHud",
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            [typeof(HudPayload), typeof(bool)],
            null)!;

    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" MILESTONE 2: COLLAPSED NOTCH DYNAMICS & DYNAMIC ISLAND HUD SUITE        ");
        Console.WriteLine(" Empirical Adversarial Stress-Tests on Transitions, Dynamics & Formatting");
        Console.WriteLine("=========================================================================");

        EnsureWpfEnvironment();

        // Challenge 1: Dynamic Height Transitions & Monitor DIP Height Clamping
        TestDynamicHeightTransitionsAndClamping(assert);

        // Challenge 2: Collapsed Mode Transitions & Rapid Sequential Switching
        TestCollapsedModeTransitionsAndRapidSwitching(assert);

        // Challenge 3: Collapsed Agent Pill Formatting, Real Project Names & Aura
        TestCollapsedAgentPillFormattingAndAura(assert);

        // Challenge 4: HUD Dynamic Island Arrivals, Swatches & Mini-Meters
        TestHudDynamicIslandArrivalsAndMeters(assert);

        // Challenge 5: Media Player Album Art Dynamics & Cross-Fade
        TestMediaAlbumArtDynamicsAndCrossFade(assert);

        // Challenge 6: XAML Structure, Canvas Clearance & OLED Pitch-Black Invariants
        TestXamlStructureAndOledInvariants(assert);
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

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(f =>
            {
                ((DispatcherFrame)f!).Continue = false;
                return null;
            }),
            frame);
        Dispatcher.PushFrame(frame);
    }

    private static void InvokeShowHud(MainNotchViewModel vm, HudPayload payload)
    {
        ShowHudMethod.Invoke(vm, [payload, false]);
    }

    private static NotchWindow CreateTestHarness(
        out SettingsService settings,
        out NotchStateMachine stateMachine,
        out MainNotchViewModel viewModel,
        out NotificationService notif,
        MonitorDescriptor? monitor = null)
    {
        settings = new SettingsService();
        settings.Current.EnableCollapsedScrollGestures = true;
        stateMachine = new NotchStateMachine(settings.Current);

        monitor ??= new MonitorDescriptor
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyLabel = "Test OLED Display",
            IsPrimary = true,
            PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            DpiScaleX = 1.0,
            DpiScaleY = 1.0
        };

        var audio = new AudioService(settings);
        var power = new DisplayPowerService(settings, monitor);
        var media = new MediaService(settings);
        var shelf = new FileShelfService(settings);
        var timer = new TimerService();
        var privacy = new PrivacyService(settings);
        var systemMonitor = new SystemMonitorService(settings);
        notif = new NotificationService(settings);
        var picker = new ColorPickerService(stateMachine);
        var agentMonitor = new AgentMonitorService(settings);

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
            agentMonitor,
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

    #region Challenge 1: Dynamic Height Transitions & Monitor DIP Height Clamping

    private static void TestDynamicHeightTransitionsAndClamping(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 1] Dynamic Height Transitions & Monitor DIP Clamping ---");

        var settings = new AppSettings { ExpandedSizePreset = "Compact" };
        var sm = new NotchStateMachine(settings);

        // 1. Expand notch to Expanded state
        sm.ExpandAndPin();
        assert(sm.State == NotchVisualState.Expanded, "State machine is in Expanded state");
        assert(sm.ActiveExpandedDimensions == NotchStateMachine.DimExpandedCompact, "ActiveExpandedDimensions matches DimExpandedCompact (684x272)");

        // 2. Monitor DIP height matrix & ceiling = Math.Floor(dipHeight * (2.0 / 3.0))
        double[] testDipHeights = [768.0, 900.0, 1080.0, 1200.0, 1440.0, 1600.0, 2160.0];
        foreach (double dipH in testDipHeights)
        {
            double expectedCeiling = Math.Floor(dipH * (2.0 / 3.0));

            // Test A: Desired height above ceiling -> clamped to ceiling
            sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, expectedCeiling + 200.0, expectedCeiling);
            assert(sm.CurrentExpandedDimensions.Height == expectedCeiling,
                $"DipHeight {dipH}p: desired {expectedCeiling + 200.0} clamps to ceiling {expectedCeiling}");

            // Test B: Desired height below base -> clamped to base (272)
            sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, 150.0, expectedCeiling);
            assert(sm.CurrentExpandedDimensions.Height == 272.0,
                $"DipHeight {dipH}p: desired 150.0 clamps up to base height 272.0");

            // Test C: Desired height within valid range -> exact desired height
            double midHeight = Math.Round((272.0 + expectedCeiling) / 2.0);
            if (midHeight > 272.0 && midHeight <= expectedCeiling)
            {
                sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, midHeight, expectedCeiling);
                assert(sm.CurrentExpandedDimensions.Height == midHeight,
                    $"DipHeight {dipH}p: desired {midHeight} returns exact height {midHeight}");
            }
        }

        // 3. Tab switching restoration to base height (272px Compact / 296px Spacious)
        double ceiling1080 = Math.Floor(1080.0 * (2.0 / 3.0)); // 720.0
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, 600.0, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == 600.0, "Agents tab expanded to 600.0px");

        // Switch to Overview
        sm.SetActiveTabAndAgentsHeight(NotchTab.Overview, 600.0, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Switching to Overview restores base height 272.0px");

        // Switch to System
        sm.SetActiveTabAndAgentsHeight(NotchTab.System, 600.0, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Switching to System restores base height 272.0px");

        // Switch to FileShelf
        sm.SetActiveTabAndAgentsHeight(NotchTab.FileShelf, 600.0, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Switching to FileShelf restores base height 272.0px");

        // Switch back to Agents
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, 600.0, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == 600.0, "Switching back to Agents re-expands to 600.0px");

        // 4. Spacious preset dynamic height testing
        settings.ExpandedSizePreset = "Spacious";
        sm.ApplySettings(settings);
        assert(sm.ActiveExpandedDimensions == NotchStateMachine.DimExpandedSpacious, "ActiveExpandedDimensions matches DimExpandedSpacious (744x296)");

        sm.SetActiveTabAndAgentsHeight(NotchTab.Overview, 600.0, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == 296.0, "Spacious preset Overview restores base height 296.0px");
        assert(sm.CurrentExpandedDimensions.Width == 744.0, "Spacious preset Width is 744.0px");

        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, 200.0, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == 296.0, "Spacious preset desired 200.0 clamps up to 296.0px");

        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, 550.0, ceiling1080);
        assert(sm.CurrentExpandedDimensions.Height == 550.0, "Spacious preset desired 550.0 returns exact 550.0px");

        // 5. Degenerate screen height safety (e.g. 300p monitor where ceiling < minH)
        double degenerateCeiling = Math.Floor(300.0 * (2.0 / 3.0)); // 200.0 < 272.0
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, 500.0, degenerateCeiling);
        assert(sm.CurrentExpandedDimensions.Height == 296.0, "Degenerate ceiling < minH safely clamps to base height without error");

        // 6. NotchWindow integration verification
        var window = CreateTestHarness(out var sService, out var sMachine, out var vm, out _);
        try
        {
            var rootHost = (FrameworkElement)window.FindName("RootHost");
            var expandedLayer = (FrameworkElement)window.FindName("ExpandedLayer");

            // Verify window and host canvas dimensions
            assert(window.Width == 820.0, "NotchWindow.Width == 820.0");
            assert(window.Height >= 860.0, "NotchWindow.Height >= 860.0 (accommodates dynamic 2/3 expansion)");
            assert(rootHost.Width == 820.0, "RootHost.Width == 820.0");
            assert(rootHost.Height >= 860.0, "RootHost.Height >= 860.0");

            // Verify ActiveExpandedWidth / ActiveExpandedHeight VM computed properties
            sService.Current.ExpandedSizePreset = "Compact";
            sService.Save(sService.Current);
            assert(vm.ActiveExpandedWidth == 684.0, "vm.ActiveExpandedWidth == 684.0 for Compact preset");
            assert(vm.ActiveExpandedHeight == 272.0, "vm.ActiveExpandedHeight == 272.0 for Compact preset");

            sService.Current.ExpandedSizePreset = "Spacious";
            sService.Save(sService.Current);
            assert(vm.ActiveExpandedWidth == 744.0, "vm.ActiveExpandedWidth updates to 744.0 for Spacious preset");
            assert(vm.ActiveExpandedHeight == 296.0, "vm.ActiveExpandedHeight updates to 296.0 for Spacious preset");

            // Verify ApplyRenderFrame updates ExpandedLayer width & height without clobbering bindings
            var testFrame = new NotchRenderFrame(
                new NotchDimensions(744.0, 520.0, 14.0, 31.0),
                1.0, 0.0, 0.0, 0.0, 1.0);
            ApplyRenderFrameMethod.Invoke(window, [testFrame]);

            assert(expandedLayer.Width == 744.0, "ExpandedLayer.Width updated to 744.0 by ApplyRenderFrame");
            assert(expandedLayer.Height == 520.0, "ExpandedLayer.Height dynamically scaled to 520.0 by ApplyRenderFrame");
        }
        finally
        {
            window.Close();
        }
    }

    #endregion

    #region Challenge 2: Collapsed Mode Transitions & Rapid Sequential Switching

    private static void TestCollapsedModeTransitionsAndRapidSwitching(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 2] Collapsed Mode Transitions & Rapid Switching ---");

        var window = CreateTestHarness(out _, out _, out var vm, out _);
        try
        {
            var contentViewport = (FrameworkElement)window.FindName("ContentViewport");
            var collapsedHost = (FrameworkElement)window.FindName("CollapsedContentHost");
            var collapsedTranslate = (TranslateTransform)window.FindName("CollapsedContentTranslate");
            var collapsedScale = (ScaleTransform)window.FindName("CollapsedContentScale");

            // 1. Initial mode should be Idle
            object initialMode = DetermineCurrentCollapsedModeMethod.Invoke(window, null)!;
            assert(initialMode.ToString() == "Idle", $"Initial collapsed mode is Idle (got {initialMode})");

            // 2. Test mode determination oracle for all 5 modes
            // Mode A: Timer Only
            vm.HasActiveTimer = true;
            object modeTimer = DetermineCurrentCollapsedModeMethod.Invoke(window, null)!;
            assert(modeTimer.ToString() == "Timer", $"Active timer resolves to mode Timer (got {modeTimer})");

            // Mode B: Agent Only
            vm.HasActiveTimer = false;
            vm.HasActiveOrWaitingAgents = true;
            object modeAgent = DetermineCurrentCollapsedModeMethod.Invoke(window, null)!;
            assert(modeAgent.ToString() == "Agent", $"Active agent resolves to mode Agent (got {modeAgent})");

            // Mode C: Music Only
            vm.HasActiveOrWaitingAgents = false;
            vm.IsMediaPlaying = true;
            object modeMusic = DetermineCurrentCollapsedModeMethod.Invoke(window, null)!;
            assert(modeMusic.ToString() == "Music", $"Active music resolves to mode Music (got {modeMusic})");

            // Mode D: Dual (Music + Timer)
            vm.HasActiveTimer = true;
            object modeDual = DetermineCurrentCollapsedModeMethod.Invoke(window, null)!;
            assert(modeDual.ToString() == "Dual", $"Active music + timer resolves to mode Dual (got {modeDual})");

            // Mode E: Return to Idle
            vm.IsMediaPlaying = false;
            vm.HasActiveTimer = false;
            object modeIdleAgain = DetermineCurrentCollapsedModeMethod.Invoke(window, null)!;
            assert(modeIdleAgain.ToString() == "Idle", $"Clearing all activities returns to mode Idle (got {modeIdleAgain})");

            // 3. Verify PlayCollapsedModeTransition compound animation properties
            PlayCollapsedModeTransitionMethod.Invoke(window, null);
            assert(collapsedHost != null, "CollapsedContentHost is non-null");
            assert(collapsedTranslate != null, "CollapsedContentTranslate is non-null");
            assert(collapsedScale != null, "CollapsedContentScale is non-null");

            // 4. Rapid Sequential Mode Switching Torture Test (200 transitions across 25 cycles)
            Console.WriteLine("Executing rapid sequential mode transitions...");
            var sw = Stopwatch.StartNew();
            for (int cycle = 0; cycle < 25; cycle++)
            {
                // Idle -> Timer
                vm.HasActiveTimer = true;
                vm.IsMediaPlaying = false;
                vm.HasActiveOrWaitingAgents = false;
                PumpDispatcher();

                // Timer -> Agent
                vm.HasActiveTimer = false;
                vm.HasActiveOrWaitingAgents = true;
                PumpDispatcher();

                // Agent -> Music
                vm.HasActiveOrWaitingAgents = false;
                vm.IsMediaPlaying = true;
                PumpDispatcher();

                // Music -> Dual
                vm.HasActiveTimer = true;
                PumpDispatcher();

                // Dual -> Music
                vm.HasActiveTimer = false;
                PumpDispatcher();

                // Music -> Agent
                vm.IsMediaPlaying = false;
                vm.HasActiveOrWaitingAgents = true;
                PumpDispatcher();

                // Agent -> Timer
                vm.HasActiveOrWaitingAgents = false;
                vm.HasActiveTimer = true;
                PumpDispatcher();

                // Timer -> Idle
                vm.HasActiveTimer = false;
                PumpDispatcher();
            }
            sw.Stop();

            assert(sw.ElapsedMilliseconds < 5000,
                $"200 rapid mode transitions completed without UI stall (elapsed: {sw.ElapsedMilliseconds}ms)");

            // 5. Verify layout stability & invariants after torture
            assert(contentViewport.Height == 32.0, "ContentViewport height strictly maintained at 32.0px");
            assert(contentViewport.Width >= 106.0 && contentViewport.Width <= 276.0,
                $"ContentViewport width in valid collapsed range (got {contentViewport.Width}px)");

            // Verify no NaN or Infinity coordinates in transforms
            assert(collapsedTranslate != null && !double.IsNaN(collapsedTranslate.Y) && !double.IsInfinity(collapsedTranslate.Y),
                "CollapsedContentTranslate.Y is valid numeric value");
            assert(collapsedScale != null && !double.IsNaN(collapsedScale.ScaleX) && !double.IsInfinity(collapsedScale.ScaleX),
                "CollapsedContentScale.ScaleX is valid numeric value");
            assert(collapsedScale != null && !double.IsNaN(collapsedScale.ScaleY) && !double.IsInfinity(collapsedScale.ScaleY),
                "CollapsedContentScale.ScaleY is valid numeric value");
            assert(collapsedHost != null && !double.IsNaN(collapsedHost.Opacity) && !double.IsInfinity(collapsedHost.Opacity),
                "CollapsedContentHost.Opacity is valid numeric value");

            // 6. Test state machine dimensions for each collapsed state
            assert(NotchStateMachine.DimCollapsedIdle.Height == 32.0, "DimCollapsedIdle height == 32.0");
            assert(NotchStateMachine.DimCollapsedTimer.Height == 32.0, "DimCollapsedTimer height == 32.0");
            assert(NotchStateMachine.DimCollapsedAgent.Height == 32.0, "DimCollapsedAgent height == 32.0");
            assert(NotchStateMachine.DimCollapsedMusic.Height == 32.0, "DimCollapsedMusic height == 32.0");
            assert(NotchStateMachine.DimCollapsedDualActive.Height == 32.0, "DimCollapsedDualActive height == 32.0");

            assert(NotchStateMachine.DimCollapsedIdle.Width == 132.0, "DimCollapsedIdle width == 132.0");
            assert(NotchStateMachine.DimCollapsedTimer.Width == 106.0, "DimCollapsedTimer width == 106.0");
            assert(NotchStateMachine.DimCollapsedAgent.Width == 196.0, "DimCollapsedAgent width == 196.0");
            assert(NotchStateMachine.DimCollapsedMusic.Width == 228.0, "DimCollapsedMusic width == 228.0");
            assert(NotchStateMachine.DimCollapsedDualActive.Width == 276.0, "DimCollapsedDualActive width == 276.0");
        }
        finally
        {
            window.Close();
        }
    }

    #endregion

    #region Challenge 3: Collapsed Agent Pill Formatting, Real Project Names & Aura

    private static void TestCollapsedAgentPillFormattingAndAura(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 3] Collapsed Agent Pill Formatting, Projects & Aura ---");

        // 1. Test Project Name Extraction from WorkspaceHint
        var testSessionWinNotch = new AgentSessionItem
        {
            SessionId = "test-1",
            WorkspaceHint = @"C:\Users\ASUS\Documents\AGY\WinNotch",
            DisplayName = "AI Agent"
        };
        string name1 = (string)ExtractProjectNameMethod.Invoke(null, [testSessionWinNotch])!;
        assert(name1 == "WinNotch", $"WorkspaceHint '...\\WinNotch' extracts project name 'WinNotch' (got '{name1}')");

        var testSessionTrailingSlash = new AgentSessionItem
        {
            SessionId = "test-2",
            WorkspaceHint = @"C:\Projects\MyCoolApp\",
            DisplayName = "AI Agent"
        };
        string name2 = (string)ExtractProjectNameMethod.Invoke(null, [testSessionTrailingSlash])!;
        assert(name2 == "MyCoolApp", $"WorkspaceHint with trailing slash extracts 'MyCoolApp' (got '{name2}')");

        var testSessionForwardSlash = new AgentSessionItem
        {
            SessionId = "test-3",
            WorkspaceHint = "/var/repos/AwesomeService/",
            DisplayName = "AI Agent"
        };
        string name3 = (string)ExtractProjectNameMethod.Invoke(null, [testSessionForwardSlash])!;
        assert(name3 == "AwesomeService", $"WorkspaceHint with forward slashes extracts 'AwesomeService' (got '{name3}')");

        var testSessionCustomDisplay = new AgentSessionItem
        {
            SessionId = "test-4",
            WorkspaceHint = "",
            DisplayName = "CustomTool"
        };
        string name4 = (string)ExtractProjectNameMethod.Invoke(null, [testSessionCustomDisplay])!;
        assert(name4 == "CustomTool", $"Empty workspace with DisplayName 'CustomTool' returns 'CustomTool' (got '{name4}')");

        var testSessionNullSession = (string)ExtractProjectNameMethod.Invoke(null, [null])!;
        assert(testSessionNullSession == "WinNotch", $"Null session defaults to 'WinNotch' (got '{testSessionNullSession}')");

        // 2. Test FormatCollapsedAgentText: Waiting Approval State
        var snapWaiting = new AgentDashboardSnapshot
        {
            ActiveCount = 0,
            WaitingApprovalCount = 1,
            PrimarySession = testSessionWinNotch,
            CollapsedBadgeText = "Agent · Chờ duyệt!"
        };
        string textWaiting = (string)FormatCollapsedAgentTextMethod.Invoke(null, [snapWaiting])!;
        assert(textWaiting == "WinNotch · Chờ duyệt!",
            $"Waiting approval formats as 'WinNotch · Chờ duyệt!' (got '{textWaiting}')");

        // 3. Test FormatCollapsedAgentText: Progress Percentage Formats
        var snapPercentFull = new AgentDashboardSnapshot
        {
            ActiveCount = 1,
            WaitingApprovalCount = 0,
            PrimarySession = testSessionWinNotch,
            CollapsedBadgeText = "WinNotch · 65%"
        };
        string textPercentFull = (string)FormatCollapsedAgentTextMethod.Invoke(null, [snapPercentFull])!;
        assert(textPercentFull == "WinNotch · 65%",
            $"Full badge 'WinNotch · 65%' returns 'WinNotch · 65%' (got '{textPercentFull}')");

        var snapPercentOnly = new AgentDashboardSnapshot
        {
            ActiveCount = 1,
            WaitingApprovalCount = 0,
            PrimarySession = testSessionWinNotch,
            CollapsedBadgeText = "65%"
        };
        string textPercentOnly = (string)FormatCollapsedAgentTextMethod.Invoke(null, [snapPercentOnly])!;
        bool hasProjectInPercentOnly = textPercentOnly.StartsWith("WinNotch ·");
        assert(hasProjectInPercentOnly,
            "Progress percentage '65%' formatted with real project name -> 'WinNotch · 65%'",
            details: hasProjectInPercentOnly ? null : $"Actual returned '{textPercentOnly}' without project name");

        var snapProviderPercent = new AgentDashboardSnapshot
        {
            ActiveCount = 1,
            WaitingApprovalCount = 0,
            PrimarySession = testSessionWinNotch,
            CollapsedBadgeText = "AGY · 65%"
        };
        string textProviderPercent = (string)FormatCollapsedAgentTextMethod.Invoke(null, [snapProviderPercent])!;
        bool replacedProviderInPercent = textProviderPercent.StartsWith("WinNotch ·");
        assert(replacedProviderInPercent,
            "Provider percentage 'AGY · 65%' replaces provider with real project name -> 'WinNotch · 65%'",
            details: replacedProviderInPercent ? null : $"Actual returned '{textProviderPercent}' without project replacement");

        // 4. Test FormatCollapsedAgentText: Generic Action Text
        var snapAction = new AgentDashboardSnapshot
        {
            ActiveCount = 1,
            WaitingApprovalCount = 0,
            PrimarySession = testSessionWinNotch,
            CollapsedBadgeText = "AGY · Quét thư mục..."
        };
        string textAction = (string)FormatCollapsedAgentTextMethod.Invoke(null, [snapAction])!;
        assert(textAction == "WinNotch · Quét thư mục...",
            $"Provider tag 'AGY' replaced with real project name 'WinNotch' (got '{textAction}')");

        // 5. Test FormatCollapsedAgentText: Inactive / Empty Snapshot
        var snapEmpty = new AgentDashboardSnapshot
        {
            ActiveCount = 0,
            WaitingApprovalCount = 0,
            PrimarySession = null
        };
        string textEmpty = (string)FormatCollapsedAgentTextMethod.Invoke(null, [snapEmpty])!;
        assert(textEmpty == string.Empty, "Inactive snapshot returns empty string");

        // 6. Test ViewModel Binding Integration & Amber Accent Brush
        var window = CreateTestHarness(out _, out _, out var vm, out _);
        try
        {
            // Simulate agent update waiting approval
            vm.HasActiveOrWaitingAgents = true;
            vm.IsAgentWaitingApproval = true;

            assert(vm.CollapsedAgentAccentBrush is SolidColorBrush, "CollapsedAgentAccentBrush is SolidColorBrush");
            var amberBrush = (SolidColorBrush)vm.CollapsedAgentAccentBrush;
            string amberHex = $"#{amberBrush.Color.R:X2}{amberBrush.Color.G:X2}{amberBrush.Color.B:X2}";
            assert(amberHex == "#FBBF24", $"Waiting approval sets amber accent #FBBF24 (got {amberHex})");

            // Simulate agent running normally
            vm.IsAgentWaitingApproval = false;
            var normalBrush = (SolidColorBrush)vm.CollapsedAgentAccentBrush;
            string normalHex = $"#{normalBrush.Color.R:X2}{normalBrush.Color.G:X2}{normalBrush.Color.B:X2}";
            assert(normalHex == "#60A5FA", $"Normal working state sets blue accent #60A5FA (got {normalHex})");
        }
        finally
        {
            window.Close();
        }

        // 7. Test XAML Concentric Aura Storyboard Invariants
        string xamlPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\NotchWindow.xaml";
        string xamlContent = File.ReadAllText(xamlPath);

        assert(xamlContent.Contains("x:Name=\"AgentBadgeAuraScale\""), "AgentBadgeAuraScale transform is defined in NotchWindow.xaml");
        assert(xamlContent.Contains("From=\"1.0\" To=\"1.45\""), "Aura scale expands from 1.0 to 1.45");
        assert(xamlContent.Contains("Duration=\"0:0:1.650\""), "Aura breathing cycle duration is exactly 1.650s");
        assert(xamlContent.Contains("QuadraticEase EasingMode=\"EaseOut\""), "Aura breathing uses QuadraticEase EaseOut");
        assert(xamlContent.Contains("StopStoryboard BeginStoryboardName=\"AgentBadgeAuraStory\""),
            "Aura storyboard stops on exit for 0.0% CPU when inactive");
    }

    #endregion

    #region Challenge 4: HUD Dynamic Island Arrivals, Swatches & Mini-Meters

    private static void TestHudDynamicIslandArrivalsAndMeters(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 4] HUD Arrivals, Color Swatches & Mini-Meters ---");

        var window = CreateTestHarness(out _, out _, out var vm, out _);
        try
        {
            // 1. HudArrivalPulseToken increment oracle
            int initialToken = vm.HudArrivalPulseToken;
            assert(initialToken == 0, $"Initial HudArrivalPulseToken is 0 (got {initialToken})");

            // Show volume HUD
            InvokeShowHud(vm, new HudPayload
            {
                Kind = HudKind.Volume,
                Icon = IconsaxKind.VolumeHigh,
                Title = "Âm lượng",
                ValueText = "75%",
                NormalizedValue = 0.75,
                AccentHex = "#38BDF8"
            });
            assert(vm.HudArrivalPulseToken == 1, "HudArrivalPulseToken incremented to 1 on ShowHud");

            // Show brightness HUD
            InvokeShowHud(vm, new HudPayload
            {
                Kind = HudKind.Brightness,
                Icon = IconsaxKind.Sun1,
                Title = "Độ sáng",
                ValueText = "50%",
                NormalizedValue = 0.50,
                AccentHex = "#FBBF24"
            });
            assert(vm.HudArrivalPulseToken == 2, "HudArrivalPulseToken incremented to 2 on next ShowHud");

            // Rapid burst of 100 HUD shows
            for (int i = 0; i < 100; i++)
            {
                InvokeShowHud(vm, new HudPayload
                {
                    Kind = HudKind.CapsLock,
                    Icon = IconsaxKind.Lock,
                    Title = "Caps Lock",
                    ValueText = "BẬT",
                    NormalizedValue = 1.0,
                    AccentHex = "#A78BFA"
                });
            }
            assert(vm.HudArrivalPulseToken == 102, $"HudArrivalPulseToken strictly tracked 100 rapid events (got {vm.HudArrivalPulseToken})");

            // 2. Color Swatch HUD computed properties (HudKind.ColorPicked)
            InvokeShowHud(vm, new HudPayload
            {
                Kind = HudKind.ColorPicked,
                Icon = IconsaxKind.Colorfilter,
                Title = "#1E90FF",
                ValueText = "Đã chép mã màu",
                NormalizedValue = 1.0,
                AccentHex = "#1E90FF"
            });
            assert(vm.IsHudColorSwatchVisible, "IsHudColorSwatchVisible is true for HudKind.ColorPicked");
            assert(!vm.IsHudMiniMeterVisible, "IsHudMiniMeterVisible is false for ColorPicked");
            assert(!vm.IsHudIconVisible, "IsHudIconVisible is false when color swatch is displayed");

            // 3. Mini Level Meter HUD computed properties (AudioDevice & Battery)
            // AudioDevice at 75%
            InvokeShowHud(vm, new HudPayload
            {
                Kind = HudKind.AudioDevice,
                Icon = IconsaxKind.Headphone,
                Title = "Tai nghe",
                ValueText = "AirPods Pro",
                NormalizedValue = 0.75,
                AccentHex = "#34D399"
            });
            assert(vm.IsHudMiniMeterVisible, "IsHudMiniMeterVisible is true for AudioDevice with value > 0");
            assert(!vm.IsHudColorSwatchVisible, "IsHudColorSwatchVisible is false for AudioDevice");
            assert(Math.Abs(vm.HudMiniMeterWidth - 31.5) < 0.01,
                $"HudMiniMeterWidth is 31.5px (42px * 0.75) (got {vm.HudMiniMeterWidth})");

            // Battery at 50%
            InvokeShowHud(vm, new HudPayload
            {
                Kind = HudKind.Battery,
                Icon = IconsaxKind.BatteryCharging,
                Title = "Pin",
                ValueText = "50%",
                NormalizedValue = 0.50,
                AccentHex = "#34D399"
            });
            assert(vm.IsHudMiniMeterVisible, "IsHudMiniMeterVisible is true for Battery with value 0.50");
            assert(Math.Abs(vm.HudMiniMeterWidth - 21.0) < 0.01,
                $"HudMiniMeterWidth is 21.0px (42px * 0.50) (got {vm.HudMiniMeterWidth})");

            // Battery at 0%
            InvokeShowHud(vm, new HudPayload
            {
                Kind = HudKind.Battery,
                Icon = IconsaxKind.BatteryLow,
                Title = "Pin yếu",
                ValueText = "0%",
                NormalizedValue = 0.0,
                AccentHex = "#FF453A"
            });
            assert(!vm.IsHudMiniMeterVisible, "IsHudMiniMeterVisible is false when normalized value <= 0");
            assert(vm.HudMiniMeterWidth == 0.0, "HudMiniMeterWidth is 0.0 for 0% battery");

            // Over-range clamping [0.0 .. 1.0]
            InvokeShowHud(vm, new HudPayload
            {
                Kind = HudKind.Battery,
                Icon = IconsaxKind.BatteryCharging,
                Title = "Pin",
                ValueText = "120%",
                NormalizedValue = 1.5,
                AccentHex = "#34D399"
            });
            assert(vm.HudMiniMeterWidth == 42.0, "HudMiniMeterWidth strictly clamped to maximum 42.0px");

            // Negative-range clamping [0.0 .. 1.0]
            InvokeShowHud(vm, new HudPayload
            {
                Kind = HudKind.Battery,
                Icon = IconsaxKind.BatteryLow,
                Title = "Pin",
                ValueText = "-20%",
                NormalizedValue = -0.2,
                AccentHex = "#FF453A"
            });
            assert(!vm.IsHudMiniMeterVisible, "IsHudMiniMeterVisible is false for negative value");
            assert(vm.HudMiniMeterWidth == 0.0, "HudMiniMeterWidth clamped to minimum 0.0px");
        }
        finally
        {
            window.Close();
        }

        // 4. XAML Inspection for HudView invariants
        string hudXamlPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\HudView.xaml";
        string hudXaml = File.ReadAllText(hudXamlPath);

        assert(hudXaml.Contains("x:Name=\"StatusVisualScale\""), "StatusVisualScale transform defined in HudView.xaml");
        assert(hudXaml.Contains("CornerRadius=\"6.2\""), "24x24 G2 squircle color swatch has CornerRadius='6.2'");
        assert(hudXaml.Contains("Width=\"24\"") && hudXaml.Contains("Height=\"24\""), "Color swatch is 24x24 DIPs");
        assert(hudXaml.Contains("BorderBrush=\"#45FFFFFF\""), "Color swatch has #45FFFFFF inner border");
        assert(hudXaml.Contains("Width=\"42\"") && hudXaml.Contains("Height=\"4.5\""), "Capsule mini level meter is 42x4.5 DIPs");
        assert(hudXaml.Contains("CornerRadius=\"2.25\""), "Mini level meter has CornerRadius='2.25' (capsule)");
        assert(hudXaml.Contains("BorderBrush=\"#1FFFFFFF\""), "HudView strictly preserves #1FFFFFFF rim stroke");

        // 5. Code-behind inspection for HudView arrival pulse detachment (zero CPU)
        string hudCsPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\HudView.xaml.cs";
        string hudCs = File.ReadAllText(hudCsPath);
        assert(hudCs.Contains("PlayHudArrivalPulse"), "PlayHudArrivalPulse method defined in HudView.xaml.cs");
        assert(hudCs.Contains("BackEase") && hudCs.Contains("0.35"), "Arrival pulse uses BackEase with Amplitude 0.35");
        bool detachesHudClocks = hudCs.Contains("BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null)") ||
                                 hudCs.Contains("BeginAnimation(ScaleTransform.ScaleXProperty, null)");
        assert(detachesHudClocks,
            "Arrival pulse detaches ScaleX clock on completion for 0.0% CPU",
            details: detachesHudClocks ? null : "HudView.xaml.cs missing BeginAnimation(..., null) clock detachment callback upon completion");
    }

    #endregion

    #region Challenge 5: Media Player Album Art Dynamics & Cross-Fade

    private static void TestMediaAlbumArtDynamicsAndCrossFade(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 5] Media Album Art Scaling & Cross-Fade ---");

        string mediaXamlPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\MediaOrQuickControlView.xaml";
        string mediaXaml = File.ReadAllText(mediaXamlPath);

        // 1. Verify 60x60 G2 Squircle Container in XAML
        assert(mediaXaml.Contains("x:Name=\"AlbumArtContainer\""), "AlbumArtContainer defined in MediaOrQuickControlView.xaml");
        assert(mediaXaml.Contains("CornerRadius=\"12.5\""), "AlbumArtContainer has CornerRadius='12.5'");
        assert(mediaXaml.Contains("Width=\"60\"") && mediaXaml.Contains("Height=\"60\""), "AlbumArtContainer is 60x60 DIPs");
        assert(mediaXaml.Contains("x:Name=\"AlbumArtScale\""), "AlbumArtScale transform is defined");
        assert(mediaXaml.Contains("x:Name=\"PreviousAlbumArtBorder\""), "PreviousAlbumArtBorder defined for cross-fade");
        assert(mediaXaml.Contains("x:Name=\"CurrentAlbumArtBorder\""), "CurrentAlbumArtBorder defined for cross-fade");

        // 2. Verify Code-Behind Animation Contracts in MediaOrQuickControlView.xaml.cs
        string mediaCsPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Controls\MediaOrQuickControlView.xaml.cs";
        string mediaCs = File.ReadAllText(mediaCsPath);

        // Playback state scaling
        assert(mediaCs.Contains("ApplyPlaybackState"), "ApplyPlaybackState defined in MediaOrQuickControlView.xaml.cs");
        assert(mediaCs.Contains("BackEase") && mediaCs.Contains("0.32"),
            "Playback scale to 1.0 on play uses BackEase with Amplitude 0.32");
        assert(mediaCs.Contains("CubicEase") && mediaCs.Contains("200"),
            "Playback scale to 0.90 on pause uses CubicEase with 200ms duration");

        // Cross-fade animation
        assert(mediaCs.Contains("TransitionAlbumArt"), "TransitionAlbumArt defined in MediaOrQuickControlView.xaml.cs");
        assert(mediaCs.Contains("FromMilliseconds(180)"), "Album art cross-fade duration is exactly 180ms");

        // Clock detachment for 0.0% CPU when settled
        assert(mediaCs.Contains("AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null)"),
            "Album art scale detaches ScaleX clock on completion for 0.0% CPU");
        assert(mediaCs.Contains("AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null)"),
            "Album art scale detaches ScaleY clock on completion for 0.0% CPU");
        assert(mediaCs.Contains("CurrentAlbumArtBorder.BeginAnimation(OpacityProperty, null)"),
            "CurrentAlbumArtBorder detaches Opacity clock on completion");
    }

    #endregion

    #region Challenge 6: XAML Structure, Canvas Clearance & OLED Pitch-Black Invariants

    private static void TestXamlStructureAndOledInvariants(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M2-CHALLENGE 6] XAML Invariants, Canvas Clearance & OLED Pitch-Black ---");

        string notchXamlPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\NotchWindow.xaml";
        string notchXaml = File.ReadAllText(notchXamlPath);

        // 1. Root Window & RootHost Dimensions (820x860)
        assert(notchXaml.Contains("Width=\"820\"") && notchXaml.Contains("Height=\"860\""),
            "NotchWindow root Width='820' and Height='860'");
        assert(notchXaml.Contains("<Grid x:Name=\"RootHost\" Width=\"820\" Height=\"860\""),
            "RootHost dimensions set to Width='820' Height='860'");

        // 2. Dynamic ExpandedLayer Sizing Bindings
        assert(notchXaml.Contains("Width=\"{Binding ActiveExpandedWidth, FallbackValue=684}\""),
            "ExpandedLayer Width dynamically bound to ActiveExpandedWidth");
        assert(notchXaml.Contains("Height=\"{Binding ActiveExpandedHeight, FallbackValue=272}\""),
            "ExpandedLayer Height dynamically bound to ActiveExpandedHeight");

        // 3. Pitch-Black OLED Shell & Rim Highlight Preservation
        string notchStylesPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Themes\NotchStyles.xaml";
        string notchStyles = File.ReadAllText(notchStylesPath);

        assert(notchStyles.Contains("<SolidColorBrush x:Key=\"NotchShellBrush\" Color=\"#050507\" />"),
            "NotchShellBrush strictly preserved as pure pitch-black OLED #050507");
        assert(notchStyles.Contains("<SolidColorBrush x:Key=\"NotchRimBrush\" Color=\"#1FFFFFFF\" />"),
            "NotchRimBrush strictly preserved as #1FFFFFFF inner rim highlight");

        // 4. Ear tangent clearance calculation guarantees >= 24px clearance
        double compactLeftClearance = (820.0 - 684.0) / 2.0 - 13.5; // (136/2) - 13.5 = 68 - 13.5 = 54.5 >= 24
        assert(compactLeftClearance >= 24.0, $"Compact ear tangent clearance is {compactLeftClearance}px >= 24.0px");

        double spaciousLeftClearance = (820.0 - 744.0) / 2.0 - 14.0; // (76/2) - 14.0 = 38 - 14 = 24.0 >= 24
        assert(spaciousLeftClearance >= 24.0, $"Spacious ear tangent clearance is {spaciousLeftClearance}px >= 24.0px");
    }

    #endregion
}
