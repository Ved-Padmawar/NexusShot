using System.Runtime.InteropServices;
using NexusShot.Views;

namespace NexusShot;

internal static partial class Program
{
    /// <summary>OLE, not just COM: DoDragDrop is an OLE service and fails on a thread that has only
    /// been through CoInitialize.</summary>
    [LibraryImport("ole32.dll")]
    private static partial int OleInitialize(IntPtr reserved);

    [LibraryImport("ole32.dll")]
    private static partial void OleUninitialize();

    [STAThread]
    private static void Main(string[] args)
    {
        // A crash in a windowed app has nowhere to print, and the runtime's own handler needs a
        // TaskDialog to report it. Write the fault somewhere it can actually be read.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        var hr = OleInitialize(IntPtr.Zero);
        if (hr != 0 && hr != 1) throw new InvalidOperationException($"OleInitialize failed 0x{hr:X8}");
        try
        {
            if (args.Length == 2 && args[0] == "--preview-test")
            {
                var (width, height) = Render.ImageSurface.ReadSize(args[1]);
                using var application = new Application();
                var stack = new Core.QuickAccess(new Core.AppSettings { PreviewDismissSeconds = 0 });
                using var preview = new FloatingPreview(stack, stack.Show(new Core.ScreenshotHistoryItem
                {
                    FilePath = args[1], CapturedAt = DateTimeOffset.Now, Width = width, Height = height,
                }));
                preview.PlaceAt(Platform.Monitors.WorkAreaUnderCursor(),
                    Platform.Monitors.DpiScaleUnderCursor(preview.Handle), 0);
                preview.Show();
                application.Run();
                return;
            }

            // Headless render check: exercises the real renderer and exporter with no window.
            if (args.Length >= 2 && args[0] == "--render-test")
            {
                RenderTest.Run(args[1]);
                return;
            }

            // Files and captures go to the running instance, which owns the hotkeys, history and cards.
            var request = Core.LaunchRequest.Parse(args, File.Exists);
            if (!Platform.SingleInstance.Claim(request)) return;

            try
            {
                using var app = new App();
                app.Run(request);
            }
            finally
            {
                Platform.SingleInstance.Release();
            }
        }
        catch (Exception exception)
        {
            LogCrash(exception);
            throw;
        }
        finally
        {
            OleUninitialize();
        }
    }

    private static void LogCrash(Exception? exception)
    {
        if (exception is null) return;

        Core.Log.Error("app.crashed", exception);

        // Also to a fixed temp path: a crash the log rotation happened to eat is a crash nobody can
        // debug, and this file is where the support instructions point.
        try
        {
            var log = Path.Combine(Path.GetTempPath(), "nexusshot-crash.log");
            File.WriteAllText(log, $"{DateTime.Now:O}{Environment.NewLine}{exception}");
        }
        catch (IOException)
        {
            // Nothing useful to do if even the log cannot be written.
        }
    }
}
