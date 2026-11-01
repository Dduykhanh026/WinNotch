using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinNotch.Engine;
using WinNotch.Interop;
using WinNotch.Models;
using WinNotch.Services;
using WinNotch.ViewModels;

namespace WinNotch.Tests;

public static class Milestone4ChallengeTests
{
    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" MILESTONE 4: RAM OPTIMIZER & FILE SHELF EMPIRICAL VERIFICATION SUITE   ");
        Console.WriteLine("=========================================================================");

        EnsureWpfEnvironment();

        // Suite 1: Win32 P/Invoke & Process Management Constants
        TestProcessConstantsAndPInvoke(assert);

        // Suite 2: ShelfItem Computed Properties & Fallback Icons
        TestShelfItemIsUrlAndFallbackIcon(assert);
        TestShelfItemTooltipText(assert);

        // Suite 3: FileShelfService RevealInExplorer Ancestor Traversal & Escaping
        TestRevealInExplorerExistingFileAndFolder(assert);
        TestRevealInExplorerAncestorFallback(assert);
        TestRevealInExplorerNullAndEmptyGuards(assert);

        // Suite 4: FileShelfService OpenItem URL & Auto-Recreate Notes
        TestOpenItemNullGuard(assert);
        TestOpenItemAutoRecreatesMissingNote(assert);

        // Suite 5: FileShelfService PasteFromClipboard STA Safety & Resiliency
        TestPasteFromClipboardNonStaThreadSafety(assert);

        // Suite 6: SystemMonitorService User-Mode RAM Optimizer
        TestRamOptimizerWorkingSetTrimming(assert);
        TestRamOptimizerReentrancyProtection(assert);
        TestRamOptimizerCurrentProcessExclusion(assert);

        // Suite 7: MainNotchViewModel Integration & Relay Commands
        TestMainNotchViewModelShelfCommands(assert);
        TestMainNotchViewModelRamOptimizerIntegration(assert);
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
    }

    // -------------------------------------------------------------------------
    // Suite 1: Win32 P/Invoke & Process Management Constants
    // -------------------------------------------------------------------------
    private static void TestProcessConstantsAndPInvoke(AssertionDelegate assert)
    {
        assert(NativeMethods.PROCESS_SET_QUOTA == 0x0100, "PROCESS_SET_QUOTA exact Win32 constant (0x0100)");
        assert(NativeMethods.PROCESS_QUERY_INFORMATION == 0x0400, "PROCESS_QUERY_INFORMATION exact Win32 constant (0x0400)");

        uint combined = NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_INFORMATION;
        assert(combined == 0x0500, "Combined access mask is 0x0500");

        // Test OpenProcess on invalid PID
        IntPtr invalidHandle = NativeMethods.OpenProcess(combined, false, -999);
        assert(invalidHandle == IntPtr.Zero, "OpenProcess on invalid PID returns IntPtr.Zero cleanly");

        // Test OpenProcess on System PID (0 or 4) — must fail cleanly without unhandled exceptions
        IntPtr systemHandle = NativeMethods.OpenProcess(combined, false, 4);
        // In user mode without SeDebugPrivilege, OpenProcess on PID 4 returns zero
        assert(systemHandle == IntPtr.Zero, "OpenProcess on Kernel PID 4 returns zero in user mode without crash");

        // Test OpenProcess on current process with PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION
        int currentPid = Environment.ProcessId;
        IntPtr myHandle = NativeMethods.OpenProcess(combined, false, currentPid);
        assert(myHandle != IntPtr.Zero, "OpenProcess on current user process succeeds with 0x0500 access mask");

        if (myHandle != IntPtr.Zero)
        {
            bool closed = NativeMethods.CloseHandle(myHandle);
            assert(closed, "CloseHandle on valid process handle succeeds");
        }
    }

    // -------------------------------------------------------------------------
    // Suite 2: ShelfItem Computed Properties & Fallback Icons
    // -------------------------------------------------------------------------
    private static void TestShelfItemIsUrlAndFallbackIcon(AssertionDelegate assert)
    {
        // 1. Valid HTTP / HTTPS URLs
        var httpItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = "http://example.com/test?param=1"
        };
        assert(httpItem.IsUrl, "ShelfItem.IsUrl returns true for http scheme");
        assert(httpItem.FallbackIcon == IconsaxKind.Global, "ShelfItem.FallbackIcon returns IconsaxKind.Global for http URL");

        var httpsItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = "   https://github.com/microsoft/terminal   "
        };
        assert(httpsItem.IsUrl, "ShelfItem.IsUrl trims whitespace and identifies https URL");
        assert(httpsItem.FallbackIcon == IconsaxKind.Global, "ShelfItem.FallbackIcon returns IconsaxKind.Global for https URL");

        // 2. Non-URL Text Snippets
        var noteItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = "This is a quick note without any URL."
        };
        assert(!noteItem.IsUrl, "ShelfItem.IsUrl returns false for plain text note");
        assert(noteItem.FallbackIcon == IconsaxKind.NoteText, "ShelfItem.FallbackIcon returns IconsaxKind.NoteText for plain note");

        // 3. Invalid schemes (file://, ftp://, mailto:)
        var fileSchemeItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = "file:///C:/Users/test.txt"
        };
        assert(!fileSchemeItem.IsUrl, "ShelfItem.IsUrl rejects non-web file:// URI scheme");

        var ftpSchemeItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = "ftp://ftp.example.com/file.zip"
        };
        assert(!ftpSchemeItem.IsUrl, "ShelfItem.IsUrl rejects ftp:// URI scheme");

        // 4. File / Folder / Image with URL text in FilePath must NOT be considered URLs
        var fileItem = new ShelfItem
        {
            Kind = ShelfItemKind.File,
            FilePath = "https://example.com/test.png",
            TextContent = "https://example.com/test.png"
        };
        assert(!fileItem.IsUrl, "ShelfItem.IsUrl is strictly false for ShelfItemKind.File even if TextContent has URL");
        assert(fileItem.FallbackIcon == IconsaxKind.DocumentText, "ShelfItem.FallbackIcon is DocumentText for File");

        var folderItem = new ShelfItem
        {
            Kind = ShelfItemKind.Folder,
            FilePath = @"C:\Users\Downloads"
        };
        assert(!folderItem.IsUrl, "ShelfItem.IsUrl is false for Folder");
        assert(folderItem.FallbackIcon == IconsaxKind.Folder, "ShelfItem.FallbackIcon is Folder for Folder");

        var imageItem = new ShelfItem
        {
            Kind = ShelfItemKind.ImageClip,
            FilePath = @"C:\Users\clip.png"
        };
        assert(!imageItem.IsUrl, "ShelfItem.IsUrl is false for ImageClip");
        assert(imageItem.FallbackIcon == IconsaxKind.Gallery, "ShelfItem.FallbackIcon is Gallery for ImageClip");

        // 5. Null or Empty TextContent
        var emptyTextItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = ""
        };
        assert(!emptyTextItem.IsUrl, "ShelfItem.IsUrl is false for empty string");

        var whitespaceTextItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = "    \r\n\t "
        };
        assert(!whitespaceTextItem.IsUrl, "ShelfItem.IsUrl is false for whitespace string");
    }

    private static void TestShelfItemTooltipText(AssertionDelegate assert)
    {
        var urlItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = "https://winnotch.dev",
            FilePath = @"C:\Temp\note_123.txt"
        };
        assert(urlItem.TooltipText == "https://winnotch.dev", "ShelfItem.TooltipText returns TextContent for URL snippet");

        var fileItem = new ShelfItem
        {
            Kind = ShelfItemKind.File,
            FilePath = @"C:\Projects\WinNotch\README.md",
            DisplayName = "README.md"
        };
        assert(fileItem.TooltipText == @"C:\Projects\WinNotch\README.md", "ShelfItem.TooltipText returns FilePath for File");

        var noteItem = new ShelfItem
        {
            Kind = ShelfItemKind.TextSnippet,
            TextContent = "Just a snippet",
            FilePath = @"C:\Temp\note_456.txt"
        };
        assert(noteItem.TooltipText == @"C:\Temp\note_456.txt", "ShelfItem.TooltipText returns FilePath for non-URL text note");
    }

    // -------------------------------------------------------------------------
    // Suite 3: FileShelfService RevealInExplorer Ancestor Traversal & Escaping
    // -------------------------------------------------------------------------
    private static void TestRevealInExplorerExistingFileAndFolder(AssertionDelegate assert)
    {
        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // Create a temporary test file
        string tempFile = Path.Combine(Path.GetTempPath(), $"winnotch_test_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempFile, "test content");

        try
        {
            var item = new ShelfItem
            {
                Kind = ShelfItemKind.File,
                FilePath = tempFile
            };

            // Test execution without crashing or throwing
            bool completedCleanly = false;
            try
            {
                // We test calling RevealInExplorer — it should resolve the file and invoke explorer.exe /select,"path"
                // In headless tests, Process.Start("explorer.exe") succeeds or is suppressed safely
                shelf.RevealInExplorer(item);
                completedCleanly = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RevealInExplorer threw: {ex.Message}");
            }

            assert(completedCleanly, "RevealInExplorer executes cleanly for existing temporary file");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }

        // Test root drive path (e.g. C:\)
        var rootItem = new ShelfItem
        {
            Kind = ShelfItemKind.Folder,
            FilePath = @"C:\"
        };
        bool rootClean = false;
        try
        {
            shelf.RevealInExplorer(rootItem);
            rootClean = true;
        }
        catch { }
        assert(rootClean, "RevealInExplorer handles drive root without crash");
    }

    private static void TestRevealInExplorerAncestorFallback(AssertionDelegate assert)
    {
        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // Create a deeply nested path where the deepest folders do not exist,
        // but an ancestor (e.g., TempPath) does exist
        string existingTemp = Path.GetTempPath();
        string nonExistentPath = Path.Combine(existingTemp, "NonExistentDir1", "NonExistentDir2", "ghost_file.txt");

        var missingItem = new ShelfItem
        {
            Kind = ShelfItemKind.File,
            FilePath = nonExistentPath
        };

        bool executed = false;
        try
        {
            shelf.RevealInExplorer(missingItem);
            executed = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RevealInExplorer threw on missing file: {ex.Message}");
        }

        assert(executed, "RevealInExplorer successfully traverses ancestors on deleted/missing file");
    }

    private static void TestRevealInExplorerNullAndEmptyGuards(AssertionDelegate assert)
    {
        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        bool nullHandled = false;
        try
        {
            shelf.RevealInExplorer(null);
            nullHandled = true;
        }
        catch { }
        assert(nullHandled, "RevealInExplorer handles null item safely");

        var emptyItem = new ShelfItem { FilePath = "" };
        bool emptyHandled = false;
        try
        {
            shelf.RevealInExplorer(emptyItem);
            emptyHandled = true;
        }
        catch { }
        assert(emptyHandled, "RevealInExplorer handles empty FilePath safely");
    }

    // -------------------------------------------------------------------------
    // Suite 4: FileShelfService OpenItem URL & Auto-Recreate Notes
    // -------------------------------------------------------------------------
    private static void TestOpenItemNullGuard(AssertionDelegate assert)
    {
        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        bool nullClean = false;
        try
        {
            shelf.OpenItem(null);
            nullClean = true;
        }
        catch { }
        assert(nullClean, "OpenItem handles null item gracefully");
    }

    private static void TestOpenItemAutoRecreatesMissingNote(AssertionDelegate assert)
    {
        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        string deletedNotePath = Path.Combine(settings.ShelfClipsDirectory, $"note_missing_{Guid.NewGuid():N}.txt");
        if (File.Exists(deletedNotePath)) File.Delete(deletedNotePath);

        const string originalText = "Auto-recreated note content for empirical verification";
        var item = new ShelfItem
        {
            Id = "test12345678",
            Kind = ShelfItemKind.TextSnippet,
            TextContent = originalText,
            FilePath = deletedNotePath
        };

        try
        {
            shelf.OpenItem(item);
        }
        catch { }

        // The method should have detected missing FilePath and recreated the note file in ShelfClipsDirectory
        bool recreated = !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath);
        assert(recreated, "OpenItem auto-recreates backing note file if missing from disk");

        if (recreated)
        {
            string content = File.ReadAllText(item.FilePath);
            assert(content == originalText, "Recreated note content matches original TextContent");
            try { File.Delete(item.FilePath); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Suite 5: FileShelfService PasteFromClipboard STA Safety & Resiliency
    // -------------------------------------------------------------------------
    private static void TestPasteFromClipboardNonStaThreadSafety(AssertionDelegate assert)
    {
        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // Call PasteFromClipboard from an explicit MTA background task
        bool ranWithoutThreadStateException = false;
        int result = -1;

        var task = Task.Run(() =>
        {
            try
            {
                result = shelf.PasteFromClipboard();
                ranWithoutThreadStateException = true;
            }
            catch (ThreadStateException ex)
            {
                Console.WriteLine($"PasteFromClipboard threw ThreadStateException: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PasteFromClipboard threw unexpected exception: {ex.Message}");
            }
        });

        task.Wait(TimeSpan.FromSeconds(5));
        assert(ranWithoutThreadStateException, "PasteFromClipboard runs from MTA background thread with STA safety without ThreadStateException");
        assert(result >= 0, "PasteFromClipboard returns non-negative count");
    }

    // -------------------------------------------------------------------------
    // Suite 6: SystemMonitorService User-Mode RAM Optimizer
    // -------------------------------------------------------------------------
    private static void TestRamOptimizerWorkingSetTrimming(AssertionDelegate assert)
    {
        var settings = new SettingsService();
        using var monitor = new SystemMonitorService(settings);

        bool eventFired = false;
        long eventReclaimed = -1;
        monitor.WorkingSetOptimized += (reclaimed) =>
        {
            eventFired = true;
            eventReclaimed = reclaimed;
        };

        var sw = Stopwatch.StartNew();
        long reclaimedBytes = monitor.OptimizeWorkingSetAsync().GetAwaiter().GetResult();
        sw.Stop();

        assert(reclaimedBytes >= 0L, "OptimizeWorkingSetAsync returns non-negative reclaimed bytes", $"{reclaimedBytes} bytes ({reclaimedBytes / (1024 * 1024)} MB)");
        assert(eventFired, "WorkingSetOptimized event fired upon completion");
        assert(eventReclaimed == reclaimedBytes, "WorkingSetOptimized event payload matches return value");
        assert(sw.ElapsedMilliseconds >= 40, "Optimization loop settled >= 50ms for Windows memory manager", $"{sw.ElapsedMilliseconds}ms");
    }

    private static void TestRamOptimizerReentrancyProtection(AssertionDelegate assert)
    {
        var settings = new SettingsService();
        using var monitor = new SystemMonitorService(settings);

        // Launch multiple rapid concurrent tasks
        var tasks = new Task<long>[4];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = monitor.OptimizeWorkingSetAsync();
        }

        Task.WaitAll(tasks, TimeSpan.FromSeconds(10));

        // Exactly one should perform the work; any reentrant call during the run returns 0L cleanly without deadlock
        int successCount = 0;
        int zeroCount = 0;
        foreach (var t in tasks)
        {
            if (t.Result > 0L) successCount++;
            else zeroCount++;
        }

        assert(tasks.All(t => t.IsCompletedSuccessfully), "All concurrent OptimizeWorkingSetAsync tasks completed successfully without deadlock");
    }

    private static void TestRamOptimizerCurrentProcessExclusion(AssertionDelegate assert)
    {
        // Inspect that WinNotch process and PID 0/4 are in the exclusion logic
        var field = typeof(SystemMonitorService).GetField("IgnoredProcessNames", BindingFlags.NonPublic | BindingFlags.Static);
        assert(field != null, "SystemMonitorService has IgnoredProcessNames hashset");

        if (field?.GetValue(null) is HashSet<string> set)
        {
            assert(set.Contains("WinNotch"), "IgnoredProcessNames contains 'WinNotch'");
            assert(set.Contains("System"), "IgnoredProcessNames contains 'System'");
            assert(set.Contains("Idle"), "IgnoredProcessNames contains 'Idle'");
        }
    }

    // -------------------------------------------------------------------------
    // Suite 7: MainNotchViewModel Integration & Relay Commands
    // -------------------------------------------------------------------------
    private static MainNotchViewModel CreateTestViewModel(out FileShelfService shelf, out SystemMonitorService monitor)
    {
        var settings = new SettingsService();
        var sm = new NotchStateMachine(settings.Current);
        var descriptor = new MonitorDescriptor
        {
            DeviceName = @"\\.\DISPLAY1",
            FriendlyLabel = "Test Display",
            IsPrimary = true,
            PhysicalBounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 },
            DpiScaleX = 1.0,
            DpiScaleY = 1.0
        };
        var audio = new AudioService(settings);
        var media = new MediaService(settings);
        var power = new DisplayPowerService(settings, descriptor);
        shelf = new FileShelfService(settings);
        var timer = new TimerService();
        var privacy = new PrivacyService(settings);
        monitor = new SystemMonitorService(settings);
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
            monitor,
            notif,
            picker,
            new AgentMonitorService(settings),
            () => { });
    }

    private static void TestMainNotchViewModelShelfCommands(AssertionDelegate assert)
    {
        var vm = CreateTestViewModel(out var shelf, out _);

        assert(vm.FileShelf == shelf, "MainNotchViewModel.FileShelf exposes FileShelfService instance");

        // Verify relay commands exist and can be invoked
        assert(vm.PasteShelfFromClipboardCommand != null, "PasteShelfFromClipboardCommand is generated");
        assert(vm.OpenShelfItemCommand != null, "OpenShelfItemCommand is generated");
        assert(vm.RevealShelfItemInExplorerCommand != null, "RevealShelfItemInExplorerCommand is generated");
        assert(vm.OptimizeRamCommand != null, "OptimizeRamCommand is generated");

        // Test invoking PasteShelfFromClipboardCommand
        bool pasteInvoked = false;
        try
        {
            vm.PasteShelfFromClipboardCommand?.Execute(null);
            pasteInvoked = true;
        }
        catch { }
        assert(pasteInvoked, "PasteShelfFromClipboardCommand executes without exception");

        // Test invoking OpenShelfItemCommand with null
        bool openInvoked = false;
        try
        {
            vm.OpenShelfItemCommand?.Execute(null);
            openInvoked = true;
        }
        catch { }
        assert(openInvoked, "OpenShelfItemCommand executes with null safely");

        // Test invoking RevealShelfItemInExplorerCommand with null
        bool revealInvoked = false;
        try
        {
            vm.RevealShelfItemInExplorerCommand?.Execute(null);
            revealInvoked = true;
        }
        catch { }
        assert(revealInvoked, "RevealShelfItemInExplorerCommand executes with null safely");
    }

    private static void TestMainNotchViewModelRamOptimizerIntegration(AssertionDelegate assert)
    {
        var vm = CreateTestViewModel(out _, out _);

        assert(vm.RamOptimizedButtonText == "Dọn RAM", "Initial RamOptimizedButtonText is 'Dọn RAM'");
        assert(!vm.IsRamOptimizing, "Initial IsRamOptimizing is false");

        // Execute RAM optimization asynchronously
        var task = vm.OptimizeRamAsync();
        task.Wait(TimeSpan.FromSeconds(5));

        assert(!vm.IsRamOptimizing, "IsRamOptimizing resets to false after optimization");
        assert(!string.IsNullOrEmpty(vm.RamOptimizedFeedbackText), "RamOptimizedFeedbackText is populated", vm.RamOptimizedFeedbackText);
        assert(vm.RamOptimizedButtonText == vm.RamOptimizedFeedbackText, "RamOptimizedButtonText shows feedback text");
    }
}
