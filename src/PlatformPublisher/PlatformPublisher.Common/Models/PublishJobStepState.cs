namespace PlatformPublisher.Common.Models;

public enum PublishJobStepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

public sealed class PublishJobStepState
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public PublishJobStepStatus Status { get; set; } = PublishJobStepStatus.Pending;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
