using Avalonia.Threading;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Ui.Services;

/// <summary>将高频队列进度合并后再投递到 UI 线程，避免多工作目录并行时淹没 Dispatcher。</summary>
public sealed class QueueUiProgressSink
{
    private readonly QueueProgressBatchBuffer _pending = new();
    private readonly Action<QueueWorkerProgress> _handler;

    public QueueUiProgressSink(Action<QueueWorkerProgress> handler) => _handler = handler;

    public void Post(QueueWorkerProgress progress)
    {
        if (!_pending.Enqueue(progress))
            return;

        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        foreach (var progress in _pending.Drain())
            _handler(progress);
    }
}
