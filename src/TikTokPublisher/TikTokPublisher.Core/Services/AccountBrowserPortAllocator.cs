using System.Collections.Concurrent;

namespace TikTokPublisher.Core.Services;

/// <summary>为每个账号分配稳定的 WebView2 CDP 调试端口，避免多账号并行时端口冲突。</summary>
public static class AccountBrowserPortAllocator
{
    public const int BasePort = 9222;
    public const int PortSpan = 4000;

    private static readonly ConcurrentDictionary<string, int> AssignedPorts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, string> PortOwners = new();

    public static int Allocate(string accountId)
    {
        var key = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId.Trim();
        return AssignedPorts.GetOrAdd(key, _ => AllocateNewPort(key));
    }

    public static void Release(string accountId)
    {
        var key = string.IsNullOrWhiteSpace(accountId) ? "default" : accountId.Trim();
        if (!AssignedPorts.TryRemove(key, out var port))
            return;
        PortOwners.TryRemove(port, out _);
    }

    private static int AllocateNewPort(string accountId)
    {
        var seed = StableHash(accountId);
        for (var offset = 0; offset < PortSpan; offset++)
        {
            var port = BasePort + (int)((seed + (ulong)offset) % (ulong)PortSpan);
            if (PortOwners.TryAdd(port, accountId))
                return port;
        }

        throw new InvalidOperationException("无法为内置浏览器分配 CDP 端口（端口池已满）。");
    }

    private static ulong StableHash(string value)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037;
            const ulong prime = 1099511628211;
            var hash = offset;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= prime;
            }
            return hash;
        }
    }
}
