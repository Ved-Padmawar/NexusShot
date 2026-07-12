using NexusShot.Views;

namespace NexusShot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A crash in a windowed app has nowhere to print, and the runtime's own handler needs a
        // TaskDialog to report it. Write the fault somewhere it can actually be read.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        try
        {
            // Headless render check: exercises the real renderer and exporter with no window.
            if (args.Length >= 2 && args[0] == "--render-test")
            {
                RenderTest.Run(args[1]);
                return;
            }

            // Opening a file goes straight to the editor, so NexusShot can be a file association.
            if (args.Length == 1 && File.Exists(args[0]))
            {
                RunEditor(args[0]);
                return;
            }

            using var app = new App();
            app.Run();
        }
        catch (Exception exception)
        {
            LogCrash(exception);
            throw;
        }
    }

    private static void RunEditor(string path)
    {
        using var application = new Application();
        using var window = new EditorWindow(path);

        // ResizeClient is in physical pixels and the app is per-monitor DPI aware, so the requested
        // size has to be scaled or the window comes out half-size on a scaled display.
        var scale = Functions.GetDpiForWindow(window.Handle) / 96.0;
        window.ResizeClient((int)(1180 * scale), (int)(820 * scale));
        window.Center();
        window.Show();
        window.SetForeground();
        application.Run();
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
