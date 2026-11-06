using System.Collections.Concurrent;

namespace NexusShot.Platform;

/// <summary>One bounded media queue keeps PNG and clipboard work off the message pump, and
/// confines the export device to one thread. A busy queue fails visibly instead of retaining
/// an unlimited number of desktop-sized bitmaps.</summary>
internal static class MediaWorker
{
    private static readonly BlockingCollection<Action> Queue = new(8);

    static MediaWorker()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in Queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "NexusShot media" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public static Task<T> Run<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Queue.TryAdd(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception exception) { completion.SetException(exception); }
        })) completion.SetException(new InvalidOperationException("Image processing is busy. Please retry shortly."));
        return completion.Task;
    }
}
