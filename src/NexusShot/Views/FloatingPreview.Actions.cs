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
    /// <summary>The hover state: a scrim, and each button where <see cref="CardLayout"/> puts it.</summary>
    private void DrawActions(Ui ui, Rect card)
    {
        ui.FillRect(card, Theme.ImageScrim);
        var glyph = S(11);
        var now = Environment.TickCount64;

        // Pin: the accent when engaged, so its state is legible without a label.
        if (ui.OverlayButton(Ui.Id("preview.pin"), ButtonRect(CardAction.Pin), Icons.Pin, glyph,
            on: _card.IsPinned, round: true))
            _stack.TogglePin(_card);

        if (ui.OverlayButton(Ui.Id("preview.edit"), ButtonRect(CardAction.Edit), Icons.Edit, glyph, round: true))
            Post(RaiseEditRequested);

        if (ui.OverlayButton(Ui.Id("preview.save"), ButtonRect(CardAction.SaveAs), Icons.Save, glyph, round: true))
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

        var hot = ui.IsHot(id) || ui.IsActive(id);
        ui.FillRounded(bounds, radius, ui.IsActive(id) ? ui.Theme.AccentPressed : hot ? ui.Theme.Accent : Ui.OverlayRest);
        ui.StrokeRounded(bounds, radius, Ui.OverlayBorder);
        ui.Text(confirmation > 0.5 ? "Copied" : label, bounds, hot ? ui.Theme.TextOnAccent : Rgba.White, S(10),
            Weight.Semibold, TextAlign.Center);
        return clicked;
    }

    private Rect ButtonRect(CardAction action) => Scaled(CardLayout.Button(action));

    /// <summary>Dismisses the card without acting on the capture. Acted on last: Dismiss tears the
    /// window down, and the frame still has to finish.</summary>
    private void DrawClose(Ui ui, Rect bounds)
    {
        if (ui.OverlayButton(Ui.Id("preview.close"), bounds, Icons.Close, S(11), destructive: true, round: true))
            Dismiss();
    }

    /// <summary>Posted rather than handled inline from the button click: dismissing here must not
    /// run underneath the frame that just drew the button.</summary>
    private void RaiseEditRequested()
    {
        if (_dismissing) return;
        EditRequested?.Invoke(_card.Item);
        Dismiss();
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
                return TextRecognition.CopyText(pixels, _stack.Settings.OcrLanguage);
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
        ui.FillRounded(badge, (float)S(4), Theme.ImageScrim);
        ui.Icon(Icons.Pin, badge, ui.Theme.Accent, S(11));
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
