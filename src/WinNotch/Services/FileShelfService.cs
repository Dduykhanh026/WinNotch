using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinNotch.Interop;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class FileShelfService
{
    private static readonly Guid ShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SettingsService _settingsService;

    public ObservableCollection<ShelfItem> Items { get; } = [];

    public event Action? ShelfChanged;

    public FileShelfService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadFromDisk();
    }

    public bool CanAcceptDataObject(IDataObject data)
    {
        return data.GetDataPresent(DataFormats.FileDrop) ||
               data.GetDataPresent(DataFormats.Bitmap) ||
               data.GetDataPresent(DataFormats.UnicodeText) ||
               data.GetDataPresent(DataFormats.Text);
    }

    public int AcceptDrop(IDataObject data)
    {
        int added = 0;

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (string rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath)) continue;
                if (AddFileOrFolder(rawPath)) added++;
            }
        }
        else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if (AddBitmapClip(bitmap)) added++;
        }
        else if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
        {
            string? text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) as string;
            if (!string.IsNullOrWhiteSpace(text) && AddTextSnippet(text))
            {
                added++;
            }
        }

        if (added > 0)
        {
            SaveToDisk();
            ShelfChanged?.Invoke();
        }

        return added;
    }

    public void RemoveItem(ShelfItem? item)
    {
        if (item == null) return;
        if (Items.Remove(item))
        {
            CleanupManagedClipIfNeeded(item);
            SaveToDisk();
            ShelfChanged?.Invoke();
        }
    }

    public void ClearAll()
    {
        foreach (ShelfItem item in Items)
        {
            CleanupManagedClipIfNeeded(item);
        }
        Items.Clear();
        SaveToDisk();
        ShelfChanged?.Invoke();
    }

    public void CopyItemToClipboard(ShelfItem? item)
    {
        if (item == null) return;
        try
        {
            if (item.Kind == ShelfItemKind.TextSnippet && !string.IsNullOrEmpty(item.TextContent))
            {
                Clipboard.SetText(item.TextContent);
            }
            else if (!string.IsNullOrWhiteSpace(item.FilePath) && (File.Exists(item.FilePath) || Directory.Exists(item.FilePath)))
            {
                var list = new StringCollection { item.FilePath };
                Clipboard.SetFileDropList(list);
            }
        }
        catch { }
    }

    public void DragOutSingle(DependencyObject dragSource, ShelfItem item)
    {
        var dataObj = new DataObject();
        bool hasPayload = false;

        if (!string.IsNullOrWhiteSpace(item.FilePath) && (File.Exists(item.FilePath) || Directory.Exists(item.FilePath)))
        {
            dataObj.SetData(DataFormats.FileDrop, new[] { item.FilePath });
            hasPayload = true;
        }

        if (item.Kind == ShelfItemKind.TextSnippet && !string.IsNullOrEmpty(item.TextContent))
        {
            dataObj.SetData(DataFormats.UnicodeText, item.TextContent);
            hasPayload = true;
        }

        if (!hasPayload) return;

        DragDropEffects effect = DragDrop.DoDragDrop(dragSource, dataObj, DragDropEffects.Copy);
        if (effect != DragDropEffects.None && _settingsService.Current.AutoRemoveShelfItemAfterDrag)
        {
            RemoveItem(item);
        }
    }

    public void DragOutAll(DependencyObject dragSource)
    {
        string[] validPaths = Items
            .Select(i => i.FilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && (File.Exists(p) || Directory.Exists(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validPaths.Length == 0) return;

        var dataObj = new DataObject(DataFormats.FileDrop, validPaths);
        DragDropEffects effect = DragDrop.DoDragDrop(dragSource, dataObj, DragDropEffects.Copy);
        if (effect != DragDropEffects.None && _settingsService.Current.AutoRemoveShelfItemAfterDrag)
        {
            ClearAll();
        }
    }

    private bool AddFileOrFolder(string path)
    {
        bool isDir = Directory.Exists(path);
        bool isFile = !isDir && File.Exists(path);
        if (!isDir && !isFile) return false;

        ShelfItem? existing = Items.FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            int idx = Items.IndexOf(existing);
            if (idx > 0) Items.Move(idx, 0);
            return true;
        }

        string name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = path;

        string subLabel = "Thư mục";
        if (isFile)
        {
            try
            {
                var fi = new FileInfo(path);
                subLabel = FormatBytes(fi.Length);
            }
            catch
            {
                subLabel = Path.GetExtension(path);
            }
        }

        var item = new ShelfItem
        {
            Kind = isDir ? ShelfItemKind.Folder : ShelfItemKind.File,
            FilePath = path,
            DisplayName = name,
            SubLabel = subLabel,
            AddedAtUtc = DateTime.UtcNow
        };

        Items.Insert(0, item);
        QueueThumbnailLoad(item);
        return true;
    }

    private bool AddBitmapClip(BitmapSource bitmap)
    {
        try
        {
            string fileName = $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            string fullPath = Path.Combine(_settingsService.ShelfClipsDirectory, fileName);

            using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(fs);
            }

            var fi = new FileInfo(fullPath);
            var item = new ShelfItem
            {
                Kind = ShelfItemKind.ImageClip,
                FilePath = fullPath,
                DisplayName = fileName,
                SubLabel = FormatBytes(fi.Length),
                AddedAtUtc = DateTime.UtcNow
            };

            Items.Insert(0, item);
            QueueThumbnailLoad(item);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool AddTextSnippet(string text)
    {
        try
        {
            string trimmed = text.Trim();
            string fileName = $"note_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt";
            string fullPath = Path.Combine(_settingsService.ShelfClipsDirectory, fileName);
            File.WriteAllText(fullPath, trimmed, Encoding.UTF8);

            string preview = trimmed.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 26) preview = preview[..26] + "…";

            bool isUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

            var item = new ShelfItem
            {
                Kind = ShelfItemKind.TextSnippet,
                FilePath = fullPath,
                DisplayName = preview,
                SubLabel = isUrl ? "Liên kết URL" : $"{trimmed.Length} ký tự",
                TextContent = trimmed,
                AddedAtUtc = DateTime.UtcNow
            };

            Items.Insert(0, item);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_settingsService.ShelfFilePath)) return;

            string json = File.ReadAllText(_settingsService.ShelfFilePath);
            var loaded = JsonSerializer.Deserialize<List<ShelfItem>>(json, JsonOptions);
            if (loaded == null) return;

            bool pruned = false;
            foreach (ShelfItem item in loaded)
            {
                bool exists = item.Kind == ShelfItemKind.TextSnippet
                    ? (!string.IsNullOrEmpty(item.TextContent) || File.Exists(item.FilePath))
                    : (File.Exists(item.FilePath) || Directory.Exists(item.FilePath));

                if (!exists)
                {
                    pruned = true;
                    continue;
                }

                // Ensure TextSnippet still has backing .txt file for FileDrop compatibility
                if (item.Kind == ShelfItemKind.TextSnippet && !File.Exists(item.FilePath) && !string.IsNullOrEmpty(item.TextContent))
                {
                    string fileName = $"note_{item.Id[..8]}.txt";
                    item.FilePath = Path.Combine(_settingsService.ShelfClipsDirectory, fileName);
                    File.WriteAllText(item.FilePath, item.TextContent, Encoding.UTF8);
                }

                Items.Add(item);
                if (item.Kind != ShelfItemKind.TextSnippet)
                {
                    QueueThumbnailLoad(item);
                }
            }

            if (pruned) SaveToDisk();
        }
        catch
        {
            // Ignore malformed shelf.json
        }
    }

    private void SaveToDisk()
    {
        try
        {
            string json = JsonSerializer.Serialize(Items.ToList(), JsonOptions);
            File.WriteAllText(_settingsService.ShelfFilePath, json);
        }
        catch { }
    }

    private void CleanupManagedClipIfNeeded(ShelfItem item)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(item.FilePath) &&
                item.FilePath.StartsWith(_settingsService.ShelfClipsDirectory, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }
        }
        catch { }
    }

    private static void QueueThumbnailLoad(ShelfItem item)
    {
        string path = item.FilePath;
        if (string.IsNullOrWhiteSpace(path)) return;

        Task.Run(() =>
        {
            BitmapSource? thumb = ExtractShellThumbnail(path, 96);
            if (thumb != null)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => item.Thumbnail = thumb);
            }
        });
    }

    private static BitmapSource? ExtractShellThumbnail(string path, int pixelSize)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            int hr = NativeMethods.SHCreateItemFromParsingName(
                path,
                IntPtr.Zero,
                ShellItemImageFactoryGuid,
                out NativeMethods.IShellItemImageFactory? factory);

            if (hr != 0 || factory == null) return null;

            hr = factory.GetImage(
                new NativeMethods.SIZE(pixelSize, pixelSize),
                NativeMethods.SIIGBF.SIIGBF_RESIZETOFIT | NativeMethods.SIIGBF.SIIGBF_BIGGERSIZEOK,
                out hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBitmap);
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.1} MB";
        double gb = mb / 1024.0;
        return $"{gb:0.2} GB";
    }
}
