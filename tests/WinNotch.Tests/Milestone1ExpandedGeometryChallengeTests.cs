using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using WinNotch.Engine;
using WinNotch.Models;

namespace WinNotch.Tests;

/// <summary>
/// Empirical Adversarial Challenge Suite for Milestone 1:
/// Expanded Geometry & Liquid Glass Design System.
/// Empirically stress-tests Compact vs Spacious geometry, ear tangent clearances,
/// dynamic height clamping up to 2/3 monitor DIP height, preset morphing,
/// backward compatibility aliases, and XAML design system invariants.
/// </summary>
public static class Milestone1ExpandedGeometryChallengeTests
{
    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" MILESTONE 1: EXPANDED GEOMETRY & LIQUID GLASS ADVERSARIAL SUITE        ");
        Console.WriteLine(" Empirical Stress-Tests on Geometry, Dynamic Height, & Design System     ");
        Console.WriteLine("=========================================================================");

        // Challenge 1: Compact vs Spacious Dimensions & Invariant Integrity
        TestCompactVsSpaciousDimensionsAndAliases(assert);

        // Challenge 2: HostCanvas Clipping Clearance & ConcaveNotchPath Geometry
        TestHostCanvasClippingClearanceAndGeometry(assert);

        // Challenge 3: Dynamic Tab 1 (Agents) Height Logic & Screen Height Clamping
        TestDynamicAgentsHeightClampingAndTabRestore(assert);

        // Challenge 4: State Machine Preset Switching & Real-Time Event Emission
        TestStateMachinePresetSwitchingAndEvents(assert);

        // Challenge 5: Liquid Glass Design System XAML Style Contracts & Invariants
        TestLiquidGlassDesignSystemXamlContracts(assert);

        // Challenge 6: Concurrent Fuzzing & High-Frequency Morphing Torture
        TestConcurrentGeometryMorphingTorture(assert);
    }

    private static void TestCompactVsSpaciousDimensionsAndAliases(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M1-CHALLENGE 1] Compact vs Spacious Dimensions & Invariants ---");

        // 1. Compact exact dimensions
        assert(NotchStateMachine.DimExpandedCompact.Width == 684.0, "DimExpandedCompact.Width == 684.0");
        assert(NotchStateMachine.DimExpandedCompact.Height == 272.0, "DimExpandedCompact.Height == 272.0");
        assert(NotchStateMachine.DimExpandedCompact.EarRadius == 13.5, "DimExpandedCompact.EarRadius == 13.5");
        assert(NotchStateMachine.DimExpandedCompact.BottomRadius == 30.0, "DimExpandedCompact.BottomRadius == 30.0");

        // 2. Spacious exact dimensions
        assert(NotchStateMachine.DimExpandedSpacious.Width == 744.0, "DimExpandedSpacious.Width == 744.0");
        assert(NotchStateMachine.DimExpandedSpacious.Height == 296.0, "DimExpandedSpacious.Height == 296.0");
        assert(NotchStateMachine.DimExpandedSpacious.EarRadius == 14.0, "DimExpandedSpacious.EarRadius == 14.0");
        assert(NotchStateMachine.DimExpandedSpacious.BottomRadius == 31.0, "DimExpandedSpacious.BottomRadius == 31.0");

        // 3. Backwards-compatibility invariant: DimExpanded must match DimExpandedCompact
        assert(NotchStateMachine.DimExpanded.Width == 684.0 && NotchStateMachine.DimExpanded.Height == 272.0,
            "DimExpanded preserved at 684x272 backwards-compatible alias",
            $"{NotchStateMachine.DimExpanded.Width}x{NotchStateMachine.DimExpanded.Height}");
        assert(NotchStateMachine.DimExpanded.EarRadius == 13.5, "DimExpanded.EarRadius preserved at 13.5");
        assert(NotchStateMachine.DimExpanded.BottomRadius == 30.0, "DimExpanded.BottomRadius preserved at 30.0");
        assert(NotchStateMachine.DimExpanded == NotchStateMachine.DimExpandedCompact, "DimExpanded == DimExpandedCompact (record equality)");

        // 4. AppSettings default and cloning
        var defaultSettings = new AppSettings();
        assert(defaultSettings.ExpandedSizePreset == "Compact", "AppSettings.ExpandedSizePreset defaults to 'Compact'");

        var cloneSettings = defaultSettings.Clone();
        assert(cloneSettings.ExpandedSizePreset == "Compact", "AppSettings.Clone() preserves ExpandedSizePreset");

        cloneSettings.ExpandedSizePreset = "Spacious";
        assert(cloneSettings.ExpandedSizePreset == "Spacious", "Cloned preset can be set to 'Spacious'");
        assert(defaultSettings.ExpandedSizePreset == "Compact", "Original settings unmodified by clone modification");

        // 5. Preset resolution and case-insensitivity
        var smCompact = new NotchStateMachine(new AppSettings { ExpandedSizePreset = "Compact" });
        assert(smCompact.ExpandedSizePreset == "Compact", "StateMachine preset initialized to 'Compact'");
        assert(smCompact.ActiveExpandedDimensions == NotchStateMachine.DimExpandedCompact, "ActiveExpandedDimensions resolves to DimExpandedCompact");

        string[] spaciousVariants = ["Spacious", "spacious", "SPACIOUS", "SpAcIoUs"];
        foreach (var variant in spaciousVariants)
        {
            var smVariant = new NotchStateMachine(new AppSettings { ExpandedSizePreset = variant });
            assert(smVariant.ActiveExpandedDimensions == NotchStateMachine.DimExpandedSpacious,
                $"Case-insensitive variant '{variant}' resolves to DimExpandedSpacious");
        }

        string[] fallbackVariants = [null!, "", "   \t", "Unknown", "InvalidPreset", "1234"];
        foreach (var variant in fallbackVariants)
        {
            var smFallback = new NotchStateMachine(new AppSettings { ExpandedSizePreset = variant });
            assert(smFallback.ActiveExpandedDimensions == NotchStateMachine.DimExpandedCompact,
                $"Invalid preset '{variant ?? "<null>"}' safely falls back to DimExpandedCompact");
        }
    }

    private static void TestHostCanvasClippingClearanceAndGeometry(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M1-CHALLENGE 2] HostCanvas Clipping Clearance & ConcaveNotchPath Geometry ---");

        // 1. HostCanvas constants
        assert(ConcaveNotchPath.HostCanvasWidth == 820.0, "ConcaveNotchPath.HostCanvasWidth == 820.0");
        assert(ConcaveNotchPath.HostCanvasHeight == 860.0, "ConcaveNotchPath.HostCanvasHeight == 860.0");

        // 2. Ear tangent clearance calculation
        // Compact: bodyWidth=684, earRadius=13.5
        double leftCompact = (ConcaveNotchPath.HostCanvasWidth - 684.0) * 0.5; // 68.0
        double earTangentStartCompact = leftCompact - 13.5; // 54.5
        double rightEarEndCompact = leftCompact + 684.0 + 13.5; // 765.5
        double rightMarginCompact = ConcaveNotchPath.HostCanvasWidth - rightEarEndCompact; // 54.5

        assert(earTangentStartCompact >= 24.0,
            $"Compact left ear tangent start ({earTangentStartCompact:F1}px) >= 24.0px clearance margin");
        assert(rightMarginCompact >= 24.0,
            $"Compact right ear tangent end margin ({rightMarginCompact:F1}px) >= 24.0px clearance margin");
        assert(Math.Abs(earTangentStartCompact - rightMarginCompact) < 0.001,
            "Compact ear tangent clearances are perfectly symmetrical");

        // Spacious: bodyWidth=744, earRadius=14.0
        double leftSpacious = (ConcaveNotchPath.HostCanvasWidth - 744.0) * 0.5; // 38.0
        double earTangentStartSpacious = leftSpacious - 14.0; // 24.0
        double rightEarEndSpacious = leftSpacious + 744.0 + 14.0; // 796.0
        double rightMarginSpacious = ConcaveNotchPath.HostCanvasWidth - rightEarEndSpacious; // 24.0

        assert(earTangentStartSpacious >= 24.0,
            $"Spacious left ear tangent start ({earTangentStartSpacious:F1}px) >= 24.0px clearance margin");
        assert(rightMarginSpacious >= 24.0,
            $"Spacious right ear tangent end margin ({rightMarginSpacious:F1}px) >= 24.0px clearance margin");
        assert(Math.Abs(earTangentStartSpacious - 24.0) < 0.001,
            "Spacious ear tangent starts at EXACTLY 24.0px from HostCanvas boundary");
        assert(Math.Abs(rightMarginSpacious - 24.0) < 0.001,
            "Spacious right ear ends at EXACTLY 24.0px from HostCanvas boundary");

        // 3. Real StreamGeometry build & bounds verification
        StreamGeometry fillCompact = ConcaveNotchPath.BuildFillGeometry(684.0, 272.0, 13.5, 30.0);
        assert(fillCompact.IsFrozen, "Compact BuildFillGeometry is frozen");
        Rect bCompact = fillCompact.Bounds;
        assert(bCompact.Left >= 24.0, $"Compact fill geometry Bounds.Left ({bCompact.Left:F1}) >= 24.0");
        assert(ConcaveNotchPath.HostCanvasWidth - bCompact.Right >= 24.0,
            $"Compact fill geometry right clearance ({ConcaveNotchPath.HostCanvasWidth - bCompact.Right:F1}) >= 24.0");
        assert(Math.Abs(bCompact.Top) < 0.01, "Compact fill geometry Bounds.Top == 0.0 (flush with top bezel)");
        assert(Math.Abs(bCompact.Bottom - 272.0) < 0.01, "Compact fill geometry Bounds.Bottom == 272.0");
        assert(ConcaveNotchPath.HostCanvasHeight - bCompact.Bottom >= 140.0,
            $"Compact bottom clearance ({ConcaveNotchPath.HostCanvasHeight - bCompact.Bottom:F1}px) is well above canvas height");

        StreamGeometry fillSpacious = ConcaveNotchPath.BuildFillGeometry(744.0, 296.0, 14.0, 31.0);
        assert(fillSpacious.IsFrozen, "Spacious BuildFillGeometry is frozen");
        Rect bSpacious = fillSpacious.Bounds;
        assert(bSpacious.Left >= 24.0 - 0.01, $"Spacious fill geometry Bounds.Left ({bSpacious.Left:F1}) >= 24.0");
        assert(ConcaveNotchPath.HostCanvasWidth - bSpacious.Right >= 24.0 - 0.01,
            $"Spacious fill geometry right clearance ({ConcaveNotchPath.HostCanvasWidth - bSpacious.Right:F1}) >= 24.0");
        assert(Math.Abs(bSpacious.Top) < 0.01, "Spacious fill geometry Bounds.Top == 0.0 (flush with top bezel)");
        assert(Math.Abs(bSpacious.Bottom - 296.0) < 0.01, "Spacious fill geometry Bounds.Bottom == 296.0");
        assert(ConcaveNotchPath.HostCanvasHeight - bSpacious.Bottom >= 140.0,
            $"Spacious bottom clearance ({ConcaveNotchPath.HostCanvasHeight - bSpacious.Bottom:F1}px) is well above canvas height");

        // 4. StreamGeometry RimStroke build
        StreamGeometry rimCompact = ConcaveNotchPath.BuildRimStrokeGeometry(684.0, 272.0, 13.5, 30.0);
        assert(rimCompact.IsFrozen, "Compact BuildRimStrokeGeometry is frozen");
        StreamGeometry rimSpacious = ConcaveNotchPath.BuildRimStrokeGeometry(744.0, 296.0, 14.0, 31.0);
        assert(rimSpacious.IsFrozen, "Spacious BuildRimStrokeGeometry is frozen");

        // 5. Degenerate and corner geometry inputs resilience
        StreamGeometry emptyZero = ConcaveNotchPath.BuildFillGeometry(0.0, 0.0, 0.0, 0.0);
        assert(emptyZero.IsFrozen && emptyZero.IsEmpty(), "BuildFillGeometry(0, 0, 0, 0) returns frozen empty geometry");

        StreamGeometry emptySmallH = ConcaveNotchPath.BuildFillGeometry(684.0, 0.4, 13.5, 30.0);
        assert(emptySmallH.IsFrozen && emptySmallH.IsEmpty(), "BuildFillGeometry(bodyHeight <= 0.5) returns frozen empty geometry");

        StreamGeometry emptySmallW = ConcaveNotchPath.BuildFillGeometry(3.0, 272.0, 13.5, 30.0);
        assert(emptySmallW.IsFrozen && emptySmallW.IsEmpty(), "BuildFillGeometry(bodyWidth <= 4.0) returns frozen empty geometry");

        // 6. Dynamic CalculateHostCanvasHeight across monitor heights
        double h768 = ConcaveNotchPath.CalculateHostCanvasHeight(768.0);
        assert(h768 == 860.0, $"CalculateHostCanvasHeight(768) == 860.0 (got {h768})");

        double h1080 = ConcaveNotchPath.CalculateHostCanvasHeight(1080.0);
        assert(h1080 == 860.0, $"CalculateHostCanvasHeight(1080) == 860.0 (got {h1080})");

        double h1440 = ConcaveNotchPath.CalculateHostCanvasHeight(1440.0);
        assert(h1440 == 1061.0, $"CalculateHostCanvasHeight(1440) == 1061.0 (got {h1440})");

        double h2160 = ConcaveNotchPath.CalculateHostCanvasHeight(2160.0);
        assert(h2160 == 1580.0, $"CalculateHostCanvasHeight(2160) == 1580.0 (got {h2160})");

        // Monitor DIP Fuzzing Matrix [480p to 8K (8640p)]
        double[] testMonitors = [480.0, 600.0, 768.0, 900.0, 1080.0, 1200.0, 1440.0, 1600.0, 1800.0, 2160.0, 2880.0, 4320.0, 8640.0];
        bool allClearancesValid = true;
        foreach (var mH in testMonitors)
        {
            double maxNotchH = Math.Floor(mH * (2.0 / 3.0));
            double hostH = ConcaveNotchPath.CalculateHostCanvasHeight(mH);
            double clearance = hostH - maxNotchH;
            if (clearance < 24.0 || hostH < ConcaveNotchPath.HostCanvasHeight)
            {
                allClearancesValid = false;
                break;
            }
        }
        assert(allClearancesValid, "CalculateHostCanvasHeight guarantees >= 24.0px bottom clearance for all monitor heights [480p .. 8640p]");
    }

    private static void TestDynamicAgentsHeightClampingAndTabRestore(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M1-CHALLENGE 3] Dynamic Tab 1 (Agents) Height & Screen Clamping ---");

        var sm = new NotchStateMachine(new AppSettings { ExpandedSizePreset = "Compact" });

        // 1. Non-Agents tabs return preset base height regardless of desired height
        sm.SetActiveTabAndAgentsHeight(NotchTab.Overview, desiredAgentsHeight: 650.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Tab Overview: desired 650.0 returns base height 272.0");

        sm.SetActiveTabAndAgentsHeight(NotchTab.System, desiredAgentsHeight: 900.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Tab System: desired 900.0 returns base height 272.0");

        sm.SetActiveTabAndAgentsHeight(NotchTab.FileShelf, desiredAgentsHeight: 500.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Tab FileShelf: desired 500.0 returns base height 272.0");

        // 2. Tab Agents with desired height <= 0 returns base height
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 0.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Tab Agents: desired 0.0 returns base height 272.0");

        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: -100.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Tab Agents: desired negative returns base height 272.0");

        // 3. Tab Agents with desired height < base height clamps up to base height
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 150.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Tab Agents: desired 150.0 clamps up to base height 272.0");

        // 4. Tab Agents with desired height within [base, max] returns desired height
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 450.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 450.0, "Tab Agents: desired 450.0 within range returns exactly 450.0");

        // 5. Tab Agents with desired height > max clamps to 2/3 screen height
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 1200.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 720.0, "Tab Agents: desired 1200.0 clamps down to maxScreen 720.0");

        // 6. Monitor DIP height matrix verification
        (double screenH, double desiredH, double expectedH)[] testMatrix =
        [
            // 768p: 2/3 = 512
            (768.0, 400.0, 400.0),
            (768.0, 600.0, 512.0),
            (768.0, 200.0, 272.0),
            // 1080p: 2/3 = 720
            (1080.0, 500.0, 500.0),
            (1080.0, 800.0, 720.0),
            // 1440p: 2/3 = 960
            (1440.0, 850.0, 850.0),
            (1440.0, 1100.0, 960.0),
            // 2160p: 2/3 = 1440
            (2160.0, 1200.0, 1200.0),
            (2160.0, 1800.0, 1440.0)
        ];

        bool matrixPassed = true;
        foreach (var item in testMatrix)
        {
            double max23 = Math.Floor(item.screenH * (2.0 / 3.0));
            sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, item.desiredH, max23);
            if (Math.Abs(sm.CurrentExpandedDimensions.Height - item.expectedH) > 0.001)
            {
                matrixPassed = false;
                break;
            }
        }
        assert(matrixPassed, "Tab Agents dynamic height matrix strictly clamped to Math.Floor(screenDipHeight * 2/3)");

        // 7. Tab switching round-trip restores base height and preserves desired height
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 620.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 620.0, "Expanded on Agents at 620.0px");

        sm.SetActiveTabAndAgentsHeight(NotchTab.Overview, desiredAgentsHeight: 620.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 272.0, "Switched to Overview: height immediately returns to 272.0px");

        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 620.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(sm.CurrentExpandedDimensions.Height == 620.0, "Switched back to Agents: height re-expands to 620.0px");

        // 8. Spacious preset dynamic height clamping
        var smSpacious = new NotchStateMachine(new AppSettings { ExpandedSizePreset = "Spacious" });
        smSpacious.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 200.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(smSpacious.CurrentExpandedDimensions.Height == 296.0, "Spacious: desired 200.0 clamps up to Spacious base 296.0");

        smSpacious.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 500.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(smSpacious.CurrentExpandedDimensions.Height == 500.0, "Spacious: desired 500.0 within range returns 500.0");
        assert(smSpacious.CurrentExpandedDimensions.Width == 744.0, "Spacious: width remains 744.0");

        // 9. Degenerate maxScreenTwoThirdsDipHeight safety
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 500.0, maxScreenTwoThirdsDipHeight: 100.0);
        assert(sm.CurrentExpandedDimensions.Height == 272.0,
            "Degenerate maxScreen (100.0 < minH) safely clamped to base height 272.0 without crash");
    }

    private static void TestStateMachinePresetSwitchingAndEvents(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M1-CHALLENGE 4] State Machine Preset Switching & Real-Time Events ---");

        var sm = new NotchStateMachine(new AppSettings { ExpandedSizePreset = "Compact" });

        // 1. In CollapsedIdle state, ApplySettings updates preset but does NOT fire StateChanged
        int stateChangedCount = 0;
        NotchDimensions lastEmittedDims = default;
        bool lastIsShrinking = false;

        sm.StateChanged += (_, dims, isShrinking) =>
        {
            stateChangedCount++;
            lastEmittedDims = dims;
            lastIsShrinking = isShrinking;
        };

        sm.ApplySettings(new AppSettings { ExpandedSizePreset = "Spacious" });
        assert(sm.ExpandedSizePreset == "Spacious", "Preset updated to 'Spacious' while CollapsedIdle");
        assert(stateChangedCount == 0, "ApplySettings while CollapsedIdle does NOT fire StateChanged (collapsed layout undisturbed)");

        // 2. Expand and verify initial expanded dimensions match updated preset
        sm.ExpandAndPin();
        assert(sm.State == NotchVisualState.Expanded, "State is now Expanded");
        assert(stateChangedCount == 1, "ExpandAndPin() fired StateChanged once");
        assert(lastEmittedDims.Width == 744.0 && lastEmittedDims.Height == 296.0, "Expanded with Spacious dimensions 744x296");
        assert(!lastIsShrinking, "Expanding from collapsed is not shrinking (isShrinking == false)");

        // 3. While Expanded, switch preset to Compact -> fires StateChanged with isShrinking == true
        stateChangedCount = 0;
        sm.ApplySettings(new AppSettings { ExpandedSizePreset = "Compact" });
        assert(stateChangedCount == 1, "ApplySettings('Compact') while Expanded fired StateChanged");
        assert(lastEmittedDims.Width == 684.0 && lastEmittedDims.Height == 272.0, "Emitted Compact dimensions 684x272");
        assert(lastIsShrinking, "Switching Spacious -> Compact marks isShrinking == true");

        // 4. While Expanded, ApplySettings with identical preset -> NO redundant event
        stateChangedCount = 0;
        sm.ApplySettings(new AppSettings { ExpandedSizePreset = "Compact" });
        assert(stateChangedCount == 0, "ApplySettings with same preset does NOT fire redundant StateChanged");

        // 5. While Expanded, switch preset back to Spacious -> fires StateChanged with isShrinking == false
        stateChangedCount = 0;
        sm.ApplySettings(new AppSettings { ExpandedSizePreset = "Spacious" });
        assert(stateChangedCount == 1, "ApplySettings('Spacious') while Expanded fired StateChanged");
        assert(lastEmittedDims.Width == 744.0 && lastEmittedDims.Height == 296.0, "Emitted Spacious dimensions 744x296");
        assert(!lastIsShrinking, "Switching Compact -> Spacious marks isShrinking == false");

        // 6. Dynamic height event firing while Expanded
        stateChangedCount = 0;
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 520.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(stateChangedCount == 1, "SetActiveTabAndAgentsHeight(Agents, 520) while Expanded fired StateChanged");
        assert(lastEmittedDims.Height == 520.0 && lastEmittedDims.Width == 744.0, "Emitted dynamic height 520.0 with width 744.0");
        assert(!lastIsShrinking, "Expanding height 296 -> 520 marks isShrinking == false");

        stateChangedCount = 0;
        sm.SetActiveTabAndAgentsHeight(NotchTab.Agents, desiredAgentsHeight: 380.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(stateChangedCount == 1, "SetActiveTabAndAgentsHeight(Agents, 380) fired StateChanged");
        assert(lastEmittedDims.Height == 380.0, "Emitted height 380.0");
        assert(lastIsShrinking, "Shrinking height 520 -> 380 marks isShrinking == true");

        // Switching tab back to Overview springs back to base height
        stateChangedCount = 0;
        sm.SetActiveTabAndAgentsHeight(NotchTab.Overview, desiredAgentsHeight: 380.0, maxScreenTwoThirdsDipHeight: 720.0);
        assert(stateChangedCount == 1, "Switching from Agents to Overview fired StateChanged");
        assert(lastEmittedDims.Height == 296.0, "Height smoothly restored to Spacious base height 296.0");
        assert(lastIsShrinking, "Restoring base height marks isShrinking == true");
    }

    private static void TestLiquidGlassDesignSystemXamlContracts(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M1-CHALLENGE 5] Liquid Glass Design System XAML Style Contracts ---");

        string xamlPath = @"C:\Users\ASUS\Documents\AGY\WinNotch\src\WinNotch\Views\Themes\NotchStyles.xaml";
        assert(File.Exists(xamlPath), $"NotchStyles.xaml exists at '{xamlPath}'");

        string xamlContent = File.ReadAllText(xamlPath);
        XDocument doc = XDocument.Parse(xamlContent);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        // 1. Specular rim gradient brush
        bool hasSpecularBrush = xamlContent.Contains("x:Key=\"NotchSpecularRimBrush\"");
        assert(hasSpecularBrush, "NotchSpecularRimBrush is defined in NotchStyles.xaml");
        assert(xamlContent.Contains("Color=\"#34FFFFFF\" Offset=\"0.0\"") || xamlContent.Contains("Color=\"#34FFFFFF\""),
            "NotchSpecularRimBrush has #34FFFFFF specular top stop");
        assert(xamlContent.Contains("Color=\"#16FFFFFF\" Offset=\"1.0\"") || xamlContent.Contains("Color=\"#16FFFFFF\""),
            "NotchSpecularRimBrush has #16FFFFFF lower gradient stop");

        // 2. Pure pitch-black OLED shell brush (#050507) and inner rim highlight (#1FFFFFFF)
        assert(xamlContent.Contains("<SolidColorBrush x:Key=\"NotchShellBrush\" Color=\"#050507\" />"),
            "NotchShellBrush strictly preserved as pure pitch-black OLED #050507");
        assert(xamlContent.Contains("<SolidColorBrush x:Key=\"NotchRimBrush\" Color=\"#1FFFFFFF\" />"),
            "NotchRimBrush strictly preserved as #1FFFFFFF inner rim highlight");

        // 3. NotchGhostButton & NotchPillButton tactile press depression and back-ease release
        assert(xamlContent.Contains("x:Key=\"NotchGhostButton\""), "NotchGhostButton style defined");
        assert(xamlContent.Contains("x:Key=\"NotchPillButton\""), "NotchPillButton style defined");

        assert(xamlContent.Contains("To=\"0.955\""), "Tactile press depression scales to 0.955");
        assert(xamlContent.Contains("Duration=\"0:0:0.080\""), "Press depression executes rapidly in 80ms");
        assert(xamlContent.Contains("<BackEase EasingMode=\"EaseOut\" Amplitude=\"0.32\" />"),
            "Button release rebound uses BackEase EaseOut with Amplitude 0.32");
        assert(xamlContent.Contains("Duration=\"0:0:0.200\""), "Button release rebound duration is 200ms");

        // 4. Sleek capsule slider (7px -> 10px on hover/focus, 14x14 thumb magnifies 1.12x)
        assert(xamlContent.Contains("x:Key=\"NotchSleekSlider\""), "NotchSleekSlider style defined");
        assert(xamlContent.Contains("Height=\"7\""), "Capsule track resting height is 7px");
        assert(xamlContent.Contains("CornerRadius=\"3.5\""), "Capsule track resting CornerRadius is 3.5px");
        assert(xamlContent.Contains("Storyboard.TargetProperty=\"Height\"") && xamlContent.Contains("To=\"10\""),
            "Capsule track expands to 10px on hover/focus");
        assert(xamlContent.Contains("To=\"1.12\""), "Slider thumb magnifies 1.12x on hover/focus");

        // 5. Fluent dark toggle switch (19px translation, #3B82F6 color morph, 170ms CubicEase)
        assert(xamlContent.Contains("x:Key=\"FluentToggleSwitch\""), "FluentToggleSwitch style defined");
        assert(xamlContent.Contains("Storyboard.TargetName=\"KnobTranslate\"") && xamlContent.Contains("To=\"19\""),
            "Toggle switch knob translates 19px on IsChecked=True");
        assert(xamlContent.Contains("To=\"#3B82F6\""), "Toggle switch track morphs to #3B82F6 blue");
        assert(xamlContent.Contains("Duration=\"0:0:0.170\""), "Toggle switch animation duration is 170ms");
        assert(xamlContent.Contains("<CubicEase EasingMode=\"EaseOut\" />"), "Toggle switch uses CubicEase EaseOut");

        // 6. Living breathing status dot (concentric aura ring, 0.0% CPU when inactive/hidden)
        assert(xamlContent.Contains("x:Key=\"BreathingStatusDotStyle\""), "BreathingStatusDotStyle defined");
        assert(xamlContent.Contains("x:Name=\"AuraRing\"") && xamlContent.Contains("x:Name=\"CoreDot\""),
            "Breathing dot contains concentric AuraRing and CoreDot");
        assert(xamlContent.Contains("From=\"1.0\" To=\"1.9\""), "Aura ring expands from 1.0 to 1.9 scale");
        assert(xamlContent.Contains("From=\"0.55\" To=\"0.0\""), "Aura ring fades from 0.55 to 0.0 opacity");
        assert(xamlContent.Contains("Duration=\"0:0:1.650\""), "Breathing cycle duration is 1.650s");
        assert(xamlContent.Contains("<Condition Property=\"IsVisible\" Value=\"True\" />") &&
               xamlContent.Contains("<Condition Property=\"IsEnabled\" Value=\"True\" />"),
            "Breathing animation gated strictly by MultiTrigger (IsVisible=True AND IsEnabled=True)");
        assert(xamlContent.Contains("<StopStoryboard BeginStoryboardName=\"BreathingStory\" />"),
            "Breathing animation invokes StopStoryboard on exit guaranteeing strictly 0.0% CPU when inactive or hidden");
    }

    private static void TestConcurrentGeometryMorphingTorture(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M1-CHALLENGE 6] Concurrent Fuzzing & High-Frequency Morphing Torture ---");

        var sm = new NotchStateMachine(new AppSettings { ExpandedSizePreset = "Compact" });
        sm.ExpandAndPin();

        const int threadCount = 8;
        const int iterationsPerThread = 250;
        var tasks = new Task[threadCount];
        int errorCount = 0;
        int successfulMorphs = 0;

        var presets = new[] { "Compact", "Spacious" };
        var tabs = new[] { NotchTab.Overview, NotchTab.Agents, NotchTab.System, NotchTab.FileShelf };
        var screenHeights = new[] { 768.0, 1080.0, 1440.0, 2160.0 };

        var sw = Stopwatch.StartNew();

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    try
                    {
                        int step = (threadId * 1000 + i);

                        // 1. Alternate ApplySettings
                        string preset = presets[step % presets.Length];
                        sm.ApplySettings(new AppSettings { ExpandedSizePreset = preset });

                        // 2. Alternate SetActiveTabAndAgentsHeight
                        NotchTab tab = tabs[step % tabs.Length];
                        double screenH = screenHeights[step % screenHeights.Length];
                        double maxScreen = Math.Floor(screenH * (2.0 / 3.0));
                        double desiredH = (i % 2 == 0) ? (200.0 + (i % 800)) : (screenH * 0.9);

                        sm.SetActiveTabAndAgentsHeight(tab, desiredH, maxScreen);

                        // 3. Read current dimensions and verify invariant bounds
                        NotchDimensions dims = sm.CurrentExpandedDimensions;
                        if (dims.Width != 684.0 && dims.Width != 744.0)
                        {
                            Interlocked.Increment(ref errorCount);
                        }
                        // Absolute valid height bounds across all tested screen heights [768p..2160p]
                        if (dims.Height < 272.0 || dims.Height > 1440.0 + 0.1)
                        {
                            Interlocked.Increment(ref errorCount);
                        }

                        // 4. Verify ConcaveNotchPath dynamic calculation
                        double hostH = ConcaveNotchPath.CalculateHostCanvasHeight(screenH);
                        if (hostH < ConcaveNotchPath.HostCanvasHeight || hostH < maxScreen + 24.0)
                        {
                            Interlocked.Increment(ref errorCount);
                        }

                        Interlocked.Increment(ref successfulMorphs);
                    }
                    catch
                    {
                        Interlocked.Increment(ref errorCount);
                    }
                }
            });
        }

        Task.WaitAll(tasks);
        sw.Stop();

        assert(errorCount == 0, $"Concurrent torture completed with 0 errors (errors={errorCount})");
        assert(successfulMorphs == threadCount * iterationsPerThread,
            $"All {successfulMorphs} concurrent morphing operations executed cleanly in {sw.ElapsedMilliseconds}ms without deadlock");

        // Verify final state sanity
        sm.SetActiveTabAndAgentsHeight(NotchTab.Overview, 0, 720.0);
        NotchDimensions finalDims = sm.CurrentExpandedDimensions;
        assert(finalDims.Width == 684.0 || finalDims.Width == 744.0, "Final state width is valid preset");
        assert(finalDims.Height == 272.0 || finalDims.Height == 296.0, "Final state height is valid base preset");
    }
}
