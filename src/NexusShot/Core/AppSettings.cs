using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NexusShot.Core;

public enum CaptureMode { Region, FullScreen, ActiveWindow }

public enum AppTheme { System, Light, Dark }

/// <summary>What follows a capture once it is filed.</summary>
public enum AfterCapture { Card, Editor, CopyOnly }

/// <summary>The screen corner quick-access cards stack from.</summary>
public enum CardCorner { BottomLeft, BottomRight, TopLeft, TopRight }

/// <summary>Every global shortcut. The value is the id RegisterHotKey is given.</summary>
public enum HotkeyId
{
    CaptureRegion = 1,
    CaptureFullScreen = 2,
    CaptureActiveWindow = 3,
    OpenMainWindow = 4,
    RestoreClosed = 5,
    CaptureText = 6,
    TimedCapture = 7,
}

public static class HotkeyIds
{
    public static readonly HotkeyId[] All = Enum.GetValues<HotkeyId>();

    public static string Title(this HotkeyId id) => id switch
    {
        HotkeyId.CaptureRegion => "Capture region",
        HotkeyId.CaptureFullScreen => "Capture full screen",
        HotkeyId.CaptureActiveWindow => "Capture active window",
        HotkeyId.OpenMainWindow => "Open NexusShot",
        HotkeyId.RestoreClosed => "Restore closed captures",
        HotkeyId.CaptureText => "Capture text",
        HotkeyId.TimedCapture => "Timed capture",
        _ => id.ToString(),
    };
}

/// <summary>
/// A persisted global shortcut: raw Win32 modifier flags plus a virtual-key code. Key 0 is unbound.
/// </summary>
public sealed class HotkeyBinding
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;

    public uint Modifiers { get; set; }
    public uint Key { get; set; }

    public HotkeyBinding Clone() => new() { Modifiers = Modifiers, Key = Key };
    public bool IsSameGesture(HotkeyBinding other) => Modifiers == other.Modifiers && Key == other.Key;

    /// <summary>F1-F24, PrtScn and Pause type nothing, so they may be taken system-wide alone, as the
    /// Snipping Tool takes PrtScn. Any other key needs Ctrl, Alt or Win: bare, or with Shift - which
    /// only changes what it types - it would swallow that key in every app.</summary>
    public bool IsUsable => Key == 0 || (Modifiers & ~Shift) != 0 || Key is >= 0x70 and <= 0x87 or 0x2C or 0x13;
}

public sealed class AppSettings
{
    private const uint ControlShift = HotkeyBinding.Control | HotkeyBinding.Shift;

    public string ScreenshotFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NexusShot");

    public CaptureMode DefaultCaptureMode { get; set; } = CaptureMode.Region;

    /// <summary>Seconds before a floating preview auto-dismisses. Zero keeps it until acted on.</summary>
    public int PreviewDismissSeconds { get; set; }

    /// <summary>The countdown before a timed capture, long enough to open a menu or hover a
    /// tooltip that a keypress would close.</summary>
    public int TimedCaptureSeconds { get; set; } = 5;

    public bool CopyToClipboardAutomatically { get; set; } = true;

    /// <summary>Captures older than this many days go to the Recycle Bin; 0 keeps everything. One of
    /// <see cref="Retention.Choices"/>.</summary>
    public int KeepCapturesDays { get; set; }

    /// <summary>Adds the name of the app in front to a capture's file name.</summary>
    public bool NameAfterApp { get; set; }

    /// <summary>Reads the text in each capture in the background, for the Library search.</summary>
    public bool FindTextInCaptures { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>An <see cref="Core.Accent"/> preset, by name.</summary>
    public string Accent { get; set; } = Core.Accent.Default.Name;

    public AfterCapture AfterCapture { get; set; } = AfterCapture.Card;
    public bool ShutterSound { get; set; }

    /// <summary>Whether NexusShot looks for a newer release at startup, every six hours and after
    /// wake. It only ever says one exists; updating is always the user's click.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public bool IncludeCursor { get; set; }

    /// <summary>The magnifier beside the pointer in the region picker.</summary>
    public bool ShowMagnifier { get; set; } = true;

    /// <summary>The written format of new captures.</summary>
    public ImageFormat CaptureFormat { get; set; } = ImageFormat.Png;

    /// <summary>A BCP-47 tag for text recognition, or null for the user's profile languages.</summary>
    public string? OcrLanguage { get; set; }

    public CardCorner CardCorner { get; set; } = CardCorner.BottomLeft;
    public bool PinNewCards { get; set; }

    /// <summary>Colours applied from the picker, newest first, and the ones the user kept. Hex
    /// strings, as annotations store them.</summary>
    public List<string> RecentColors { get; set; } = [];
    public List<string> SavedColors { get; set; } = [];

    public const int MaxRecentColors = 10;
    public const int MaxSavedColors = 14;

    /// <summary>Moves a colour to the front of the recent list.</summary>
    public void RememberColor(string hex) => RecentColors = Front(RecentColors, hex, MaxRecentColors);

    /// <summary>Keeps a colour. Newest last, so the saved row grows toward its add button.</summary>
    public void SaveColor(string hex)
    {
        SavedColors.RemoveAll(existing => string.Equals(existing, hex, StringComparison.OrdinalIgnoreCase));
        SavedColors.Add(hex);
        if (SavedColors.Count > MaxSavedColors) SavedColors.RemoveAt(0);
    }

    public void ForgetSavedColor(string hex) =>
        SavedColors.RemoveAll(existing => string.Equals(existing, hex, StringComparison.OrdinalIgnoreCase));

    private static List<string> Front(List<string> list, string hex, int limit) =>
        [hex, .. list.Where(existing => !string.Equals(existing, hex, StringComparison.OrdinalIgnoreCase)).Take(limit - 1)];

    public HotkeyBinding CaptureRegionHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'S' };
    public HotkeyBinding CaptureFullScreenHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'F' };
    public HotkeyBinding CaptureActiveWindowHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'W' };
    public HotkeyBinding OpenMainWindowHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'N' };
    public HotkeyBinding RestoreClosedHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'H' };
    public HotkeyBinding CaptureTextHotkey { get; set; } = new() { Modifiers = ControlShift, Key = 'O' };

    /// <summary>Unbound by default: the tray menu reaches it, and a delay is rarely wanted from a
    /// shortcut you are already pressing.</summary>
    public HotkeyBinding TimedCaptureHotkey { get; set; } = new();

    public HotkeyBinding Hotkey(HotkeyId id) => id switch
    {
        HotkeyId.CaptureRegion => CaptureRegionHotkey,
        HotkeyId.CaptureFullScreen => CaptureFullScreenHotkey,
        HotkeyId.CaptureActiveWindow => CaptureActiveWindowHotkey,
        HotkeyId.OpenMainWindow => OpenMainWindowHotkey,
        HotkeyId.RestoreClosed => RestoreClosedHotkey,
        HotkeyId.CaptureText => CaptureTextHotkey,
        HotkeyId.TimedCapture => TimedCaptureHotkey,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    /// <summary>The one writer of a shortcut. Null once <paramref name="gesture"/> is bound; otherwise
    /// why it was refused, and nothing changed. A gesture two actions shared would fail to register
    /// for the second, which would then be reported as taken by another app.</summary>
    public string? Bind(HotkeyId id, HotkeyBinding gesture)
    {
        if (!gesture.IsUsable) return "Add Ctrl, Alt or Win - on its own that key would stop typing everywhere.";
        if (OwnerOf(gesture, id) is { } owner) return $"{owner.Title()} already uses that shortcut.";
        var binding = Hotkey(id);
        binding.Modifiers = gesture.Modifiers;
        binding.Key = gesture.Key;
        return null;
    }

    /// <summary>Unbinds whatever <see cref="Bind"/> would refuse, keeping the first of any duplicates:
    /// a hand-edited or older settings file is held to the same rules as the recorder.</summary>
    public void DropUnusableHotkeys()
    {
        foreach (var id in HotkeyIds.All)
        {
            var binding = Hotkey(id);
            var clashesWithEarlier = OwnerOf(binding, id) is { } owner && owner < id;
            if (binding.IsUsable && !clashesWithEarlier) continue;
            binding.Modifiers = 0;
            binding.Key = 0;
        }
    }

    private HotkeyId? OwnerOf(HotkeyBinding gesture, HotkeyId except) => gesture.Key == 0
        ? null
        : HotkeyIds.All.Where(other => other != except && Hotkey(other).IsSameGesture(gesture))
            .Select(other => (HotkeyId?)other).FirstOrDefault();
}

public sealed class ScreenshotHistoryItem
{
    public required string FilePath { get; set; }
    public required DateTimeOffset CapturedAt { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Starred in the Library: filterable, and never removed by <see cref="Retention"/>.</summary>
    public bool Favorite { get; set; }

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
[JsonSerializable(typeof(Dictionary<string, TextIndex.Entry>))]
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

    /// <summary>Locates the settings directory, creating it when possible. A profile that cannot
    /// be written to must not stop the app starting: reads fall back to defaults, and writes
    /// already log and continue.</summary>
    public Storage(string? directory = null)
    {
        _directory = directory ?? Environment.GetEnvironmentVariable("NEXUSSHOT_DATA_DIRECTORY") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusShot");

        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings.directory_failed", exception, _directory);
        }
    }

    public string SettingsPath => Path.Combine(_directory, "settings.json");
    public string HistoryPath => Path.Combine(_directory, "history.json");
    public string TextIndexPath => Path.Combine(_directory, "text-index.json");

    public TextIndex LoadTextIndex() => new(Read(TextIndexPath, AppJsonContext.Default.DictionaryStringEntry));

    public void SaveTextIndex(TextIndex index) =>
        Write(TextIndexPath, new Dictionary<string, TextIndex.Entry>(index.Entries), AppJsonContext.Default.DictionaryStringEntry);

    public AppSettings LoadSettings()
    {
        var settings = Read(SettingsPath, AppJsonContext.Default.AppSettings) ?? new AppSettings();
        // Valid JSON can still contain nulls: nullable annotations do not validate persisted input.
        var defaults = new AppSettings();
        settings.CaptureRegionHotkey ??= defaults.CaptureRegionHotkey;
        settings.CaptureFullScreenHotkey ??= defaults.CaptureFullScreenHotkey;
        settings.CaptureActiveWindowHotkey ??= defaults.CaptureActiveWindowHotkey;
        settings.OpenMainWindowHotkey ??= defaults.OpenMainWindowHotkey;
        settings.RestoreClosedHotkey ??= defaults.RestoreClosedHotkey;
        settings.CaptureTextHotkey ??= defaults.CaptureTextHotkey;
        settings.TimedCaptureHotkey ??= defaults.TimedCaptureHotkey;
        settings.DropUnusableHotkeys();
        if (string.IsNullOrWhiteSpace(settings.ScreenshotFolder)) settings.ScreenshotFolder = defaults.ScreenshotFolder;
        settings.PreviewDismissSeconds = Math.Clamp(settings.PreviewDismissSeconds, 0, 120);
        settings.TimedCaptureSeconds = Math.Clamp(settings.TimedCaptureSeconds, 1, 30);
        if (!Enum.IsDefined(settings.Theme)) settings.Theme = AppTheme.System;
        if (!Enum.IsDefined(settings.DefaultCaptureMode)) settings.DefaultCaptureMode = CaptureMode.Region;
        if (!Enum.IsDefined(settings.AfterCapture)) settings.AfterCapture = AfterCapture.Card;
        if (!Enum.IsDefined(settings.CardCorner)) settings.CardCorner = CardCorner.BottomLeft;
        if (!Enum.IsDefined(settings.CaptureFormat)) settings.CaptureFormat = ImageFormat.Png;
        if (!Retention.Choices.Contains(settings.KeepCapturesDays)) settings.KeepCapturesDays = 0;
        settings.Accent = Accent.Named(settings.Accent).Name;
        settings.RecentColors = ValidColors(settings.RecentColors, AppSettings.MaxRecentColors);
        settings.SavedColors = ValidColors(settings.SavedColors, AppSettings.MaxSavedColors);
        return settings;
    }

    /// <summary>Drops anything in a colour list that is not a colour, and caps its length.</summary>
    private static List<string> ValidColors(List<string>? colors, int limit) =>
        (colors ?? []).Where(hex => Palette.TryParse(hex, out _)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit).ToList();

    public void SaveSettings(AppSettings settings) =>
        Write(SettingsPath, settings, AppJsonContext.Default.AppSettings);

    public List<ScreenshotHistoryItem> LoadHistory()
    {
        var history = Read(HistoryPath, AppJsonContext.Default.ListScreenshotHistoryItem) ?? [];
        history.RemoveAll(item => item is null || string.IsNullOrWhiteSpace(item.FilePath));
        return history.DistinctBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

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
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Defaults beat refusing to start; the unreadable file is kept aside before a save overwrites it.
            Log.Error("settings.read_failed", exception, Path.GetFileName(path));
            Preserve(path);
            return null;
        }
    }

    /// <summary>Copies an unreadable file to <c>.bak</c> so the next write does not destroy it. Kept
    /// at one copy: a second failure means the first is the one worth having.</summary>
    private static void Preserve(string path)
    {
        var backup = path + ".bak";
        try
        {
            if (File.Exists(backup)) return;
            File.Copy(path, backup);
            Log.Info("settings.preserved", Path.GetFileName(backup));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Error("settings.preserve_failed", exception, Path.GetFileName(path));
        }
    }

    private static void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        try
        {
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
                JsonSerializer.Serialize(stream, value, type);

            // Atomic replace: a crash leaves the previous file, never a half-written one.
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A lost write is survivable, but it should leave a trace.
            Log.Error("settings.write_failed", exception, Path.GetFileName(path));
        }
    }
}
