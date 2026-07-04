using Avalonia.Threading;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Ui.Services;

/// <summary>将高频队列进度合并后再投递到 UI 线程，避免多工作目录并行时淹没 Dispatcher。</summary>
public sealed class QueueUiProgressSink
{
    private readonly object _lock = new();
    private readonly Dictionary<string, QueueWorkerProgress> _pending = new(StringComparer.Ordinal);
    private readonly Action<QueueWorkerProgress> _handler;
    private bool _flushScheduled;

    public QueueUiProgressSink(Action<QueueWorkerProgress> handler) => _handler = handler;

    public void Post(QueueWorkerProgress progress)
    {
        var key = BuildKey(progress);
        lock (_lock)
        {
            _pending[key] = progress;
            if (_flushScheduled) return;
            _flushScheduled = true;
        }

        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        List<QueueWorkerProgress> batch;
        lock (_lock)
        {
            batch = _pending.Values.ToList();
            _pending.Clear();
            _flushScheduled = false;
        }

        foreach (var progress in batch)
            _handler(progress);
    }

    private static string BuildKey(QueueWorkerProgress progress)
    {
        var workspace = progress.WorkspaceRoot ?? "";
        var project = progress.Item?.ProjectDir ?? "";
        var step = progress.StepKey ?? "";
        return $"{workspace}|{project}|{step}";
    }
}
