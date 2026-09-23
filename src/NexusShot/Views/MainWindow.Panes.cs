using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>The shell's two panes as drawn each frame: the sidebar (brand, capture actions,
/// history, footer) and the detail pane (preview, action bar, empty state).</summary>
public sealed partial class MainWindow
{
    // ============================  SIDEBAR  ============================

    private void DrawSidebar(Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds)
    {
        var theme = ui.Theme;
        ui.FillRect(bounds, theme.SurfaceSunken);

        // The brand sits at the top of the rail: the window controls are over on the right, so
        // nothing here has to clear them.
        var y = bounds.Y + S(16);

        DrawBrandMark(ui, new Rect(bounds.X + S(18), y, S(22), S(22)));
        ui.Text("NexusShot", new Rect(bounds.X + S(50), y, S(110), S(22)),
            theme.TextPrimary, (float)S(Metrics.FontSubtitle), bold: true);

        // The pill hangs off the rail's trailing edge, mirroring the mark's inset on the left, and is
        // sized to its text so a two-digit version does not overflow it.
        var pillFont = S(10);
        var pillWidth = Math.Max(S(40), ui.MeasureText(AppVersion, pillFont) + S(16));
        var pill = new Rect(bounds.Right - S(18) - pillWidth, y + S(2), pillWidth, S(18));

        ui.FillRounded(pill, (float)S(9), theme.SurfaceOverlay);
        ui.Text(AppVersion, pill, theme.TextTertiary, (float)pillFont, align: TextAlign.Center);

        y += S(38);

        y = DrawCaptureAction(ui, bounds, y, Ui.Id("capture.region"), Icons.CaptureRegion, "Region",
            Hint(_settings.CaptureRegionHotkey), CaptureMode.Region);
        y = DrawCaptureAction(ui, bounds, y, Ui.Id("capture.fullscreen"), Icons.CaptureScreen, "Full screen",
            Hint(_settings.CaptureFullScreenHotkey), CaptureMode.FullScreen);
        y = DrawCaptureAction(ui, bounds, y, Ui.Id("capture.window"), Icons.CaptureWindow, "Active window",
            Hint(_settings.CaptureActiveWindowHotkey), CaptureMode.ActiveWindow);

        y += S(14);

        ui.FillRect(new Rect(bounds.X, y, bounds.Width, 1), theme.StrokeSubtle);
        ui.Text("RECENT", new Rect(bounds.X + S(20), y + S(10), bounds.Width, S(18)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), bold: true);

        y += S(34);

        var footer = S(48);
        var list = new Rect(bounds.X, y, bounds.Width, bounds.Bottom - y - footer);
        DrawHistory(ui, target, list);

        DrawSidebarFooter(ui, new Rect(bounds.X, bounds.Bottom - footer, bounds.Width, footer));
    }

    /// <summary>The version stamped onto the assembly at build time (<c>-p:Version</c>). Read rather
    /// than hardcoded, so a tagged release cannot ship a badge that disagrees with it.</summary>
    private static readonly string AppVersion = FormatVersion();

    private static string FormatVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>The mark's points in client pixels, and the bounds they were laid out for.</summary>
    private sealed record BrandMarkPoints(
        Rect Bounds, float Radius, float Thickness,
        Point[] Diagonal, Point[] UpperMark, Point[] LowerMark);

    private BrandMarkPoints? _brandMark;

    /// <summary>The app mark, drawn rather than loaded: a slate tile split by a 135° diagonal, and
    /// two crop marks, on the 960-unit grid of the icon source.
    ///
    /// The point arrays are laid out once per size: the mark is redrawn every frame, and only a
    /// resize or a DPI change moves any of it.</summary>
    private void DrawBrandMark(Ui ui, Rect bounds)
    {
        if (_brandMark is not { } mark || mark.Bounds != bounds)
        {
            const double unit = 960;
            var k = bounds.Width / unit;

            double X(double u) => bounds.X + u * k;
            double Y(double u) => bounds.Y + u * k;

            mark = new BrandMarkPoints(
                bounds,
                Radius: (float)(220 * k),
                // Crop marks: corners on the diagonal, so each arm crosses both halves.
                Thickness: (float)(84 * k),
                Diagonal: [new Point(X(0), Y(0)), new Point(X(unit), Y(0)), new Point(X(0), Y(unit))],
                UpperMark: [new Point(X(268), Y(488)), new Point(X(268), Y(268)), new Point(X(488), Y(268))],
                LowerMark: [new Point(X(472), Y(692)), new Point(X(692), Y(692)), new Point(X(692), Y(472))]);

            _brandMark = mark;
        }

        // The slate tile, then the half above the 135° diagonal in cyan - intersected with the tile,
        // so it inherits the rounded corners rather than overhanging them.
        ui.FillRounded(bounds, mark.Radius, Tile);
        ui.FillRoundedRegion(bounds, mark.Radius, mark.Diagonal, Cyan);

        ui.Polyline(mark.UpperMark, Marks, mark.Thickness);
        ui.Polyline(mark.LowerMark, Marks, mark.Thickness);
    }

    private static readonly Rgba Tile = new(0x3A, 0x46, 0x52, 0xFF);

    private static readonly Rgba Cyan = new(0x46, 0xBA, 0xE3, 0xFF);

    private static readonly Rgba Marks = new(0x18, 0x22, 0x2B, 0xFF);

    private double DrawCaptureAction(
        Ui ui, Rect sidebar, double y, int id,
        string glyph, string label, string shortcut, CaptureMode mode)
    {
        var theme = ui.Theme;
        var row = new Rect(sidebar.X + S(10), y, sidebar.Width - S(20), S(38));

        // Posted: this runs inside Render, and capture hides the window and spins the overlay's own
        // message loop.
        if (ui.Interact(id, row)) Post(() => CaptureRequested?.Invoke(mode));

        var fill = ui.IsActive(id) ? theme.FillPressed : ui.IsHot(id) ? theme.FillHover : default;
        if (fill.A > 0) ui.FillRounded(row, (float)S(Metrics.RadiusControl), fill);

        // The icon carries the accent: it is the only colour in an otherwise neutral row.
        ui.Icon(glyph, new Rect(row.X + S(8), row.Y, S(20), row.Height), theme.Accent, S(15));

        ui.Text(label, new Rect(row.X + S(38), row.Y, row.Width - S(48), row.Height),
            theme.TextPrimary, (float)S(Metrics.FontBody));

        if (shortcut.Length != 0)
            ui.Text(shortcut, new Rect(row.X, row.Y, row.Width - S(10), row.Height),
                theme.TextTertiary, (float)S(Metrics.FontCaption), align: TextAlign.Right);

        return y + S(39);
    }

    private void DrawHistory(Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds)
    {
        var theme = ui.Theme;

        if (_history.Count == 0)
        {
            ui.Text("Nothing captured yet", bounds, theme.TextTertiary,
                (float)S(Metrics.FontCaption), align: TextAlign.Center);
            return;
        }

        var rowHeight = S(48);
        var gap = S(2);
        var y = bounds.Y - _scroll;

        // Clipped, not just culled: a row straddling an edge draws in full, and would paint over the
        // RECENT rule above and the footer's border below. The clip takes the pointer with it.
        ui.PushClip(bounds);

        for (var i = 0; i < _history.Count; i++)
        {
            var item = _history[i];
            var row = new Rect(bounds.X + S(10), y, bounds.Width - S(20), rowHeight);
            y += rowHeight + gap;

            if (row.Bottom < bounds.Y || row.Y > bounds.Bottom) continue;

            var id = Ui.Id(HistoryRow, i);
            var selected = ReferenceEquals(item, _selected);
            if (ui.Interact(id, row))
            {
                _selected = item;
                _settingsOpen = false;
            }

            // Selection is an elevated neutral pill, not a tint: it sits behind the thumbnail, so a
            // coloured fill would cast onto the capture.
            if (selected)
            {
                ui.FillRounded(row, (float)S(Metrics.RadiusControl), theme.RowSelectFill);
                ui.StrokeRounded(row, (float)S(Metrics.RadiusControl), theme.RowSelectStroke);
            }
            else if (ui.IsActive(id))
                ui.FillRounded(row, (float)S(Metrics.RadiusControl), theme.RowPressedFill);
            else if (ui.IsHot(id))
                ui.FillRounded(row, (float)S(Metrics.RadiusControl), theme.RowHoverFill);

            // Thumbnail: 52x34, filling its slot, on an overlay backing so a transparent PNG reads.
            var slot = new Rect(row.X + S(8), row.Y + S(7), S(52), S(34));
            ui.FillRounded(slot, (float)S(4), theme.SurfaceOverlay);
            DrawThumbnail(target, item, slot);

            var textX = slot.Right + S(10);
            var textWidth = row.Right - textX - S(8);

            ui.Text(Truncate(item.FileName, 22),
                new Rect(textX, row.Y + S(7), textWidth, S(18)),
                theme.TextPrimary, (float)S(Metrics.FontBody), middle: false);

            ui.Text($"{item.Width}×{item.Height}  ·  {Ago(item.CapturedAt)}",
                new Rect(textX, row.Y + S(26), textWidth, S(16)),
                theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false);
        }

        _historyViewport = Math.Max(1, bounds.Height);
        _historyHeight = _history.Count * (rowHeight + gap);

        ui.Scrollbar(bounds, _historyHeight, _scroll);
        ui.PopClip();
    }

    /// <summary>The list's content and visible heights, measured as it is drawn, so the wheel
    /// handler scrolls against the list that exists rather than an estimate of it.</summary>
    private double _historyHeight;

    private double _historyViewport = 1;

    private void DrawSidebarFooter(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        ui.FillRect(new Rect(bounds.X, bounds.Y, bounds.Width, 1), theme.StrokeSubtle);

        var size = S(32);
        var y = bounds.Y + (bounds.Height - size) / 2;

        // The toggle flips light and dark. "System" is a deliberate choice, made in Settings - a
        // button that cycles through three states leaves you guessing which one you are in.
        if (ui.Tile(Ui.Id("main.newcapture"), new Rect(bounds.X + S(12), y, size, size), false,
            Icons.Theme, S(15), "Switch theme"))
        {
            _settings.Theme = SystemTheme.Resolve(_settings.Theme).IsDark
                ? AppTheme.Light
                : AppTheme.Dark;
            SaveSettings();
        }

        if (ui.Tile(Ui.Id("main.settings"), new Rect(bounds.Right - S(12) - size, y, size, size), _settingsOpen,
            Icons.Settings, S(15), "Settings", neutral: true))
        {
            _settingsOpen = !_settingsOpen;
        }
    }

    // ============================  DETAIL PANE  ============================

    private void DrawDetail(Ui ui, IComObject<ID2D1RenderTarget> target, Rect bounds)
    {
        var theme = ui.Theme;

        if (_selected is not { } item)
        {
            DrawEmptyState(ui, bounds);
            return;
        }

        var bar = S(64);

        // The preview well: sunken and rounded, so the capture reads as inset from the chrome.
        // The top margin clears the caption buttons floating over this pane's top-right.
        var well = new Rect(
            bounds.X + S(24),
            bounds.Y + S(48),
            bounds.Width - S(48),
            bounds.Height - S(48) - bar - S(12));

        ui.FillRounded(well, (float)S(Metrics.RadiusContainer), theme.SurfaceSunken);
        ui.StrokeRounded(well, (float)S(Metrics.RadiusContainer), theme.StrokeSubtle);

        var bitmap = GetPreviewBitmap(target, item, well);
        if (bitmap is null)
        {
            ui.Text(_previewLoading ? "Loading capture…" : "Could not open this capture", well, theme.TextTertiary,
                (float)S(Metrics.FontBody), align: TextAlign.Center);
        }
        else
        {
            // Inset from the well, then fill it: the image floats inside the frame rather than
            // touching it, but a small capture still uses the space it was given.
            var fit = well.Deflate(S(20)).Fit(new Size(bitmap.Width, bitmap.Height), enlarge: true);
            target.DrawBitmap(
                bitmap.Bitmap, 1f,
                D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
                AnnotationRenderer.ToRect(fit));
        }

        DrawDetailBar(ui, item, new Rect(bounds.X, well.Bottom, bounds.Width, bar));
    }

    private void DrawDetailBar(Ui ui, ScreenshotHistoryItem item, Rect bounds)
    {
        var theme = ui.Theme;

        ui.Text(Truncate(item.FileName, 42),
            new Rect(bounds.X + S(24), bounds.Y + S(12), bounds.Width * 0.5, S(20)),
            theme.TextPrimary, (float)S(Metrics.FontSubtitle), bold: true, middle: false);

        ui.Text($"{item.Width} × {item.Height}   ·   {item.CapturedAt.LocalDateTime:d MMM yyyy, HH:mm}",
            new Rect(bounds.X + S(24), bounds.Y + S(34), bounds.Width * 0.5, S(16)),
            theme.TextTertiary, (float)S(Metrics.FontCaption), middle: false);

        // Actions, right-aligned. Buttons hug their content rather than being fixed-width blocks.
        var y = bounds.Y + (bounds.Height - S(32)) / 2;
        var right = bounds.Right - S(24);

        var font = S(Metrics.FontBody);
        var glyph = S(14);

        // Edit carries the accent: it is what this pane is for.
        var edit = ui.ButtonWidth("Edit", font, glyph);
        right -= edit;
        if (ui.Button(Ui.Id("main.edit"), new Rect(right, y, edit, S(32)), "Edit",
            primary: true, glyph: Icons.Edit, glyphSize: glyph, fontSize: font))
            Post(() => EditRequested?.Invoke(item));

        var copy = ui.ButtonWidth("Copy", font, glyph);
        right -= copy + S(8);
        if (ui.Button(Ui.Id("main.copy"), new Rect(right, y, copy, S(32)), "Copy",
            glyph: Icons.Copy, glyphSize: glyph, fontSize: font,
            confirmation: _copied.Progress(Environment.TickCount64)))
            CopyToClipboard(item);

        // The icon-only actions sit in a taller box with a larger glyph: at 14px they read as
        // afterthoughts next to the labelled buttons they share a row with.
        var icon = S(36);
        var iconGlyph = S(17);
        var iconY = bounds.Y + (bounds.Height - icon) / 2;

        right -= icon + S(6);
        if (ui.Tile(Ui.Id("main.remove"), new Rect(right, iconY, icon, icon), false, Icons.Delete, iconGlyph, "Remove",
            destructive: true))
            Post(() => Delete(item));

        right -= icon + S(4);
        if (ui.Tile(Ui.Id("main.share"), new Rect(right, iconY, icon, icon), false, Icons.Share, iconGlyph, "Share"))
            Post(() => Share(item));

        right -= icon + S(4);
        if (ui.Tile(Ui.Id("main.reveal"), new Rect(right, iconY, icon, icon), false, Icons.Reveal, iconGlyph,
            "Show in Explorer"))
            Reveal(item.FilePath);

        // Close sits apart from the pair that act on the file: it only dismisses the view.
        right -= icon + S(14);
        if (ui.Tile(Ui.Id("main.dismiss"), new Rect(right, iconY, icon, icon), false, Icons.Close, iconGlyph,
            "Close  (Esc)"))
            Deselect();
    }

    /// <summary>The pane with nothing shown. What it says depends on whether the history is empty:
    /// "no captures yet" in front of a list of them would be wrong.</summary>
    private void DrawEmptyState(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        var centre = bounds.Center;
        var empty = _history.Count == 0;

        ui.Icon(Icons.EmptyState,
            new Rect(bounds.X, centre.Y - S(66), bounds.Width, S(48)),
            theme.TextTertiary, S(38));

        ui.Text(empty ? "No captures yet" : "Nothing selected",
            new Rect(bounds.X, centre.Y - S(6), bounds.Width, S(24)),
            theme.TextSecondary, (float)S(Metrics.FontSubtitle), align: TextAlign.Center);

        // The region hotkey is rebindable and can be cleared, so the hint reads the live binding
        // rather than naming a default the user may no longer have.
        var region = _settings.CaptureRegionHotkey;
        var gesture = region.Key == 0 ? null : Describe(region);
        ui.Text(
            empty
                ? gesture is null
                    ? "Capture a region to get started"
                    : $"Press {gesture} to capture a region"
                : gesture is null
                    ? "Pick a capture from the list"
                    : $"Pick a capture from the list, or press {gesture} for a new one",
            new Rect(bounds.X, centre.Y + S(20), bounds.Width, S(20)),
            theme.TextTertiary, (float)S(Metrics.FontBody), align: TextAlign.Center);
    }
}
