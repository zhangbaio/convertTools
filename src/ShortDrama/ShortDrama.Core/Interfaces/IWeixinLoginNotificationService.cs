using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IWeixinLoginNotificationService
{
    Task NotifyLoginRequiredAsync(
        WeixinLoginNotificationRequest request,
        CancellationToken cancellationToken);
}
