using System.Text.Json;

namespace NexusShot.App.Services;

public interface IAppLogger
{
    void Info(string eventName, object? data = null);
    void Error(string eventName, Exception exception, object? data = null);
}

/// <summary>Small JSON-lines logger that avoids screenshot content and rotates at 1 MB.</summary>
public sealed class FileLogger : IAppLogger
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusShot", "logs");
    private readonly Lock _sync = new();
    public void Info(string eventName, object? data = null) => Write("Information", eventName, null, data);
    public void Error(string eventName, Exception exception, object? data = null) => Write("Error", eventName, exception, data);
    private void Write(string level, string eventName, Exception? exception, object? data)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, "nexusshot.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, Path.Combine(_directory, $"nexusshot-{DateTime.UtcNow:yyyyMMddHHmmss}.log"), true);
                var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, level, eventName, error = exception?.Message, data });
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* Logging must not affect capture. */ }
    }
}
