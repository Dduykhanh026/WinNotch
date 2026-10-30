using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinNotch.Models;

namespace WinNotch.Services;

public sealed class SettingsService
{
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryValueName = "WinNotch";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string DataDirectory { get; }
    public string SettingsFilePath { get; }
    public string ShelfFilePath { get; }
    public string ShelfClipsDirectory { get; }
    public bool IsPortableMode { get; }

    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService()
    {
        string baseDir = AppContext.BaseDirectory;
        IsPortableMode = File.Exists(Path.Combine(baseDir, "portable.mode"));

        DataDirectory = IsPortableMode
            ? Path.Combine(baseDir, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNotch");

        Directory.CreateDirectory(DataDirectory);

        ShelfClipsDirectory = Path.Combine(DataDirectory, "ShelfClips");
        Directory.CreateDirectory(ShelfClipsDirectory);

        SettingsFilePath = Path.Combine(DataDirectory, "settings.json");
        ShelfFilePath = Path.Combine(DataDirectory, "shelf.json");

        Load();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    Current = loaded;
                }
            }
        }
        catch
        {
            Current = new AppSettings();
        }

        Current.StartWithWindows = IsRegisteredInStartup();
    }

    public void Save(AppSettings updated)
    {
        Current = updated.Clone();
        ApplyStartupRegistration(Current.StartWithWindows);

        try
        {
            string json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore transient IO failures
        }

        SettingsChanged?.Invoke(Current);
    }

    public void UpdateLastMediaApp(string appHint, string displayName)
    {
        if (string.IsNullOrWhiteSpace(appHint) ||
            (string.Equals(Current.LastMediaAppHint, appHint, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(Current.LastMediaAppDisplayName, displayName, StringComparison.Ordinal)))
        {
            return;
        }

        Current.LastMediaAppHint = appHint;
        Current.LastMediaAppDisplayName = displayName;

        try
        {
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch
        {
            // Ignore transient IO failures
        }
    }

    private static bool IsRegisteredInStartup()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: false);
            return key?.GetValue(AppRegistryValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyStartupRegistration(bool enable)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AppRegistryValueName, $"\"{exePath}\"");
                }
            }
            else
            {
                if (key.GetValue(AppRegistryValueName) != null)
                {
                    key.DeleteValue(AppRegistryValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // Ignore registry permission errors
        }
    }
}
