using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Common.Publishing;

public interface IPlatformPublishAdapter
{
    PublishPlatform Platform { get; }
    bool IsAvailable { get; }
    string AvailabilityMessage { get; }

    Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken);

    Task OpenLoginAsync(PublishJob job, CancellationToken cancellationToken);
}
