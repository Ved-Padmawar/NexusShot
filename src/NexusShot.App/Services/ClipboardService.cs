using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NexusShot.App.Services;

public interface IClipboardService
{
    Task CopyImageAsync(string imagePath, CancellationToken cancellationToken);
}

public sealed class ClipboardService : IClipboardService
{
    public async Task CopyImageAsync(string imagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
