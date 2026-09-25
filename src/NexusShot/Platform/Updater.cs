using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using NexusShot.Core;

namespace NexusShot.Platform;

/// <summary>
/// Finds, downloads, verifies and runs a newer release's installer. Run silently it upgrades in place
/// and, given /UPDATE=1, relaunches NexusShot. An unsigned download is deleted unrun.
/// </summary>
public static class Updater
{
    /// <summary>The running build's version. Declared before the client, which reads it.</summary>
    public static Version Current { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Null when this build is current.</summary>
    public static async Task<UpdateRelease?> CheckAsync(CancellationToken token = default)
    {
        using var response = await Http.GetAsync(Updates.LatestReleaseUrl, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var release = Updates.Parse(await response.Content.ReadAsStringAsync(token));
        return release is not null && Updates.IsNewer(release.Version, Current) ? release : null;
    }

    /// <summary>The verified installer's path. <paramref name="progress"/> is called off the UI thread.</summary>
    public static async Task<string> DownloadAsync(UpdateRelease release, Action<double> progress, CancellationToken token = default)
    {
        var signature = await Http.GetStringAsync(release.SignatureUrl, token);

        // Emptied first, so an older or half-finished download never lingers.
        var folder = Path.Combine(Path.GetTempPath(), "NexusShot update");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"NexusShot-{release.Version.ToString(3)}.exe");

        try { return await DownloadToAsync(release, path, signature, progress, token); }
        catch
        {
            // Cancelled or failed: a partial installer must never be left where it could be run.
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    private static async Task<string> DownloadToAsync(UpdateRelease release, string path, string signature,
        Action<double> progress, CancellationToken token)
    {
        using var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? release.InstallerSize;

        byte[] digest;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        await using (var source = await response.Content.ReadAsStreamAsync(token))
        await using (var file = File.Create(path))
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (total > 0) progress(Math.Min(1, received / (double)total));
            }
            digest = hash.GetHashAndReset();
        }

        if (!Updates.IsSigned(digest, signature))
            throw new InvalidDataException("the download is not signed by NexusShot's release key");
        return path;
    }

    /// <summary>The caller exits straight after, so the installer can replace the running exe.</summary>
    public static void Install(string installer) =>
        Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1")
        {
            UseShellExecute = true,
        });

    /// <summary>GitHub's API refuses requests without a User-Agent.</summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"NexusShot/{Current.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
