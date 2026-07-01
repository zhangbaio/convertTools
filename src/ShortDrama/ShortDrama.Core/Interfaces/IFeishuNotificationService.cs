using ShortDrama.Core.Models;

namespace ShortDrama.Core.Interfaces;

public interface IFeishuNotificationService
{
    Task SendTextAsync(
        FeishuNotificationSettings settings,
        string title,
        string message,
        CancellationToken cancellationToken);

    Task SendTextWithOptionalImageAsync(
        FeishuNotificationSettings settings,
        string title,
        string message,
        string? imagePath,
        CancellationToken cancellationToken);
}
