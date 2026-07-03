namespace TikTokPublisher.Core.Queue;

/// <summary>同一 TikTok 账号同时只允许一个上传任务（对齐 Python <c>UploadSlotCoordinator</c>）。</summary>
public sealed class UploadSlotCoordinator
{
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool TryAcquire(string accountKey)
    {
        var key = Normalize(accountKey);
        lock (_lock)
        {
            if (_active.Contains(key)) return false;
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
