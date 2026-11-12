namespace TikTokPublisher.Core.Queue;

/// <summary>
/// 本机同时上传的剧数。同一个账号可以同时占多个名额，这些剧共用一个浏览器的多个标签页。
/// 已经在上传的账号还有下一部可以传时，别的账号先不开新浏览器。
/// </summary>
public sealed class UploadSlotCoordinator
{
    public const int MaxCapacity = 8;

    private readonly Dictionary<string, int> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<object, IReadOnlyList<UploadReadyDemand>> _ready =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _lock = new();
    private int _capacity;
    private string _lastAccount = "";

    /// <summary>0 表示不限制全机同时上传数。</summary>
    public void Configure(int capacity)
    {
        lock (_lock) _capacity = Math.Clamp(capacity, 0, MaxCapacity);
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

    /// <summary>登记某个队列里已经可以上传、但还没拿到上传名额的剧。</summary>
    public void SetReady(object owner, IReadOnlyList<UploadReadyDemand> ready)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_lock)
        {
            if (ready.Count == 0)
                _ready.Remove(owner);
            else
                _ready[owner] = ready
                    .Select(item => item with { AccountKey = Normalize(item.AccountKey) })
                    .ToArray();
        }
    }

    public void ClearReady(object owner)
    {
        lock (_lock) _ready.Remove(owner);
    }

    public bool TryAcquire(string accountKey)
    {
        var key = Normalize(accountKey);
        lock (_lock)
        {
            var total = _active.Values.Sum();
            if (_capacity > 0 && total >= _capacity)
                return false;
            var mine = _active.GetValueOrDefault(key);
            if (mine == 0 && AnotherAccountShouldKeepTheSlot(key))
                return false;
            _active[key] = mine + 1;
            _lastAccount = key;
            return true;
        }
    }

    public void Release(string accountKey)
    {
        var key = Normalize(accountKey);
        lock (_lock)
        {
            var current = _active.GetValueOrDefault(key);
            if (current <= 1)
                _active.Remove(key);
            else
                _active[key] = current - 1;
            if (current > 0)
                _lastAccount = key;
        }
    }

    private bool AnotherAccountShouldKeepTheSlot(string key)
    {
        foreach (var pair in _active)
        {
            if (pair.Value <= 0 || string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ReadyWaiting(pair.Key) > 0)
                return true;
        }

        return _lastAccount.Length > 0
               && !string.Equals(_lastAccount, key, StringComparison.OrdinalIgnoreCase)
               && ReadyWaiting(_lastAccount) > 0;
    }

    private int ReadyWaiting(string accountKey)
    {
        var count = 0;
        foreach (var list in _ready.Values)
        {
            foreach (var item in list)
            {
                if (string.Equals(item.AccountKey, accountKey, StringComparison.OrdinalIgnoreCase))
                    count += Math.Max(0, item.Count);
            }
        }

        return count;
    }

    private static string Normalize(string? accountKey) =>
        string.IsNullOrWhiteSpace(accountKey) ? "default" : accountKey.Trim();
}

public readonly record struct UploadReadyDemand(string AccountKey, int Count);
