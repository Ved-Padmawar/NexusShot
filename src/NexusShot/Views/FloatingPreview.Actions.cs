using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The card's buttons: copy, save as, edit, pin, close, and the transient feedback copy shows.
///
/// Separated from the window itself, which owns placement, the fade animation and the drag source.
/// </summary>
public sealed partial class FloatingPreview
{
    private static readonly Rgba ActionBackground = new(0x20, 0x20, 0x24, 0xE6);
    private static readonly Rgba ActionBorder = new(0xFF, 0xFF, 0xFF, 0x26);

    /// <summary>Hover and press wash white over the rest fill and leave the border alone, which is
    /// what the stock button template these were gave them.</summary>
    private static readonly Rgba ActionOverlayHover = new(0xFF, 0xFF, 0xFF, 0x0F);
    private static readonly Rgba ActionOverlayPressed = new(0xFF, 0xFF, 0xFF, 0x0A);

    private static readonly Rgba CloseBackground = new(0x32, 0x32, 0x36, 0xF2);
    private static readonly Rgba CloseHover = new(0xC4, 0x2B, 0x1C, 0xFF);
    private static readonly Rgba CloseBorder = new(0xFF, 0xFF, 0xFF, 0x59);

    /// <summary>Dismisses the card without acting on the capture.</summary>
    private void DrawClose(Ui ui, Rect card)
    {
        var size = S(16);
        var bounds = new Rect(card.Right - size - S(4), S(4), size, size);

        var id = Ui.Id("preview.close");
        var clicked = ui.Interact(id, bounds);
        var hot = ui.IsHot(id) || ui.IsActive(id);

        var center = bounds.Center;
        var radius = (float)(size / 2);
        ui.FillCircle(center, radius, hot ? CloseHover : CloseBackground);
        ui.StrokeCircle(center, radius, CloseBorder);
        ui.Icon(Icons.Close, bounds, Rgba.White, S(8));

        // Acted on last: Dismiss tears the window down, and the frame still has to finish.
        if (clicked) Dismiss();
    }

    /// <summary>The hover actions: a full-card scrim behind a centred row of circular buttons.</summary>
    private void DrawActions(Ui ui, Rect card)
    {
        ui.FillRect(card, ui.Theme.HoverScrim);

        const int count = 4;
        var size = S(22);
        var spacing = S(5);
        var totalWidth = size * count + spacing * (count - 1);
        var x = card.Center.X - totalWidth / 2;
        var y = card.Center.Y - size / 2;
        var glyph = S(10);

        // Copy leaves the card up: the capture is on the clipboard, but you may still want to drag
        // it, edit it, or copy it again.
        if (ActionButton(ui, Ui.Id("preview.copy"), new Rect(x, y, size, size), Icons.Copy, glyph, false, _copied.Progress(Environment.TickCount64)))
        {
            Post(Copy);
        }
        x += size + spacing;

        if (ActionButton(ui, Ui.Id("preview.save"), new Rect(x, y, size, size), Icons.Save, glyph, false))
        {
            Post(SaveAs);
        }
        x += size + spacing;

        if (ActionButton(ui, Ui.Id("preview.edit"), new Rect(x, y, size, size), Icons.Edit, glyph, false))
        {
            Post(RaiseEditRequested);
        }
        x += size + spacing;

        // Pin: the accent when engaged, so its state is legible without a label.
        if (ActionButton(ui, Ui.Id("preview.pin"), new Rect(x, y, size, size), Icons.Pin, glyph, IsPinned))
        {
            IsPinned = !IsPinned;
            _remaining = _dismissSeconds;
            Post(() => PinnedChanged?.Invoke());
        }
    }

    /// <summary>Posted rather than handled inline from the button click: dismissing here must not
    /// run underneath the frame that just drew the button.</summary>
    private void RaiseEditRequested()
    {
        if (_dismissing) return;
        EditRequested?.Invoke(_item);
        Dismiss();
    }

    /// <summary>A circular overlay action button, washed a little lighter on hover and press.</summary>
    private bool ActionButton(Ui ui, int id, Rect bounds, string glyph, double glyphSize, bool selected,
        double confirmation = 0)
    {
        var clicked = ui.Interact(id, bounds);

        var center = bounds.Center;
        var radius = (float)(bounds.Width / 2);

        ui.FillCircle(center, radius, selected ? ui.Theme.Accent : ActionBackground);

        // Over whatever the button already is, so an engaged pin brightens from the accent rather
        // than snapping back to grey.
        if (ui.IsActive(id)) ui.FillCircle(center, radius, ActionOverlayPressed);
        else if (ui.IsHot(id)) ui.FillCircle(center, radius, ActionOverlayHover);

        ui.StrokeCircle(center, radius, ActionBorder);
        if (confirmation < 1)
            ui.Icon(glyph, bounds, Rgba.White.WithAlpha((byte)(255 * (1 - confirmation))), glyphSize);
        if (confirmation > 0)
            ui.Icon(Icons.Tick, bounds, Rgba.White.WithAlpha((byte)(255 * confirmation)),
                glyphSize * (0.8 + 0.2 * confirmation));

        return clicked;
    }

    private void Copy()
    {
        if (_dismissing) return;
        try
        {
            ClipboardImage.Copy(_item.FilePath);
            // Only a completed copy earns a tick. A second successful click restarts the hold.
            _copied.Start(Environment.TickCount64);
            WindowInterop.SetTimer(Handle, CopyFeedbackTimerId, 16, IntPtr.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            _copied.Stop();
            WindowInterop.KillTimer(Handle, CopyFeedbackTimerId);
            Log.Error("preview.copy", exception, _item.FilePath);
        }
        Invalidate();
    }

    private void StepCopyFeedback()
    {
        if (_copied.NextFrameDelay(Environment.TickCount64) is { } delay)
            WindowInterop.SetTimer(Handle, CopyFeedbackTimerId, delay, IntPtr.Zero);
        else
            WindowInterop.KillTimer(Handle, CopyFeedbackTimerId);

        if (_hovered) Invalidate();
    }

    /// <summary>A pinned card that is not hovered still says so, quietly, in the corner.</summary>
    private void DrawPin(Ui ui, Rect card)
    {
        var badge = new Rect(card.Right - S(24), S(5), S(18), S(18));
        ui.FillRounded(badge, (float)S(4), ui.Theme.HoverScrim);
        ui.Icon(Icons.Pin, badge, ui.Theme.Accent, S(11));
    }

    /// <summary>Writes a copy wherever the user picks, then dismisses: the capture has landed
    /// somewhere permanent, so the card has done its job.</summary>
    private void SaveAs()
    {
        if (_dismissing) return;

        // The picker pumps its own message loop, so the countdown keeps ticking behind it - and
        // would close the window while the user is still typing a filename.
        _remaining = _dismissSeconds;
        _savingAs = true;

        string? destination;
        try
        {
            destination = FilePicker.SavePng(Handle, Path.GetFileName(_item.FilePath),
                Path.GetDirectoryName(_item.FilePath));
        }
        finally
        {
            _savingAs = false;
        }

        if (destination is null) return;

        try
        {
            File.Copy(_item.FilePath, destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Dismiss();
    }
}
