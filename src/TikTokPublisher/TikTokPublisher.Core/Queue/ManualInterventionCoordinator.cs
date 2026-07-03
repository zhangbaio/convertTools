namespace TikTokPublisher.Core.Queue;

/// <summary>人工介入结果。</summary>
public enum ManualInterventionResult
{
    /// <summary>未触发人工介入（未启用 / 已停止 / 已处理过）。</summary>
    NotHandled,
    /// <summary>用户标记为成功。</summary>
    Success,
    /// <summary>用户标记为失败。</summary>
    Failed,
    /// <summary>等待期间被停止。</summary>
    Stopped,
}

/// <summary>
/// 人工介入调度器（对齐 Python <c>_wait_for_manual_intervention_if_available</c>）：
/// 单项目上传失败时暂停等待用户在 UI 上标记「成功 / 失败」。
/// </summary>
public sealed class ManualInterventionCoordinator
{
    private readonly object _lock = new();
    private readonly HashSet<string> _resolvedProjectDirs = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<string>? _pending;
    private string _pendingProjectDir = "";
    private QueueProjectItem? _pendingItem;
    private string _pendingErrorMessage = "";

    /// <summary>当前是否有等待用户处理的项目。</summary>
    public bool HasPending
    {
        get
        {
            lock (_lock) return _pending is not null;
        }
    }

    public QueueProjectItem? PendingItem
    {
        get
        {
            lock (_lock) return _pendingItem;
        }
    }

    public string PendingErrorMessage
    {
        get
        {
            lock (_lock) return _pendingErrorMessage;
        }
    }

    /// <summary>UI 侧调用：标记当前等待项目「成功 / 失败」。返回是否命中一个等待中的项目。</summary>
    public bool Resolve(string action)
    {
        var normalized = (action ?? "").Trim().ToLowerInvariant();
        if (normalized is not ("success" or "failed")) return false;
        lock (_lock)
        {
            var pending = _pending;
            if (pending is null) return false;
            _pending = null;
            _pendingItem = null;
            _pendingProjectDir = "";
            _pendingErrorMessage = "";
            pending.TrySetResult(normalized);
            return true;
        }
    }

    /// <summary>已被 UI 处理过的项目不再重复触发人工介入。</summary>
    public bool WasResolved(string projectDir)
    {
        lock (_lock) return _resolvedProjectDirs.Contains(projectDir);
    }

    public event Action<QueueProjectItem, string>? PendingChanged;

    /// <summary>Worker 侧调用：注册一个待人工介入的项目并异步等待。</summary>
    public async Task<ManualInterventionResult> AwaitAsync(
        QueueProjectItem item,
        string errorMessage,
        CancellationToken ct)
    {
        if (item is null) return ManualInterventionResult.NotHandled;
        if (WasResolved(item.ProjectDir)) return ManualInterventionResult.NotHandled;

        TaskCompletionSource<string> tcs;
        lock (_lock)
        {
            if (_pending is not null) return ManualInterventionResult.NotHandled;
            tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            _pendingItem = item;
            _pendingProjectDir = item.ProjectDir;
            _pendingErrorMessage = errorMessage ?? "";
        }

        PendingChanged?.Invoke(item, errorMessage ?? "");

        try
        {
            await using var _ = ct.Register(() => tcs.TrySetCanceled(ct));
            string action;
            try
            {
                action = await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_pending, tcs))
                    {
                        _pending = null;
                        _pendingItem = null;
                        _pendingProjectDir = "";
                        _pendingErrorMessage = "";
                    }
                    _resolvedProjectDirs.Add(item.ProjectDir);
                }
                PendingChanged?.Invoke(item, "");
                return ManualInterventionResult.Stopped;
            }

            lock (_lock) _resolvedProjectDirs.Add(item.ProjectDir);
            PendingChanged?.Invoke(item, "");
            return action switch
            {
                "success" => ManualInterventionResult.Success,
                "failed" => ManualInterventionResult.Failed,
                _ => ManualInterventionResult.NotHandled,
            };
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_pending, tcs))
                {
                    _pending = null;
                    _pendingItem = null;
                    _pendingProjectDir = "";
                    _pendingErrorMessage = "";
                }
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _pending?.TrySetCanceled();
            _pending = null;
            _pendingItem = null;
            _pendingProjectDir = "";
            _pendingErrorMessage = "";
            _resolvedProjectDirs.Clear();
        }
    }
}
