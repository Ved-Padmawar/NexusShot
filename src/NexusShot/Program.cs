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

            // A file from "Open with" opens in the running instance, so a save gets a card like a capture.
            var file = args.Length == 1 && File.Exists(args[0]) ? Path.GetFullPath(args[0]) : null;
            if (!Platform.SingleInstance.Claim(file)) return;

            try
            {
                using var app = new App();
                if (file is not null) app.Open([file]);
                app.Run(showWindow: file is null && !Platform.Startup.IsStartupLaunch(args));
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
