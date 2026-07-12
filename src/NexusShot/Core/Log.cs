using System.Text.Json;

namespace NexusShot.Core;

/// <summary>
/// JSON-lines logging, under %LOCALAPPDATA%\NexusShot\logs.
///
/// One object per line, so a log can be grepped and also parsed. Rotates at 1 MB, keeping one
/// previous file - enough to see what led to a crash, not enough to grow without bound.
///
/// Image contents are never logged. A screenshot tool's logs would otherwise be a record of
/// everything the user captured.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusShot", "logs");

    private static string Current => Path.Combine(Directory, "nexusshot.log");
    private static string Previous => Path.Combine(Directory, "nexusshot.1.log");

    public static void Info(string @event, object? data = null) => Write("info", @event, data, null);

    public static void Error(string @event, Exception exception, object? data = null) =>
        Write("error", @event, data, exception);

    private static void Write(string level, string @event, object? data, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                Rotate();

                var line = JsonSerializer.Serialize(new LogEntry
                {
                    Time = DateTimeOffset.Now,
                    Level = level,
                    Event = @event,
                    Data = data?.ToString(),
                    Error = exception?.ToString(),
                }, LogJsonContext.Default.LogEntry);

                File.AppendAllText(Current, line + Environment.NewLine);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Logging must never be the thing that takes the app down.
        }
    }

    private static void Rotate()
    {
        var file = new FileInfo(Current);
        if (!file.Exists || file.Length < MaxBytes) return;

        File.Delete(Previous);
        File.Move(Current, Previous);
    }
}

public sealed class LogEntry
{
    public DateTimeOffset Time { get; set; }
    public required string Level { get; set; }
    public required string Event { get; set; }
    public string? Data { get; set; }
    public string? Error { get; set; }
}

/// <summary>Compact, not indented: one entry per line is the whole point of JSON lines.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = false)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LogEntry))]
internal partial class LogJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
