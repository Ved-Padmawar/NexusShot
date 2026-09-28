using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>One editor per file: found by the file it shows or is saving to, and followed through a
/// Save As.</summary>
public class EditorRegistryTests
{
    private sealed class Editor
    {
        public string? Saving { get; set; }
    }

    private static EditorRegistry<Editor> NewRegistry() => new(editor => editor.Saving);

    [Fact]
    public void AFileIsOwnedByTheEditorShowingItOrSavingToIt()
    {
        var registry = NewRegistry();
        var showing = new Editor();
        var saving = new Editor { Saving = @"C:\shots\b.png" };
        registry.Add(@"C:\shots\a.png", showing);
        registry.Add(@"C:\shots\c.png", saving);

        Assert.Same(showing, registry.Owner(@"C:\SHOTS\A.PNG"));
        Assert.Same(saving, registry.Owner(@"C:\shots\b.png"));
        Assert.Null(registry.Owner(@"C:\shots\d.png"));
    }

    [Fact]
    public void AnEditorCannotSaveOverAnotherEditorsFile()
    {
        var registry = NewRegistry();
        var first = new Editor();
        var second = new Editor();
        registry.Add("a.png", first);
        registry.Add("b.png", second);

        Assert.False(registry.CanSaveTo(second, "a.png"));
        Assert.True(registry.CanSaveTo(first, "a.png"));
        Assert.True(registry.CanSaveTo(second, "new.png"));
    }

    [Fact]
    public void SaveAsMovesTheEditorToItsNewFile()
    {
        var registry = NewRegistry();
        var editor = new Editor();
        registry.Add("a.png", editor);

        registry.Move(editor, "a.png", "a_edited.png");

        Assert.Null(registry.Owner("a.png"));
        Assert.Same(editor, registry.Owner("a_edited.png"));
        Assert.Single(registry.All);
    }

    [Fact]
    public void RemovingLeavesAPathTakenByAnotherEditor()
    {
        var registry = NewRegistry();
        var closing = new Editor();
        var other = new Editor();
        registry.Add("b.png", other);

        registry.Remove(closing, "b.png");

        Assert.Same(other, registry.Owner("b.png"));
    }
}
