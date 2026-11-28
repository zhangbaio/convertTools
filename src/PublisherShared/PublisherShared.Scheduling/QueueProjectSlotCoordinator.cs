namespace PublisherShared.Scheduling;

/// <summary>
/// 本机所有账号队列共享的剧集运行名额。一部剧从开始执行到上传结束、失败或停止都占 1 个名额，
/// 已经完成的剧不占。单个账号最多同时跑几部由该账号发布配置里的项目并发数决定，
/// 达到上限的队列不再登记需求，也就不会挡住后面的账号。
/// </summary>
public sealed class QueueProjectSlotCoordinator
{
    public const string AccountOrderMode = "account_order";
    public const string RoundRobinMode = "round_robin";
    public const string ServerFillFirstMode = "fill_first";
    public const int DefaultCapacity = 4;
    public const int MaxCapacity = 12;

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<object, IReadOnlyList<QueueProjectSlotDemand>> _demand =
        new(ReferenceEqualityComparer.Instance);
    private int _capacity = DefaultCapacity;
    private string _mode = AccountOrderMode;

    public static string NormalizeMode(string? mode) =>
        string.Equals((mode ?? "").Trim(), RoundRobinMode, StringComparison.OrdinalIgnoreCase)
            ? RoundRobinMode
            : AccountOrderMode;

    /// <summary>
    /// 服务端分发模式。空值和未知值不当成一次更新；调用方没收到模式时按轮流处理。
    /// </summary>
    public static string NormalizeServerDispatchMode(string? mode)
    {
        var text = (mode ?? "").Trim();
        if (text.Length == 0)
            return "";
        if (string.Equals(text, ServerFillFirstMode, StringComparison.OrdinalIgnoreCase))
            return ServerFillFirstMode;
        if (string.Equals(text, RoundRobinMode, StringComparison.OrdinalIgnoreCase))
            return RoundRobinMode;
        return "";
    }

    /// <summary>按账号分发时前面的账号先占名额，轮流分发时占得少的账号先拿。没收到模式时按轮流。</summary>
    public static string SlotModeForServer(string? serverDispatchMode) =>
        string.Equals(NormalizeServerDispatchMode(serverDispatchMode), ServerFillFirstMode, StringComparison.OrdinalIgnoreCase)
            ? AccountOrderMode
            : RoundRobinMode;

    public static bool ServerUsesRoundRobin(string? serverDispatchMode) =>
        SlotModeForServer(serverDispatchMode) == RoundRobinMode;

    public static int NormalizeCapacity(int capacity) =>
        Math.Clamp(capacity <= 0 ? DefaultCapacity : capacity, 1, MaxCapacity);

    public void Configure(int capacity, string? mode)
    {
        lock (_lock)
        {
            _capacity = NormalizeCapacity(capacity);
            _mode = NormalizeMode(mode);
        }
    }

    public int Capacity
    {
        get { lock (_lock) return _capacity; }
    }

    public int ActiveCount
    {
        get { lock (_lock) return _active.Values.Sum(); }
    }

    public int ActiveFor(string? accountKey)
    {
        lock (_lock) return _active.GetValueOrDefault(Normalize(accountKey));
    }

    /// <summary>登记某个队列此刻还能马上开始、但还没拿到名额的剧。</summary>
    public void SetDemand(object owner, IReadOnlyList<QueueProjectSlotDemand> demand)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_lock)
        {
            if (demand.Count == 0)
                _demand.Remove(owner);
            else
                _demand[owner] = demand
                    .Select(d => d with { AccountKey = Normalize(d.AccountKey) })
                    .ToArray();
        }
    }

    public void ClearDemand(object owner)
    {
        lock (_lock) _demand.Remove(owner);
    }

    /// <summary>
    /// 按账号顺序时，排在前面且还有剧在等的账号先拿名额，后面的账号只用前面账号用不上的名额。
    /// 轮流时，当前占得最少的账号先拿。
    /// </summary>
    public bool TryAcquire(string? accountKey, int accountOrder)
    {
        var key = Normalize(accountKey);
        lock (_lock)
        {
            if (_active.Values.Sum() >= _capacity)
                return false;
            var mine = _active.GetValueOrDefault(key);
            foreach (var other in AggregateDemand())
            {
                if (string.Equals(other.AccountKey, key, StringComparison.OrdinalIgnoreCase) || other.Count <= 0)
                    continue;
                var otherActive = _active.GetValueOrDefault(other.AccountKey);
                if (_mode == AccountOrderMode ? other.AccountOrder < accountOrder : otherActive < mine)
                    return false;
            }

            _active[key] = mine + 1;
            return true;
        }
    }

    public void Release(string? accountKey)
    {
        var key = Normalize(accountKey);
        lock (_lock)
        {
            var current = _active.GetValueOrDefault(key);
            if (current <= 1)
                _active.Remove(key);
            else
                _active[key] = current - 1;
        }
    }

    private IEnumerable<QueueProjectSlotDemand> AggregateDemand() =>
        _demand.Values
            .SelectMany(list => list)
            .GroupBy(d => d.AccountKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new QueueProjectSlotDemand(
                group.Key,
                group.Min(d => d.AccountOrder),
                group.Sum(d => d.Count)));

    private static string Normalize(string? accountKey) =>
        string.IsNullOrWhiteSpace(accountKey) ? "default" : accountKey.Trim();
}

public sealed record QueueProjectSlotDemand(string AccountKey, int AccountOrder, int Count);
