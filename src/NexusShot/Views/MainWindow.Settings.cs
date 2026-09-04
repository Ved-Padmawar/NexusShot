using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The settings pane and the row widgets it is built from.
///
/// Split from the shell because none of this is reachable from anywhere else: the pane owns its own
/// scroll, its dropdowns, the number-field draft and the hotkey recorder table.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Settings replace the detail pane in place rather than opening a dialog: every change applies
    /// immediately, so there is nothing to confirm or cancel. Rows sit directly in the column with
    /// hairline separators doing the grouping, rather than being boxed into nested cards.
    /// </summary>
    private void DrawSettings(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;

        // The header starts below the caption, so its title and close button clear the window
        // controls floating over this pane's top-right rather than crowding them.
        var header = new Rect(
            bounds.X,
            bounds.Y + CaptionHeight,
            bounds.Width,
            S(14) * 2 + S(Metrics.FontTitle) + S(4));

        ui.Text("Settings", new Rect(header.X + S(28), header.Y, header.Width, header.Height),
            theme.TextPrimary, (float)S(Metrics.FontTitle), bold: true);

        if (ui.Tile(Ui.Id("main.history.clear"), new Rect(header.Right - S(28) - S(36), header.Center.Y - S(18), S(36), S(36)),
            false, Icons.Close, S(14), "Close settings"))
        {
            // The click that got here already committed any focused box, on the way in.
            _settingsOpen = false;
            CloseDropdowns();
        }
        ui.FillRect(new Rect(bounds.X, header.Bottom, bounds.Width, 1), theme.StrokeSubtle);

        // The scrollable body. Clipped, so a long list cannot paint over the header.
        var body = new Rect(bounds.X, header.Bottom, bounds.Width, bounds.Bottom - header.Bottom);
        _settingsViewport = Math.Max(1, body.Height);
        ui.PushClip(body);

        // Centred column: MaxWidth=640, Margin="32,0,32,32". Sections and rows provide their
        // own vertical rhythm; keeping it here instead of at each call site makes the whole pane
        // feel like one system rather than a stack of individually nudged controls.
        var width = Math.Min(S(640), body.Width - S(64));
        var x = body.X + (body.Width - width) / 2;

        // Settled before anything is positioned: clamping after the layout would draw one frame at
        // the bad offset and snap back on the next.
        _settingsScroll = Math.Clamp(
            _settingsScroll, 0, Math.Max(0, _settingsHeight - _settingsViewport));

        // Laid out from a fixed origin and shifted by the scroll, so the extent measured below is a
        // property of the content alone - an extent that moved with the scroll position would feed
        // back into the next frame's layout.
        var top = body.Y - _settingsScroll;
        var y = top;

        y = Section(ui, "CAPTURE", x, y, width);

        y = Row(ui, x, y, width, "Save folder", Shorten(_settings.ScreenshotFolder, 52),
            row =>
            {
                if (!ui.Button(Ui.Id("settings.folder.change"), ActionSlot(row, S(92)), "Change…",
                    fontSize: S(Metrics.FontCaption))) return;

                // The picker is modal and would pump its loop mid-frame.
                Post(() =>
                {
                    if (FolderPicker.Pick(Handle, _settings.ScreenshotFolder) is not { } folder)
                        return;

                    _settings.ScreenshotFolder = folder;
                    SaveSettings();
                    Invalidate();
                });
            });

        y = Row(ui, x, y, width, "Default capture mode", null,
            row => _captureModeBox.Field(ui, Ui.Id("settings.capturemode"), ActionSlot(row, S(148)),
                ["Region", "Full screen", "Active window"],
                (int)_settings.DefaultCaptureMode,
                index =>
                {
                    _settings.DefaultCaptureMode = (CaptureMode)index;
                    SaveSettings();
                }));

        y = Row(ui, x, y, width,
            "Copy to clipboard automatically",
            "Every capture lands on the clipboard, ready to paste.",
            row => Switch(ui, Ui.Id("settings.autocopy"), ActionSlot(row, S(44)), _settings.CopyToClipboardAutomatically,
                value =>
                {
                    _settings.CopyToClipboardAutomatically = value;
                    SaveSettings();
                }));

        y = Row(ui, x, y, width,
            "Save screenshots automatically",
            "Captures are written straight into the save folder.",
            row => Switch(ui, Ui.Id("settings.autosave"), ActionSlot(row, S(44)), _settings.SaveAutomatically,
                value =>
                {
                    _settings.SaveAutomatically = value;
                    SaveSettings();
                }));

        y = Section(ui, "SHORTCUTS", x, y, width);

        // On the header line: a reset belongs to the group, not to a row of its own.
        if (!HotkeysAreDefault())
        {
            var reset = new Rect(x + width - S(132), y - S(36), S(132), S(28));
            if (ui.Button(Ui.Id("settings.reset"), reset, "Restore defaults",
                glyph: Icons.Undo, glyphSize: S(12), fontSize: S(Metrics.FontCaption),
                destructive: true))
                ResetHotkeys();
        }

        ui.Text(
            "Click a shortcut, then press the new keys. Backspace unbinds, Delete restores that one, Esc cancels.",
            new Rect(x, y, width, S(16)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false);
        y += S(30);

        var hotkeyWidth = HotkeyWidth(ui);

        foreach (var (id, binding, title) in Hotkeys)
            y = Hotkey(ui, id, x, y, width, hotkeyWidth, title, binding(_settings));

        if (_hotkeyWarning is { } warning)
        {
            ui.Text(warning, new Rect(x, y + S(6), width, S(20)),
                theme.Danger, (float)S(Metrics.FontCaption), middle: false);
            y += S(28);
        }

        y = Section(ui, "PREVIEW", x, y, width);

        y = Row(ui, x, y, width,
            "Auto-dismiss after",
            "Seconds before a floating preview disappears. 0 keeps it open.",
            row => NumberField(ui, Ui.Id("settings.dismissdelay"), ActionSlot(row, S(120)), _settings.PreviewDismissSeconds, 0, 120,
                value =>
                {
                    _settings.PreviewDismissSeconds = value;
                    SaveSettings();
                }));

        y = Section(ui, "GENERAL", x, y, width);

        y = Row(ui, x, y, width, "Theme", null,
            row => _themeBox.Field(ui, Ui.Id("settings.theme"), ActionSlot(row, S(148)),
                ["System", "Light", "Dark"],
                (int)_settings.Theme,
                index =>
                {
                    _settings.Theme = (AppTheme)index;
                    SaveSettings();
                }));

        y = Row(ui, x, y, width,
            "Start NexusShot with Windows", null,
            row => Switch(ui, Ui.Id("settings.startup"), ActionSlot(row, S(44)), _settings.StartWithWindows,
                value =>
                {
                    _settings.StartWithWindows = value;
                    Startup.Set(value);
                    SaveSettings();
                }));

        // Last frame's extent, which the scroll position was clamped against above - so the thumb
        // and the rows agree.
        ui.Scrollbar(body, _settingsHeight, _settingsScroll);
        ui.PopClip();

        // Open lists paint after the clip is popped: a list is allowed to overhang the rows below it
        // and the body's own edge, which is the point of a dropdown.
        _captureModeBox.DrawOpen(ui, body);
        _themeBox.DrawOpen(ui, body);

        // The trailing margin is part of the content: added to the extent after measuring, it would
        // buy scroll travel no pixel occupies.
        y += S(32);
        _settingsHeight = y - top;
    }

    /// <summary>Owner for the per-capture history row ids, which are derived by index.</summary>
    private static readonly int HistoryRow = Ui.Id("main.history.row");

    private readonly Dropdown _captureModeBox = new();
    private readonly Dropdown _themeBox = new();

    /// <summary>An open list is anchored to a row, so anything that moves or hides that row - a
    /// scroll, Escape, leaving settings - has to take the list with it.</summary>
    private void CloseDropdowns()
    {
        _captureModeBox.Close();
        _themeBox.Close();
    }

    private bool DropdownOpen => _captureModeBox.IsOpen || _themeBox.IsOpen;

    /// <summary>SectionHeaderStyle: caption, SemiBold, TextTertiary. A generous leading gap and a
    /// smaller trailing gap make the heading belong to the rows below it, while keeping adjacent
    /// groups visually distinct.</summary>
    private double Section(Ui ui, string title, double x, double y, double width)
    {
        y += S(28);
        ui.Text(title, new Rect(x, y, width, S(16)),
            ui.Theme.TextTertiary, (float)S(Metrics.FontCaption), bold: true, middle: false);
        return y + S(16) + S(10);
    }

    /// <summary>
    /// SettingRowStyle: at least 48 high, bottom border StrokeSubtle, ColumnSpacing=24.
    /// A title, an optional caption, and a control on the right. The control draws itself into
    /// the slot the row hands it. The minimum height deliberately leaves eight pixels above and
    /// below a 32-pixel control, so consecutive buttons never read as one clumped control stack.
    /// </summary>
    private double Row(
        Ui ui, double x, double y, double width,
        string title, string? caption, Action<Rect> control)
    {
        var theme = ui.Theme;
        var pad = S(10);

        var textHeight = caption is null ? S(18) : S(18) + S(4) + S(18);
        var height = Math.Max(S(48), pad * 2 + textHeight);
        var row = new Rect(x, y, width, height);

        var textWidth = width - S(260) - S(24);

        if (caption is null)
        {
            ui.Text(title, new Rect(x, row.Y, textWidth, height),
                theme.TextPrimary, (float)S(Metrics.FontBody));
        }
        else
        {
            ui.Text(title, new Rect(x, row.Y + pad, textWidth, S(18)),
                theme.TextPrimary, (float)S(Metrics.FontBody), middle: false);
            ui.Text(caption, new Rect(x, row.Y + pad + S(18) + S(4), textWidth, S(18)),
                theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false, wrap: true);
        }

        control(row);

        ui.FillRect(new Rect(x, row.Bottom, width, 1), theme.StrokeSubtle);
        return row.Bottom + 1;
    }

    /// <summary>The right-aligned slot a row's control sits in. ShellButtonStyle is 32 tall.</summary>
    private Rect ActionSlot(Rect row, double width) =>
        new(row.Right - width, row.Center.Y - S(16), width, S(32));

    /// <summary>A toggle switch: a track with a knob that slides.</summary>
    private void Switch(Ui ui, int id, Rect slot, bool value, Action<bool> set)
    {
        var track = new Rect(slot.Right - S(40), slot.Center.Y - S(10), S(40), S(20));
        if (ui.Interact(id, track)) set(!value);

        ui.FillRounded(track, (float)S(10),
            value ? ui.Theme.Accent
            : ui.IsHot(id) ? ui.Theme.StrokeStrong
            : ui.Theme.StrokeDefault);

        var knob = new Point(value ? track.Right - S(10) : track.X + S(10), track.Center.Y);
        ui.FillCircle(knob, (float)S(7), Rgba.White);
    }

    /// <summary>An editable number box. Clicking focuses it; the window's key handler types into it.
    /// Commits on Enter or on clicking away, clamped to the range. An empty box means the minimum.</summary>
    private void NumberField(Ui ui, int id, Rect slot, int value, int min, int max, Action<int> set)
    {
        var focused = _editingNumber == id;
        if (focused) _numberBounds = slot;

        if (ui.Interact(id, slot) && !focused)
        {
            _editingNumber = id;
            _numberDraft = value.ToString();
        }

        var radius = (float)S(Metrics.RadiusControl);
        ui.FillRounded(slot, radius, ui.Theme.SurfaceOverlay);
        ui.StrokeRounded(slot, radius,
            focused ? ui.Theme.Accent
            : ui.IsHot(id) ? ui.Theme.StrokeStrong
            : ui.Theme.StrokeDefault,
            focused ? 1.5f : 1f);

        var text = focused ? _numberDraft : value.ToString();
        var inner = slot.Deflate(S(10));

        ui.Text(text, inner, ui.Theme.TextPrimary, (float)S(Metrics.FontBody));

        if (!focused) return;

        // A caret, so a focused empty box does not read as a dead one.
        var caretX = inner.X + ui.MeasureText(text, S(Metrics.FontBody)) + S(1);
        ui.FillRect(new Rect(caretX, slot.Y + S(8), S(1.5), slot.Height - S(16)), ui.Theme.TextPrimary);

        // Held so the key handler can commit into the right setting without knowing which row it is.
        _numberCommit = () =>
        {
            var parsed = int.TryParse(_numberDraft, out var typed) ? typed : min;
            set(Math.Clamp(parsed, min, max));
        };
    }

    /// <summary>The number box being typed into, if any, and the text as typed.</summary>
    private int? _editingNumber;
    private string _numberDraft = "";

    /// <summary>Where the focused box sits, so a click landing anywhere else commits it.</summary>
    private Rect _numberBounds;

    /// <summary>Writes the focused box's draft back into whichever setting it belongs to.</summary>
    private Action? _numberCommit;

    /// <summary>Commits and unfocuses the number box, if one is focused.</summary>
    private void CommitNumberField()
    {
        if (_editingNumber is null) return;

        _numberCommit?.Invoke();
        _numberCommit = null;
        _editingNumber = null;
    }

    private const string Recording = "Press keys…";

    /// <summary>The width every recorder shares: the widest label any of them can show. Sizing each
    /// to its own label leaves a ragged column, and a button that resizes when armed jumps.</summary>
    private double HotkeyWidth(Ui ui)
    {
        var font = S(Metrics.FontCaption);
        var widest = ui.MeasureText(Recording, font);

        foreach (var (_, binding, _) in Hotkeys)
            widest = Math.Max(widest, ui.MeasureText(Describe(binding(_settings)), font));

        return Math.Max(S(96), widest + S(28));
    }

    /// <summary>A hotkey recorder. Clicking arms it; the next key press becomes the binding. The
    /// window's key handler does the recording - there is nothing here to listen with.</summary>
    private double Hotkey(
        Ui ui, int id, double x, double y, double width, double slotWidth,
        string title, HotkeyBinding binding)
    {
        return Row(ui, x, y, width, title, null, row =>
        {
            var clearId = Ui.Id(id, 1);
            var recording = _recordingHotkey == id;
            var label = recording ? Recording : Describe(binding);

            // The clear gutter is always reserved, so clearing does not shift the recorder.
            var clearable = binding.Key != 0 && !recording;
            var full = ActionSlot(row, slotWidth);
            var clear = new Rect(full.Right - S(22), full.Center.Y - S(11), S(22), S(22));
            var slot = new Rect(full.X - S(26), full.Y, full.Width, full.Height);

            if (clearable)
            {
                if (ui.Interact(clearId, clear))
                {
                    binding.Modifiers = 0;
                    binding.Key = 0;
                    _hotkeyWarning = null;
                    SaveSettings();
                    HotkeysChanged?.Invoke();
                    Invalidate();
                }

                if (ui.IsHot(clearId))
                    ui.FillRounded(clear, (float)S(Metrics.RadiusControl), ui.Theme.FillHover);

                ui.Text("✕", clear,
                    ui.IsHot(clearId) ? ui.Theme.TextPrimary : ui.Theme.TextTertiary,
                    (float)S(Metrics.FontCaption), align: TextAlign.Center);
            }

            // The click lands after `recording` was read, so this frame still draws the old label.
            // Without the repaint, "Press keys…" would not appear until the next mouse move.
            if (ui.Interact(id, slot))
            {
                _recordingHotkey = recording ? null : id;
                _hotkeyWarning = null;
                RecordingChanged?.Invoke(_recordingHotkey is not null);
                Invalidate();
            }

            ui.FillRounded(slot, (float)S(Metrics.RadiusControl),
                recording ? ui.Theme.FillSelected
                : ui.IsHot(id) ? ui.Theme.FillHover
                : ui.Theme.SurfaceOverlay);

            ui.StrokeRounded(slot, (float)S(Metrics.RadiusControl),
                recording ? ui.Theme.Accent : ui.Theme.StrokeSubtle,
                recording ? 1.5f : 1f);

            ui.Text(label, slot,
                recording ? ui.Theme.Accent : ui.Theme.TextSecondary,
                (float)S(Metrics.FontCaption), align: TextAlign.Center);
        });
    }

    /// <summary>Describe() for a sidebar hint: unbound draws nothing rather than the word "None".</summary>
    private static string Hint(HotkeyBinding binding) => binding.Key == 0 ? "" : Describe(binding);

    /// <summary>A binding as text: "Ctrl + Shift + S".</summary>
    private static string Describe(HotkeyBinding binding)
    {
        if (binding.Key == 0) return "None";

        var parts = new List<string>(4);
        if ((binding.Modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((binding.Modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((binding.Modifiers & 0x0008) != 0) parts.Add("Win");
        parts.Add(KeyName(binding.Key));

        return string.Join(" + ", parts);
    }

    private static string KeyName(uint key) => key switch
    {
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",           // F1..F24
        0x2C => "PrtScn",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x20 => "Space",
        >= 0x30 and <= 0x5A => ((char)key).ToString(),      // 0-9, A-Z
        _ => $"0x{key:X2}",
    };

    /// <summary>A path, shortened from the middle so both ends stay readable.</summary>
    private static string Shorten(string path, int limit)
    {
        if (path.Length <= limit) return path;
        var keep = (limit - 3) / 2;
        return $"{path[..keep]}…{path[^keep..]}";
    }
}
