using System.Runtime.InteropServices;
using NexusShot.Core;
using NexusShot.Render;
using NexusShot.Views;

namespace NexusShot.Tests;

/// <summary>
/// The Library window and its Settings sheet: a real, hidden <see cref="MainWindow"/> whose chrome is
/// drawn offscreen and clicked where it drew itself. Work the window posts to its own queue is pumped
/// here, as its message loop would. The sheet's positions are read from its own design units - header
/// 52, tab row 58 + 12, a 620-wide card - so a redesign breaks these loudly rather than silently.
/// </summary>
public sealed class LibraryWindowTests : IDisposable
{
    private const int Width = 1000;
    private const int Height = 700;

    private readonly string _directory = Directory.CreateTempSubdirectory("nexusshot-library-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>A hidden Library over <paramref name="history"/>, on its own STA thread, as the app
    /// runs it.</summary>
    private void WithLibrary(List<ScreenshotHistoryItem> history, Action<Library> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var storage = new Storage(_directory);
                var settings = new AppSettings { ScreenshotFolder = _directory };
                using var screen = new Offscreen(Width, Height);
                using var window = new MainWindow(storage, settings, history);
                test(new Library(window, screen, settings, storage));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class Library(MainWindow window, Offscreen screen, AppSettings settings, Storage storage)
    {
        private readonly Ui _ui = new(screen.Resources);

        public MainWindow Window => window;
        public AppSettings Settings => settings;
        public Storage Storage => storage;

        public void Frame(Point pointer, bool down) =>
            screen.Frame(_ui, pointer, down, () => window.DrawChrome(_ui, Width, Height));

        /// <summary>A click, then the window's queued work, then a frame with the pointer away that
        /// reports whether anything started animating.</summary>
        public bool Click(Point at)
        {
            Frame(at, down: true);
            Frame(at, down: false);
            Pump();
            Frame(new Point(-1, -1), down: false);
            return _ui.Animating;
        }

        /// <summary>Lets a sheet finish opening, so it stops moving under the pointer.</summary>
        public void Settle()
        {
            Thread.Sleep((int)Metrics.Motion + 40);
            Frame(new Point(-1, -1), down: false);
        }

        public bool Typing => _ui.HasKeyboardFocus;

        public PointerCursor CursorAt(Point point) => _ui.CursorAt(point);

        public void Type(string text)
        {
            foreach (var character in text) _ui.Char(character);
            Frame(new Point(-1, -1), down: false);
        }

        public void Key(ushort key) => SendMessageW(window.Handle, 0x0100, key, 0);

        public void Pump()
        {
            while (PeekMessageW(out var message, window.Handle, 0, 0, 1))
            {
                TranslateMessage(message);
                DispatchMessageW(message);
            }
        }

        /// <summary>Opens Settings from the tools row's rightmost button and waits for the sheet.</summary>
        public void OpenSettings()
        {
            for (var x = Width - 2; x > Width - 120; x -= 4)
                if (Click(new Point(x, 56 + 22)))
                {
                    Settle();
                    return;
                }
            throw new InvalidOperationException("no Settings button in the tools row");
        }

        private const double SheetWidth = 620;
        private const double SheetHeight = 52 + 58 + 12 + 1 + 490;
        public static double SheetLeft => (Width - SheetWidth) / 2;
        public static double SheetRight => SheetLeft + SheetWidth;
        public static double SheetTop => Math.Max(16, (Height - SheetHeight) / 2);
        public static double BodyTop => SheetTop + 52 + 58 + 12 + 1;

        public void OpenTab(int index)
        {
            var tabWidth = (SheetWidth - 24 - 4 * 5) / 6;
            Click(new Point(SheetLeft + 12 + index * (tabWidth + 4) + tabWidth / 2, SheetTop + 52 + 29));
        }

        /// <summary>Clicks down the column the right-aligned controls end in, after each click
        /// asking <paramref name="done"/> whether to stop.</summary>
        public void SweepControls(Func<bool> done)
        {
            for (var y = BodyTop; y < BodyTop + 490; y += 6)
            {
                Click(new Point(SheetRight - 40, y));
                if (done()) return;
            }
        }
    }

    private List<ScreenshotHistoryItem> Captures(int count) => [.. Enumerable.Range(0, count).Select(i =>
    {
        var path = Path.Combine(_directory, $"capture-{i}.png");
        File.WriteAllBytes(path, [0]);
        return new ScreenshotHistoryItem { FilePath = path, CapturedAt = DateTimeOffset.Now.AddMinutes(-i) };
    })];

    [Fact]
    public void TheHeaderOffersEveryCaptureInOrder() => WithLibrary([], library =>
    {
        var requested = new List<string>();
        library.Window.CaptureRequested += mode => requested.Add(mode.ToString());
        library.Window.CaptureTextRequested += () => requested.Add("Text");
        library.Window.TimedCaptureRequested += () => requested.Add("Timed");

        // Short of the caption buttons, which would minimise or close the window.
        for (var x = 0; x < Width - 260; x += 6)
            library.Click(new Point(x, 28));

        Assert.Equal(["Region", "ActiveWindow", "FullScreen", "Text", "Timed"], requested.Distinct());
    });

    [Fact]
    public void AppearanceChoicesApplyPersistAndRetheme() => WithLibrary([], library =>
    {
        var rethemed = 0;
        library.Window.ThemeChanged += () => rethemed++;
        library.OpenSettings();
        library.OpenTab(5);

        library.SweepControls(() => library.Settings.Theme == AppTheme.Light && library.Settings.Accent == "Mono");

        Assert.Equal(AppTheme.Light, library.Settings.Theme);
        Assert.Equal("Mono", library.Settings.Accent);
        Assert.True(rethemed >= 2);
        var saved = library.Storage.LoadSettings();
        Assert.Equal((AppTheme.Light, "Mono"), (saved.Theme, saved.Accent));
    });

    [Fact]
    public void TheTimerStepperStopsAtItsLimit() => WithLibrary([], library =>
    {
        library.OpenSettings();
        library.OpenTab(1);

        var row = -1.0;
        for (var y = Library.BodyTop; y < Library.BodyTop + 490 && row < 0; y += 6)
        {
            var before = library.Settings.TimedCaptureSeconds;
            library.Click(new Point(Library.SheetRight - 40, y));
            if (library.Settings.TimedCaptureSeconds == before + 1) row = y;
        }
        Assert.True(row > 0, "no timer stepper answered");

        for (var i = 0; i < 40; i++) library.Click(new Point(Library.SheetRight - 40, row));

        Assert.Equal(30, library.Settings.TimedCaptureSeconds);
        Assert.Equal(30, library.Storage.LoadSettings().TimedCaptureSeconds);
    });

    [Fact]
    public void AClosedSheetNoLongerAnswersClicks() => WithLibrary([], library =>
    {
        library.OpenSettings();
        library.OpenTab(5);
        library.Key(0x1B);   // Escape
        library.Settle();

        library.SweepControls(() => false);

        Assert.Equal(AppTheme.System, library.Settings.Theme);
        Assert.Equal(Accent.Default.Name, library.Settings.Accent);
    });

    /// <summary>Select, Select all and Delete in the tools row, left to right, until the prompt opens.</summary>
    private static void AskToDeleteAll(Library library)
    {
        for (var x = 0; x < Width / 2 && !library.Window.ConfirmOpen; x += 6)
            library.Click(new Point(x, 56 + 22));
        Assert.True(library.Window.ConfirmOpen, "the delete prompt never opened");
        library.Settle();
    }

    /// <summary>The prompt is a centred 480 x 156 card with Delete at its bottom right; swept from
    /// that corner inward, so Cancel beside it is never reached first.</summary>
    private static void ConfirmDelete(Library library)
    {
        for (var x = Width / 2 + 214; x > Width / 2 && library.Window.ConfirmOpen; x -= 6)
            library.Click(new Point(x, Height / 2 + 38));
    }

    [Fact]
    public void DeletingSelectedCapturesRemovesTheFilesAndTheirHistory()
    {
        var captures = Captures(2);
        WithLibrary(captures, library =>
        {
            AskToDeleteAll(library);

            ConfirmDelete(library);

            Assert.False(library.Window.ConfirmOpen);
            Assert.Empty(library.Storage.LoadHistory());
        });

        Assert.Empty(captures);
        Assert.Empty(Directory.GetFiles(_directory, "capture-*.png"));
    }

    [Fact]
    public void EscapeCancelsTheDeletePromptAndKeepsTheFiles() => WithLibrary(Captures(2), library =>
    {
        AskToDeleteAll(library);

        library.Key(0x1B);
        library.Pump();

        Assert.False(library.Window.ConfirmOpen);
        Assert.Equal(2, Directory.GetFiles(_directory, "capture-*.png").Length);
    });

    [Fact]
    public void ASearchNarrowsWhatSelectAllDeletes()
    {
        var captures = Captures(3);
        WithLibrary(captures, library =>
        {
            // From left of the folder and Settings buttons: the folder one opens Explorer.
            for (var x = Width - 100; x > Width / 2 && !library.Typing; x -= 6)
                library.Click(new Point(x, 56 + 22));
            Assert.True(library.Typing, "no search field in the tools row");
            library.Type("capture-1");

            AskToDeleteAll(library);
            ConfirmDelete(library);
        });

        Assert.Equal(["capture-0.png", "capture-2.png"], captures.Select(item => item.FileName));
        Assert.False(File.Exists(Path.Combine(_directory, "capture-1.png")));
    }

    [Fact]
    public void TheTitleBarButtonsKeepTheArrowWhileTheToolsRowShowsTheHand() => WithLibrary([], library =>
    {
        library.Frame(new Point(-1, -1), down: false);

        Assert.Equal(PointerCursor.Arrow, library.CursorAt(new Point(Width - 10, 10)));    // close
        Assert.Equal(PointerCursor.Hand, library.CursorAt(new Point(Width - 32, 56 + 22)));   // settings
        Assert.Equal(PointerCursor.Text, library.CursorAt(new Point(Width - 200, 56 + 22)));  // search
    });

    [Fact]
    public void TheLibraryUnderAnOpenSheetIgnoresClicks() => WithLibrary([], library =>
    {
        var requested = 0;
        library.Window.CaptureRequested += _ => requested++;
        library.OpenSettings();

        // The header band is above the scrim's close area, so these reach the library if anything.
        for (var x = 0; x < Width - 260; x += 6)
            library.Click(new Point(x, 28));

        Assert.Equal(0, requested);
    });

    private static readonly UpdateRelease Release = new(new Version(9, 0, 0), "installer", 0, "signature");

    /// <summary>Clicks along the tools row between the capture count and Search until the update
    /// button answers, returning where it was.</summary>
    private static Point ClickUpdateButton(Library library, Func<bool> answered)
    {
        for (var x = Width / 2; x < Width - 330; x += 6)
        {
            var at = new Point(x, 56 + 22);
            library.Click(at);
            if (answered()) return at;
        }
        throw new InvalidOperationException("no update button answered in the tools row");
    }

    /// <summary>Sweeps the prompt's button row from its right end: the confirming button comes first.</summary>
    private static void ConfirmRestart(Library library, Func<bool> done)
    {
        library.Settle();
        for (var x = Width / 2 + 214; x > Width / 2 - 214 && !done(); x -= 6)
            library.Click(new Point(x, Height / 2 + 38));
    }

    [Fact]
    public void AFoundUpdateDownloadsFromTheButtonThenSavesEditorsAndRestarts() => WithLibrary([], library =>
    {
        var progress = new List<double>();
        (string Installer, bool Save)? installed = null;
        library.Window.UnsavedEditors = () => 2;
        library.Window.InstallRequested += (installer, save) => installed = (installer, save);
        library.Window.DownloadInstaller = (release, report, _) =>
        {
            Assert.Same(Release, release);
            foreach (var step in new[] { 0.25, 0.5, 1.0 }) { report(step); progress.Add(step); }
            return Task.FromResult(@"C:\update\NexusShot-9.0.0.exe");
        };
        library.Window.OfferUpdate(Release);

        ClickUpdateButton(library, () => progress.Count > 0);
        Assert.Null(installed);   // a finished download waits for the prompt

        ConfirmRestart(library, () => installed is not null);

        Assert.Equal((@"C:\update\NexusShot-9.0.0.exe", true), installed);
    });

    [Fact]
    public void AFailedDownloadRetriesAndACancelledRestartWaitsOnTheButton() => WithLibrary([], library =>
    {
        var attempts = 0;
        var installs = 0;
        library.Window.UnsavedEditors = () => 0;
        library.Window.InstallRequested += (_, _) => installs++;
        library.Window.DownloadInstaller = (_, _, _) => ++attempts == 1
            ? Task.FromException<string>(new InvalidDataException("the download is not signed"))
            : Task.FromResult("installer.exe");
        library.Window.OfferUpdate(Release);

        var button = ClickUpdateButton(library, () => attempts == 1);
        library.Click(button);           // "Update failed · Retry"
        Assert.Equal(2, attempts);

        library.Key(0x1B);               // Escape: not now
        library.Settle();
        Assert.Equal(0, installs);

        library.Click(button);           // "Restart to update" brings the prompt back
        Assert.Equal(2, attempts);
        ConfirmRestart(library, () => installs > 0);

        Assert.Equal(1, installs);
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(in Message message);

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(IntPtr window, uint message, nuint wParam, nint lParam);
}
