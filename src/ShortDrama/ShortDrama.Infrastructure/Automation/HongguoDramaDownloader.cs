using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation;

public sealed class HongguoDramaDownloader : IDramaDownloader
{
    private const string DefaultQuality = "1080P+";
    private const int ChunkSize = 65_536;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(350);
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif"];
    private static readonly string[] PosterKeys =
    [
        "poster",
        "poster_url",
        "cover",
        "cover_url",
        "thumbnail",
        "thumbnail_url",
        "thumb",
        "thumb_url",
        "image",
        "image_url",
        "img",
        "img_url",
        "pic",
        "pic_url",
        "book_pic",
        "book_cover",
        "video_cover",
        "vertical_cover",
        "vertical_cover_url",
        "horizontal_cover",
        "horizontal_cover_url"
    ];

    private static readonly string[] PosterKeywords =
    [
        "poster",
        "cover",
        "thumb",
        "thumbnail",
        "image",
        "img",
        "pic"
    ];

    private static readonly Dictionary<string, string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
        ["image/bmp"] = ".bmp",
        ["image/heic"] = ".heic",
        ["image/heif"] = ".heif",
        ["image/heic-sequence"] = ".heic",
        ["image/heif-sequence"] = ".heif"
    };

    private static readonly ProductInfoHeaderValue UserAgentProduct = new("ShortDramaDesktop", "1.0");
    private static readonly string UserAgentString =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    private readonly HttpClient _httpClient;

    public HongguoDramaDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<DramaDownloadResult> DownloadAsync(
        DramaDownloadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BookId))
        {
            return new DramaDownloadResult(
                Ok: false,
                OutputDir: request.OutputDir,
                VideoCount: 0,
                Message: "缺少 book_id，无法执行下载。可在项目目录放置 shortdrama-project.json、pipeline-config.json 或 book_id.txt。");
        }

        Directory.CreateDirectory(request.OutputDir);

        var metadata = ProjectAutomationMetadata.Resolve(request.OutputDir);
        var displayName = string.IsNullOrWhiteSpace(metadata.Title) ? request.DisplayName : metadata.Title;
        var accessOptions = HongguoAccessOptionsResolver.Resolve(request.OutputDir);
        var client = new HongguoApiClient(_httpClient, accessOptions);

        progress?.Report($"开始下载《{displayName}》...");

        IReadOnlyList<EpisodeDownloadTask> tasks;
        try
        {
            var episodes = await client.GetEpisodesAsync(request.BookId, cancellationToken);
            tasks = BuildTasks(request.BookId, episodes, request.Episodes);
        }
        catch (Exception ex)
        {
            return new DramaDownloadResult(
                Ok: false,
                OutputDir: request.OutputDir,
                VideoCount: CountVideoFiles(request.OutputDir),
                Message: ex.Message);
        }

        if (tasks.Count == 0)
        {
            return new DramaDownloadResult(
                Ok: false,
                OutputDir: request.OutputDir,
                VideoCount: CountVideoFiles(request.OutputDir),
                Message: "没有可下载的剧集。");
        }

        var quality = string.IsNullOrWhiteSpace(request.Quality) ? DefaultQuality : request.Quality.Trim();
        var concurrency = Math.Clamp(request.Concurrent, 1, 10);
        var failures = new List<string>();
        var semaphore = new SemaphoreSlim(concurrency);
        var videoTasks = tasks.Select(task => DownloadEpisodeWithConcurrencyAsync(
            client,
            task,
            tasks.Count,
            quality,
            request.OutputDir,
            progress,
            semaphore,
            failures,
            cancellationToken));

        try
        {
            await Task.WhenAll(videoTasks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        var posterUrl = ReadFirstNonEmpty([metadata.PosterUrl], tasks.Select(task => task.PosterUrl));
        var ensuredPoster = await EnsurePosterAsync(
            request.OutputDir,
            displayName,
            posterUrl,
            progress,
            cancellationToken);

        var finalVideoCount = CountVideoFiles(request.OutputDir);
        if (!ensuredPoster.Ok)
        {
            failures.Add(ensuredPoster.Message ?? "海报下载失败");
        }

        if (failures.Count > 0)
        {
            return new DramaDownloadResult(
                Ok: false,
                OutputDir: request.OutputDir,
                VideoCount: finalVideoCount,
                Message: string.Join("；", failures.Distinct(StringComparer.Ordinal)));
        }

        return new DramaDownloadResult(
            Ok: finalVideoCount > 0,
            OutputDir: request.OutputDir,
            VideoCount: finalVideoCount,
            Message: finalVideoCount > 0
                ? $"下载完成，共 {finalVideoCount} 个视频。"
                : "下载执行完成，但未发现视频文件。");
    }

    private async Task DownloadEpisodeWithConcurrencyAsync(
        HongguoApiClient client,
        EpisodeDownloadTask task,
        int totalCount,
        string quality,
        string outputDir,
        IProgress<string>? progress,
        SemaphoreSlim semaphore,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var result = await DownloadEpisodeAsync(
                client,
                task,
                totalCount,
                quality,
                outputDir,
                progress,
                cancellationToken);

            if (!result.Ok && !string.IsNullOrWhiteSpace(result.Message))
            {
                lock (failures)
                {
                    failures.Add($"第{task.EpisodeNumber:00}集: {result.Message}");
                }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<EpisodeDownloadResult> DownloadEpisodeAsync(
        HongguoApiClient client,
        EpisodeDownloadTask task,
        int totalCount,
        string quality,
        string outputDir,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var progressReporter = new EpisodeProgressReporter(task, totalCount, progress);
        var finalPath = Path.Combine(outputDir, $"第{task.EpisodeNumber}集.mp4");
        var tempPath = $"{finalPath}.part";

        try
        {
            progressReporter.ReportStatus("准备", "等待获取链接");

            if (HasValidVideoFile(finalPath))
            {
                progressReporter.Report(100d, 0d, "已存在", "视频已存在，跳过");
                return EpisodeDownloadResult.Success();
            }

            var detail = await client.GetVideoDetailAsync(task.VideoId, quality, cancellationToken);
            var videoUrl = ExtractVideoUrl(detail);
            if (string.IsNullOrWhiteSpace(videoUrl))
            {
                throw new InvalidOperationException("视频链接缺失");
            }

            CleanupDownloadArtifacts(finalPath, keepVideo: false);

            using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgentString);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0L;
            var downloadedBytes = 0L;
            var startAt = DateTime.UtcNow;
            var nextPercentToReport = 0d;
            var lastProgressAt = DateTime.MinValue;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(tempPath);
            var buffer = new byte[ChunkSize];

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloadedBytes += read;

                if (totalBytes <= 0)
                {
                    continue;
                }

                var percent = downloadedBytes * 100d / totalBytes;
                var elapsedSeconds = Math.Max((DateTime.UtcNow - startAt).TotalSeconds, 0.001d);
                var speedBytesPerSecond = downloadedBytes / elapsedSeconds;
                var nowUtc = DateTime.UtcNow;
                if (percent >= nextPercentToReport || nowUtc - lastProgressAt >= ProgressInterval)
                {
                    progressReporter.Report(percent, speedBytesPerSecond, "直连", null);
                    nextPercentToReport = Math.Floor(percent / 5d) * 5d + 5d;
                    lastProgressAt = nowUtc;
                }
            }

            await target.FlushAsync(cancellationToken);

            if (!HasValidVideoFile(tempPath))
            {
                throw new InvalidOperationException("下载结果校验失败：未生成完整视频文件");
            }

            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(tempPath, finalPath);
            CleanupDownloadArtifacts(finalPath, keepVideo: true);
            progressReporter.Report(100d, 0d, "完成", "下载完成");
            return EpisodeDownloadResult.Success();
        }
        catch (OperationCanceledException)
        {
            CleanupDownloadArtifacts(finalPath, keepVideo: false);
            progressReporter.ReportStatus("已取消", "已取消");
            throw;
        }
        catch (Exception ex)
        {
            CleanupDownloadArtifacts(finalPath, keepVideo: false);
            progressReporter.ReportStatus("失败", ex.Message);
            return EpisodeDownloadResult.Failure(ex.Message);
        }
    }

    private async Task<PosterEnsureResult> EnsurePosterAsync(
        string outputDir,
        string displayName,
        string? posterUrl,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var existingPoster = FindExistingPoster(outputDir, displayName);
        if (!string.IsNullOrWhiteSpace(existingPoster))
        {
            progress?.Report($"海报已存在，跳过: {Path.GetFileName(existingPoster)}");
            return PosterEnsureResult.Success(existingPoster);
        }

        if (string.IsNullOrWhiteSpace(posterUrl))
        {
            progress?.Report("未找到海报 URL，跳过海报下载。");
            return PosterEnsureResult.Success(null);
        }

        progress?.Report("开始下载海报...");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, posterUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgentString);
            request.Headers.UserAgent.Add(UserAgentProduct);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var extension = ResolveImageExtension(posterUrl, response.Content.Headers.ContentType?.MediaType);
            var targetPath = Path.Combine(outputDir, $"{SanitizeFileStem(displayName)}{extension}");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(targetPath);
            await source.CopyToAsync(file, cancellationToken);
            await file.FlushAsync(cancellationToken);

            progress?.Report($"海报下载完成: {Path.GetFileName(targetPath)}");
            return PosterEnsureResult.Success(targetPath);
        }
        catch (Exception ex)
        {
            progress?.Report($"海报下载失败: {ex.Message}");
            return PosterEnsureResult.Failure($"海报下载失败: {ex.Message}");
        }
    }

    private static IReadOnlyList<EpisodeDownloadTask> BuildTasks(
        string bookId,
        IReadOnlyList<JsonElement> episodes,
        string selection)
    {
        var selectedEpisodes = ParseEpisodeSelection(selection);
        var tasks = new List<EpisodeDownloadTask>();

        for (var index = 0; index < episodes.Count; index++)
        {
            var episode = episodes[index];
            var videoId = GetString(episode, "video_id");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var episodeNumber = ExtractEpisodeNumber(episode, index + 1);
            if (selectedEpisodes is not null && !selectedEpisodes.Contains(episodeNumber))
            {
                continue;
            }

            var title = ReadFirstNonEmpty(
                GetString(episode, "title"),
                GetString(episode, "name"),
                $"第{episodeNumber:00}集");

            tasks.Add(new EpisodeDownloadTask(
                Order: tasks.Count + 1,
                BookId: bookId,
                VideoId: videoId,
                EpisodeNumber: episodeNumber,
                EpisodeTitle: title,
                PosterUrl: ExtractPosterUrl(episode)));
        }

        return tasks.OrderBy(item => item.EpisodeNumber).ThenBy(item => item.Order).ToArray();
    }

    private static HashSet<int>? ParseEpisodeSelection(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection) ||
            string.Equals(selection.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var set = new HashSet<int>();
        foreach (var part in selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var rangeParts = part.Split('-', 2, StringSplitOptions.TrimEntries);
                if (rangeParts.Length != 2 ||
                    !int.TryParse(rangeParts[0], out var start) ||
                    !int.TryParse(rangeParts[1], out var end))
                {
                    continue;
                }

                if (start > end)
                {
                    (start, end) = (end, start);
                }

                for (var value = Math.Max(1, start); value <= end; value++)
                {
                    set.Add(value);
                }

                continue;
            }

            if (int.TryParse(part, out var single) && single > 0)
            {
                set.Add(single);
            }
        }

        return set.Count == 0 ? null : set;
    }

    private static int ExtractEpisodeNumber(JsonElement episode, int fallback)
    {
        var episodeNumber = GetInt(episode, "episode_num");
        if (episodeNumber is > 0)
        {
            return episodeNumber.Value;
        }

        var title = ReadFirstNonEmpty(GetString(episode, "title"), GetString(episode, "name"));
        if (!string.IsNullOrWhiteSpace(title))
        {
            var digits = new string(title.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var parsed) && parsed > 0)
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static string? ExtractVideoUrl(JsonElement payload)
    {
        return ReadFirstNonEmpty(
            GetString(payload, "url"),
            GetString(payload, "play_url"),
            FindFirstHttpUrl(payload));
    }

    private static string ExtractPosterUrl(JsonElement element)
    {
        foreach (var key in PosterKeys)
        {
            var direct = GetString(element, key);
            if (LooksLikeHttpUrl(direct))
            {
                return direct!;
            }
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (PosterKeywords.Any(keyword => property.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    var nested = ExtractPosterUrl(property.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = ExtractPosterUrl(property.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = ExtractPosterUrl(item);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString()?.Trim();
            if (LooksLikeHttpUrl(value) && LooksLikeImageUrl(value))
            {
                return value!;
            }
        }

        return string.Empty;
    }

    private static string? FindFirstHttpUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString()?.Trim();
            return LooksLikeHttpUrl(value) ? value : null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var nested = FindFirstHttpUrl(property.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstHttpUrl(item);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool HasValidVideoFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupDownloadArtifacts(string path, bool keepVideo)
    {
        var tempPath = $"{path}.part";
        DeleteIfExists(tempPath);

        if (!keepVideo)
        {
            DeleteIfExists(path);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private static int CountVideoFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Count(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static string? FindExistingPoster(string directory, string displayName)
    {
        var stem = SanitizeFileStem(displayName);
        foreach (var extension in ImageExtensions)
        {
            var candidate = Path.Combine(directory, $"{stem}{extension}");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            : null;
    }

    private static string ResolveImageExtension(string url, string? mediaType)
    {
        var normalizedType = (mediaType ?? string.Empty).Split(';', 2)[0].Trim();
        if (ImageContentTypes.TryGetValue(normalizedType, out var extension))
        {
            return extension;
        }

        return ResolveImageExtensionFromUrl(url);
    }

    private static string ResolveImageExtensionFromUrl(string url)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(url).AbsolutePath);
            return ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                ? string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension
                : ".jpg";
        }
        catch
        {
            return ".jpg";
        }
    }

    private static string SanitizeFileStem(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "cover" : sanitized;
    }

    private static bool LooksLikeHttpUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeImageUrl(string? value)
    {
        if (!LooksLikeHttpUrl(value))
        {
            return false;
        }

        try
        {
            var extension = Path.GetExtension(new Uri(value!).AbsolutePath);
            return ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ReadFirstNonEmpty(params IEnumerable<string?>[] values)
    {
        foreach (var collection in values)
        {
            foreach (var value in collection)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return string.Empty;
    }

    private static string ReadFirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
            return intValue;
        }

        return null;
    }

    private sealed class EpisodeProgressReporter
    {
        private readonly EpisodeDownloadTask _task;
        private readonly int _totalCount;
        private readonly IProgress<string>? _progress;

        public EpisodeProgressReporter(EpisodeDownloadTask task, int totalCount, IProgress<string>? progress)
        {
            _task = task;
            _totalCount = totalCount;
            _progress = progress;
        }

        public void Report(double percent, double speedBytesPerSecond, string status, string? message)
        {
            _progress?.Report(BuildLine(percent, speedBytesPerSecond, status, message));
        }

        public void ReportStatus(string status, string? message)
        {
            _progress?.Report(BuildLine(0d, 0d, status, message));
        }

        private string BuildLine(double percent, double speedBytesPerSecond, string status, string? message)
        {
            var text = $"[{_task.Order:00}/{_totalCount:00}] {_task.Label} {Math.Clamp(percent, 0d, 100d):0.0}% | {FormatSpeed(speedBytesPerSecond)} | {status}";
            return string.IsNullOrWhiteSpace(message) ? text : $"{text} - {message}";
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0d)
            {
                return "0 B/s";
            }

            var units = new[] { "B/s", "KB/s", "MB/s", "GB/s" };
            var speed = bytesPerSecond;
            var index = 0;
            while (speed >= 1024d && index < units.Length - 1)
            {
                speed /= 1024d;
                index++;
            }

            return $"{speed:0.0} {units[index]}";
        }
    }

    private sealed record EpisodeDownloadTask(
        int Order,
        string BookId,
        string VideoId,
        int EpisodeNumber,
        string EpisodeTitle,
        string PosterUrl)
    {
        public string Label => string.IsNullOrWhiteSpace(EpisodeTitle) ? $"第{EpisodeNumber:00}集" : EpisodeTitle.Trim();
    }

    private sealed record EpisodeDownloadResult(bool Ok, string? Message)
    {
        public static EpisodeDownloadResult Success() => new(true, null);
        public static EpisodeDownloadResult Failure(string message) => new(false, message);
    }

    private sealed record PosterEnsureResult(bool Ok, string? Message, string? Path)
    {
        public static PosterEnsureResult Success(string? path) => new(true, null, path);
        public static PosterEnsureResult Failure(string message) => new(false, message, null);
    }
}
