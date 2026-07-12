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

        using var app = new Application();

        // Until the main window lands, the editor is the app: pass it a PNG to open.
        var path = args.Length > 0 ? args[0] : null;
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("Usage: NexusShot.exe <image.png>");
            return;
        }

        using var window = new EditorWindow(path);
        window.ResizeClient(1180, 820);
        window.Center();
        window.Show();
        window.SetForeground();
        app.Run();
    }
}
