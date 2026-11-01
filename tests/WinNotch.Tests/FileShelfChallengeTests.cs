using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Models;
using WinNotch.Services;

namespace WinNotch.Tests;

public static class FileShelfChallengeTests
{
    public static void RunAll(AssertionDelegate assert)
    {
        Console.WriteLine();
        Console.WriteLine("=========================================================================");
        Console.WriteLine(" MILESTONE 4: FILE SHELF ENHANCEMENTS EMPIRICAL ADVERSARIAL SUITE        ");
        Console.WriteLine("=========================================================================");

        EnsureWpfEnvironment();

        // Suite 1: ShelfItem.IsUrl Adversarial Edge Cases & Oracles
        TestShelfItemIsUrlAdversarialEdgeCases(assert);
        TestShelfItemIsUrlRelativeAndNonHttpSchemes(assert);
        TestShelfItemIsUrlMalformedAndHostValidation(assert);
        TestShelfItemIsUrlCrossKindIsolationAndIcons(assert);

        // Suite 2: PasteFromClipboard STA Apartment & Concurrency Stress
        TestPasteFromClipboardMtaThreadIndependence(assert);
        TestPasteFromClipboardConcurrentMtaTorture(assert);

        // Suite 3: Clipboard Ingestion Precedence & Extreme File Drop Batching
        TestClipboardIngestionPrecedence(assert);
        TestPasteExtremeFileDropBatching(assert);

        // Suite 4: 32-Bit PNG Alpha Transparency Preservation
        Test32BitPngAlphaTransparencyPreservation(assert);
        TestCorruptPngStreamResilience(assert);

        // Suite 5: OpenItem Edge Cases, URL Dispatch & Note Regeneration
        TestOpenItemEdgeCasesAndNullGuards(assert);
        TestOpenItemMissingNoteRegeneration(assert);
        TestOpenItemShortIdRegeneration(assert);

        // Suite 6: RevealInExplorer Root Drive, Ancestor Traversal & Malformed Paths
        TestRevealInExplorerSpecialCharactersAndTrailingSlash(assert);
        TestRevealInExplorerRootDriveVsSubfolder(assert);
        TestRevealInExplorerAncestorTraversalDeepAndGhostDrive(assert);
        TestRevealInExplorerMalformedPaths(assert);
    }

    private static BitmapSource CreateTest32BitBitmap(int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;      // B
            pixels[i + 1] = 100;  // G
            pixels[i + 2] = 50;   // R
            pixels[i + 3] = 255;  // A
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
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
    // Suite 1: ShelfItem.IsUrl Adversarial Edge Cases & Oracles
    // -------------------------------------------------------------------------
    private static void TestShelfItemIsUrlAdversarialEdgeCases(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S1] ShelfItem.IsUrl Empty / Whitespace / Null Edge Cases ---");

        var item = new ShelfItem { Kind = ShelfItemKind.TextSnippet, TextContent = null };
        assert(!item.IsUrl, "Null TextContent returns IsUrl == false");
        assert(item.FallbackIcon == IconsaxKind.NoteText, "Null TextContent fallback icon is NoteText");

        item.TextContent = string.Empty;
        assert(!item.IsUrl, "Empty TextContent returns IsUrl == false");
        assert(item.FallbackIcon == IconsaxKind.NoteText, "Empty TextContent fallback icon is NoteText");

        item.TextContent = "   \t  \r\n   ";
        assert(!item.IsUrl, "Whitespace TextContent returns IsUrl == false");
        assert(item.FallbackIcon == IconsaxKind.NoteText, "Whitespace TextContent fallback icon is NoteText");

        item.TextContent = "Just a quick note without URLs";
        assert(!item.IsUrl, "Plain note text returns IsUrl == false");
        assert(item.FallbackIcon == IconsaxKind.NoteText, "Plain note text fallback icon is NoteText");
    }

    private static void TestShelfItemIsUrlRelativeAndNonHttpSchemes(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S1] ShelfItem.IsUrl Relative URLs & Non-HTTP Schemes ---");

        string[] relativeUrls =
        [
            "/dashboard",
            "./index.html",
            "../parent/doc.txt",
            "?query=true",
            "#section-header",
            "file.txt",
            "www.google.com",
            "github.com/microsoft"
        ];

        int relFailCount = 0;
        foreach (var rel in relativeUrls)
        {
            var item = new ShelfItem { Kind = ShelfItemKind.TextSnippet, TextContent = rel };
            if (!item.IsUrl) relFailCount++;
        }
        assert(relFailCount == relativeUrls.Length, $"All {relativeUrls.Length} relative / schemeless URIs rejected by IsUrl ({relFailCount}/{relativeUrls.Length})");

        string[] nonHttpSchemes =
        [
            "file:///C:/Users/test.txt",
            "ftp://ftp.is.co.za/rfc/rfc1808.txt",
            "javascript:alert(1)",
            "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==",
            "mailto:support@winnotch.dev",
            "ws://echo.websocket.org",
            "wss://echo.websocket.org",
            "about:blank",
            "tel:+18005550199",
            "ssh://git@github.com:microsoft/terminal.git",
            "custom-proto://open",
            "view-source:https://example.com"
        ];

        int nonHttpFailCount = 0;
        foreach (var nonHttp in nonHttpSchemes)
        {
            var item = new ShelfItem { Kind = ShelfItemKind.TextSnippet, TextContent = nonHttp };
            if (!item.IsUrl) nonHttpFailCount++;
        }
        assert(nonHttpFailCount == nonHttpSchemes.Length, $"All {nonHttpSchemes.Length} non-HTTP schemes rejected by IsUrl ({nonHttpFailCount}/{nonHttpSchemes.Length})");
    }

    private static void TestShelfItemIsUrlMalformedAndHostValidation(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S1] ShelfItem.IsUrl Malformed Strings & Host Validation ---");

        string[] malformedStrings =
        [
            "http://",
            "https://",
            "http://:80",
            "http://example .com",
            "https://\0evil.com",
            "http://foo\r\nbar.com"
        ];

        int malformedFailCount = 0;
        foreach (var mal in malformedStrings)
        {
            var item = new ShelfItem { Kind = ShelfItemKind.TextSnippet, TextContent = mal };
            if (!item.IsUrl) malformedFailCount++;
        }
        assert(malformedFailCount == malformedStrings.Length, $"All {malformedStrings.Length} malformed URLs safely rejected by IsUrl ({malformedFailCount}/{malformedStrings.Length})");

        // Valid URLs with whitespace padding, query parameters, IPv4/IPv6, and port numbers
        string[] validUrls =
        [
            "http://localhost",
            "http://localhost:5000",
            "http://127.0.0.1:8080/api/v1?id=42#frag",
            "http://[::1]:8080/test",
            "  https://github.com/microsoft/terminal  ",
            "\thttp://example.com/path?foo=bar&baz=1#top\n",
            "HTTPS://MICROSOFT.COM/EN-US/"
        ];

        int validPassCount = 0;
        foreach (var val in validUrls)
        {
            var item = new ShelfItem { Kind = ShelfItemKind.TextSnippet, TextContent = val };
            if (item.IsUrl && item.FallbackIcon == IconsaxKind.Global && item.TooltipText == item.TextContent)
            {
                validPassCount++;
            }
        }
        assert(validPassCount == validUrls.Length, $"All {validUrls.Length} valid URLs recognized by IsUrl with Global icon and TooltipText ({validPassCount}/{validUrls.Length})");
    }

    private static void TestShelfItemIsUrlCrossKindIsolationAndIcons(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S1] ShelfItem.IsUrl Cross-Kind Isolation & Tooltip Oracle ---");

        const string testUrl = "https://winnotch.dev";
        const string fakeFilePath = @"C:\Users\clip.png";

        var fileItem = new ShelfItem { Kind = ShelfItemKind.File, FilePath = fakeFilePath, TextContent = testUrl };
        assert(!fileItem.IsUrl, "ShelfItemKind.File rejects IsUrl even if TextContent has URL");
        assert(fileItem.FallbackIcon == IconsaxKind.DocumentText, "ShelfItemKind.File uses IconsaxKind.DocumentText");
        assert(fileItem.TooltipText == fakeFilePath, "ShelfItemKind.File TooltipText returns FilePath");

        var folderItem = new ShelfItem { Kind = ShelfItemKind.Folder, FilePath = @"C:\Users\Docs", TextContent = testUrl };
        assert(!folderItem.IsUrl, "ShelfItemKind.Folder rejects IsUrl even if TextContent has URL");
        assert(folderItem.FallbackIcon == IconsaxKind.Folder, "ShelfItemKind.Folder uses IconsaxKind.Folder");
        assert(folderItem.TooltipText == @"C:\Users\Docs", "ShelfItemKind.Folder TooltipText returns FilePath");

        var imageItem = new ShelfItem { Kind = ShelfItemKind.ImageClip, FilePath = fakeFilePath, TextContent = testUrl };
        assert(!imageItem.IsUrl, "ShelfItemKind.ImageClip rejects IsUrl even if TextContent has URL");
        assert(imageItem.FallbackIcon == IconsaxKind.Gallery, "ShelfItemKind.ImageClip uses IconsaxKind.Gallery");
        assert(imageItem.TooltipText == fakeFilePath, "ShelfItemKind.ImageClip TooltipText returns FilePath");

        var textSnippetItem = new ShelfItem { Kind = ShelfItemKind.TextSnippet, FilePath = @"C:\Temp\note.txt", TextContent = testUrl };
        assert(textSnippetItem.IsUrl, "ShelfItemKind.TextSnippet accepts IsUrl when TextContent has URL");
        assert(textSnippetItem.FallbackIcon == IconsaxKind.Global, "ShelfItemKind.TextSnippet uses IconsaxKind.Global for URL");
        assert(textSnippetItem.TooltipText == testUrl, "ShelfItemKind.TextSnippet TooltipText returns URL TextContent");
    }

    // -------------------------------------------------------------------------
    // Suite 2: PasteFromClipboard STA Apartment & Concurrency Stress
    // -------------------------------------------------------------------------
    private static void TestPasteFromClipboardMtaThreadIndependence(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S2] PasteFromClipboard MTA Background Thread Execution ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        bool mtaExecuted = false;
        Exception? caughtException = null;

        var mtaThread = new Thread(() =>
        {
            try
            {
                shelf.PasteFromClipboard();
                mtaExecuted = true;
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }
        });
        mtaThread.SetApartmentState(ApartmentState.MTA);
        mtaThread.IsBackground = true;
        mtaThread.Start();
        bool joined = mtaThread.Join(TimeSpan.FromSeconds(5));

        assert(joined, "PasteFromClipboard on MTA thread joined within 5s");
        assert(mtaExecuted, "PasteFromClipboard on MTA thread executed successfully");
        assert(caughtException == null, "PasteFromClipboard threw zero ThreadStateException or other exceptions", caughtException?.ToString());
    }

    private static void TestPasteFromClipboardConcurrentMtaTorture(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S2] PasteFromClipboard Concurrent MTA Torture (10 Threads) ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        const int threadCount = 10;
        var threads = new Thread[threadCount];
        int successCount = 0;
        int exceptionCount = 0;

        for (int i = 0; i < threadCount; i++)
        {
            threads[i] = new Thread(() =>
            {
                try
                {
                    shelf.PasteFromClipboard();
                    Interlocked.Increment(ref successCount);
                }
                catch
                {
                    Interlocked.Increment(ref exceptionCount);
                }
            });
            threads[i].SetApartmentState(ApartmentState.MTA);
            threads[i].IsBackground = true;
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join(TimeSpan.FromSeconds(5));

        assert(successCount == threadCount, $"All {threadCount} concurrent MTA threads completed PasteFromClipboard ({successCount}/{threadCount})");
        assert(exceptionCount == 0, "Zero exceptions thrown during concurrent MTA torture");
    }

    // -------------------------------------------------------------------------
    // Suite 3: Clipboard Ingestion Precedence & Extreme File Drop Batching
    // -------------------------------------------------------------------------
    private static void TestClipboardIngestionPrecedence(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S3] Clipboard Ingestion Precedence Oracle ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // 1. DataObject with BOTH FileDrop and UnicodeText
        // Precedence rule: FileDrop MUST take priority over Text!
        string tempFile = Path.Combine(Path.GetTempPath(), $"precedence_test_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempFile, "temp content");

        try
        {
            var dataObj1 = new DataObject();
            dataObj1.SetData(DataFormats.FileDrop, new[] { tempFile });
            dataObj1.SetData(DataFormats.UnicodeText, "https://should-be-ignored.com");

            int initialCount = shelf.Items.Count;
            int added1 = shelf.AcceptDrop(dataObj1);

            assert(added1 == 1, "AcceptDrop ingested 1 item when both FileDrop and UnicodeText present");
            assert(shelf.Items.Count == initialCount + 1, "Shelf item count incremented by 1");
            assert(shelf.Items[0].Kind == ShelfItemKind.File, "Ingested item is Kind == File, proving FileDrop took precedence over Text");
            assert(shelf.Items[0].FilePath == tempFile, "Ingested item matches FileDrop path");

            // 2. DataObject with BOTH Bitmap and Text
            // Precedence rule: Image MUST take priority over Text!
            var bmp = CreateTest32BitBitmap(16, 16);
            var dataObj2 = new DataObject();
            dataObj2.SetData(DataFormats.Bitmap, bmp);
            dataObj2.SetData(DataFormats.UnicodeText, "https://also-should-be-ignored.com");

            int added2 = shelf.AcceptDrop(dataObj2);
            assert(added2 == 1, "AcceptDrop ingested 1 item when both Bitmap and UnicodeText present");
            assert(shelf.Items[0].Kind == ShelfItemKind.ImageClip, "Ingested item is Kind == ImageClip, proving Bitmap took precedence over Text");

            // 3. DataObject with ONLY Text
            var dataObj3 = new DataObject();
            dataObj3.SetData(DataFormats.UnicodeText, "https://winnotch.dev");

            int added3 = shelf.AcceptDrop(dataObj3);
            assert(added3 == 1, "AcceptDrop ingested 1 item when only UnicodeText present");
            assert(shelf.Items[0].Kind == ShelfItemKind.TextSnippet, "Ingested item is Kind == TextSnippet");
            assert(shelf.Items[0].IsUrl, "Ingested TextSnippet correctly recognized as URL");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            shelf.ClearAll();
        }
    }

    private static void TestPasteExtremeFileDropBatching(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S3] Extreme FileDrop Batching (500 Files) ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);
        shelf.ClearAll();

        const int fileCount = 500;
        string tempDir = Path.Combine(Path.GetTempPath(), $"winnotch_batch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePaths = new string[fileCount];

        try
        {
            for (int i = 0; i < fileCount; i++)
            {
                filePaths[i] = Path.Combine(tempDir, $"batch_file_{i:D4}.txt");
                File.WriteAllText(filePaths[i], $"Batch file payload {i}");
            }

            var dataObj = new DataObject(DataFormats.FileDrop, filePaths);

            var sw = Stopwatch.StartNew();
            int added = shelf.AcceptDrop(dataObj);
            sw.Stop();

            assert(added == fileCount, $"AcceptDrop ingested all {fileCount} files in batch (added={added}) in {sw.ElapsedMilliseconds}ms");
            assert(shelf.Items.Count == fileCount, $"Shelf Items.Count is exactly {fileCount}");

            // Verify persistence to disk
            bool shelfFileExists = File.Exists(settings.ShelfFilePath);
            assert(shelfFileExists, "shelf.json exists on disk after batch drop");

            if (shelfFileExists)
            {
                string json = File.ReadAllText(settings.ShelfFilePath);
                assert(json.Contains("batch_file_0000.txt") && json.Contains("batch_file_0499.txt"), "shelf.json contains first and last batch items");
            }
        }
        finally
        {
            shelf.ClearAll();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Suite 4: 32-Bit PNG Alpha Transparency Preservation
    // -------------------------------------------------------------------------
    private static void Test32BitPngAlphaTransparencyPreservation(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S4] 32-Bit PNG Alpha Transparency Preservation ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // Create a 16x16 32-bit test bitmap with known alpha values
        int width = 16, height = 16;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];

        // Pixel (0, 0): Completely transparent (A=0, R=0, G=0, B=0)
        pixels[0] = 0; pixels[1] = 0; pixels[2] = 0; pixels[3] = 0;

        // Pixel (1, 1): 50% Semi-transparent red (A=128, R=255, G=0, B=0)
        int idx1 = (1 * width + 1) * 4;
        pixels[idx1] = 0; pixels[idx1 + 1] = 0; pixels[idx1 + 2] = 255; pixels[idx1 + 3] = 128;

        // Pixel (2, 2): 100% Opaque green (A=255, R=0, G=255, B=0)
        int idx2 = (2 * width + 2) * 4;
        pixels[idx2] = 0; pixels[idx2 + 1] = 255; pixels[idx2 + 2] = 0; pixels[idx2 + 3] = 255;

        var bmpSource = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bmpSource.Freeze();

        // Encode to PNG stream (as browsers do when copying PNGs with alpha to clipboard)
        using var pngMs = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmpSource));
        encoder.Save(pngMs);
        pngMs.Position = 0;

        // Feed to AddBitmapClip directly (or via DataObject with "PNG" / "image/png")
        var decoder = new PngBitmapDecoder(pngMs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource decodedFrame = decoder.Frames[0];

        // Verify that decodedFrame retains 32-bit format
        assert(decodedFrame.Format.BitsPerPixel == 32, "Decoded PNG frame has 32 bits per pixel");
        assert(decodedFrame.Format == PixelFormats.Bgra32 || decodedFrame.Format == PixelFormats.Pbgra32, "Decoded PNG frame uses Bgra32/Pbgra32 format");

        // Verify that AddBitmapClip saves this without flattening alpha
        var addBitmapClipMethod = typeof(FileShelfService).GetMethod("AddBitmapClip", BindingFlags.NonPublic | BindingFlags.Instance);
        assert(addBitmapClipMethod != null, "FileShelfService has AddBitmapClip method");

        if (addBitmapClipMethod != null)
        {
            bool added = (bool)addBitmapClipMethod.Invoke(shelf, new object[] { decodedFrame })!;
            assert(added, "AddBitmapClip succeeded");

            if (added && shelf.Items.Count > 0)
            {
                var savedItem = shelf.Items[0];
                assert(savedItem.Kind == ShelfItemKind.ImageClip, "Saved item is Kind == ImageClip");
                assert(File.Exists(savedItem.FilePath), "Saved PNG file exists on disk");

                // Read saved PNG back from disk and verify pixels
                using var readStream = new FileStream(savedItem.FilePath, FileMode.Open, FileAccess.Read);
                var verifyDecoder = new PngBitmapDecoder(readStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapSource verifyFrame = verifyDecoder.Frames[0];

                assert(verifyFrame.Format.BitsPerPixel == 32, "Saved PNG file retains 32 bits per pixel");

                byte[] readPixels = new byte[height * stride];
                verifyFrame.CopyPixels(readPixels, stride, 0);

                byte alpha0 = readPixels[3];
                byte alpha1 = readPixels[idx1 + 3];
                byte alpha2 = readPixels[idx2 + 3];

                assert(alpha0 == 0, $"Pixel (0,0) alpha is 0 (got {alpha0})");
                assert(Math.Abs(alpha1 - 128) <= 2, $"Pixel (1,1) semi-transparent alpha ~128 preserved (got {alpha1})");
                assert(alpha2 == 255, $"Pixel (2,2) opaque alpha 255 preserved (got {alpha2})");

                shelf.RemoveItem(savedItem);
            }
        }
    }

    private static void TestCorruptPngStreamResilience(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S4] Corrupt PNG Stream Resiliency ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // Feed DataObject with corrupt PNG stream (5 junk bytes)
        var corruptDataObj = new DataObject();
        var corruptMs = new MemoryStream(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01 });
        corruptDataObj.SetData("PNG", corruptMs);

        bool completedCleanly = false;
        try
        {
            shelf.AcceptDrop(corruptDataObj);
            completedCleanly = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AcceptDrop threw on corrupt PNG stream: {ex.Message}");
        }

        assert(completedCleanly, "AcceptDrop safely handles corrupted PNG stream without unhandled exception");
    }

    // -------------------------------------------------------------------------
    // Suite 5: OpenItem Edge Cases, URL Dispatch & Note Regeneration
    // -------------------------------------------------------------------------
    private static void TestOpenItemEdgeCasesAndNullGuards(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S5] OpenItem Null Guards & Missing Files ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        bool nullClean = false;
        try
        {
            shelf.OpenItem(null);
            nullClean = true;
        }
        catch { }
        assert(nullClean, "OpenItem(null) does not throw");

        bool emptyClean = false;
        try
        {
            shelf.OpenItem(new ShelfItem { FilePath = "" });
            emptyClean = true;
        }
        catch { }
        assert(emptyClean, "OpenItem with empty FilePath does not throw");

        bool ghostClean = false;
        try
        {
            shelf.OpenItem(new ShelfItem { Kind = ShelfItemKind.File, FilePath = @"C:\NonExistent_Ghost_File_12345.xyz" });
            ghostClean = true;
        }
        catch { }
        assert(ghostClean, "OpenItem with non-existent FilePath exits cleanly without throwing");
    }

    private static void TestOpenItemMissingNoteRegeneration(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S5] OpenItem Missing Note Auto-Recreation ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        const string testNote = "Secret empirical verification note";
        string ghostPath = Path.Combine(settings.ShelfClipsDirectory, $"ghost_note_{Guid.NewGuid():N}.txt");

        var noteItem = new ShelfItem
        {
            Id = "1234567890abcdef",
            Kind = ShelfItemKind.TextSnippet,
            TextContent = testNote,
            FilePath = ghostPath
        };

        // Ensure ghostPath does not exist
        if (File.Exists(ghostPath)) File.Delete(ghostPath);

        try
        {
            shelf.OpenItem(noteItem);
        }
        catch { }

        // OpenItem should recreate the file in ShelfClipsDirectory as note_{id[..8]}.txt
        string expectedPath = Path.Combine(settings.ShelfClipsDirectory, "note_12345678.txt");
        bool recreated = File.Exists(expectedPath);

        assert(recreated, "OpenItem auto-recreated missing note file at expected path");
        if (recreated)
        {
            string content = File.ReadAllText(expectedPath);
            assert(content == testNote, "Recreated note content matches original TextContent exactly");
            try { File.Delete(expectedPath); } catch { }
        }
    }

    private static void TestOpenItemShortIdRegeneration(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S5] OpenItem Short ID (<8 chars) Safe Regeneration ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        const string testNote = "Short ID note content";
        var shortIdItem = new ShelfItem
        {
            Id = "abc", // Length 3 (< 8)
            Kind = ShelfItemKind.TextSnippet,
            TextContent = testNote,
            FilePath = @"C:\Ghost\deleted.txt"
        };

        bool shortIdClean = false;
        try
        {
            shelf.OpenItem(shortIdItem);
            shortIdClean = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OpenItem threw on short ID: {ex.Message}");
        }

        assert(shortIdClean, "OpenItem handles item with Id length < 8 without ArgumentOutOfRangeException");

        string expectedPath = Path.Combine(settings.ShelfClipsDirectory, "note_abc.txt");
        if (File.Exists(expectedPath))
        {
            assert(File.ReadAllText(expectedPath) == testNote, "note_abc.txt has correct content");
            try { File.Delete(expectedPath); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Suite 6: RevealInExplorer Root Drive, Ancestor Traversal & Malformed Paths
    // -------------------------------------------------------------------------
    private static void TestRevealInExplorerSpecialCharactersAndTrailingSlash(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S6] RevealInExplorer Special Characters & Trailing Slashes ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // Existing file with unicode and spaces
        string tempFile = Path.Combine(Path.GetTempPath(), $"tiếng việt 📁 (test) #1_{Guid.NewGuid():N}.txt");
        File.WriteAllText(tempFile, "sample");

        try
        {
            var item = new ShelfItem { Kind = ShelfItemKind.File, FilePath = tempFile };
            bool executed = false;
            try
            {
                shelf.RevealInExplorer(item);
                executed = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RevealInExplorer threw on unicode path: {ex.Message}");
            }
            assert(executed, "RevealInExplorer handles unicode characters and spaces in file path");

            // Path with trailing backslash (must be safely trimmed to avoid quote escape)
            var slashItem = new ShelfItem { Kind = ShelfItemKind.File, FilePath = tempFile + @"\" };
            bool slashExecuted = false;
            try
            {
                shelf.RevealInExplorer(slashItem);
                slashExecuted = true;
            }
            catch { }
            assert(slashExecuted, "RevealInExplorer handles trailing backslash without command-line escaping bug");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static void TestRevealInExplorerRootDriveVsSubfolder(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S6] RevealInExplorer Root Drive vs Subfolder ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // Root drive C:\
        var rootItem = new ShelfItem { Kind = ShelfItemKind.Folder, FilePath = @"C:\" };
        bool rootClean = false;
        try
        {
            shelf.RevealInExplorer(rootItem);
            rootClean = true;
        }
        catch { }
        assert(rootClean, "RevealInExplorer handles drive root (C:\\) cleanly");

        // Subdirectory C:\Windows
        var subDirItem = new ShelfItem { Kind = ShelfItemKind.Folder, FilePath = @"C:\Windows" };
        bool subDirClean = false;
        try
        {
            shelf.RevealInExplorer(subDirItem);
            subDirClean = true;
        }
        catch { }
        assert(subDirClean, "RevealInExplorer handles existing subdirectory (C:\\Windows) cleanly");
    }

    private static void TestRevealInExplorerAncestorTraversalDeepAndGhostDrive(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S6] RevealInExplorer Ancestor Traversal Deep & Ghost Drive ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        // Deep missing path under Temp
        string deepMissingPath = Path.Combine(Path.GetTempPath(), "Ghost1", "Ghost2", "Ghost3", "missing.txt");
        var deepItem = new ShelfItem { Kind = ShelfItemKind.File, FilePath = deepMissingPath };

        bool deepTraversed = false;
        try
        {
            shelf.RevealInExplorer(deepItem);
            deepTraversed = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RevealInExplorer threw on deep ancestor traversal: {ex.Message}");
        }
        assert(deepTraversed, "RevealInExplorer climbs ancestor tree until existing directory found");

        // Ghost drive letter Z:\ that does not exist
        string ghostDrivePath = @"Z:\NonExistentGhostFolder\missing.txt";
        var ghostItem = new ShelfItem { Kind = ShelfItemKind.File, FilePath = ghostDrivePath };

        bool ghostClean = false;
        try
        {
            shelf.RevealInExplorer(ghostItem);
            ghostClean = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RevealInExplorer threw on ghost drive: {ex.Message}");
        }
        assert(ghostClean, "RevealInExplorer gracefully terminates when climbing root of non-existent drive");
    }

    private static void TestRevealInExplorerMalformedPaths(AssertionDelegate assert)
    {
        Console.WriteLine("--- [M4-S6] RevealInExplorer Malformed Paths & Null Characters ---");

        var settings = new SettingsService();
        var shelf = new FileShelfService(settings);

        string[] malformedPaths =
        [
            "http://example.com/test",
            "ftp://ftp.example.com",
            ":::invalid:::",
            "   "
        ];

        int cleanCount = 0;
        foreach (var p in malformedPaths)
        {
            try
            {
                shelf.RevealInExplorer(new ShelfItem { FilePath = p });
                cleanCount++;
            }
            catch { }
        }
        assert(cleanCount == malformedPaths.Length, $"All {malformedPaths.Length} malformed paths handled safely without unhandled exception ({cleanCount}/{malformedPaths.Length})");
    }
}
