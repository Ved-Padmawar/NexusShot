namespace NexusShot.Core;

/// <summary>A capture asked for from the command line, as the Library header offers them.</summary>
public enum CaptureCommand { Region, Window, Screen, Text }

/// <summary>
/// What a launch asks for: a file to open ("Open with"), a capture (<c>--capture region</c>, for
/// scripts and other tools' shortcuts), or just the app - quietly, when Windows starts it at sign-in.
/// A second launch hands the same request to the instance already running.
/// </summary>
public sealed record LaunchRequest(string? File = null, CaptureCommand? Capture = null, bool AtSignIn = false)
{
    public const string CaptureFlag = "--capture";

    /// <summary>Unrecognised arguments are ignored rather than refused: a shortcut with a typo still
    /// opens the app.</summary>
    public static LaunchRequest Parse(IReadOnlyList<string> args, Func<string, bool> fileExists)
    {
        var atSignIn = args.Any(arg => string.Equals(arg, Startup, StringComparison.OrdinalIgnoreCase));
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (string.Equals(args[i], CaptureFlag, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<CaptureCommand>(args[i + 1], ignoreCase: true, out var capture)
                && Enum.IsDefined(capture))
                return new LaunchRequest(Capture: capture, AtSignIn: atSignIn);
        }
        return args.Count == 1 && fileExists(args[0])
            ? new LaunchRequest(File: Path.GetFullPath(args[0]))
            : new LaunchRequest(AtSignIn: atSignIn);
    }

    /// <summary>Marks a launch as Windows', not the user's: a sign-in launch stays in the tray.</summary>
    public const string Startup = "--startup";

    /// <summary>A capture or a file wants its result, not the Library, in front.</summary>
    public bool ShowsLibrary => File is null && Capture is null && !AtSignIn;
}
