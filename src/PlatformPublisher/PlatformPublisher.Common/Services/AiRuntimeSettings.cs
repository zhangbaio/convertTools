namespace PlatformPublisher.Common.Services;

public sealed record AiRuntimeSettings(
    string Endpoint,
    string ApiKey,
    string Model,
    int TimeoutSeconds,
    string AsrLocalModelDirectory,
    string AsrVadPath,
    string AsrAppId,
    string AsrAccessToken,
    string AsrLanguage);

public interface IAiRuntimeSettingsProvider
{
    AiRuntimeSettings Load();
}

public sealed class EmptyAiRuntimeSettingsProvider : IAiRuntimeSettingsProvider
{
    public static EmptyAiRuntimeSettingsProvider Instance { get; } = new();

    public AiRuntimeSettings Load() => new(
        string.Empty,
        string.Empty,
        string.Empty,
        60,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        "zh-CN");
}
