using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NexusShot.Core;

public enum CaptureMode { Region, FullScreen, ActiveWindow }

public enum AppTheme { System, Light, Dark }

/// <summary>
/// A persisted global shortcut: raw Win32 modifier flags (ALT=1, CONTROL=2, SHIFT=4, WIN=8) plus a
/// virtual-key code. Modifier-less bindings are valid - a single key like F9 or PrtScn is a
/// legitimate capture shortcut, as in the system snipping tool.
/// </summary>
public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public uint Key { get; set; }

    public HotkeyBinding Clone() => new() { Modifiers = Modifiers, Key = Key };
    public bool IsSameGesture(HotkeyBinding other) => Modifiers == other.Modifiers && Key == other.Key;
}

public sealed class AppSettings
{
    private const uint ControlShift = 0x0002 | 0x0004;

    public string ScreenshotFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NexusShot");

    public CaptureMode DefaultCaptureMode { get; set; } = CaptureMode.Region;

    /// <summary>Seconds before a floating preview auto-dismisses. Zero keeps it until acted on.</summary>
    public int PreviewDismissSeconds { get; set; }

    public bool CopyToClipboardAutomatically { get; set; } = true;
    public bool SaveAutomatically { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;

    public HotkeyBinding CaptureRegionHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'S' };
    public HotkeyBinding CaptureFullScreenHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'F' };
    public HotkeyBinding CaptureActiveWindowHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'W' };
    public HotkeyBinding OpenMainWindowHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'N' };
}

public sealed class ScreenshotHistoryItem
{
    public required string FilePath { get; set; }
    public required DateTimeOffset CapturedAt { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>
/// The source generator for the app's JSON. Reflection-based serialisation does not survive Native
/// AOT trimming, so every persisted type is declared here and the generator writes the readers and
/// writers at compile time.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<ScreenshotHistoryItem>))]
internal partial class AppJsonContext : JsonSerializerContext;

/// <summary>
/// Settings and history, persisted next to the executable's data in %APPDATA%.
///
/// Writes go through a temporary file and a replace, so a crash mid-write cannot leave a truncated
/// settings file that fails to parse on next launch and loses everything.
/// </summary>
public sealed class Storage
{
    private readonly string _directory;

    public Storage()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusShot");
        Directory.CreateDirectory(_directory);
    }

    public string SettingsPath => Path.Combine(_directory, "settings.json");
    public string HistoryPath => Path.Combine(_directory, "history.json");

    public AppSettings LoadSettings() =>
        Read(SettingsPath, AppJsonContext.Default.AppSettings) ?? new AppSettings();

    public void SaveSettings(AppSettings settings) =>
        Write(SettingsPath, settings, AppJsonContext.Default.AppSettings);

    public List<ScreenshotHistoryItem> LoadHistory() =>
        Read(HistoryPath, AppJsonContext.Default.ListScreenshotHistoryItem) ?? [];

    public void SaveHistory(List<ScreenshotHistoryItem> history) =>
        Write(HistoryPath, history, AppJsonContext.Default.ListScreenshotHistoryItem);

    private static T? Read<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, type);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // A corrupt or unreadable file falls back to defaults rather than refusing to start.
            return null;
        }
    }

    private static void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        try
        {
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, value, type);

            // Replace is atomic: a crash here leaves the previous file intact rather than a
            // half-written one that will not parse.
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            // Losing a settings write is survivable; crashing the app over it is not.
        }
    }
}
