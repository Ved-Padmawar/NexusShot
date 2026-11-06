using System.Security.Cryptography;
using System.Text.Json;

namespace NexusShot.Core;

/// <summary>A published release: its version, its installer, and the signature the installer must carry.</summary>
public sealed record UpdateRelease(Version Version, string InstallerUrl, long InstallerSize, string SignatureUrl);

/// <summary>
/// Reading the latest release and deciding whether an installer is genuine. Genuine means signed with
/// the key in the repository secrets: a checksum beside the installer could be replaced along with it.
/// </summary>
public static class Updates
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/Ved-Padmawar/NexusShot/releases/latest";

    /// <summary>Asset URLs anywhere else are ignored.</summary>
    public const string DownloadPrefix = "https://github.com/Ved-Padmawar/NexusShot/releases/download/";

    /// <summary>The release signing key's public half: ECDSA P-256, SubjectPublicKeyInfo, base64.</summary>
    public const string SigningKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIZTOrsClu9rgENaTIqyCG3dldW4//FyoTaSl3EpoqLcD2HVJVtXThKS4yZ3twx6a5oGjGANkkZTZ69s8MF/CjA==";

    /// <summary>Null unless the release has both its installer and its signature, from this repository.
    /// A field of the wrong type throws InvalidOperationException from the element accessors, not
    /// JsonException, so both mean "no update".</summary>
    public static UpdateRelease? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tag) || Normalise(tag.GetString()?.TrimStart('v', 'V')) is not { } version)
                return null;

            var installerName = $"NexusShot-{version.ToString(3)}.exe";
            string? installer = null, signature = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (url is null || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
                    if (string.Equals(name, installerName, StringComparison.OrdinalIgnoreCase))
                    {
                        installer = url;
                        size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                    }
                    else if (string.Equals(name, installerName + ".sig", StringComparison.OrdinalIgnoreCase))
                        signature = url;
                }

            return installer is null || signature is null ? null : new UpdateRelease(version, installer, size, signature);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Major, minor and patch only: 0.3.0.0 must not read as newer than 0.3.0.</summary>
    public static bool IsNewer(Version latest, Version current) =>
        Normalise(latest.ToString())!.CompareTo(Normalise(current.ToString())) > 0;

    /// <summary>Whether <paramref name="signature"/> (a .sig file's base64) signs this SHA-256.</summary>
    public static bool IsSigned(byte[] sha256, string signature, string publicKey = SigningKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyHash(sha256, Convert.FromBase64String(signature.Trim()));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static Version? Normalise(string? text) =>
        Version.TryParse(text, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
            : null;
}
