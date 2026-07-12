using NexusShot.Views;

namespace NexusShot;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Headless render check: exercises the real renderer and exporter with no window, so the
        // drawing code can be verified without fighting Windows for foreground focus.
        if (args.Length >= 2 && args[0] == "--render-test")
        {
            RenderTest.Run(args[1]);
            return;
        }

        // Until the main window lands, the editor is the app: pass it a PNG to open.
        var path = args.Length > 0 ? args[0] : null;
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("Usage: NexusShot.exe <image.png>");
            return;
        }

        // A crash in a windowed app has nowhere to print, and DirectN's own handler needs a
        // TaskDialog to report it. Write the fault somewhere it can actually be read.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception);

        try
        {
            using var app = new Application();
            using var window = new EditorWindow(path);

            // ResizeClient is in physical pixels, and the app is per-monitor DPI aware, so the
            // requested size has to be scaled or the window comes out half-size at 200%.
            var scale = Functions.GetDpiForWindow(window.Handle) / 96.0;
            window.ResizeClient((int)(1180 * scale), (int)(820 * scale));
            window.Center();
            window.Show();
            window.SetForeground();
            app.Run();
        }
        catch (Exception exception)
        {
            LogCrash(exception);
            throw;
        }
    }

    private static void LogCrash(Exception? exception)
    {
        if (exception is null) return;
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
