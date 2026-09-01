using PlatformPublisher.Core.Models;

namespace PlatformPublisher.Core.Publishing;

public interface IPlatformPublishAdapter
{
    PublishPlatform Platform { get; }
    bool IsAvailable { get; }
    string AvailabilityMessage { get; }

    Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken);

    Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken);
}
