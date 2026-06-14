using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Notifications;

public sealed class NoopWeixinLoginNotificationService : IWeixinLoginNotificationService
{
    public static NoopWeixinLoginNotificationService Instance { get; } = new();

    public Task NotifyLoginRequiredAsync(
        WeixinLoginNotificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
