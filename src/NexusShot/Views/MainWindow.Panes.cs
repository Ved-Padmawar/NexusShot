using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>The library as drawn each frame: the header with the capture actions, the tools row, and
/// the grid of captures grouped by day - or the empty state before there are any.</summary>
public sealed partial class MainWindow
{
    /// <summary>The grid's window in the client area: everything below the tools row.</summary>
    private Rect GridBounds(double width, double height) => new(0, S(56) + S(44), width, height - S(56) - S(44));

    /// <summary>The chrome layer's library part: the header, the tools row, and what floats over the
    /// grid - its scrollbar, or the message standing in for it.</summary>
    private void DrawLibraryChrome(Ui ui, double width, double height)
    {
        DrawHeader(ui, width);
        DrawToolsRow(ui, width);

        var grid = GridBounds(width, height);
        if (_history.Count == 0) DrawEmptyState(ui, grid);
        else if (_gridEmpty)
            ui.Text("No captures match", grid, ui.Theme.TextTertiary, S(Metrics.FontMd), align: TextAlign.Center);
        else ui.Scrollbar(grid, _gridHeight, _scroll.Position);
    }

    // ============================  HEADER  ============================

    /// <summary>The brand, and the capture actions in one segmented group. The header is the drag
    /// band; the buttons are cut out of it.</summary>
    private void DrawHeader(Ui ui, double width)
    {
        var theme = ui.Theme;
        var band = new Rect(0, 0, width, S(56));

        var tile = new Rect(S(16), band.Center.Y - S(12), Math.Round(S(24)), Math.Round(S(24)));
        _brand.Draw(ui, tile);

        var brandFont = S(14);
        var brand = ui.MeasureText("NexusShot", brandFont, Weight.Bold, Face.Display);
        ui.Text("NexusShot", new Rect(tile.Right + S(10), band.Y, brand + 1, band.Height), theme.TextPrimary,
            brandFont, Weight.Bold, face: Face.Display);
        ui.Text("Library", new Rect(tile.Right + S(10) + brand + S(5), band.Y, S(60), band.Height),
            theme.TextTertiary, brandFont, Weight.Medium, face: Face.Display);

        (string Label, Icon Icon, Action Run)[] actions =
        [
            ("Region", Icons.CaptureRegion, () => CaptureRequested?.Invoke(CaptureMode.Region)),
            ("Window", Icons.CaptureWindow, () => CaptureRequested?.Invoke(CaptureMode.ActiveWindow)),
            ("Screen", Icons.CaptureScreen, () => CaptureRequested?.Invoke(CaptureMode.FullScreen)),
            ("Text", Icons.Ocr, () => CaptureTextRequested?.Invoke()),
            ($"{_settings.TimedCaptureSeconds}s", Icons.Timer, () => TimedCaptureRequested?.Invoke()),
        ];
        HotkeyId?[] shortcuts = [HotkeyId.CaptureRegion, HotkeyId.CaptureActiveWindow, HotkeyId.CaptureFullScreen,
            HotkeyId.CaptureText, HotkeyId.TimedCapture];

        var widths = actions.Select(action => ui.ButtonWidth(action.Label, action.Icon, small: true)).ToArray();
        var groupWidth = widths.Sum() + S(2) * (actions.Length - 1) + S(6);
        var free = new Rect(tile.Right + S(10) + brand + S(70), 0, width - CaptionButtonsWidth - S(12), band.Height);
        var group = new Rect(Math.Max(free.X, (width - groupWidth) / 2), band.Center.Y - S(18), groupWidth, S(36));
        _headerControls.Add(group);

        ui.FillRounded(group, (float)S(Metrics.RadiusMd), theme.SurfacePane);
        ui.StrokeRounded(group, (float)S(Metrics.RadiusMd), theme.StrokeSubtle);

        var x = group.X + S(3);
        for (var i = 0; i < actions.Length; i++)
        {
            var bounds = new Rect(x, group.Y + S(3), widths[i], S(30));
            var action = actions[i];
            var tip = shortcuts[i] is { } id && _settings.Hotkey(id) is { Key: not 0 } binding
                ? Describe(binding).Replace(" + ", " ") : null;

            // Posted: this runs inside Render, and a capture runs the picker's own message loop.
            if (ui.Button(Ui.Id(Ui.Id("library.capture"), i), bounds, action.Label,
                i == 0 ? ButtonStyle.Primary : ButtonStyle.Ghost, action.Icon, small: true,
                tooltip: tip is null ? null : $"{Title(i)}  ·  {tip}"))
                Post(action.Run);
            x += widths[i] + S(2);
        }
    }

    private static string Title(int action) => action switch
    {
        0 => "Capture region",
        1 => "Capture active window",
        2 => "Capture full screen",
        3 => "Capture text",
        _ => "Timed capture",
    };

    /// <summary>The count, the update button when there is one, the search box, and the folder and
    /// settings buttons.</summary>
    private void DrawToolsRow(Ui ui, double width)
    {
        var theme = ui.Theme;
        var row = new Rect(0, S(56), width, S(44));
        ui.FillRect(new Rect(0, row.Bottom - 1, width, 1), theme.StrokeSubtle);

        DrawSelectionBar(ui, row);

        var right = width - S(16);
        if (ui.IconButton(Ui.Id("library.settings"), new Rect(right - S(32), row.Center.Y - S(16), S(32), S(32)),
            Icons.Settings, "Settings", "Ctrl ,"))
            OpenSettings();
        right -= S(32) + S(8);

        if (ui.IconButton(Ui.Id("library.folder"), new Rect(right - S(32), row.Center.Y - S(16), S(32), S(32)),
            Icons.Folder, "Open captures folder"))
            Post(() => OpenFolder(_settings.ScreenshotFolder));
        right -= S(32) + S(8);

        var search = new Rect(right - S(220), row.Center.Y - S(15), S(220), S(30));
        if (ShowsUpdateButton)
            DrawUpdateButton(ui, new Rect(search.X - S(8) - UpdateButtonWidth, search.Y, UpdateButtonWidth, S(30)));

        var result = ui.Field(Ui.Id("library.search"), search, _query, character => !char.IsControl(character), 80,
            leading: Icons.Search, placeholder: "Search", face: Face.Text);
        if (result.Changed && result.Text != _query)
        {
            _query = result.Text;
            _scroll.Reset();
        }
    }

    /// <summary>The row's left end: the count and a Select button, or, while picking, how many are
    /// picked with Select all, Delete and Cancel beside it.</summary>
    private void DrawSelectionBar(Ui ui, Rect row)
    {
        var theme = ui.Theme;
        var x = S(16);
        var font = S(Metrics.FontSm);

        Rect Place(double width)
        {
            var bounds = new Rect(x, row.Center.Y - S(15), width, S(30));
            x += width + S(8);
            return bounds;
        }

        if (!_selection.Active)
        {
            var count = _history.Count == 1 ? "1 capture" : $"{_history.Count} captures";
            var countWidth = ui.MeasureText(count, font);
            ui.Text(count, new Rect(x, row.Y, countWidth + 1, row.Height), theme.TextTertiary, font);
            x += countWidth + S(14);

            if (_history.Count > 0
                && ui.Button(Ui.Id("library.select"), Place(ui.ButtonWidth("Select", Icons.Tick, small: true)), "Select",
                    ButtonStyle.Outline, Icons.Tick, small: true, tooltip: "Pick captures to delete together  ·  Ctrl click"))
                _selection.Begin();
            return;
        }

        var label = _selection.Count == 1 ? "1 selected" : $"{_selection.Count} selected";
        var labelWidth = ui.MeasureText(label, font, Weight.Semibold);
        ui.Text(label, new Rect(x, row.Y, labelWidth + 1, row.Height), theme.TextPrimary, font, Weight.Semibold);
        x += labelWidth + S(14);

        // Everything the grid shows, so a search narrows what Select all takes.
        var shown = LibraryGroups.Build(_history, _query, DateTime.Now).SelectMany(group => group.Items)
            .Select(item => item.FilePath).ToList();
        if (ui.Button(Ui.Id("library.select.all"), Place(ui.ButtonWidth("Select all", Icons.SelectAll, small: true)),
            "Select all", ButtonStyle.Outline, Icons.SelectAll, small: true, enabled: !shown.All(_selection.Contains)))
            _selection.SelectAll(shown);

        if (ui.Button(Ui.Id("library.select.delete"), Place(ui.ButtonWidth("Delete", Icons.Delete, small: true)), "Delete",
            ButtonStyle.Destructive, Icons.Delete, small: true, enabled: _selection.Count > 0))
            AskDelete(_history.Where(item => _selection.Contains(item.FilePath)).ToList());

        if (ui.Button(Ui.Id("library.select.cancel"), Place(ui.ButtonWidth("Cancel", keycap: "Esc", small: true)), "Cancel",
            ButtonStyle.Outline, keycap: "Esc", small: true))
            _selection.End();
    }

    // ============================  GRID  ============================

    private static readonly int TileOwner = Ui.Id("library.tile");

    /// <summary>A tile's look this frame: everything that decides its pixels, so a band redraws only
    /// when one of these changes.</summary>
    private readonly record struct TileLook(bool Hot, double Lift, double Shown, bool Picked, bool Copied,
        ImageSurface? Bitmap, string Ago);

    private sealed record GridTile(ScreenshotHistoryItem Item, int Index, Rect Bounds)
    {
        public bool Clicked;
        public TileLook Look;
    }

    /// <summary>A row of tiles and the space around it - the unit the grid layer is drawn in.</summary>
    private sealed record GridBand(double Top, double Bottom, string? Title, double TitleY, List<GridTile> Tiles);

    /// <summary>Each drawn band's look, by its top.</summary>
    private readonly Dictionary<int, int> _bands = [];

    /// <summary>What every band depends on; a change redraws them all.</summary>
    private int _gridSignature;

    private bool _gridEmpty;

    /// <summary>
    /// The grid, into the scrolling layer. A band is drawn when it comes within a screen of the view and
    /// again only when its look changes; one showing tile actions is drawn every frame, since those
    /// buttons answer the pointer as they draw. Visible tiles claim decode slots before those ahead.
    /// </summary>
    private void DrawGrid(Ui ui, IComObject<ID2D1RenderTarget> resources, CompositionLayers layers, Rect bounds)
    {
        var theme = ui.Theme;
        var inset = S(16);
        var layout = new LibraryLayout(bounds.Width - inset * 2, _scale);
        var groups = LibraryGroups.Build(_history, _query, DateTime.Now);
        _gridEmpty = groups.Count == 0;

        // Measured before placement, so the clamp and the scrollbar agree with this frame's rows.
        _gridViewport = Math.Max(1, bounds.Height);
        _gridHeight = S(4) + groups.Sum(group => layout.GroupHeight(group.Items.Count) + S(8)) + S(24);
        _scroll.SetRange(_gridHeight - _gridViewport);
        _thumbnails.Capacity = Math.Max(_thumbnails.Capacity, layout.CacheCapacity(_gridViewport));

        var scroll = Math.Round(_scroll.Position);
        var contentHeight = Math.Max(_gridHeight, bounds.Height);
        layers.PlaceScroll(bounds, (int)bounds.Width, (int)Math.Ceiling(contentHeight), scroll);
        var origin = new Point(bounds.X, bounds.Y - scroll);

        var signature = HashCode.Combine(bounds.Width, _scale, theme, _query);
        if (signature != _gridSignature)
        {
            _bands.Clear();
            _gridSignature = signature;
        }

        var bands = Bands(groups, layout, inset, contentHeight, scroll - bounds.Height, scroll + bounds.Height * 2);
        var now = DateTime.Now;

        Rect OnScreen(Rect content) => new(content.X + origin.X, content.Y + origin.Y, content.Width, content.Height);
        var tiles = bands.SelectMany(band => band.Tiles).ToList();
        var visible = tiles.Where(tile => Overlaps(OnScreen(tile.Bounds), bounds)).ToHashSet();
        foreach (var tile in tiles.OrderBy(tile => visible.Contains(tile) ? 0 : Math.Abs(OnScreen(tile.Bounds).Center.Y - bounds.Center.Y)))
            tile.Look = Look(ui, resources, tile, OnScreen(tile.Bounds), bounds, visible.Contains(tile), layout.DecodeWidth, now);

        foreach (var band in bands)
        {
            var key = BandKey(band);
            var top = (int)Math.Floor(band.Top);
            if (_bands.TryGetValue(top, out var drawn) && drawn == key && !band.Tiles.Any(tile => tile.Look.Shown > 0.01))
                continue;

            var area = new RECT { left = 0, top = top, right = (int)bounds.Width, bottom = (int)Math.Ceiling(band.Bottom) };
            var onScreen = new Rect(bounds.X, area.top + origin.Y, bounds.Width, area.bottom - area.top);
            using (var context = layers.BeginScroll(area, origin))
            using (var target = context.AsRenderTarget2())
            {
                ui.Retarget(target);
                // The band alone, not the viewport: a band drawn ahead must paint every one of its pixels.
                ui.PushClip(onScreen);
                ui.FillRect(onScreen, theme.SurfaceWindow);
                if (band.Title is { } title)
                    ui.Text(title.ToUpperInvariant(), new Rect(origin.X + inset + S(2), origin.Y + band.TitleY,
                        bounds.Width - inset * 2, S(16)), theme.TextTertiary, S(11), Weight.Semibold);
                foreach (var tile in band.Tiles)
                    if (PaintTile(ui, tile.Item, OnScreen(tile.Bounds), layout.ImageHeight, Ui.Id(TileOwner, tile.Index), tile.Look))
                        tile.Clicked = false;
                ui.PopClip();
                layers.EndScroll();
            }
            _bands[top] = key;
        }
        ui.Retarget(resources);

        if (bands.Count > 0)
            layers.TrimScroll(new RECT { left = 0, top = (int)Math.Floor(bands[0].Top), right = (int)bounds.Width,
                bottom = (int)Math.Ceiling(bands[^1].Bottom) });
        var kept = bands.Select(band => (int)Math.Floor(band.Top)).ToHashSet();
        foreach (var stale in _bands.Keys.Where(top => !kept.Contains(top)).ToList()) _bands.Remove(stale);

        foreach (var tile in tiles)
            if (tile.Clicked) OnTileClicked(tile.Item);
    }

    /// <summary>Each band ends where the next begins, so they cover the content without a seam.</summary>
    private List<GridBand> Bands(List<LibraryGroups.Group> groups, LibraryLayout layout, double inset,
        double contentHeight, double from, double to)
    {
        var bands = new List<GridBand>();
        var top = 0.0;
        var groupTop = S(4);
        var index = 0;
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var groupHeight = layout.GroupHeight(group.Items.Count);
            var rows = (group.Items.Count + layout.Columns - 1) / layout.Columns;
            for (var row = 0; row < rows; row++)
            {
                var rowTop = groupTop + layout.HeaderHeight + row * (layout.TileHeight + layout.RowGap);
                var bottom = row < rows - 1 ? rowTop + layout.TileHeight + layout.RowGap / 2
                    : g < groups.Count - 1 ? groupTop + groupHeight + S(8)
                    : contentHeight;

                if (bottom >= from && top <= to)
                {
                    var tiles = new List<GridTile>();
                    for (var i = row * layout.Columns; i < Math.Min(group.Items.Count, (row + 1) * layout.Columns); i++)
                        tiles.Add(new GridTile(group.Items[i], index + i, layout.Tile(inset, groupTop, i)));
                    bands.Add(new GridBand(top, bottom, row == 0 ? group.Title : null, groupTop + S(18), tiles));
                }
                top = bottom;
            }
            index += group.Items.Count;
            groupTop += groupHeight + S(8);
        }
        return bands;
    }

    private static bool Overlaps(Rect a, Rect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>The per-frame part of a tile, done whether or not its band is drawn. It answers the
    /// pointer only where it shows: the tools row covers the part scrolled under it.</summary>
    private TileLook Look(Ui ui, IComObject<ID2D1RenderTarget> resources, GridTile tile, Rect onScreen, Rect bounds,
        bool visible, int decodeWidth, DateTime now)
    {
        var id = Ui.Id(TileOwner, tile.Index);
        var hot = false;
        if (visible)
        {
            var hit = Rect.FromEdges(onScreen.Left, Math.Max(onScreen.Top, bounds.Top), onScreen.Right,
                Math.Min(onScreen.Bottom, bounds.Bottom));
            tile.Clicked = ui.Interact(id, hit);
            hot = ui.IsHot(id) || ui.IsActive(id);
        }

        return new TileLook(
            hot,
            ui.Animate(id, hot ? 1 : 0, Metrics.MotionFast),
            // While picking, a click picks: the per-tile actions would compete with it.
            ui.Animate(Ui.Id(id, 1), hot && !_selection.Active ? 1 : 0, Metrics.MotionFast),
            _selection.Contains(tile.Item.FilePath),
            string.Equals(_copiedPath, tile.Item.FilePath, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < _toastUntil,
            GetThumbnail(resources, tile.Item, decodeWidth),
            LibraryGroups.Ago(tile.Item.CapturedAt.LocalDateTime, now));
    }

    /// <summary>A hash of everything a band's pixels depend on. Animation values are quantised, so a
    /// settled tile stops changing it.</summary>
    private int BandKey(GridBand band)
    {
        var hash = new HashCode();
        hash.Add(band.Top);
        hash.Add(band.Bottom);
        hash.Add(band.Title);
        hash.Add(_selection.Active);
        foreach (var tile in band.Tiles)
        {
            var look = tile.Look;
            hash.Add(tile.Item.FilePath);
            hash.Add(tile.Bounds);
            hash.Add(look.Hot);
            hash.Add(Math.Round(look.Lift * 40));
            hash.Add(Math.Round(look.Shown * 40));
            hash.Add(look.Picked);
            hash.Add(look.Copied);
            hash.Add(look.Bitmap is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(look.Bitmap));
            hash.Add(look.Ago);
        }
        return hash.ToHashCode();
    }

    /// <summary>A capture: the image cropped to 16:10 from the top, its name and age beneath, and its
    /// actions over the image's corner while hovered. True when one of those actions took the click.</summary>
    private bool PaintTile(Ui ui, ScreenshotHistoryItem item, Rect tile, double imageHeight, int id, TileLook look)
    {
        var theme = ui.Theme;
        var image = new Rect(tile.X, tile.Y, tile.Width, imageHeight);
        var radius = (float)S(Metrics.RadiusMd);

        if (look.Lift > 0.01)
            ui.Shadow(image, radius, S(12) * look.Lift, S(5) * look.Lift, theme.Shadow.WithAlpha((byte)(theme.Shadow.A * look.Lift)));

        ui.FillRounded(image, radius, theme.SurfacePane);
        if (look.Bitmap is { } bitmap)
        {
            // Aspect-fill from the top, where a capture's title bar and headings are.
            var scale = Math.Max(image.Width / bitmap.Width, image.Height / bitmap.Height);
            var drawn = new Rect(image.X + (image.Width - bitmap.Width * scale) / 2, image.Y,
                bitmap.Width * scale, bitmap.Height * scale);
            ui.DrawBitmapRounded(bitmap.Bitmap, image, radius, drawn);
        }

        // The ring stands clear of the image: flush, the image's antialiased corner showed past it.
        if (look.Picked) ui.StrokeRounded(image.Deflate(-S(4)), radius + (float)S(4), theme.Accent, (float)S(2));
        else ui.StrokeRounded(image, radius, look.Hot ? theme.StrokeStrong : theme.StrokeDefault);

        if (_selection.Active) DrawPickBadge(ui, image, look.Picked);

        var overlayClicked = false;
        if (look.Shown > 0.01)
        {
            (Icon Icon, string Tip, Action Run, bool Danger)[] actions =
            [
                (Icons.Delete, "Delete", () => AskDelete([item]), true),
                (Icons.Folder, "Show in folder", () => Reveal(item.FilePath), false),
                (look.Copied ? Icons.Tick : Icons.Copy, look.Copied ? "Copied" : "Copy", () => Post(() => CopyToClipboard(item)), false),
                (Icons.Share, "Share", () => Post(() => Share(item)), false),
                (Icons.Edit, "Open in editor", () => Post(() => EditRequested?.Invoke(item)), false),
            ];
            var size = S(28);
            var x = image.Right - S(8) - actions.Length * size - (actions.Length - 1) * S(4);
            var y = image.Bottom - S(8) - size + S(4) * (1 - look.Shown);
            for (var i = 0; i < actions.Length; i++)
            {
                var button = new Rect(x + i * (size + S(4)), y, size, size);
                if (ui.OverlayButton(Ui.Id(id, 10 + i), button, actions[i].Icon, S(15), actions[i].Tip,
                    on: look.Copied && actions[i].Icon == Icons.Tick, destructive: actions[i].Danger))
                {
                    actions[i].Run();
                    overlayClicked = true;
                }
            }
        }

        var caption = new Rect(tile.X + S(2), image.Bottom + S(8), tile.Width - S(4), S(18));
        var agoWidth = ui.MeasureText(look.Ago, S(Metrics.FontSm));
        ui.Text(look.Ago, new Rect(caption.Right - agoWidth, caption.Y, agoWidth + 1, caption.Height), theme.TextTertiary,
            S(Metrics.FontSm));
        ui.TextFit(Path.GetFileNameWithoutExtension(item.FileName),
            new Rect(caption.X, caption.Y, caption.Width - agoWidth - S(8), caption.Height), theme.TextPrimary,
            S(Metrics.FontSm), Weight.Semibold);
        return overlayClicked;
    }

    /// <summary>A click on a tile, not on one of its actions: picks it while picking, and opens the
    /// editor on the second click inside the double-click time.</summary>
    private void OnTileClicked(ScreenshotHistoryItem item)
    {
        if (_selection.Active || _pressedWithControl)
        {
            _selection.Toggle(item.FilePath);
            return;
        }

        var tick = Environment.TickCount64;
        if (ReferenceEquals(_lastClicked, item) && tick - _lastClickTick <= WindowInterop.GetDoubleClickTime())
        {
            _lastClicked = null;
            Post(() => EditRequested?.Invoke(item));
            return;
        }
        _lastClicked = item;
        _lastClickTick = tick;
    }

    private ScreenshotHistoryItem? _lastClicked;
    private long _lastClickTick;

    /// <summary>The check in a tile's corner while picking: an empty ring, or the accent with a tick.</summary>
    private void DrawPickBadge(Ui ui, Rect image, bool picked)
    {
        var badge = new Rect(image.X + S(10), image.Y + S(10), S(22), S(22));
        var radius = (float)(badge.Width / 2);
        if (picked)
        {
            ui.FillRounded(badge, radius, ui.Theme.Accent);
            ui.Icon(Icons.Tick, badge, ui.Theme.TextOnAccent, S(15));
        }
        else
        {
            ui.FillRounded(badge, radius, Ui.OverlayRest);
            ui.StrokeRounded(badge, radius, Rgba.White, (float)S(1.5));
        }
    }

    /// <summary>Before the first capture: what the library is for, and the four ways to fill it. The
    /// hotkeys shown are the live bindings, which can be changed or cleared.</summary>
    private void DrawEmptyState(Ui ui, Rect bounds)
    {
        var theme = ui.Theme;
        var width = S(460);
        var top = bounds.Center.Y - S(170);
        var x = bounds.Center.X - width / 2;

        var glyph = new Rect(bounds.Center.X - S(42), top, S(84), S(84));
        ui.FloatShadow(glyph, (float)S(22));
        ui.FillRounded(glyph, (float)S(22), theme.SurfaceRaised);
        ui.StrokeRounded(glyph, (float)S(22), theme.StrokeDefault);
        ui.Icon(Icons.CaptureRegion, glyph, theme.AccentText, S(30));

        top = glyph.Bottom + S(22);
        ui.Text("Nothing captured yet", new Rect(x, top, width, S(36)), theme.TextPrimary, S(Metrics.Font2Xl),
            Weight.Semibold, TextAlign.Center, face: Face.Display);
        top += S(44);
        ui.Text("Grab part of the screen and it lands here, ready to mark up, copy or share. Drop an image anywhere on this window to open it.",
            new Rect(x, top, width, S(44)), theme.TextSecondary, S(Metrics.FontMd), align: TextAlign.Center, middle: false,
            wrap: true);
        top += S(64);

        (string Label, Icon Icon, HotkeyId Hotkey, Action Run)[] actions =
        [
            ("Region", Icons.CaptureRegion, HotkeyId.CaptureRegion, () => CaptureRequested?.Invoke(CaptureMode.Region)),
            ("Window", Icons.CaptureWindow, HotkeyId.CaptureActiveWindow, () => CaptureRequested?.Invoke(CaptureMode.ActiveWindow)),
            ("Full screen", Icons.CaptureScreen, HotkeyId.CaptureFullScreen, () => CaptureRequested?.Invoke(CaptureMode.FullScreen)),
            ("Text", Icons.Ocr, HotkeyId.CaptureText, () => CaptureTextRequested?.Invoke()),
        ];

        var cell = (width - S(8)) / 2;
        for (var i = 0; i < actions.Length; i++)
        {
            var bounds_ = new Rect(x + (i % 2) * (cell + S(8)), top + (i / 2) * S(56), cell, S(48));
            var id = Ui.Id(Ui.Id("library.empty"), i);
            if (ui.Interact(id, bounds_)) Post(actions[i].Run);

            var radius = (float)S(Metrics.RadiusMd);
            ui.FillRounded(bounds_, radius, ui.IsHot(id) ? theme.SurfaceHover : theme.SurfaceRaised);
            ui.StrokeRounded(bounds_, radius, ui.IsHot(id) ? theme.StrokeStrong : theme.StrokeDefault);
            ui.Icon(actions[i].Icon, new Rect(bounds_.X + S(14), bounds_.Y, S(16), bounds_.Height), theme.TextPrimary, S(16));
            ui.Text(actions[i].Label, new Rect(bounds_.X + S(40), bounds_.Y, cell - S(120), bounds_.Height), theme.TextPrimary,
                S(Metrics.FontMd), Weight.Semibold);

            if (_settings.Hotkey(actions[i].Hotkey) is { Key: not 0 } binding)
            {
                var cap = Describe(binding).Replace(" + ", " ");
                ui.Keycap(cap, new Point(bounds_.Right - S(14) - ui.KeycapWidth(cap), bounds_.Center.Y));
            }
        }
    }
}
