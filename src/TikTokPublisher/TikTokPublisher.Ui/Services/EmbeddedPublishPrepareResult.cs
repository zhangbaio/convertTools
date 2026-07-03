namespace TikTokPublisher.Ui.Services;

public sealed record EmbeddedPublishPrepareResult(bool Ok, string? CdpEndpoint, string Message);
