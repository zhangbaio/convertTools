using Avalonia.Threading;
using System.Collections.Concurrent;
using System.Threading;

namespace ShortDrama.Desktop.Services;

internal sealed class BufferedUiProgress<T>(Action<IReadOnlyList<T>> consumeBatch) : IProgress<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private int _scheduled;

    public void Report(T value)
    {
        _queue.Enqueue(value);
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(DrainQueue, DispatcherPriority.Background);
    }

    private void DrainQueue()
    {
        var batch = new List<T>(64);
        while (batch.Count < 64 && _queue.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        Interlocked.Exchange(ref _scheduled, 0);

        if (batch.Count > 0)
        {
            consumeBatch(batch);
        }

        if (!_queue.IsEmpty)
        {
            ScheduleDrain();
        }
    }
}
