namespace TikTokPublisher.Core.Services;

public static class TikTokVideoConstraints
{
    public const long MinSizeBytes = 5L * 1024 * 1024;
    public const long MaxSizeBytes = 4L * 1024 * 1024 * 1024;
    public const double MinDurationSeconds = 15.0;
    public const double MaxDurationSeconds = 20 * 60.0;
    public const long PaddingTargetBytes = MinSizeBytes + 128 * 1024;

    public static readonly HashSet<string> PaddingSupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov",
    };

    public const double DefaultMaxContinuousSilenceSeconds = 20.0;
    public const double DefaultSilenceThresholdDb = -45.0;
}
