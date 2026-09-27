using System.Runtime.CompilerServices;

namespace NexusShot.Tests;

/// <summary>Points the app's data directory at a throwaway folder before any test runs, so tests
/// never write into the user's real %LOCALAPPDATA%\NexusShot log.</summary>
internal static class TestData
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nexusshot-tests-{Environment.ProcessId}");
        Environment.SetEnvironmentVariable("NEXUSSHOT_DATA_DIRECTORY", directory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
}
