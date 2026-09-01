namespace PlatformPublisher.Common.Models;

public sealed class PublishAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public PublishPlatform Platform { get; set; } = PublishPlatform.WeixinChannel;
    public string Name { get; set; } = string.Empty;
    public string BaseConfigPath { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
