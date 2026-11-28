using PublisherShared.Scheduling;
namespace TikTokPublisher.Core.Queue;

/// <summary>
/// 一台电脑上的自动派单执行顺序。剧先全部进入各账号队列，再按服务端分发模式启动勾选队列。
/// 按账号分发时，同一时间只启动 1 个账号，这个账号里的剧再受全局同时运行数量限制。
/// 轮流分发时，同时启动的账号数不超过全局同时运行数量，每个账号先只跑 1 部。
/// </summary>
public sealed class TikTokDispatchQueueScheduler
{
    private readonly object _lock = new();
    private readonly List<Account> _accounts = [];

    public void Note(string workspace, string? accountId, int accountOrder, IEnumerable<string> projectDirs)
    {
        var root = Normalize(workspace);
        if (root.Length == 0)
            return;
        var id = (accountId ?? "").Trim();
        lock (_lock)
        {
            var account = FindOrAdd(root, id, accountOrder);
            foreach (var dir in projectDirs)
            {
                var full = Normalize(dir);
                if (full.Length == 0 || account.Pending.Contains(full) || account.Running.Contains(full))
                    continue;
                account.Pending.Add(full);
            }
        }
    }

    public IReadOnlyList<TikTokDispatchStart> Take(int globalConcurrency, bool roundRobin, Func<string, bool>? isBusy = null)
    {
        lock (_lock)
        {
            var limit = roundRobin
                ? Math.Clamp(globalConcurrency <= 0 ? QueueProjectSlotCoordinator.DefaultCapacity : globalConcurrency, 1, QueueProjectSlotCoordinator.MaxCapacity)
                : 1;
            var running = _accounts.Count(account => account.IsRunning);
            var room = limit - running;
            if (room <= 0)
                return [];

            var starts = new List<TikTokDispatchStart>();
            while (room > 0)
            {
                var next = _accounts
                    .Where(account => !account.IsRunning && account.Pending.Count > 0 && isBusy?.Invoke(account.Workspace) != true)
                    .OrderBy(account => roundRobin ? account.Started : 0)
                    .ThenBy(account => account.Order)
                    .ThenBy(account => account.AccountId, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (next is null)
                    break;

                var dirs = roundRobin
                    ? new List<string> { next.Pending[0] }
                    : next.Pending.ToList();
                foreach (var dir in dirs)
                    next.Pending.Remove(dir);
                next.Running.AddRange(dirs);
                next.IsRunning = true;
                next.Started++;
                starts.Add(new TikTokDispatchStart(next.Workspace, next.AccountId, dirs, roundRobin));
                room--;
            }

            return starts;
        }
    }

    public void DropPending(string workspace, IEnumerable<string> projectDirs)
    {
        var root = Normalize(workspace);
        var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in projectDirs)
        {
            var full = Normalize(dir);
            if (full.Length > 0)
                drop.Add(full);
        }

        if (root.Length == 0 || drop.Count == 0)
            return;

        lock (_lock)
        {
            var account = _accounts.FirstOrDefault(item => string.Equals(item.Workspace, root, StringComparison.OrdinalIgnoreCase));
            account?.Pending.RemoveAll(drop.Contains);
        }
    }

    public void ClearPending(string workspace)
    {
        var root = Normalize(workspace);
        if (root.Length == 0)
            return;

        lock (_lock)
        {
            var account = _accounts.FirstOrDefault(item => string.Equals(item.Workspace, root, StringComparison.OrdinalIgnoreCase));
            account?.Pending.Clear();
        }
    }

    public void Complete(string workspace)
    {
        var root = Normalize(workspace);
        lock (_lock)
        {
            var account = _accounts.FirstOrDefault(item => string.Equals(item.Workspace, root, StringComparison.OrdinalIgnoreCase));
            if (account is null || !account.IsRunning)
                return;
            account.IsRunning = false;
            account.Running.Clear();
        }
    }

    public void Restore(string workspace, IReadOnlyList<string> projectDirs)
    {
        var root = Normalize(workspace);
        lock (_lock)
        {
            var account = _accounts.FirstOrDefault(item => string.Equals(item.Workspace, root, StringComparison.OrdinalIgnoreCase));
            if (account is null)
                return;
            account.IsRunning = false;
            if (account.Started > 0)
                account.Started--;
            foreach (var dir in projectDirs.Reverse())
            {
                var full = Normalize(dir);
                account.Running.Remove(full);
                if (full.Length > 0 && !account.Pending.Contains(full))
                    account.Pending.Insert(0, full);
            }
            account.Running.Clear();
        }
    }

    private Account FindOrAdd(string workspace, string accountId, int accountOrder)
    {
        var account = _accounts.FirstOrDefault(item => string.Equals(item.Workspace, workspace, StringComparison.OrdinalIgnoreCase));
        if (account is not null)
        {
            if (accountOrder < account.Order)
                account.Order = accountOrder;
            if (account.AccountId.Length == 0)
                account.AccountId = accountId;
            return account;
        }

        account = new Account(workspace, accountId, accountOrder);
        _accounts.Add(account);
        return account;
    }

    private static string Normalize(string? path)
    {
        var text = (path ?? "").Trim();
        if (text.Length == 0)
            return "";
        try
        {
            return Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return text;
        }
    }

    private sealed class Account(string workspace, string accountId, int order)
    {
        public string Workspace { get; } = workspace;
        public string AccountId { get; set; } = accountId;
        public int Order { get; set; } = order;
        public int Started { get; set; }
        public bool IsRunning { get; set; }
        public List<string> Pending { get; } = [];
        public List<string> Running { get; } = [];
    }
}

public sealed record TikTokDispatchStart(
    string Workspace,
    string AccountId,
    IReadOnlyList<string> ProjectDirs,
    bool SingleDrama);
