namespace TikTokPublisher.Core.Services;

public static class FfmpegLocator
{
    public static string ResolveFfmpeg()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var fullPath = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (File.Exists(bundled))
            return bundled;

        throw new InvalidOperationException("未找到 ffmpeg，无法从视频抽帧生成封面。");
    }
}
