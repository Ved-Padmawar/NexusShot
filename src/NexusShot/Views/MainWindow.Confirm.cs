using NexusShot.Core;
using NexusShot.Render;

namespace NexusShot.Views;

/// <summary>The delete prompt, drawn rather than a message box so it follows the theme.</summary>
public sealed partial class MainWindow
{
    /// <summary>Null while the prompt is closed.</summary>
    private List<ScreenshotHistoryItem>? _pendingDelete;

    /// <summary>Kept so the card keeps its text while it fades out.</summary>
    private int _promptCount;

    internal bool ConfirmOpen => _pendingDelete is not null;

    private void AskDelete(IReadOnlyList<ScreenshotHistoryItem> items)
    {
        if (items.Count == 0) return;
        _pendingDelete = [.. items];
        _promptCount = items.Count;
        Invalidate();
    }

    private void DrawConfirm(Ui ui, double width, double height)
    {
        var open = ui.Animate(Ui.Id("confirm.open"), ConfirmOpen ? 1 : 0, Metrics.MotionFast);
        if (open <= 0.01) return;

        var theme = ui.Theme;
        ui.FillRect(new Rect(0, 0, width, height), theme.Scrim.WithAlpha((byte)(theme.Scrim.A * open)));

        var pad = S(24);
        var card = new Rect((width - S(480)) / 2, (height - S(156)) / 2 + S(10) * (1 - open), S(480), S(156));

        // A press on the scrim cancels, and must not also land on the library.
        if (ConfirmOpen && ui.PointerPressed && !card.Contains(ui.Pointer) && ui.Pointer.Y > CaptionHeight)
            _pendingDelete = null;
        ui.Inert = !ConfirmOpen;

        var radius = (float)S(Metrics.RadiusLg);
        ui.Shadow(card, radius, S(60), S(24), theme.Shadow);
        ui.FillRounded(card, radius, theme.SurfaceWindow);
        ui.StrokeRounded(card, radius, theme.StrokeDefault);

        var badge = new Rect(card.X + pad, card.Y + pad, S(36), S(36));
        ui.FillRounded(badge, (float)(badge.Width / 2), theme.Danger.WithAlpha(40));
        ui.Icon(Icons.Delete, badge, theme.Danger, S(18));

        var textX = badge.Right + S(14);
        var title = _promptCount == 1 ? "Delete this capture?" : $"Delete {_promptCount} captures?";
        ui.Text(title, new Rect(textX, card.Y + pad - S(2), card.Right - pad - textX, S(24)), theme.TextPrimary,
            S(Metrics.FontLg), Weight.Semibold, face: Face.Display);
        ui.Text(_promptCount == 1
                ? "The file is deleted from disk and cannot be undone."
                : "The files are deleted from disk and cannot be undone.",
            new Rect(textX, card.Y + pad + S(26), card.Right - pad - textX, S(20)), theme.TextSecondary, S(Metrics.FontMd));

        var buttonY = card.Bottom - pad - S(32);
        var deleteWidth = ui.ButtonWidth("Delete", Icons.Delete);
        var deleteBounds = new Rect(card.Right - pad - deleteWidth, buttonY, deleteWidth, S(32));
        var cancelWidth = ui.ButtonWidth("Cancel", keycap: "Esc");
        var cancelBounds = new Rect(deleteBounds.X - S(8) - cancelWidth, buttonY, cancelWidth, S(32));

        if (ui.Button(Ui.Id("confirm.cancel"), cancelBounds, "Cancel", ButtonStyle.Outline, keycap: "Esc"))
            _pendingDelete = null;

        if (ui.Button(Ui.Id("confirm.delete"), deleteBounds, "Delete", ButtonStyle.Destructive, Icons.Delete)
            && _pendingDelete is { } items)
        {
            _pendingDelete = null;
            Post(() => DeleteCaptures(items));
        }

        ui.Inert = false;
    }
}
