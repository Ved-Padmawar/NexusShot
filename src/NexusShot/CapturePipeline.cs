using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;
using NexusShot.Views;

namespace NexusShot;

/// <summary>
/// Owns what happens to a capture after the pixels exist: filing it, showing a quick-access card,
/// opening/raising its editor, and keeping the history list, the shell's grid, and any open card or
/// editor in sync with each other. The shell, the previews, and the editors each fire their own
/// events; this is the one place that listens and sequences what follows.
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private readonly Storage _storage;
    private readonly AppSettings _settings;
    private readonly List<ScreenshotHistoryItem> _history;
    private readonly MainWindow _main;

    private readonly EditorRegistry<EditorWindow> _editors = new(editor => editor.PendingSavePath);

    /// <summary>The window per open card; the cards themselves belong to <see cref="QuickAccess"/>.</summary>
    private readonly Dictionary<QuickAccessCard, FloatingPreview> _previews = [];

    public QuickAccess QuickAccess { get; }

    private RestoreStrip? _strip;

    public CapturePipeline(Storage storage, AppSettings settings,
        List<ScreenshotHistoryItem> history, MainWindow main)
    {
        _storage = storage;
        _settings = settings;
        _history = history;
        _main = main;
        QuickAccess = new QuickAccess(settings);

        _main.EditRequested += Edit;
        _main.OpenRequested += Open;
    }

    /// <summary>A pasted image, filed like a capture, goes straight to the editor: pasting it was the
    /// user asking to work on it.</summary>
    public void LandInEditor(ScreenshotHistoryItem item)
    {
        _main.AddCapture(item);
        Edit(item);
    }

    /// <summary>A filed capture joins the library, then goes where the user asked: a card, straight
    /// into the editor, or nowhere - the clipboard already has it.</summary>
    public void Land(ScreenshotHistoryItem item)
    {
        Log.Info("capture", $"{item.Width}x{item.Height}");
        _main.AddCapture(item);
        switch (_settings.AfterCapture)
        {
            case AfterCapture.Editor:
                Edit(item);
                break;
            case AfterCapture.Card:
                ShowPreview(item);
                break;
        }
    }

    public void RefreshExistingPreview(ScreenshotHistoryItem item)
    {
        if (QuickAccess.Find(item.FilePath) is not null) ShowPreview(item);
    }

    /// <summary>Opens image files in the editor; they join the history only once saved.</summary>
    public void Open(IReadOnlyList<string> paths)
    {
        var images = paths.Where(ImageFiles.CanOpen).ToArray();
        if (images.Length == 0)
        {
            UserFeedback.Info(_main.Handle, "NexusShot opens PNG, JPEG and BMP images.");
            return;
        }

        foreach (var path in images)
            Edit(new ScreenshotHistoryItem { FilePath = path, CapturedAt = DateTimeOffset.Now });
    }

    /// <summary>Opens the restore strip, or closes it if it is already up.</summary>
    public void ToggleRestoreStrip()
    {
        if (_strip is not null)
        {
            _strip.Close();
            return;
        }

        _strip = new RestoreStrip(QuickAccess);
        _strip.RestoreRequested += item =>
        {
            if (!Restore(item)) UserFeedback.Error(_main.Handle, $"{item.FileName} no longer exists.");
        };
        _strip.HistoryRequested += () =>
        {
            _main.Reveal();
        };
        _strip.Dismissed += () => _strip = null;
        _strip.Open();
    }

    /// <summary>A capture deleted in the Library or in Explorer leaves no card up and nothing to
    /// restore.</summary>
    public void ForgetCapture(string path)
    {
        if (QuickAccess.Forget(path) is { } card && _previews.Remove(card, out var preview))
        {
            preview.Dismiss();
            ReflowPreviews();
        }
        _strip?.Relayout();
    }

    /// <summary>False when the file is gone, which also drops it from the restore list.</summary>
    public bool Restore(ScreenshotHistoryItem item)
    {
        if (!File.Exists(item.FilePath))
        {
            QuickAccess.Forget(item.FilePath);
            return false;
        }

        ShowPreview(item);
        return true;
    }

    /// <summary>Open editors with changes not yet saved. Commits any open text box first, since
    /// typed text counts.</summary>
    public int UnsavedEditors => _editors.All.Count(editor => editor.HasUnsavedChanges());

    /// <summary>Closes every editor without asking: each saves first when <paramref name="save"/> is
    /// set, or drops its changes. A save that fails stops here, leaving that editor open with its
    /// error, and <paramref name="completed"/> does not run.</summary>
    public void CloseEditors(bool save, Action completed)
    {
        foreach (var editor in _editors.All.ToArray())
        {
            if (save && editor.HasUnsavedChanges())
            {
                editor.SaveThenClose(() => CloseEditors(save, completed));
                return;
            }
            editor.Dispose();
        }
        completed();
    }

    public void CloseEditors(Action completed)
    {
        foreach (var editor in _editors.All.ToArray())
        {
            if (!editor.RequestClose(() => CloseEditors(completed))) return;
            editor.Dispose();
        }
        completed();
    }

    /// <summary>Shows a card for the capture, or refreshes the one already up, then reflows.</summary>
    private void ShowPreview(ScreenshotHistoryItem item)
    {
        var card = QuickAccess.Show(item);
        if (_previews.TryGetValue(card, out var existing))
        {
            existing.Refresh();
            ReflowPreviews();
            return;
        }

        var preview = new FloatingPreview(QuickAccess, card);
        preview.EditRequested += Edit;
        preview.Dismissed += closed =>
        {
            QuickAccess.Close(closed.Card);
            _previews.Remove(closed.Card);
            ReflowPreviews();
        };

        _previews[card] = preview;
        ReflowPreviews();
        preview.Show();
    }

    /// <summary>
    /// Lays the cards out from the bottom-left corner upward.
    ///
    /// Anything that runs off the top of the work area is dismissed rather than drawn off-screen -
    /// a card you cannot see is a card you cannot act on, and it would sit there holding a bitmap.
    /// </summary>
    private void ReflowPreviews()
    {
        // The monitor under the pointer, not the shell's: that is the screen being looked at.
        var work = Monitors.WorkAreaUnderCursor();
        var scale = Monitors.DpiScaleUnderCursor(_main.Handle);

        // Newest nearest the corner; older cards ride up above it.
        var offset = 0.0;
        foreach (var card in QuickAccess.Open.ToArray())
        {
            if (!_previews.TryGetValue(card, out var preview)) continue;

            var height = FloatingPreview.StackHeight(scale);

            if (offset + height > work.Height * 0.8)
            {
                preview.Dismiss();
                continue;
            }

            preview.PlaceAt(work, scale, offset);
            offset += height;
        }
    }

    private void Edit(ScreenshotHistoryItem item)
    {
        if (_editors.Owner(item.FilePath) is { } existing)
        {
            existing.Reveal();
            return;
        }

        var editor = new EditorWindow(item.FilePath, _settings, () => _storage.SaveSettings(_settings));
        editor.CanSaveTo = path => _editors.CanSaveTo(editor, path);
        var editorPath = item.FilePath;
        _editors.Add(editorPath, editor);

        // One new card per editing session; later saves only refresh a card still up.
        var carded = false;

        editor.Closed += () =>
        {
            // The editor releases its own device resources on destroy; this just drops our handle.
            _editors.Remove(editor, editorPath);

            // The capture may have just been re-saved, so its cached bitmap is the old pixels.
            _main.DropCache(editorPath);
            _main.Invalidate();
        };

        // Save overwrites the capture, so its history row and its card are both stale.
        editor.Saved += path =>
        {
            var (width, height) = ImageSurface.ReadSize(path);

            var entry = _history.FirstOrDefault(candidate => string.Equals(candidate.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                entry.Width = width;
                entry.Height = height;
                _storage.SaveHistory(_history);
            }

            _main.DropCache(path);
            _main.Invalidate();

            var saved = entry ?? new ScreenshotHistoryItem
            {
                FilePath = path,
                CapturedAt = DateTimeOffset.Now,
                Width = width,
                Height = height,
            };
            if (carded) RefreshExistingPreview(saved);
            else ShowPreview(saved);
            carded = true;
        };

        // Save As writes a new file; it belongs in the history, and gets a card of its own.
        editor.SavedAs += path =>
        {
            _editors.Move(editor, editorPath, path);
            editorPath = path;
            carded = true;
            _main.DropCache(path);
            var (width, height) = ImageSurface.ReadSize(path);
            var existing = _history.FirstOrDefault(entry => string.Equals(entry.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Width = width;
                existing.Height = height;
                _storage.SaveHistory(_history);
                _main.Invalidate();
                ShowPreview(existing);
                return;
            }

            var item = new ScreenshotHistoryItem
            {
                FilePath = path,
                CapturedAt = DateTimeOffset.Now,
                Width = width,
                Height = height,
            };

            _main.AddCapture(item);
            ShowPreview(item);
        };

        var scale = Functions.GetDpiForWindow(editor.Handle) / 96.0;
        editor.ResizeClient((int)(1180 * scale), (int)(820 * scale));
        editor.Center();
        editor.Reveal();
    }

    /// <summary>Open editors follow the library's theme and accent rather than the ones they were
    /// opened with.</summary>
    public void RethemeEditors()
    {
        foreach (var editor in _editors.All) editor.Retheme();
    }

    public void Dispose()
    {
        _main.EditRequested -= Edit;
        _main.OpenRequested -= Open;
        foreach (var editor in _editors.All.ToArray()) editor.Dispose();
        _editors.Clear();

        foreach (var preview in _previews.Values.ToArray()) preview.Dispose();
        _strip?.Dispose();
        _previews.Clear();
    }
}
