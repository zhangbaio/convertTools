using System.Runtime.ExceptionServices;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed record QueueExecutionTarget(WorkspaceQueueTarget Target, TikTokAccountProfile Account);

public sealed record PreparedQueueExecution(
    QueueExecutionTarget Execution,
    QueueRunOptions Options,
    List<QueueProjectItem> Items,
    IReadOnlyList<QueueProjectItem> SelectedItems,
    IReadOnlyList<QueueProjectItem> RepairedItems,
    int SelectedRunCount,
    ExceptionDispatchInfo? ValidationError);

/// <summary>Validates and reserves workspace commands through preparation, run and terminal persistence.</summary>
public sealed class QueueExecutionUseCase
{
    private readonly WorkspaceQueueOrchestrator _runner;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public QueueExecutionUseCase(WorkspaceQueueOrchestrator runner) => _runner = runner;

    public async Task<T> InSessionAsync<T>(IReadOnlyList<WorkspaceQueueTarget> targets, AccountStore accounts,
        Func<IReadOnlyList<QueueExecutionTarget>, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var resolved = targets.Select(target => ResolveTarget(target, accounts)).ToArray();
        var roots = resolved.Select(target => target.Target.WorkspaceRoot).ToArray();
        if (roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length)
            throw new InvalidOperationException("执行目标中包含重复工作目录，已取消启动");

        var sessions = resolved.Select(target => new Session(target.Account.Id, ct)).ToArray();
        try
        {
            lock (_gate)
            {
                var busy = roots.FirstOrDefault(root => _sessions.ContainsKey(root) || _runner.IsWorkspaceOccupied(root));
                if (busy is not null)
                    throw new InvalidOperationException($"目标工作目录队列正在准备、运行、收尾或删除项目：{busy}");

                for (var index = 0; index < roots.Length; index++)
                    _sessions.Add(roots[index], sessions[index]);
            }

            var token = sessions.Length == 1 ? sessions[0].Token : ct;
            return await execute(resolved, token).ConfigureAwait(true);
        }
        finally
        {
            lock (_gate)
            {
                for (var index = 0; index < roots.Length; index++)
                {
                    if (_sessions.TryGetValue(roots[index], out var current) && ReferenceEquals(current, sessions[index]))
                        _sessions.Remove(roots[index]);
                }
            }
            foreach (var session in sessions)
                session.Dispose();
        }
    }

    internal static QueueExecutionTarget ResolveTarget(WorkspaceQueueTarget target, AccountStore accounts)
    {
        var id = target.AccountProfileId?.Trim();
        if (string.IsNullOrEmpty(id))
            id = accounts.ActiveAccountId;
        var account = accounts.Accounts.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal));
        if (account is null)
            throw new InvalidOperationException($"队列账号不存在，请重新选择账号：{id}");

        if (string.IsNullOrWhiteSpace(target.WorkspaceRoot))
            throw new InvalidOperationException("请先为账号配置上传工作目录");
        var root = NormalizePath(target.WorkspaceRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"工作目录不存在：{root}");
        var accountWorkspace = account.ResolveWorkspacePath();
        if (!string.IsNullOrWhiteSpace(accountWorkspace) &&
            !string.Equals(root, NormalizePath(accountWorkspace), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"目标工作目录与账号「{account.DisplayName}」的配置不一致：{root}");

        string[]? filter = null;
        if (target.ProjectDirFilter is not null)
        {
            filter = target.ProjectDirFilter.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (filter.Length == 0)
                throw new InvalidOperationException("执行项目筛选为空，已取消启动队列。");
            if (filter.Any(path => !WorkspaceQueueService.IsProjectInsideWorkspace(root, path)))
                throw new InvalidOperationException("执行项目不属于目标工作目录。");
        }

        return new(target with { WorkspaceRoot = root, AccountProfileId = account.Id, ProjectDirFilter = filter }, account);
    }

    public async Task<PreparedQueueExecution?> PrepareAsync(QueueExecutionTarget execution, QueueRunOptions options,
        AccountStore accounts, IReadOnlyList<QueueProjectItem>? displayedSnapshot, CancellationToken ct,
        bool skipIdleWorkspace = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(SessionToken(execution), ct);
        if (execution.Target.PreserveCompletedSteps && options.ForceRerunCompletedSteps)
        {
            options = options.Clone();
            options.ForceRerunCompletedSteps = false;
        }
        return await Task.Run(() => Prepare(execution, options, accounts, displayedSnapshot, linked.Token, skipIdleWorkspace), linked.Token)
            .ConfigureAwait(false);
    }

    private static PreparedQueueExecution? Prepare(QueueExecutionTarget execution, QueueRunOptions options,
        AccountStore accounts, IReadOnlyList<QueueProjectItem>? displayedSnapshot, CancellationToken ct, bool skipIdleWorkspace)
    {
        var target = execution.Target;
        var root = target.WorkspaceRoot;
        var filter = target.ProjectDirFilter?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = WorkspaceQueueService.WithoutDeletedProjects(root,
            WorkspaceQueueService.ResolveExecutionSnapshot(root, displayedSnapshot, target.PreferPersistedQueueSnapshot));
        if (items.Count == 0 && filter is null)
            return null;

        ct.ThrowIfCancellationRequested();
        var selected = items.Where(item => item.Enabled && !item.Archived &&
            (filter is null || filter.Contains(NormalizePath(item.ProjectDir)))).ToArray();
        if (selected.Length == 0 && filter is null && skipIdleWorkspace)
            return null;
        if (selected.Length == 0)
            throw new InvalidOperationException($"目标工作目录未匹配到可执行项目，已取消启动队列：{root}");

        var repaired = new List<QueueProjectItem>();
        foreach (var item in selected)
        {
            if (QueueAccountBindingResolver.RepairForWorkspaceDefault(accounts, item, execution.Account))
                repaired.Add(item);
        }

        try
        {
            var count = QueueWorkerRunner.ValidateRunSelection(items, options, accounts, target.ProjectDirFilter);
            if (count == 0 && filter is null && skipIdleWorkspace)
                return null;
            if (count == 0)
                throw new InvalidOperationException("当前步骤没有可继续执行的项目");
            return new(execution, options, items, selected, repaired, count, null);
        }
        catch (Exception ex)
        {
            // Retain repaired bindings even if the command has no runnable items.
            return new(execution, options, items, selected, repaired, 0, ExceptionDispatchInfo.Capture(ex));
        }
    }

    public Task<QueueWorkerSummary?> StartPreparedAsync(PreparedQueueExecution prepared, AccountStore accounts,
        IQueuePublishHost host, FinalAction fallbackFinalAction,
        Action<QueueWorkerProgress> onProgress, Action<string, IReadOnlyList<QueueProjectItem>> onPersist,
        CancellationToken ct, Action? onStarted = null)
    {
        var target = prepared.Execution.Target;
        prepared.ValidationError?.Throw();
        return StartCoreAsync(target, prepared, accounts, host, fallbackFinalAction, onProgress, onPersist,
            SessionToken(prepared.Execution), ct, onStarted);
    }

    private CancellationToken SessionToken(QueueExecutionTarget execution)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(execution.Target.WorkspaceRoot, out var session) || session.AccountId != execution.Account.Id)
                throw new InvalidOperationException("队列启动请求已结束，请重新执行");
            return session.Token;
        }
    }

    private async Task<QueueWorkerSummary?> StartCoreAsync(WorkspaceQueueTarget target, PreparedQueueExecution prepared,
        AccountStore accounts, IQueuePublishHost host, FinalAction fallbackFinalAction, Action<QueueWorkerProgress> onProgress,
        Action<string, IReadOnlyList<QueueProjectItem>> onPersist, CancellationToken sessionToken, CancellationToken ct, Action? onStarted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, ct);
        linked.Token.ThrowIfCancellationRequested();
        var label = string.IsNullOrWhiteSpace(target.DisplayLabel)
            ? $"{prepared.Execution.Account.DisplayName} · {target.WorkspaceRoot}"
            : target.DisplayLabel;
        return await _runner.RunWorkspaceAsync(target.WorkspaceRoot, prepared.Items, prepared.Options, host, accounts,
            target.FinalActionOverride ?? fallbackFinalAction, label, onProgress, onPersist, linked.Token,
            target.ProjectDirFilter, onStarted, selectionValidated: true).ConfigureAwait(false);
    }

    public bool TryStopWorkspace(string workspaceRoot, string? accountId, Action beforeStop)
    {
        if (!TryNormalizePath(workspaceRoot, out var root))
            return false;
        Session? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(root, out session) || session.AccountId != accountId)
                return false;
        }

        try { beforeStop(); }
        finally { session.Cancel(); }
        return true;
    }

    public bool TryStopAll(Action beforeStop)
    {
        Session[] active;
        lock (_gate)
            active = _sessions.Values.ToArray();
        if (active.Length == 0)
            return false;

        try { beforeStop(); }
        finally
        {
            foreach (var session in active)
                session.Cancel();
        }
        return true;
    }

    public IDisposable? TryReserveWorkspaceForDeletion(string workspaceRoot)
    {
        var root = NormalizePath(workspaceRoot);
        lock (_gate)
            return _sessions.ContainsKey(root) ? null : _runner.TryReserveWorkspaceForDeletion(root);
    }

    public bool IsBusy(string workspaceRoot)
    {
        if (!TryNormalizePath(workspaceRoot, out var root))
            return false;
        lock (_gate)
            return _sessions.ContainsKey(root) || _runner.IsWorkspaceOccupied(root);
    }

    public bool IsActive(string workspaceRoot)
    {
        if (!TryNormalizePath(workspaceRoot, out var root))
            return false;
        lock (_gate)
            return _sessions.ContainsKey(root);
    }

    public IReadOnlyList<string> ActiveWorkspaces()
    {
        lock (_gate)
            return _sessions.Keys.ToArray();
    }

    internal bool IsOwnedBy(string workspaceRoot, string accountId)
    {
        lock (_gate)
            return _sessions.TryGetValue(NormalizePath(workspaceRoot), out var session) && session.AccountId == accountId;
    }

    public async Task<bool> WaitForIdleAsync(string workspaceRoot, TimeSpan timeout)
    {
        var root = NormalizePath(workspaceRoot);
        var deadline = DateTime.UtcNow + timeout;
        while (IsBusy(root))
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;
            Task? completion;
            lock (_gate)
                completion = _sessions.GetValueOrDefault(root)?.Completion;
            var delay = Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
            if (completion is null)
                await delay.ConfigureAwait(true);
            else
                await Task.WhenAny(completion, delay).ConfigureAwait(true);
        }
        return true;
    }

    internal static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool TryNormalizePath(string path, out string root)
    {
        root = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try { root = NormalizePath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return true;
    }

    private sealed class Session(string accountId, CancellationToken parent) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token => _cancellation.Token;
        public string AccountId { get; } = accountId;
        public Task Completion => _completed.Task;

        public void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            _completed.TrySetResult();
        }
    }
}
