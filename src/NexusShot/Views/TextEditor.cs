using NexusShot.Core;

namespace NexusShot.Views;

/// <summary>
/// Inline text entry over an annotation. The window draws it in the same D2D pass as everything
/// else; the editing itself is <see cref="TextBuffer"/>, shared with every other field.
///
/// This was a real Win32 EDIT parked over the canvas, which gets a caret and selection for free. It
/// does not work here: a child HWND and a Direct2D surface have no defined paint order, so the two
/// invalidate each other every frame - the box flickered and its glyphs lagged a keystroke. Drawing
/// the text ourselves is what Paint.NET and Greenshot do, for the same reason.
///
/// The live text is not written back to the annotation until the edit ends, so the box owns its own
/// history: the document's undo stack has nothing of it to restore.
/// </summary>
internal sealed class TextEditor(Annotation annotation)
    : TextBuffer(annotation.Text, annotation.Format, annotation.Runs)
{
    public Annotation Annotation { get; } = annotation;
}
