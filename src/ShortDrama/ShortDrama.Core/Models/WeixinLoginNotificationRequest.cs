namespace ShortDrama.Core.Models;

public sealed record WeixinLoginNotificationRequest(
    string ProjectKey,
    string DisplayName,
    string ProjectDirectory,
    string BaseUrl,
    string? AuthFilePath,
    string? ScreenshotPath,
    string Message);
