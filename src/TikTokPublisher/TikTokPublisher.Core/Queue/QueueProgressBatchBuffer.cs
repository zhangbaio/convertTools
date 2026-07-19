namespace TikTokPublisher.Core.Queue;

/// <summary>
/// 暂存队列进度，供 UI 批量刷新。下载步骤逐条保留；其余高频步骤仍按
/// “工作目录 + 项目 + 步骤”只保留批次内的最新一条。
/// </summary>
public sealed class QueueProgressBatchBuffer
{
    private readonly object _lock = new();
    private readonly List<PendingProgress> _lossless = [];
    private readonly Dictionary<string, PendingProgress> _latestByKey = new(StringComparer.Ordinal);
    private long _sequence;
    private bool _flushScheduled;

    /// <summary>
    /// 加入一条进度。返回 <see langword="true"/> 时，调用方需要安排一次刷新。
    /// </summary>
    public bool Enqueue(QueueWorkerProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        lock (_lock)
        {
            var pending = new PendingProgress(++_sequence, progress);
            if (QueueStepLogFilters.RequiresLosslessUiDelivery(progress.StepKey) ||
                IsCriticalUiNotification(progress.Message))
                _lossless.Add(pending);
            else
                _latestByKey[BuildKey(progress)] = pending;

            if (_flushScheduled)
                return false;

            _flushScheduled = true;
            return true;
        }
    }

    /// <summary>取出当前批次，并按消息实际到达顺序返回。</summary>
    public IReadOnlyList<QueueWorkerProgress> Drain()
    {
        lock (_lock)
        {
            var batch = _lossless
                .Concat(_latestByKey.Values)
                .OrderBy(entry => entry.Sequence)
                .Select(entry => entry.Progress)
                .ToArray();

            _lossless.Clear();
            _latestByKey.Clear();
            _flushScheduled = false;
            return batch;
        }
    }

    private static string BuildKey(QueueWorkerProgress progress)
    {
        var workspace = progress.WorkspaceRoot ?? "";
        var project = progress.Item?.ProjectDir ?? "";
        var step = progress.StepKey ?? "";
        return $"{workspace}|{project}|{step}";
    }

    private static bool IsCriticalUiNotification(string? message)
    {
        var text = message ?? "";
        return text.Contains("单日创建剧集上限", StringComparison.Ordinal) ||
               text.Contains("任务队列已停止", StringComparison.Ordinal);
    }

    private readonly record struct PendingProgress(long Sequence, QueueWorkerProgress Progress);
}
