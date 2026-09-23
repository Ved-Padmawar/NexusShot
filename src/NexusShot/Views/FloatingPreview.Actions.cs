using NexusShot.Core;
using NexusShot.Platform;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>
/// The card's buttons: copy, copy text, save as, edit, pin, close, and the transient feedback the
/// copies show.
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

    /// <summary>The hover state: a scrim, and each button where <see cref="CardLayout"/> puts it.</summary>
    private void DrawActions(Ui ui, Rect card)
    {
        ui.FillRect(card, ui.Theme.HoverScrim);
        var glyph = S(9);
        var now = Environment.TickCount64;

        // Pin: the accent when engaged, so its state is legible without a label.
        if (ActionButton(ui, Ui.Id("preview.pin"), ButtonRect(CardAction.Pin), Icons.Pin, glyph, _card.IsPinned))
            _stack.TogglePin(_card);

        if (ActionButton(ui, Ui.Id("preview.edit"), ButtonRect(CardAction.Edit), Icons.Edit, glyph, false))
            Post(RaiseEditRequested);

        if (ActionButton(ui, Ui.Id("preview.save"), ButtonRect(CardAction.SaveAs), Icons.Save, glyph, false))
            Post(SaveAs);

        // Copy leaves the card up: you may still want to drag it, edit it, or copy it again.
        if (PillButton(ui, Ui.Id("preview.copy"), ButtonRect(CardAction.Copy), "Copy", _copied.Progress(now)))
            Post(Copy);

        if (PillButton(ui, Ui.Id("preview.copytext"), ButtonRect(CardAction.CopyText), "Copy text", _textCopied.Progress(now)))
            Post(CopyText);

        DrawClose(ui, ButtonRect(CardAction.Close));
    }

    /// <summary>A text pill that confirms itself by reading "Copied" for a moment.</summary>
    private bool PillButton(Ui ui, int id, Rect bounds, string label, double confirmation)
    {
        var clicked = ui.Interact(id, bounds);
        var radius = (float)(bounds.Height / 2);

        ui.FillRounded(bounds, radius, ui.IsHot(id) || ui.IsActive(id) ? ui.Theme.Accent : ActionBackground);
        ui.StrokeRounded(bounds, radius, ActionBorder);
        ui.Text(confirmation > 0.5 ? "Copied" : label, bounds, Rgba.White, (float)S(10),
            align: TextAlign.Center);
        return clicked;
    }

    private Rect ButtonRect(CardAction action) => Scaled(CardLayout.Button(action));

    /// <summary>Dismisses the card without acting on the capture.</summary>
    private void DrawClose(Ui ui, Rect bounds)
    {
        var id = Ui.Id("preview.close");
        var clicked = ui.Interact(id, bounds);
        var hot = ui.IsHot(id) || ui.IsActive(id);

        var center = bounds.Center;
        var radius = (float)(bounds.Width / 2);
        ui.FillCircle(center, radius, hot ? CloseHover : CloseBackground);
        ui.StrokeCircle(center, radius, CloseBorder);
        ui.Icon(Icons.Close, bounds, Rgba.White, S(7));

        // Acted on last: Dismiss tears the window down, and the frame still has to finish.
        if (clicked) Dismiss();
    }

    /// <summary>Posted rather than handled inline from the button click: dismissing here must not
    /// run underneath the frame that just drew the button.</summary>
    private void RaiseEditRequested()
    {
        if (_dismissing) return;
        EditRequested?.Invoke(_card.Item);
        Dismiss();
    }

    /// <summary>A circular overlay action button, washed a little lighter on hover and press.</summary>
    private bool ActionButton(Ui ui, int id, Rect bounds, string glyph, double glyphSize, bool selected)
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
        ui.Icon(glyph, bounds, Rgba.White, glyphSize);

        return clicked;
    }

    private bool _copying;

    private void Copy() => _ = CopyAsync();

    private async Task CopyAsync()
    {
        if (_dismissing || _copying) return;
        _copying = true;
        var path = _card.Item.FilePath;
        Exception? failure = null;
        try { await MediaWorker.Run(() => { ClipboardImage.Copy(path); return true; }); }
        catch (Exception exception) { failure = exception; }
        Post(() =>
        {
            _copying = false;
            if (_dismissing) return;
            if (failure is null)
            {
                _copied.Start(Environment.TickCount64);
                WindowInterop.SetTimer(Handle, CopyFeedbackTimerId, 16, IntPtr.Zero);
            }
            else
            {
                _copied.Stop();
                Log.Error("preview.copy", failure, path);
                UserFeedback.Error(Handle, "Could not copy this image. Check that the file exists and retry.");
            }
            Invalidate();
        });
    }

    private void CopyText() => _ = CopyTextAsync();

    /// <summary>Success shows as the button's tick, like Copy; only "no text" and failures notify.</summary>
    private async Task CopyTextAsync()
    {
        if (_dismissing || _copying) return;
        _copying = true;
        var path = _card.Item.FilePath;
        var lines = 0;
        Exception? failure = null;
        try
        {
            lines = await MediaWorker.Run(() =>
            {
                using var pixels = ImageSurface.Decode(path);
                return TextRecognition.CopyText(pixels);
            });
        }
        catch (Exception exception) { failure = exception; }
        Post(() =>
        {
            _copying = false;
            if (_dismissing) return;
            if (failure is not null)
            {
                Log.Error("preview.copy_text", failure, path);
                UserFeedback.Error(Handle, failure is InvalidOperationException
                    ? failure.Message
                    : "Could not read the text in this image. Please retry.");
            }
            else if (lines == 0) UserFeedback.Info(Handle, "No text found in this capture.");
            else
            {
                _textCopied.Start(Environment.TickCount64);
                WindowInterop.SetTimer(Handle, CopyFeedbackTimerId, 16, IntPtr.Zero);
            }
            Invalidate();
        });
    }

    /// <summary>One timer steps both copy confirmations.</summary>
    private void StepCopyFeedback()
    {
        var now = Environment.TickCount64;
        if (new[] { _copied.NextFrameDelay(now), _textCopied.NextFrameDelay(now) }.Min() is { } delay)
            WindowInterop.SetTimer(Handle, CopyFeedbackTimerId, delay, IntPtr.Zero);
        else
            WindowInterop.KillTimer(Handle, CopyFeedbackTimerId);

        if (_hovered) Invalidate();
    }

    /// <summary>A pinned card that is not hovered still says so, quietly, where the pin button sits.</summary>
    private void DrawPin(Ui ui)
    {
        var badge = ButtonRect(CardAction.Pin);
        ui.FillRounded(badge, (float)S(4), ui.Theme.HoverScrim);
        ui.Icon(Icons.Pin, badge, ui.Theme.Accent, S(9));
    }

    /// <summary>Writes a copy wherever the user picks, then dismisses: the capture has landed
    /// somewhere permanent, so the card has done its job.</summary>
    private void SaveAs() => _ = SaveAsAsync();

    private async Task SaveAsAsync()
    {
        if (_dismissing || _savingAs) return;
        _savingAs = true;
        string? destination;
        var source = _card.Item.FilePath;
        try
        {
            destination = FilePicker.SavePng(Handle, Path.GetFileName(source), Path.GetDirectoryName(source));
            if (destination is null) { _savingAs = false; return; }
        }
        catch (Exception exception)
        {
            _savingAs = false;
            Log.Error("preview.save_picker", exception, source);
            UserFeedback.Error(Handle, "Could not open the save dialog. Please retry.");
            return;
        }
        Exception? failure = null;
        try { await MediaWorker.Run(() => { AtomicFile.Copy(source, destination); return true; }); }
        catch (Exception exception) { failure = exception; }
        Post(() =>
        {
            _savingAs = false;
            if (_dismissing) return;
            if (failure is null) Dismiss();
            else
            {
                Log.Error("preview.save", failure, source);
                UserFeedback.Error(Handle, "Could not save this image. Check the destination and available disk space.");
            }
        });
    }
}
