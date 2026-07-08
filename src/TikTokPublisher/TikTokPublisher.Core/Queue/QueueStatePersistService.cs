namespace TikTokPublisher.Core.Queue;

/// <summary>
/// 后台合并落盘队列项目状态（对齐 Python <c>queue_state_persist.py</c>）。
/// UI 线程只 enqueue 内存快照，避免每次项目更新都在主线程全量写库。
/// </summary>
public sealed class QueueStatePersistService : IDisposable
{
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _workAvailable = new(false);
    private readonly Thread _thread;
    private readonly TimeSpan _batchInterval;
    private volatile bool _disposed;
    private Action<string>? _onPersisted;

    private readonly Dictionary<string, PendingWorkspace> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeWorkspaces = new(StringComparer.OrdinalIgnoreCase);

    public QueueStatePersistService(TimeSpan? batchInterval = null)
    {
        _batchInterval = batchInterval ?? TimeSpan.FromMilliseconds(300);
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "queue-state-persist",
        };
        _thread.Start();
    }

    public void SetOnPersisted(Action<string>? handler) => _onPersisted = handler;

    public void Enqueue(string workspaceRoot, IReadOnlyList<QueueProjectItem> items, QueueRunOptions? options = null)
    {
        if (_disposed) return;
        var workspaceKey = NormalizeWorkspace(workspaceRoot);
        if (string.IsNullOrEmpty(workspaceKey)) return;

        lock (_lock)
        {
            if (!_pending.TryGetValue(workspaceKey, out var pending))
                pending = _pending[workspaceKey] = new PendingWorkspace();
            pending.Items = items.ToArray();
            if (options is not null)
                pending.Options = options.Clone();
            _workAvailable.Set();
        }
    }

    public bool Flush(string workspaceRoot, TimeSpan timeout)
    {
        if (_disposed) return true;
        var workspaceKey = NormalizeWorkspace(workspaceRoot);
        if (string.IsNullOrEmpty(workspaceKey)) return true;

        var deadline = DateTime.UtcNow + timeout;
        lock (_lock)
            _workAvailable.Set();

        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (!_pending.ContainsKey(workspaceKey) && !_activeWorkspaces.Contains(workspaceKey))
                    return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    public bool HasPending(string workspaceRoot)
    {
        var workspaceKey = NormalizeWorkspace(workspaceRoot);
        lock (_lock)
            return _pending.ContainsKey(workspaceKey) || _activeWorkspaces.Contains(workspaceKey);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workAvailable.Set();
        try
        {
            if (_thread.IsAlive)
                _thread.Join(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Ignore shutdown join failures.
        }

        _workAvailable.Dispose();
    }

    private void RunLoop()
    {
        while (!_disposed)
        {
            _workAvailable.Wait(50);
            if (_disposed) break;

            Dictionary<string, PendingWorkspace> batch;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _workAvailable.Reset();
                    continue;
                }

                batch = new Dictionary<string, PendingWorkspace>(_pending, StringComparer.OrdinalIgnoreCase);
                _pending.Clear();
                foreach (var workspaceKey in batch.Keys)
                    _activeWorkspaces.Add(workspaceKey);
            }

            foreach (var (workspaceKey, pending) in batch)
            {
                try
                {
                    if (pending.Items is null || pending.Items.Length == 0)
                        continue;
                    var options = pending.Options ?? WorkspaceQueueService.LoadRunOptions(workspaceKey);
                    var items = CloneQueueItems(pending.Items);
                    WorkspaceQueueService.SaveProjects(workspaceKey, items, options.ToPersistentDictionary());
                    var callback = _onPersisted;
                    if (callback is not null)
                    {
                        var capturedKey = workspaceKey;
                        ThreadPool.QueueUserWorkItem(_ => callback(capturedKey));
                    }
                }
                catch
                {
                    // Persistence must not break queue execution.
                }
                finally
                {
                    lock (_lock)
                        _activeWorkspaces.Remove(workspaceKey);
                }
            }

            lock (_lock)
            {
                if (_pending.Count == 0)
                    _workAvailable.Reset();
            }

            if (_batchInterval > TimeSpan.Zero)
                Thread.Sleep(_batchInterval);
        }
    }

    private static string NormalizeWorkspace(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return "";
        try
        {
            return Path.GetFullPath(workspaceRoot.Trim());
        }
        catch
        {
            return workspaceRoot.Trim();
        }
    }

    private static List<QueueProjectItem> CloneQueueItems(IEnumerable<QueueProjectItem> items)
    {
        var cloned = new List<QueueProjectItem>();
        foreach (var item in items)
            cloned.Add(CloneQueueItemWithRetry(item));
        return cloned;
    }

    private static QueueProjectItem CloneQueueItemWithRetry(QueueProjectItem item)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return QueueProjectItem.FromPayload(item.ToPayload());
            }
            catch when (attempt < 2)
            {
                Thread.Sleep(5);
            }
        }

        return QueueProjectItem.FromPayload(item.ToPayload());
    }

    private sealed class PendingWorkspace
    {
        public QueueProjectItem[]? Items;
        public QueueRunOptions? Options;
    }
}
