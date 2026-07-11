using System.Text.Json;
using NexusShot.App.Models;
using NexusShot.App.Services;

namespace NexusShot.App.Storage;

public sealed class JsonStorageService : IStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusShot");
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    public JsonStorageService(IAppLogger? logger = null) => _logger = logger ?? new FileLogger();
    private string SettingsPath => Path.Combine(_dataDirectory, "settings.json");
    private string HistoryPath => Path.Combine(_dataDirectory, "history.json");

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "logs"));
        var settings = await LoadAsync<AppSettings>(SettingsPath, new(), cancellationToken);
        Directory.CreateDirectory(settings.ScreenshotFolder);
        return settings;
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken) => SaveAsync(SettingsPath, settings, cancellationToken);
    public async Task<IReadOnlyList<ScreenshotHistoryItem>> LoadHistoryAsync(CancellationToken cancellationToken) => await LoadAsync(HistoryPath, new List<ScreenshotHistoryItem>(), cancellationToken);
    public Task SaveHistoryAsync(IReadOnlyList<ScreenshotHistoryItem> history, CancellationToken cancellationToken) => SaveAsync(HistoryPath, history, cancellationToken);

    public async Task<string> SaveScreenshotAsync(string sourcePath, string destinationFolder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationFolder);
        var destination = UniquePath(destinationFolder);
        await using var source = File.OpenRead(sourcePath);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);
        return destination;
    }

    /// <summary>Timestamps to the second, so rapid captures need a suffix to avoid collisions.</summary>
    private static string UniquePath(string folder)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var candidate = Path.Combine(folder, $"NexusShot_{stamp}.png");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(folder, $"NexusShot_{stamp}_{suffix}.png");
        return candidate;
    }

    private async Task<T> LoadAsync<T>(string path, T fallback, CancellationToken token)
    {
        if (!File.Exists(path)) return fallback;
        try { await using var stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token) ?? fallback; }
        catch (JsonException exception)
        {
            File.Move(path, path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
            _logger.Error("storage.json_corrupt", exception, new { path = Path.GetFileName(path) });
            return fallback;
        }
    }
    private async Task SaveAsync<T>(string path, T value, CancellationToken token)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var temporaryPath = path + ".tmp";
            await using (var stream = File.Create(temporaryPath)) await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
            File.Move(temporaryPath, path, true);
        }
        finally { _writeLock.Release(); }
    }
}
