using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The settings sheet: a modal card over the library, one page per tab. Every change applies and
/// persists immediately, so there is nothing to confirm or cancel.
///
/// The page area sizes itself to its page, animated, so a short page has no dead space to scroll
/// into; it scrolls only when a page is genuinely taller than the cap.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly (string Name, Icon Icon)[] Pages =
    [
        ("General", Icons.General),
        ("Capture", Icons.Camera),
        ("Quick access", Icons.Cards),
        ("Shortcuts", Icons.Keyboard),
        ("Output", Icons.Output),
        ("Appearance", Icons.Palette),
    ];

    private int _page;
    private double _pageScroll;

    /// <summary>Each page's content height, measured as it is drawn, for the scroll extent.</summary>
    private readonly double[] _pageHeights = new double[Pages.Length];

    /// <summary>The page area's one height: tall enough for the longest page, seven shortcuts, to show
    /// whole.</summary>
    private const double PageHeight = 490;

    private readonly Dropdown _languageBox = new();
    private readonly Dropdown _cornerBox = new();

    private void CloseDropdowns()
    {
        _languageBox.Close();
        _cornerBox.Close();
    }

    private bool DropdownOpen => _languageBox.IsOpen || _cornerBox.IsOpen;

    private void OpenSettings()
    {
        _settingsOpen = true;
        _pageScroll = 0;
        CloseDropdowns();
        Invalidate();
    }

    private void CloseSettings()
    {
        _settingsOpen = false;
        CloseDropdowns();
        _ui?.Blur();
        if (_recordingHotkey is not null)
        {
            _recordingHotkey = null;
            RecordingChanged?.Invoke(false);
        }
    }

    private void ScrollSettings(double step)
    {
        CloseDropdowns();
        var before = _pageScroll;
        _pageScroll = Math.Clamp(_pageScroll - step, 0, Math.Max(0, _pageHeights[_page] - S(PageHeight)));
        if (_pageScroll != before) Invalidate();
    }

    private void DrawSettings(Ui ui, double width, double height)
    {
        var open = ui.Animate(Ui.Id("settings.open"), _settingsOpen ? 1 : 0, Metrics.Motion);
        if (open <= 0.01) return;

        var theme = ui.Theme;
        ui.FillRect(new Rect(0, 0, width, height), theme.Scrim.WithAlpha((byte)(theme.Scrim.A * open)));

        // One height for every page, so the sheet never moves under the pointer; taller pages scroll.
        var header = S(52);
        var tabs = S(58 + 12) + 1;
        var pageHeight = S(PageHeight);
        var sheetWidth = S(620);
        var sheetHeight = header + tabs + pageHeight;
        var top = Math.Max(S(16), (height - sheetHeight) / 2);
        var sheet = new Rect((width - sheetWidth) / 2, top + S(12) * (1 - open), sheetWidth, sheetHeight);

        // A scrim press closes the sheet; the frame keeps drawing it so the fade-out does not blink.
        if (_settingsOpen && ui.PointerPressed && !sheet.Contains(ui.Pointer) && !DropdownOpen
            && ui.Pointer.Y > CaptionHeight)
            CloseSettings();

        // Closing: the sheet fades out but no longer answers the pointer.
        ui.Inert = !_settingsOpen;

        var radius = (float)S(Metrics.RadiusLg);
        ui.Shadow(sheet, radius, S(60), S(24), theme.Shadow);
        ui.FillRounded(sheet, radius, theme.SurfaceWindow);
        ui.StrokeRounded(sheet, radius, theme.StrokeDefault);

        // ---- header ----
        var title = ui.MeasureText("Settings", S(Metrics.FontLg), Weight.Semibold, Face.Display);
        ui.Text("Settings", new Rect(sheet.X + S(20), sheet.Y, title + 1, header), theme.TextPrimary, S(Metrics.FontLg),
            Weight.Semibold, face: Face.Display);
        ui.Text($"NexusShot {AppVersion}", new Rect(sheet.X + S(30) + title, sheet.Y, S(200), header), theme.TextTertiary, S(11));
        if (ui.IconButton(Ui.Id("settings.close"), new Rect(sheet.Right - S(12) - S(32), sheet.Y + (header - S(32)) / 2,
            S(32), S(32)), Icons.Close, "Close", "Esc", destructive: true))
            CloseSettings();

        // ---- tabs ----
        var tabRow = new Rect(sheet.X + S(12), sheet.Y + header, sheet.Width - S(24), S(58));
        var tabWidth = (tabRow.Width - S(4) * (Pages.Length - 1)) / Pages.Length;
        for (var i = 0; i < Pages.Length; i++)
        {
            var tab = new Rect(tabRow.X + i * (tabWidth + S(4)), tabRow.Y, tabWidth, S(58));
            var id = Ui.Id(Ui.Id("settings.tab"), i);
            if (ui.Interact(id, tab) && i != _page)
            {
                _page = i;
                _pageScroll = 0;
                CloseDropdowns();
                ui.Blur();
            }

            var on = i == _page;
            var fill = on ? theme.AccentSoft : ui.IsHot(id) ? theme.SurfaceHover : default;
            if (fill.A > 0) ui.FillRounded(tab, (float)S(Metrics.RadiusMd), fill);
            ui.Icon(Pages[i].Icon, new Rect(tab.X, tab.Y + S(9), tab.Width, S(22)), on ? theme.AccentText
                : ui.IsHot(id) ? theme.TextPrimary : theme.TextSecondary, S(20));
            ui.Text(Pages[i].Name, new Rect(tab.X, tab.Y + S(34), tab.Width, S(16)),
                on || ui.IsHot(id) ? theme.TextPrimary : theme.TextSecondary, S(11.5), Weight.Semibold, TextAlign.Center);
        }
        ui.FillRect(new Rect(sheet.X, tabRow.Bottom + S(12), sheet.Width, 1), theme.StrokeSubtle);

        // ---- page ----
        var body = new Rect(sheet.X, tabRow.Bottom + S(12) + 1, sheet.Width, pageHeight);
        _pageScroll = Math.Clamp(_pageScroll, 0, Math.Max(0, _pageHeights[_page] - body.Height));
        ui.PushClip(body);
        var pageTop = body.Y - _pageScroll;
        var x = body.X + S(20);
        var inner = body.Width - S(40);
        var end = _page switch
        {
            0 => GeneralPage(ui, x, pageTop + S(16), inner),
            1 => CapturePage(ui, x, pageTop + S(16), inner),
            2 => QuickAccessPage(ui, x, pageTop + S(16), inner),
            3 => ShortcutsPage(ui, x, pageTop + S(16), inner),
            4 => OutputPage(ui, x, pageTop + S(16), inner),
            _ => AppearancePage(ui, x, pageTop + S(16), inner),
        };
        _pageHeights[_page] = end + S(20) - pageTop;

        if (_pageHeights[_page] > body.Height) ui.Scrollbar(body, _pageHeights[_page], _pageScroll);
        ui.PopClip();

        // Open menus paint after the clip, so they can hang past the sheet's edge.
        var client = new Rect(0, 0, width, height);
        _languageBox.DrawOpen(ui, client);
        _cornerBox.DrawOpen(ui, client);
        ui.Inert = false;
    }

    // ============================  PAGES  ============================

    private double GeneralPage(Ui ui, double x, double y, double width)
    {
        y = Intro(ui, "How NexusShot starts and what happens after a capture.", x, y, width);
        return Group(ui, x, y, width,
        [
            Row("Launch at sign-in", "Runs quietly in the tray", ToggleWidth,
                rect => Toggle(ui, "settings.startup", rect, _settings.StartWithWindows, value =>
                {
                    _settings.StartWithWindows = value;
                    Startup.Set(value);
                })),
            Row("After capture", "What appears once a capture is taken", Segments(ui, AfterCaptureNames),
                rect => Segmented(ui, "settings.after", rect, AfterCaptureNames, (int)_settings.AfterCapture,
                    index => _settings.AfterCapture = (AfterCapture)index)),
            Row("Copy to clipboard automatically", "Every capture lands on the clipboard too", ToggleWidth,
                rect => Toggle(ui, "settings.autocopy", rect, _settings.CopyToClipboardAutomatically,
                    value => _settings.CopyToClipboardAutomatically = value)),
            Row("Save captures automatically", "Written straight into the save folder", ToggleWidth,
                rect => Toggle(ui, "settings.autosave", rect, _settings.SaveAutomatically,
                    value => _settings.SaveAutomatically = value)),
            Row("Shutter sound", null, ToggleWidth,
                rect => Toggle(ui, "settings.shutter", rect, _settings.ShutterSound, value => _settings.ShutterSound = value)),
            Row("Updates", UpdateCaption(), UpdateControlWidth, rect => UpdateControl(ui, rect)),
            Row("Check for updates automatically", "At startup and every six hours - it only tells you", ToggleWidth,
                rect => Toggle(ui, "settings.autoupdate", rect, _settings.CheckForUpdates,
                    value => _settings.CheckForUpdates = value)),
        ]);
    }

    private static readonly string[] AfterCaptureNames = ["Card", "Editor", "Copy only"];
    private static readonly string[] CaptureModeNames = ["Region", "Window", "Screen"];
    private static readonly string[] CornerNames = ["Bottom left", "Bottom right", "Top left", "Top right"];
    private static readonly string[] FormatNames = ["PNG", "JPEG", "BMP"];
    private static readonly string[] ThemeNames = ["System", "Dark", "Light"];

    private double CapturePage(Ui ui, double x, double y, double width)
    {
        y = Intro(ui, "Defaults for timed captures and the tray.", x, y, width);

        // The segmented order is the order the header shows the modes in, not the enum's.
        CaptureMode[] modes = [CaptureMode.Region, CaptureMode.ActiveWindow, CaptureMode.FullScreen];
        var languages = OcrLanguages();
        var languageNames = languages.Select(language => language.Name).ToArray();
        var chosen = Math.Max(0, Array.FindIndex(languages, language =>
            string.Equals(language.Tag, _settings.OcrLanguage, StringComparison.OrdinalIgnoreCase)));

        return Group(ui, x, y, width,
        [
            Row("Default mode", "Used by timed capture and the tray icon", Segments(ui, CaptureModeNames),
                rect => Segmented(ui, "settings.mode", rect, CaptureModeNames, Array.IndexOf(modes, _settings.DefaultCaptureMode),
                    index => _settings.DefaultCaptureMode = modes[index])),
            Row("Timer delay", null, S(112),
                rect => Stepper(ui, "settings.timer", rect, $"{_settings.TimedCaptureSeconds} s",
                    step => _settings.TimedCaptureSeconds = Math.Clamp(_settings.TimedCaptureSeconds + step, 1, 30))),
            Row("Include cursor", null, ToggleWidth,
                rect => Toggle(ui, "settings.cursor", rect, _settings.IncludeCursor, value => _settings.IncludeCursor = value)),
            Row("Text recognition language", "Used by Capture text and Copy text", Dropdown.Width(ui, languageNames),
                rect => _languageBox.Field(ui, Ui.Id("settings.ocr"), Control(rect), languageNames, chosen, index =>
                {
                    _settings.OcrLanguage = languages[index].Tag;
                    SaveSettings();
                })),
        ]);
    }

    /// <summary>The recognition languages on this PC, after the profile default. Read once: the list
    /// changes only when a language pack is installed, which needs a new session anyway.</summary>
    private (string? Tag, string Name)[] OcrLanguages() => _ocrLanguages ??=
    [
        (null, "Windows default"),
        .. TextRecognition.Languages().Select(language => ((string?)language.Tag, language.Name)),
    ];

    private (string? Tag, string Name)[]? _ocrLanguages;

    private double QuickAccessPage(Ui ui, double x, double y, double width)
    {
        y = Intro(ui, "The floating card that appears after each capture.", x, y, width);
        var dismiss = _settings.PreviewDismissSeconds == 0 ? "Never" : $"{_settings.PreviewDismissSeconds} s";
        return Group(ui, x, y, width,
        [
            Row("Dismiss after", "Hovering pauses the countdown", S(112),
                rect => Stepper(ui, "settings.dismiss", rect, dismiss,
                    step => _settings.PreviewDismissSeconds = Math.Clamp(_settings.PreviewDismissSeconds + step, 0, 120))),
            Row("Position", null, Dropdown.Width(ui, CornerNames),
                rect => _cornerBox.Field(ui, Ui.Id("settings.corner"), Control(rect), CornerNames, (int)_settings.CardCorner, index =>
                {
                    _settings.CardCorner = (CardCorner)index;
                    SaveSettings();
                })),
            Row("Pin new cards", "Cards stay until closed", ToggleWidth,
                rect => Toggle(ui, "settings.pin", rect, _settings.PinNewCards, value => _settings.PinNewCards = value)),
        ]);
    }

    private double ShortcutsPage(Ui ui, double x, double y, double width)
    {
        // On the intro line, so it never pushes the list down or appears under the pointer.
        if (!HotkeysAreDefault())
        {
            var reset = ui.ButtonWidth("Restore all", Icons.Undo, small: true);
            if (ui.Button(Ui.Id("settings.reset"), new Rect(x + width - reset, y - S(5), reset, S(28)), "Restore all",
                ButtonStyle.Danger, Icons.Undo, small: true, tooltip: "Put every shortcut back to its default"))
                ResetHotkeys();
        }
        y = Intro(ui, "Click a shortcut, then press the new keys. Backspace clears, Esc cancels.", x, y, width);

        var rows = HotkeyIds.All.Select(id => Row(id.Title(), null, S(28 + 6 + 212), rect =>
        {
            ResetHotkeyButton(ui, id, rect with { Width = S(28) });
            HotkeyField(ui, id, rect with { X = rect.X + S(34), Width = S(212) });
        })).ToList();
        y = Group(ui, x, y, width, rows);

        if (_hotkeyWarning is { } warning)
        {
            ui.Text(warning, new Rect(x + S(2), y + S(8), width, S(18)), ui.Theme.Danger, S(Metrics.FontSm));
            y += S(26);
        }
        return y;
    }

    /// <summary>Puts one shortcut back to its default. Shown only when it differs, so the column of
    /// resets doubles as a list of what has been changed.</summary>
    private void ResetHotkeyButton(Ui ui, HotkeyId hotkey, Rect slot)
    {
        var current = _settings.Hotkey(hotkey);
        var fallback = new AppSettings().Hotkey(hotkey);
        if (current.IsSameGesture(fallback)) return;

        if (!ui.IconButton(Ui.Id($"hotkey.reset.{hotkey}"), new Rect(slot.X, slot.Center.Y - S(14), S(28), S(28)),
            Icons.Undo, "Restore default", iconSize: 15)) return;

        current.Modifiers = fallback.Modifiers;
        current.Key = fallback.Key;
        if (_recordingHotkey == hotkey)
        {
            _recordingHotkey = null;
            RecordingChanged?.Invoke(false);
        }
        _hotkeyWarning = null;
        SaveSettings();
        HotkeysChanged?.Invoke();
    }

    private double OutputPage(Ui ui, double x, double y, double width)
    {
        y = Intro(ui, "Where captures are written and how they are named.", x, y, width);
        var example = CaptureName.For(DateTime.Now) + ImageFiles.ExtensionOf(_settings.CaptureFormat);
        return Group(ui, x, y, width,
        [
            Row("Save folder", null, S(360), rect =>
            {
                var change = ui.ButtonWidth("Change…", small: true);
                var path = new Rect(rect.X, rect.Center.Y - S(16), rect.Width - change - S(6), S(32));
                ReadOnlyBox(ui, path, _settings.ScreenshotFolder, middle: true);
                if (!ui.Button(Ui.Id("settings.folder"), new Rect(rect.Right - change, rect.Center.Y - S(14), change, S(28)),
                    "Change…", ButtonStyle.Outline, small: true)) return;

                // The picker is modal and would pump its loop mid-frame.
                Post(() =>
                {
                    if (FolderPicker.Pick(Handle, _settings.ScreenshotFolder) is not { } folder) return;
                    _settings.ScreenshotFolder = folder;
                    SaveSettings();
                });
            }),
            Row("File format", null, Segments(ui, FormatNames),
                rect => Segmented(ui, "settings.format", rect, FormatNames, (int)_settings.CaptureFormat,
                    index => _settings.CaptureFormat = (ImageFormat)index)),
            Row("File name", "Named by capture time, so the library can be rebuilt from the folder", S(260),
                rect => ReadOnlyBox(ui, new Rect(rect.X, rect.Center.Y - S(16), rect.Width, S(32)), example, middle: false)),
        ]);
    }

    private double AppearancePage(Ui ui, double x, double y, double width)
    {
        y = Intro(ui, "Matches Windows unless you choose otherwise.", x, y, width);
        AppTheme[] themes = [AppTheme.System, AppTheme.Dark, AppTheme.Light];
        return Group(ui, x, y, width,
        [
            Row("Theme", null, Segments(ui, ThemeNames),
                rect => Segmented(ui, "settings.theme", rect, ThemeNames, Array.IndexOf(themes, _settings.Theme),
                    index => _settings.Theme = themes[index])),
            Row("Accent", null, Accent.Presets.Length * S(30) - S(8), rect =>
            {
                for (var i = 0; i < Accent.Presets.Length; i++)
                {
                    var preset = Accent.Presets[i];
                    var dot = new Rect(rect.X + i * S(30), rect.Center.Y - S(11), S(22), S(22));
                    var id = Ui.Id(Ui.Id("settings.accent"), i);
                    if (ui.Swatch(id, dot, preset.Color, preset.Name == _settings.Accent, ui.Theme.SurfacePane))
                    {
                        _settings.Accent = preset.Name;
                        SaveSettings();
                    }
                    ui.Tip(id, dot, preset.Name);
                }
            }),
        ]);
    }

    // ============================  ROWS & CONTROLS  ============================

    private sealed record SettingRow(string Title, string? Caption, double ControlWidth, Action<Rect> Control);

    private static SettingRow Row(string title, string? caption, double controlWidth, Action<Rect> control) =>
        new(title, caption, controlWidth, control);

    private double ToggleWidth => S(36);

    /// <summary>A standard-height control centred in its row's slot.</summary>
    private Rect Control(Rect slot) => new(slot.X, slot.Center.Y - S(16), slot.Width, S(32));

    private double Segments(Ui ui, string[] names) => ui.SegmentedWidth(names);

    private double Intro(Ui ui, string text, double x, double y, double width)
    {
        ui.Text(text, new Rect(x + S(2), y, width, S(18)), ui.Theme.TextTertiary, S(12.5));
        return y + S(18) + S(12);
    }

    /// <summary>
    /// A group of rows in one inset container, hairlines between them. Each row is at least 52 high;
    /// its title takes the width the control leaves, and its caption wraps under it rather than
    /// squeezing the control - so a segmented label never breaks onto two lines.
    /// </summary>
    private double Group(Ui ui, double x, double y, double width, IReadOnlyList<SettingRow> rows)
    {
        var theme = ui.Theme;
        var heights = rows.Select(row =>
        {
            var textWidth = width - S(30) - row.ControlWidth - S(16);
            var caption = row.Caption is null ? 0
                : S(2) + Math.Max(S(16), Math.Ceiling(ui.MeasureText(row.Caption, S(Metrics.FontSm)) / Math.Max(1, textWidth)) * S(16));
            return Math.Max(S(52), S(16) + S(18) + caption);
        }).ToArray();

        var container = new Rect(x, y, width, heights.Sum());
        var radius = (float)S(Metrics.RadiusMd);
        ui.FillRounded(container, radius, theme.SurfacePane);
        ui.StrokeRounded(container, radius, theme.StrokeSubtle);

        var top = y;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var bounds = new Rect(x + S(16), top, width - S(30), heights[i]);
            var textWidth = bounds.Width - row.ControlWidth - S(16);

            if (row.Caption is null)
                ui.Text(row.Title, new Rect(bounds.X, bounds.Y, textWidth, bounds.Height), theme.TextPrimary,
                    S(Metrics.FontMd), Weight.Medium);
            else
            {
                var block = S(18) + S(2) + (heights[i] - S(16) - S(18) - S(2));
                var textTop = bounds.Center.Y - block / 2;
                ui.Text(row.Title, new Rect(bounds.X, textTop, textWidth, S(18)), theme.TextPrimary, S(Metrics.FontMd), Weight.Medium);
                ui.Text(row.Caption, new Rect(bounds.X, textTop + S(20), textWidth, heights[i] - S(16) - S(20)),
                    theme.TextTertiary, S(Metrics.FontSm), middle: false, wrap: true);
            }

            row.Control(new Rect(bounds.Right - row.ControlWidth, bounds.Y, row.ControlWidth, bounds.Height));
            if (i < rows.Count - 1) ui.FillRect(new Rect(bounds.X, bounds.Bottom - 1, bounds.Width, 1), theme.StrokeSubtle);
            top += heights[i];
        }
        return container.Bottom;
    }

    private void Toggle(Ui ui, string name, Rect slot, bool value, Action<bool> set)
    {
        if (!ui.Toggle(Ui.Id(name), slot, value)) return;
        set(!value);
        SaveSettings();
    }

    private void Segmented(Ui ui, string name, Rect slot, string[] options, int selected, Action<int> set)
    {
        var bounds = new Rect(slot.X, slot.Center.Y - S(16), slot.Width, S(32));
        if (ui.Segmented(Ui.Id(name), bounds, options, selected) is not { } index) return;
        set(index);
        SaveSettings();
    }

    private void Stepper(Ui ui, string name, Rect slot, string text, Action<int> step)
    {
        var delta = ui.Stepper(Ui.Id(name), new Rect(slot.X, slot.Center.Y - S(16), slot.Width, S(32)), text);
        if (delta == 0) return;
        step(delta);
        SaveSettings();
    }

    /// <summary>A value shown in a field's box but not editable: the save folder, the file name.</summary>
    private void ReadOnlyBox(Ui ui, Rect bounds, string text, bool middle)
    {
        var radius = (float)S(Metrics.RadiusSm);
        ui.FillRounded(bounds, radius, ui.Theme.SurfaceWindow);
        ui.StrokeRounded(bounds, radius, ui.Theme.StrokeDefault);
        var inner = bounds.Deflate(S(10)) with { Y = bounds.Y, Height = bounds.Height };
        var font = S(11.5);
        var shown = middle ? ShortenMiddle(ui, text, inner.Width, font) : ui.Ellipsize(text, inner.Width, font, face: Face.Mono);
        ui.Text(shown, inner, ui.Theme.TextSecondary, font, face: Face.Mono);
    }

    /// <summary>A path cut from the middle, so both the drive and the folder's own name stay readable.</summary>
    private static string ShortenMiddle(Ui ui, string text, double width, double font)
    {
        if (ui.MeasureText(text, font, face: Face.Mono) <= width) return text;
        for (var keep = text.Length / 2; keep > 3; keep--)
        {
            var candidate = $"{text[..keep]}…{text[^keep..]}";
            if (ui.MeasureText(candidate, font, face: Face.Mono) <= width) return candidate;
        }
        return "…";
    }

    /// <summary>
    /// A hotkey recorder. Fixed width, so every shortcut in the list lines up in one column; the keys
    /// read from the left, and a pencil marks it editable. Clicking arms it - the window's key handler
    /// does the recording.
    /// </summary>
    private void HotkeyField(Ui ui, HotkeyId hotkey, Rect slot)
    {
        var theme = ui.Theme;
        var bounds = new Rect(slot.X, slot.Center.Y - S(16), slot.Width, S(32));
        var id = Ui.Id($"hotkey.{hotkey}");
        var recording = _recordingHotkey == hotkey;

        if (ui.Interact(id, bounds))
        {
            _recordingHotkey = recording ? null : hotkey;
            _hotkeyWarning = null;
            RecordingChanged?.Invoke(_recordingHotkey is not null);
            recording = !recording;
        }

        // The whole box is the control, pencil included, so the whole box answers the hover.
        var radius = (float)S(Metrics.RadiusSm);
        ui.FillRounded(bounds, radius, recording ? theme.AccentSoft : ui.IsHot(id) ? theme.SurfaceHover : theme.SurfaceWindow);
        ui.StrokeRounded(bounds, radius, recording ? theme.Accent : ui.IsHot(id) ? theme.StrokeStrong : theme.StrokeDefault,
            recording ? (float)S(1.5) : 1);

        var x = bounds.X + S(6);
        if (recording)
        {
            // A slow pulse, so an armed field reads as waiting rather than stuck.
            var pulse = 0.55 + 0.45 * Math.Abs(Math.Cos(ui.Now / 1100.0 * Math.PI));
            ui.KeepAnimating();
            ui.Text("Press keys…", new Rect(x + S(6), bounds.Y, bounds.Width, bounds.Height),
                theme.AccentText.WithAlpha((byte)(255 * pulse)), S(Metrics.FontSm), Weight.Semibold);
            return;
        }

        var binding = _settings.Hotkey(hotkey);

        // Interacted after the field, so a press here clears rather than arming the recorder.
        var clear = new Rect(bounds.Right - S(52), bounds.Center.Y - S(12), S(24), S(24));
        if (ui.IconButton(Ui.Id($"hotkey.clear.{hotkey}"), clear, Icons.Close, "Clear shortcut",
            enabled: binding.Key != 0, iconSize: 13))
        {
            binding.Modifiers = 0;
            binding.Key = 0;
            _hotkeyWarning = null;
            SaveSettings();
            HotkeysChanged?.Invoke();
        }

        if (binding.Key == 0)
            ui.Text("Not set", new Rect(x + S(6), bounds.Y, bounds.Width, bounds.Height), theme.TextTertiary, S(Metrics.FontSm));
        else
        {
            foreach (var part in Describe(binding).Split(" + "))
            {
                var cap = new Rect(x, bounds.Center.Y - S(10), ui.MeasureText(part, S(11), face: Face.Mono) + S(14), S(20));
                ui.FillRounded(cap, (float)S(Metrics.RadiusXs), theme.SurfaceHover);
                ui.FillRect(new Rect(cap.X + S(2), cap.Bottom - 1, cap.Width - S(4), 1), theme.StrokeDefault);
                ui.Text(part, cap, theme.TextPrimary, S(11), align: TextAlign.Center, face: Face.Mono);
                x = cap.Right + S(4);
            }
        }
        ui.Icon(Icons.Edit, new Rect(bounds.Right - S(24), bounds.Y, S(16), bounds.Height),
            ui.IsHot(id) ? theme.TextPrimary : theme.TextTertiary, S(14));
    }

    /// <summary>The version stamped onto the assembly at build time (<c>-p:Version</c>). Read rather
    /// than hardcoded, so a tagged release cannot show a version that disagrees with it.</summary>
    private static readonly string AppVersion = FormatVersion();

    private static string FormatVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }

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
}
