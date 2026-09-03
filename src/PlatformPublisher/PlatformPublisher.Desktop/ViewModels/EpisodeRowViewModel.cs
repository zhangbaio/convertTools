using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed class EpisodeRowViewModel
{
    private static readonly Regex EpisodeRegex = new(@"第\s*0*(\d+)\s*集", RegexOptions.Compiled);

    public int EpisodeNumber { get; init; }
    public string EpisodeText => EpisodeNumber > 0 ? $"第{EpisodeNumber}集" : "未识别";
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string FileSizeText { get; init; } = "-";
    public string DurationText { get; init; } = "-";
    public string ResolutionText { get; init; } = "-";
    public string PreparationStatus { get; init; } = "已找到";
    public string UploadStatus { get; init; } = "待上传";
    public string AttemptText { get; init; } = "0 次";
    public string Message { get; init; } = string.Empty;

    public static int ParseEpisodeNumber(string path)
    {
        var match = EpisodeRegex.Match(Path.GetFileNameWithoutExtension(path));
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : int.MaxValue;
    }

    public static async Task<EpisodeRowViewModel> CreateAsync(
        string path,
        string uploadStatus,
        int attemptCount,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        var (duration, resolution, message) = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        var number = ParseEpisodeNumber(path);
        return new EpisodeRowViewModel
        {
            EpisodeNumber = number == int.MaxValue ? 0 : number,
            FileName = file.Name,
            FilePath = file.FullName,
            FileSizeText = FormatBytes(file.Length),
            DurationText = duration,
            ResolutionText = resolution,
            UploadStatus = uploadStatus,
            AttemptText = $"{attemptCount} 次",
            Message = message,
        };
    }

    private static async Task<(string Duration, string Resolution, string Message)> ProbeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo("ffprobe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-v", "error", "-select_streams", "v:0",
                         "-show_entries", "stream=width,height,duration:format=duration",
                         "-of", "json", path,
                     })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return ("-", "-", "无法启动 ffprobe");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) return ("-", "-", "视频信息读取失败");
            var root = JsonNode.Parse(output)?.AsObject();
            var stream = root?["streams"]?.AsArray().FirstOrDefault()?.AsObject();
            var width = stream?["width"]?.GetValue<int>() ?? 0;
            var height = stream?["height"]?.GetValue<int>() ?? 0;
            var secondsText = stream?["duration"]?.ToString() ?? root?["format"]?["duration"]?.ToString();
            _ = double.TryParse(secondsText, out var seconds);
            return (
                seconds > 0 ? TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss") : "-",
                width > 0 && height > 0 ? $"{width}×{height}" : "-",
                string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ("-", "-", ex.Message);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / 1024d:0.0} KB";
    }
}
