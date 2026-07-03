namespace TikTokPublisher.Core.Media;

public static class MediaBinaryResolver
{
    public static string ResolveFfmpeg() => Resolve("ffmpeg");
    public static string ResolveFfprobe() => Resolve("ffprobe");

    private static string Resolve(string name)
    {
        var exe = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate)) return candidate;
        }
        return exe;
    }
}
