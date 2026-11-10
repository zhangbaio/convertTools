namespace TikTokPublisher.Core.Queue;

/// <summary>
/// 同一 TikTok 账号同时只允许一个上传任务（对齐 Python <c>UploadSlotCoordinator</c>）。
/// 设置了全机上限后，所有账号同时上传的剧数也不超过上限。
/// </summary>
public sealed class UploadSlotCoordinator
{
    public const int MaxCapacity = 8;

    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private int _capacity;

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
        get { lock (_lock) return _active.Count; }
    }

    public bool TryAcquire(string accountKey)
    {
        var key = Normalize(accountKey);
        lock (_lock)
        {
            if (_active.Contains(key)) return false;
            if (_capacity > 0 && _active.Count >= _capacity) return false;
            _active.Add(key);
            return true;
        }
    }

    public void Release(string accountKey)
    {
        var key = Normalize(accountKey);
        lock (_lock) _active.Remove(key);
    }

    private static string Normalize(string accountKey) =>
        string.IsNullOrWhiteSpace(accountKey) ? "default" : accountKey.Trim();
}
