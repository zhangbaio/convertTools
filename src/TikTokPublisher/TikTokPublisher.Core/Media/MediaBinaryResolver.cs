namespace TikTokPublisher.Core.Media;

public static class MediaBinaryResolver
{
    public static string ResolveFfmpeg() => Resolve("ffmpeg");
    public static string ResolveFfprobe() => Resolve("ffprobe");

    private static string Resolve(string name)
    {
        var bundled = ShortDrama.Infrastructure.BundledToolResolver.TryResolveBinary(name);
        if (bundled is not null)
        {
            return bundled;
        }

        return OperatingSystem.IsWindows() ? $"{name}.exe" : name;
    }
}
