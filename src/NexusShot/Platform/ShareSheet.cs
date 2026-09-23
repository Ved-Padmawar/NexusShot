using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace NexusShot.Platform;

/// <summary>The Windows share sheet: every installed target that takes an image, with no
/// integration of our own.</summary>
internal static class ShareSheet
{
    /// <summary>Call on <paramref name="window"/>'s own thread.</summary>
    public static void Share(IntPtr window, string path)
    {
        var manager = DataTransferManagerInterop.GetForWindow(window);

        // The manager outlives this call; left attached, the handler would answer the next share too.
        TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
        handler = (sender, args) =>
        {
            sender.DataRequested -= handler;
            _ = Fill(args.Request, path);
        };
        manager.DataRequested += handler;

        DataTransferManagerInterop.ShowShareUIForWindow(window);
    }

    /// <summary>Both the file and a bitmap: mail wants the attachment, some chat apps only a bitmap.</summary>
    private static async Task Fill(DataRequest request, string path)
    {
        var deferral = request.GetDeferral();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            request.Data.Properties.Title = file.Name;
            // A List: AOT has marshalling for List<T>, not for a collection expression's hidden type.
            request.Data.SetStorageItems(new List<IStorageItem> { file });
            request.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }
        catch (Exception exception)
        {
            Core.Log.Error("share.failed", exception, path);
            request.FailWithDisplayText("This capture could not be shared.");
        }
        finally { deferral.Complete(); }
    }
}
