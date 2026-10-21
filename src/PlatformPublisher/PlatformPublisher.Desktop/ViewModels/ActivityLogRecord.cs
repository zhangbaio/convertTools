using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed class ActivityLogRecord
{
    public string Text { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public PublishPlatform Platform { get; init; }
}
