using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The editor's chrome, all of it floating over the stage: the title and file actions in the top
/// band, the tool rail down the left, undo bottom-left, zoom bottom-right, and the style bar
/// bottom-centre. There is no title strip - the stage runs to the window's top edge.
///
/// Style changes (colour, size, fill, formatting, the crop frame) go straight to the document through
/// its own setters. Everything that needs the window - switching tools, undo past an open text box,
/// files, zoom, the eyedropper - is reported as intent and applied by the window.
/// </summary>
public sealed class EditorChrome(Ui ui) : IDisposable
{
    private readonly BrandMark _brand = new();

    public void Dispose() => _brand.Dispose();

    public double Scale { get; set; } = 1;
    private double S(double units) => units * Scale;

    /// <summary>The draggable band across the top, and the stage's insets for the image: clear of
    /// the title, the rail and the bottom pills.</summary>
    public double TopBand => S(52);
    public Rect Well(double width, double height) =>
        new(S(76), S(60), Math.Max(1, width - S(76) - S(40)), Math.Max(1, height - S(60) - S(76)));

    /// <summary>Tools, in rail order, with a null marking a group separator.</summary>
    private static readonly EditorTool?[] Groups =
    [
        EditorTool.Select,
        null,
        EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Arrow, EditorTool.Line,
        null,
        EditorTool.Pen, EditorTool.Brush, EditorTool.Eraser, EditorTool.Text, EditorTool.Counter,
        null,
        EditorTool.Highlight, EditorTool.Blur, EditorTool.Pixelate, EditorTool.Spotlight,
        null,
        EditorTool.Crop,
    ];

    public enum Command
    {
        None, Undo, Redo, Save, SaveAs, CopyAndClose, CopyText, Share,
        ZoomIn, ZoomOut, ZoomActual, ZoomFit, PickFromScreen,
    }

    /// <summary>What the user asked for this frame; applied by the window after the frame.</summary>
    public EditorTool? ToolPicked { get; private set; }
    public Command Requested { get; private set; }

    /// <summary>Text for the clipboard - the picker's copy button.</summary>
    public string? CopyRequested { get; private set; }

    private readonly ColorPicker _picker = new();

    /// <summary>The rectangles the chrome occupies this frame. The canvas ignores presses inside them,
    /// gaps between buttons included - a click that misses a tool must not draw under the rail.</summary>
    private readonly List<Rect> _covered = [];

    public bool Covers(Point client) =>
        _picker.Contains(client) || _covered.Exists(rect => rect.Contains(client));

    public bool PickerOpen => _picker.IsOpen;
    public void ClosePicker() => _picker.Close();
    public void AdoptPickedColor(Rgba color) => _picker.Adopt(color);
    public Func<bool> ShiftDown { set => _picker.IsShiftDown = value; }

    public sealed record Frame(
        EditorDocument Document, AppSettings Settings, double Width, double Height, double CaptionButtonsWidth,
        string Title, double Zoom, Rect ImageOnScreen, double ImageScale, Point ImageOrigin,
        string? Toast, bool Busy);

    public void Draw(Frame frame)
    {
        ToolPicked = null;
        Requested = Command.None;
        CopyRequested = null;
        _covered.Clear();
        ui.Scale = Scale;

        var document = frame.Document;
        DrawTitle(frame);
        DrawActions(frame);
        DrawRail(document, frame.Height);
        DrawHistory(document, frame.Height);
        DrawZoom(frame);

        if (document.PendingCrop is { } crop) DrawCropBar(frame, crop);
        else DrawStyleBar(frame);

        DrawToast(frame);
        DrawPicker(frame);
    }

    // ============================  TITLE & ACTIONS  ============================

    private void DrawTitle(Frame frame)
    {
        var theme = ui.Theme;
        var document = frame.Document;
        var band = new Rect(S(18), 0, frame.Width, S(44));

        var tile = new Rect(band.X, band.Center.Y - S(10), Math.Round(S(20)), Math.Round(S(20)));
        _brand.Draw(ui, tile);

        var x = tile.Right + S(10);
        var font = S(12.5);
        var maximum = frame.Width - frame.CaptionButtonsWidth - S(12) - ActionsWidth() - S(24) - x;

        var crop = document.PendingCrop ?? document.CropBounds;
        var size = crop is { } c
            ? $"{(int)c.Width} × {(int)c.Height}"
            : $"{(int)document.ImageWidth} × {(int)document.ImageHeight}";
        var count = document.Annotations.Count;
        var details = count == 0 ? size : $"{size} · {count} annotation{(count == 1 ? "" : "s")}";

        var detailsWidth = ui.MeasureText(details, font);
        var chipWidth = document.HasUnsavedChanges ? ui.MeasureText("Edited", S(11), Weight.Semibold) + S(14) : 0;
        var nameWidth = Math.Max(S(40), maximum - detailsWidth - chipWidth - S(20));

        var name = ui.Ellipsize(frame.Title, nameWidth, font, Weight.Semibold);
        var measured = ui.MeasureText(name, font, Weight.Semibold);
        ui.Text(name, new Rect(x, band.Y, measured + 1, band.Height), theme.TextPrimary, font, Weight.Semibold);
        x += measured + S(10);

        ui.Text(details, new Rect(x, band.Y, detailsWidth + 1, band.Height), theme.TextTertiary, font);
        x += detailsWidth + S(10);

        if (chipWidth <= 0) return;
        var chip = new Rect(x, band.Center.Y - S(9), chipWidth, S(18));
        ui.FillRounded(chip, (float)S(9), theme.AccentSoft);
        ui.Text("Edited", chip, theme.AccentText, S(11), Weight.Semibold, TextAlign.Center);
    }

    private double ActionsWidth() =>
        S(32) * 2 + S(6) * 4
        + ui.ButtonWidth("Save as…", Icons.Folder, small: true) + ui.ButtonWidth("Save", Icons.Save, small: true)
        + ui.ButtonWidth("Copy & close", Icons.Copy, small: true);

    private void DrawActions(Frame frame)
    {
        var right = frame.Width - frame.CaptionButtonsWidth - S(12);
        var y = S(8);
        var enabled = !frame.Busy;

        Rect Take(double width, double height)
        {
            right -= width;
            var rect = new Rect(right, y + (S(28) - height) / 2, width, height);
            right -= S(6);
            _covered.Add(rect);
            return rect;
        }

        var copyClose = Take(ui.ButtonWidth("Copy & close", Icons.Copy, small: true), S(28));
        if (ui.Button(Ui.Id("editor.copyclose"), copyClose, "Copy & close", ButtonStyle.Primary,
            Icons.Copy, small: true, enabled: enabled)) Requested = Command.CopyAndClose;

        var save = Take(ui.ButtonWidth("Save", Icons.Save, small: true), S(28));
        if (ui.Button(Ui.Id("editor.save"), save, "Save", ButtonStyle.Outline, Icons.Save, small: true, enabled: enabled,
            tooltip: "Save  (Ctrl S)")) Requested = Command.Save;

        var saveAs = Take(ui.ButtonWidth("Save as…", Icons.Folder, small: true), S(28));
        if (ui.Button(Ui.Id("editor.saveas"), saveAs, "Save as…", ButtonStyle.Outline, Icons.Folder, small: true, enabled: enabled))
            Requested = Command.SaveAs;

        if (ui.IconButton(Ui.Id("editor.share"), Take(S(32), S(32)), Icons.Share, "Share", enabled: enabled))
            Requested = Command.Share;
        if (ui.IconButton(Ui.Id("editor.copytext"), Take(S(32), S(32)), Icons.Ocr, "Copy text (OCR)", enabled: enabled))
            Requested = Command.CopyText;
    }

    // ============================  RAIL, UNDO, ZOOM  ============================

    private void DrawRail(EditorDocument document, double height)
    {
        var button = S(32);
        var gap = S(2);
        var separator = S(11);

        var tools = Groups.Count(entry => entry is not null);
        var separators = Groups.Length - tools;
        var content = tools * button + separators * separator + (Groups.Length - 1) * gap;
        var pill = new Rect(S(14), Math.Max(TopBand, (height - content - S(8)) / 2), button + S(8), content + S(8));
        ui.Pill(pill);
        _covered.Add(pill);

        var y = pill.Y + S(4);
        foreach (var entry in Groups)
        {
            if (entry is not { } tool)
            {
                ui.Separator(new Point(pill.Center.X, y + separator / 2), vertical: false);
                y += separator + gap;
                continue;
            }

            var bounds = new Rect(pill.X + S(4), y, button, button);
            if (ui.IconButton(Ui.Id(Ui.Id("editor.tool"), (int)tool), bounds, Glyph(tool), Name(tool), Shortcut(tool),
                on: document.ActiveTool == tool, side: TipSide.Right))
                ToolPicked = tool;
            y += button + gap;
        }
    }

    private void DrawHistory(EditorDocument document, double height)
    {
        var pill = new Rect(S(14), height - S(16) - S(36), S(4) + S(28) * 2 + S(2) + S(4), S(36));
        ui.Pill(pill);
        _covered.Add(pill);

        var undo = new Rect(pill.X + S(4), pill.Y + S(4), S(28), S(28));
        if (ui.IconButton(Ui.Id("editor.undo"), undo, Icons.Undo, "Undo", "Ctrl Z", enabled: document.CanUndo, iconSize: 15))
            Requested = Command.Undo;
        if (ui.IconButton(Ui.Id("editor.redo"), undo with { X = undo.Right + S(2) }, Icons.Redo, "Redo", "Ctrl Y",
            enabled: document.CanRedo, iconSize: 15))
            Requested = Command.Redo;
    }

    private void DrawZoom(Frame frame)
    {
        var width = S(4 + 28 + 2 + 48 + 2 + 28 + 2 + 11 + 2 + 28 + 4);
        var pill = new Rect(frame.Width - S(16) - width, frame.Height - S(16) - S(36), width, S(36));
        ui.Pill(pill);
        _covered.Add(pill);

        var x = pill.X + S(4);
        var y = pill.Y + S(4);
        if (ui.IconButton(Ui.Id("zoom.out"), new Rect(x, y, S(28), S(28)), Icons.Minus, "Zoom out", "Ctrl −", iconSize: 15))
            Requested = Command.ZoomOut;
        x += S(30);

        var percent = $"{Math.Round(frame.Zoom * 100)}%";
        var value = new Rect(x, y, S(48), S(28));
        var valueId = Ui.Id("zoom.actual");
        if (ui.Interact(valueId, value)) Requested = Command.ZoomActual;
        if (ui.IsHot(valueId)) ui.FillRounded(value, (float)S(Metrics.RadiusSm), ui.Theme.SurfaceHover);
        ui.Text(percent, value, ui.Theme.TextSecondary, S(Metrics.FontSm), Weight.Semibold, TextAlign.Center);
        ui.Tip(valueId, value, "Actual size", "Ctrl 0");
        x += S(50);

        if (ui.IconButton(Ui.Id("zoom.in"), new Rect(x, y, S(28), S(28)), Icons.Plus, "Zoom in", "Ctrl +", iconSize: 15))
            Requested = Command.ZoomIn;
        x += S(30);

        ui.Separator(new Point(x + S(5), pill.Center.Y));
        x += S(13);
        if (ui.IconButton(Ui.Id("zoom.fit"), new Rect(x, y, S(28), S(28)), Icons.Fit, "Fit", "Ctrl 9", iconSize: 15))
            Requested = Command.ZoomFit;
    }

    // ============================  STYLE BAR  ============================

    /// <summary>
    /// The style bar shows only the groups the current tool uses, in a fixed order at a fixed
    /// height. Its width follows the content, animated, so it never carries dead space and never
    /// jumps between tools. The tool in question is the selection's when there is one: the bar edits
    /// what is selected.
    /// </summary>
    private void DrawStyleBar(Frame frame)
    {
        var document = frame.Document;
        var selected = document.Selected;
        var tool = selected?.Tool ?? document.ActiveTool;

        var hasColor = tool is not (EditorTool.Blur or EditorTool.Pixelate or EditorTool.Eraser or EditorTool.Spotlight);
        var hasSize = tool is not (EditorTool.Highlight or EditorTool.Spotlight);
        var hasFill = tool is EditorTool.Rectangle or EditorTool.Ellipse;
        var isText = tool is EditorTool.Text;
        var isCounter = tool is EditorTool.Counter;

        // Measure first: the pill animates toward this width, and the groups lay out inside it.
        var groups = new List<(double Width, Action<Rect> Draw)>();
        if (hasColor) groups.Add((ColorGroupWidth(), rect => DrawColorGroup(rect, document)));
        if (hasSize) groups.Add((SizeLabelWidth(document) + S(10 + 112 + 10 + 44), rect => DrawSizeGroup(rect, document)));
        if (hasFill) groups.Add((S(30 * 3 + 4), rect => DrawFillGroup(rect, document)));
        if (isText) groups.Add((S(30 * 3 + 4), rect => DrawTextGroup(rect, document)));
        if (isCounter) groups.Add((CounterGroupWidth(document), rect => DrawCounterGroup(rect, document)));
        if (selected is not null) groups.Add((S(28), rect => DrawDeleteButton(rect, document)));
        if (groups.Count == 0)
        {
            var hint = tool == EditorTool.Spotlight ? "Drag to spotlight an area · Shift for square" : "Drag to highlight";
            var width = ui.MeasureText(hint, S(Metrics.FontSm));
            groups.Add((width, rect => ui.Text(hint, rect, ui.Theme.TextTertiary, S(Metrics.FontSm))));
        }

        var gap = S(10);
        var content = groups.Sum(group => group.Width) + (groups.Count - 1) * (gap * 2 + 1);
        var target = content + S(12) + S(10);
        var width_ = ui.Animate(Ui.Id("editor.stylebar"), target, Metrics.Motion);

        var pill = new Rect(Math.Round((frame.Width - width_) / 2), frame.Height - S(16) - S(44), width_, S(44));
        ui.Pill(pill);
        _covered.Add(pill);

        // Clipped while it resizes, so a group arriving or leaving never draws past the pill.
        ui.PushClip(pill);
        var x = pill.X + S(12) + (width_ - target) / 2;
        var row = new Rect(0, pill.Y + S(4), 0, S(36));
        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0)
            {
                ui.Separator(new Point(x + gap, pill.Center.Y));
                x += gap * 2 + 1;
            }
            groups[i].Draw(row with { X = x, Width = groups[i].Width });
            x += groups[i].Width;
        }
        ui.PopClip();
    }

    private double ColorGroupWidth() => Palette.Swatches.Length * S(20) + (Palette.Swatches.Length - 1) * S(7) + S(3) + ChipWidth();
    private double ChipWidth() => S(5 + 16 + 6 + 44 + 6 + 15 + 6) + S(24);

    private Rect _chip;

    private void DrawColorGroup(Rect row, EditorDocument document)
    {
        var current = Palette.Parse(document.Selected?.ColorHex ?? document.ColorHex);
        var x = row.X;
        for (var i = 0; i < Palette.Swatches.Length; i++)
        {
            var hex = Palette.Swatches[i];
            var dot = new Rect(x, row.Center.Y - S(10), S(20), S(20));
            if (ui.Swatch(Ui.Id(Ui.Id("editor.swatch"), i), dot, Palette.Parse(hex),
                current == Palette.Parse(hex), ui.Theme.SurfaceRaised))
            {
                _picker.Close();
                document.SetColor(hex);
            }
            x += S(27);
        }

        _chip = new Rect(x - S(4), row.Center.Y - S(14), ChipWidth(), S(28));
        var id = Ui.Id("editor.chip");
        if (ui.Interact(id, _chip))
        {
            if (_picker.IsOpen) _picker.Close();
            else _picker.Open(current);
        }

        var custom = Array.IndexOf(Palette.Swatches, current.ToHex()) < 0;
        var radius = (float)S(Metrics.RadiusSm);
        if (ui.IsHot(id)) ui.FillRounded(_chip, radius, ui.Theme.SurfaceHover);
        ui.StrokeRounded(_chip, radius, _picker.IsOpen || custom ? ui.Theme.StrokeStrong : ui.Theme.StrokeDefault);

        var well = new Rect(_chip.X + S(5), _chip.Center.Y - S(8), S(16), S(16));
        ui.FillChecker(well, (float)S(4));
        ui.FillRounded(well, (float)S(4), current);
        ui.StrokeRounded(well, (float)S(4), ui.Theme.StrokeDefault);

        var text = current.ToHex()[1..7];
        ui.Text(text, new Rect(well.Right + S(6), _chip.Y, S(48), _chip.Height), ui.Theme.TextSecondary, S(11), face: Face.Mono);
        if (current.A < 255)
            ui.Text($"{Math.Round(current.A / 2.55)}%", new Rect(well.Right + S(52), _chip.Y, S(26), _chip.Height),
                ui.Theme.TextTertiary, S(10.5), face: Face.Mono);
        ui.Icon(Icons.ChevronDown, new Rect(_chip.Right - S(21), _chip.Y, S(15), _chip.Height), ui.Theme.TextTertiary, S(14));
        if (!_picker.IsOpen) ui.Tip(id, _chip, "Any colour");
    }

    /// <summary>The size control: a slider for feel, a number box for precision. The label and the
    /// range follow what the tool actually sizes - a brush's footprint, a font, or a stroke.</summary>
    private void DrawSizeGroup(Rect row, EditorDocument document)
    {
        var (label, min, max) = SizeRange(document.SizingTool);
        var labelWidth = SizeLabelWidth(document);
        ui.Text(label.ToUpperInvariant(), new Rect(row.X, row.Y, labelWidth, row.Height), ui.Theme.TextTertiary, S(11), Weight.Bold);

        var value = document.ActiveThickness;
        var slider = new Rect(row.X + labelWidth + S(10), row.Y, S(112), row.Height);
        var sliderId = Ui.Id("editor.size");
        if (ui.Slider(sliderId, slider, min, max, ref value))
            document.SetStrokeThickness(Math.Round(value), isAdjusting: true);
        ui.Tip(sliderId, slider, $"{label} {min}–{max}");

        var box = new Rect(slider.Right + S(10), row.Center.Y - S(14), S(44), S(28));
        var field = ui.Field(Ui.Id("editor.size.box"), box, Math.Round(document.ActiveThickness).ToString(),
            char.IsAsciiDigit, 3, align: TextAlign.Center);
        if (field.Changed && int.TryParse(field.Text, out var typed) && typed >= min && typed <= max)
            document.SetStrokeThickness(typed);
        if (field.Step != 0)
            document.SetStrokeThickness(Math.Clamp(Math.Round(document.ActiveThickness) + field.Step, min, max));
    }

    /// <summary>The label measured, not assumed: "WIDTH" in bold capitals is wider than "SIZE".</summary>
    private double SizeLabelWidth(EditorDocument document) =>
        Math.Ceiling(ui.MeasureText(SizeRange(document.SizingTool).Label.ToUpperInvariant(), S(11), Weight.Bold)) + 1;

    /// <summary>What the size control edits and its range, per tool.</summary>
    public static (string Label, int Min, int Max) SizeRange(EditorTool tool) => tool switch
    {
        EditorTool.Brush or EditorTool.Eraser => ("Size", 1, 300),
        EditorTool.Text => ("Font", 8, 96),
        _ => ("Width", 1, 20),
    };

    private void DrawFillGroup(Rect row, EditorDocument document)
    {
        var current = document.Selected is { IsFillable: true } shape ? shape.Fill : document.ShapeFill;
        ReadOnlySpan<(ShapeFill Fill, Icon Icon, string Name)> options =
        [
            (ShapeFill.Outline, Icons.Rectangle, "Outline"),
            (ShapeFill.Tinted, Icons.FillTinted, "Tinted"),
            (ShapeFill.Solid, Icons.FillSolid, "Solid"),
        ];
        for (var i = 0; i < options.Length; i++)
        {
            var bounds = new Rect(row.X + i * S(32), row.Center.Y - S(14), S(30), S(28));
            if (ui.IconButton(Ui.Id(Ui.Id("editor.fill"), i), bounds, options[i].Icon, options[i].Name,
                soft: current == options[i].Fill, iconSize: 15))
                document.SetFill(options[i].Fill);
        }
    }

    private void DrawTextGroup(Rect row, EditorDocument document)
    {
        var text = document.Selected is { Tool: EditorTool.Text } selected ? selected : null;
        var bold = text?.IsBold ?? document.TextBold;
        var italic = text?.IsItalic ?? document.TextItalic;
        var underline = text?.IsUnderline ?? document.TextUnderline;

        var bounds = new Rect(row.X, row.Center.Y - S(14), S(30), S(28));
        if (ui.IconButton(Ui.Id("editor.bold"), bounds, Icons.Bold, "Bold", "Ctrl B", soft: bold, iconSize: 15))
            document.SetTextFormat(d => d.TextBold = !bold, a => a.IsBold = !bold);
        if (ui.IconButton(Ui.Id("editor.italic"), bounds with { X = bounds.X + S(32) }, Icons.Italic, "Italic", "Ctrl I",
            soft: italic, iconSize: 15))
            document.SetTextFormat(d => d.TextItalic = !italic, a => a.IsItalic = !italic);
        if (ui.IconButton(Ui.Id("editor.underline"), bounds with { X = bounds.X + S(64) }, Icons.Underline, "Underline", "Ctrl U",
            soft: underline, iconSize: 15))
            document.SetTextFormat(d => d.TextUnderline = !underline, a => a.IsUnderline = !underline);
    }

    private double CounterGroupWidth(EditorDocument document) =>
        ui.MeasureText($"Next {document.NextCounter}", S(Metrics.FontSm), Weight.Semibold) + S(8)
        + ui.ButtonWidth("Reset", small: true);

    private void DrawCounterGroup(Rect row, EditorDocument document)
    {
        var next = $"{document.NextCounter}";
        var font = S(Metrics.FontSm);
        var lead = ui.MeasureText("Next ", font);
        ui.Text("Next ", new Rect(row.X, row.Y, lead + 1, row.Height), ui.Theme.TextSecondary, font);
        ui.Text(next, new Rect(row.X + lead, row.Y, S(30), row.Height), ui.Theme.TextPrimary, font, Weight.Semibold);

        var width = ui.ButtonWidth("Reset", small: true);
        if (ui.Button(Ui.Id("editor.counter.reset"), new Rect(row.Right - width, row.Center.Y - S(14), width, S(28)),
            "Reset", small: true, tooltip: "Number from 1 again"))
            document.ResetCounter();
    }

    private void DrawDeleteButton(Rect row, EditorDocument document)
    {
        if (ui.IconButton(Ui.Id("editor.delete"), new Rect(row.X, row.Center.Y - S(14), S(28), S(28)), Icons.Delete,
            "Delete", "Del", iconSize: 15, destructive: true))
            document.DeleteSelected();
    }

    // ============================  CROP, TOAST, PICKER  ============================

    /// <summary>The crop session's controls, hanging under the frame they act on - or above the bottom
    /// edge, when the frame runs to it.</summary>
    private void DrawCropBar(Frame frame, Rect crop)
    {
        var document = frame.Document;
        var label = $"{(int)crop.Width} × {(int)crop.Height}";
        var labelWidth = ui.MeasureText(label, S(Metrics.FontSm), Weight.Semibold) + S(20);
        var reset = ui.ButtonWidth("Reset", small: true);
        var cancel = ui.ButtonWidth("Cancel", keycap: "Esc", small: true);
        var apply = ui.ButtonWidth("Apply", Icons.Tick, "⏎", small: true);
        var width = S(4) + labelWidth + S(13) + reset + S(6) + cancel + S(6) + apply + S(4);

        var bottom = frame.ImageOrigin.Y + (crop.Y + crop.Height) * frame.ImageScale;
        var centre = frame.ImageOrigin.X + (crop.X + crop.Width / 2) * frame.ImageScale;
        var y = Math.Min(bottom + S(14), frame.Height - S(60));
        var pill = new Rect(Math.Clamp(centre - width / 2, S(8), frame.Width - width - S(8)), y, width, S(36));
        ui.Pill(pill);
        _covered.Add(pill);

        var x = pill.X + S(4);
        var buttonY = pill.Y + S(4);
        ui.Text(label, new Rect(x, pill.Y, labelWidth, pill.Height), ui.Theme.TextSecondary, S(Metrics.FontSm),
            Weight.Semibold, TextAlign.Center);
        x += labelWidth;
        ui.Separator(new Point(x + S(6), pill.Center.Y));
        x += S(13);

        if (ui.Button(Ui.Id("crop.reset"), new Rect(x, buttonY, reset, S(28)), "Reset", small: true))
            document.ResetCropFrame();
        x += reset + S(6);
        if (ui.Button(Ui.Id("crop.cancel"), new Rect(x, buttonY, cancel, S(28)), "Cancel", keycap: "Esc", small: true))
            ToolPicked = EditorTool.Select;
        x += cancel + S(6);
        if (ui.Button(Ui.Id("crop.apply"), new Rect(x, buttonY, apply, S(28)), "Apply", ButtonStyle.Primary,
            Icons.Tick, "⏎", small: true))
        {
            document.CommitCrop();
            ToolPicked = EditorTool.Select;
        }
    }

    /// <summary>A short confirmation above the style bar: rises and fades in, then out.</summary>
    private void DrawToast(Frame frame)
    {
        var shown = ui.Animate(Ui.Id("editor.toast"), frame.Toast is null ? 0 : 1, Metrics.Motion);
        if (frame.Toast is { } message) _lastToast = message;
        if (shown <= 0.01 || _lastToast is null) return;
        ui.Toast(_lastToast, frame.Width / 2, frame.Height - S(84), shown);
    }

    private string? _lastToast;

    private void DrawPicker(Frame frame)
    {
        if (!_picker.IsOpen) return;

        var settings = frame.Settings;
        var document = frame.Document;
        var result = _picker.Draw(ui, _chip, new Rect(0, 0, frame.Width, frame.Height),
            settings.RecentColors, settings.SavedColors);

        if (result.Color is { } color)
        {
            document.SetColor(color.ToHex(), isAdjusting: result.Live);
            _pickerChanged = true;
        }
        if (result.PickFromScreen) Requested = Command.PickFromScreen;
        if (result.Copy is { } copy) CopyRequested = copy;
        if (result.Save is { } save) { settings.SaveColor(save); SettingsChanged = true; }
        if (result.Forget is { } forget) { settings.ForgetSavedColor(forget); SettingsChanged = true; }
        if (result.Closed) PickerClosed(settings);
    }

    /// <summary>True when the picker changed a persisted colour list this frame.</summary>
    public bool SettingsChanged { get; set; }

    private bool _pickerChanged;

    /// <summary>A colour applied from the picker joins the recent list when the picker closes, not on
    /// every step of a drag through the spectrum.</summary>
    public void PickerClosed(AppSettings settings)
    {
        _picker.Close();
        if (!_pickerChanged) return;
        _pickerChanged = false;
        settings.RememberColor(_picker.Current.ToHex());
        SettingsChanged = true;
    }

    // ============================  TABLES  ============================

    private static Icon Glyph(EditorTool tool) => tool switch
    {
        EditorTool.Select => Icons.Select,
        EditorTool.Rectangle => Icons.Rectangle,
        EditorTool.Ellipse => Icons.Ellipse,
        EditorTool.Line => Icons.Line,
        EditorTool.Arrow => Icons.Arrow,
        EditorTool.Pen => Icons.Pen,
        EditorTool.Brush => Icons.Brush,
        EditorTool.Eraser => Icons.Eraser,
        EditorTool.Text => Icons.Text,
        EditorTool.Highlight => Icons.Highlight,
        EditorTool.Blur => Icons.Blur,
        EditorTool.Pixelate => Icons.Pixelate,
        EditorTool.Counter => Icons.Counter,
        EditorTool.Spotlight => Icons.Spotlight,
        _ => Icons.Crop,
    };

    private static string Name(EditorTool tool) => tool switch
    {
        EditorTool.Eraser => "Eraser · pen and brush",
        _ => tool.ToString(),
    };

    /// <summary>B is Blur, not Brush; P is Pixelate, not Pen - the letters users arrive with.</summary>
    public static string Shortcut(EditorTool tool) => tool switch
    {
        EditorTool.Select => "V",
        EditorTool.Rectangle => "R",
        EditorTool.Ellipse => "E",
        EditorTool.Arrow => "A",
        EditorTool.Line => "L",
        EditorTool.Pen => "D",
        EditorTool.Brush => "M",
        EditorTool.Eraser => "X",
        EditorTool.Text => "T",
        EditorTool.Counter => "N",
        EditorTool.Highlight => "H",
        EditorTool.Blur => "B",
        EditorTool.Pixelate => "P",
        EditorTool.Spotlight => "S",
        _ => "C",
    };
}
