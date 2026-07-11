using NexusShot.App.Models;
using Windows.Foundation;

namespace NexusShot.App.Editor;

public interface IAnnotationFlattener
{
    /// <summary>
    /// Composites <paramref name="annotations"/> onto the image at <paramref name="sourcePath"/> at
    /// true pixel resolution and writes a PNG to <paramref name="destinationPath"/>.
    /// </summary>
    Task FlattenAsync(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<Annotation> annotations,
        Rect? cropBounds,
        CancellationToken cancellationToken);
}
