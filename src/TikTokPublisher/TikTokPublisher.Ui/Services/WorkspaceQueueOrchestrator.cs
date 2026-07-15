using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services;

public sealed record WorkspaceQueueRunInfo(
    string WorkspaceRoot,
    string DisplayLabel,
    bool IsRunning,
    int SuccessCount,
    int FailedCount,
    bool Stopped);

/// <summary>并行管理多个工作目录队列；上传槽位按账号全局共享。</summary>
public sealed class WorkspaceQueueOrchestrator
{
    private readonly UploadSlotCoordinator _sharedUploadSlots = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, ActiveRun> _runs = new(StringComparer.OrdinalIgnoreCase);

    public event Action<QueueProjectItem, string, string>? ManualInterventionPending;

    public bool AnyRunning
    {
        get
        {
            lock (_lock)
                return _runs.Values.Any(run => !run.Task.IsCompleted);
        }
    }

    public IReadOnlyList<WorkspaceQueueRunInfo> Snapshot()
    {
        lock (_lock)
        {
            return _runs.Values
                .Select(run => new WorkspaceQueueRunInfo(
                    run.WorkspaceRoot,
                    run.DisplayLabel,
                    !run.Task.IsCompleted,
                    run.Summary?.SuccessCount ?? 0,
                    run.Summary?.FailedCount ?? 0,
                    run.Summary?.Stopped ?? false))
                .OrderBy(info => info.WorkspaceRoot, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public async Task<QueueWorkerSummary?> RunWorkspaceAsync(
        string workspaceRoot,
        IList<QueueProjectItem> items,
        QueueRunOptions options,
        IQueuePublishHost host,
        AccountStore store,
        FinalAction finalAction,
        string displayLabel,
        Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct,
        IReadOnlyCollection<string>? projectDirFilter = null,
        Action? onStarted = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"工作目录不存在：{root}");

        CancellationTokenSource linkedCts;
        ActiveRun run;
        lock (_lock)
        {
            if (_runs.TryGetValue(root, out var existing) && !existing.Task.IsCompleted)
                throw new InvalidOperationException($"工作目录队列已在运行：{root}");

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var runner = new QueueWorkerRunner(_sharedUploadSlots);
            runner.ManualIntervention.PendingChanged += (item, message) =>
                ManualInterventionPending?.Invoke(item, message, root);

            var task = Task.Run(
                () => ExecuteRunAsync(
                    runner,
                    root,
                    items,
                    options,
                    host,
                    store,
                    finalAction,
                    onProgress,
                    onPersist,
                    linkedCts.Token,
                    projectDirFilter),
                linkedCts.Token);

            run = new ActiveRun(root, displayLabel, runner, linkedCts, task);
            _runs[root] = run;
        }

        try
        {
            // 此时 ActiveRun 已在锁内注册，远程命令可安全返回并允许后续命令追加项目。
            // 启动通知只用于握手，通知方异常不能中断或遗失已经启动的 worker。
            try { onStarted?.Invoke(); }
            catch { }
            return await run.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (_runs.TryGetValue(root, out var current) && ReferenceEquals(current, run))
                    _runs.Remove(root);
            }

            run.Cts.Dispose();
            run.Runner.ManualIntervention.Reset();
        }
    }

    public async Task<IReadOnlyList<QueueWorkerSummary?>> RunWorkspacesAsync(
        IReadOnlyList<WorkspaceQueueTarget> targets,
        IQueuePublishHost host,
        AccountStore store,
        FinalAction finalAction,
        Func<WorkspaceQueueTarget, QueueRunOptions> optionsFactory,
        Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct,
        IReadOnlyCollection<string>? projectDirFilter = null)
    {
        var tasks = targets
            .Select(target =>
            {
                var items = WorkspaceQueueService.ScanProjects(target.WorkspaceRoot).ToList();
                var options = optionsFactory(target);
                return RunWorkspaceAsync(
                    target.WorkspaceRoot,
                    items,
                    options,
                    host,
                    store,
                    finalAction,
                    target.DisplayLabel,
                    onProgress,
                    onPersist,
                    ct);
            })
            .ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void StopWorkspace(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        lock (_lock)
        {
            if (_runs.TryGetValue(root, out var run))
                run.Cts.Cancel();
        }
    }

    public void StopAll()
    {
        lock (_lock)
        {
            foreach (var run in _runs.Values)
                run.Cts.Cancel();
        }
    }

    public int TryAppendItemsToRunningWorkspace(string workspaceRoot, IEnumerable<QueueProjectItem> items)
    {
        var root = Path.GetFullPath(workspaceRoot);
        lock (_lock)
        {
            if (_runs.TryGetValue(root, out var run) && !run.Task.IsCompleted)
                return run.Runner.AddItems(items);
        }

        return 0;
    }

    public bool ResolveManualIntervention(string action, string? workspaceRoot = null)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                var root = Path.GetFullPath(workspaceRoot);
                return _runs.TryGetValue(root, out var run) && run.Runner.ManualIntervention.Resolve(action);
            }

            foreach (var run in _runs.Values)
            {
                if (run.Runner.ManualIntervention.Resolve(action))
                    return true;
            }
        }

        return false;
    }

    public bool HasManualInterventionPending
    {
        get
        {
            lock (_lock)
                return _runs.Values.Any(run => run.Runner.ManualIntervention.HasPending);
        }
    }

    private static async Task<QueueWorkerSummary?> ExecuteRunAsync(
        QueueWorkerRunner runner,
        string workspaceRoot,
        IList<QueueProjectItem> items,
        QueueRunOptions options,
        IQueuePublishHost host,
        AccountStore store,
        FinalAction finalAction,
        Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct,
        IReadOnlyCollection<string>? projectDirFilter = null)
    {
        try
        {
            return await runner.RunAsync(
                workspaceRoot,
                items,
                options,
                host,
                store,
                finalAction,
                onProgress,
                list => onPersist(workspaceRoot, list),
                ct,
                projectDirFilter).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new QueueWorkerSummary { Stopped = true };
        }
    }

    private sealed class ActiveRun
    {
        public ActiveRun(
            string workspaceRoot,
            string displayLabel,
            QueueWorkerRunner runner,
            CancellationTokenSource cts,
            Task<QueueWorkerSummary?> task)
        {
            WorkspaceRoot = workspaceRoot;
            DisplayLabel = displayLabel;
            Runner = runner;
            Cts = cts;
            Task = task;
        }

        public string WorkspaceRoot { get; }
        public string DisplayLabel { get; }
        public QueueWorkerRunner Runner { get; }
        public CancellationTokenSource Cts { get; }
        public Task<QueueWorkerSummary?> Task { get; }
        public QueueWorkerSummary? Summary => Task.IsCompletedSuccessfully ? Task.Result : null;
    }
}

public sealed record WorkspaceQueueTarget(string WorkspaceRoot, string DisplayLabel, string? AccountProfileId);
