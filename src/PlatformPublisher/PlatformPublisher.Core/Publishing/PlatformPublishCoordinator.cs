using PlatformPublisher.Core.Models;

namespace PlatformPublisher.Core.Publishing;

public sealed class PlatformPublishCoordinator
{
    private readonly IReadOnlyDictionary<PublishPlatform, IPlatformPublishAdapter> _adapters;

    public PlatformPublishCoordinator(IEnumerable<IPlatformPublishAdapter> adapters)
    {
        _adapters = adapters.ToDictionary(adapter => adapter.Platform);
    }

    public IPlatformPublishAdapter GetAdapter(PublishPlatform platform) =>
        _adapters.TryGetValue(platform, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"未注册平台适配器：{platform.DisplayName()}");
}
