using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Diagnostics;
using ShortDrama.Infrastructure.Automation;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation;

public sealed class DramaSourceRouter : IDramaSearchService, IDramaDownloader
{
    private const string SearchCapability = "search";
    private const string DownloadCapability = "download";
    private const string NewReleaseCapability = "new_release";
    private static readonly string[] SearchDefaults = ["hgnew", "hglocal", "pikachu"];
    private static readonly string[] DownloadDefaults = ["hgnew", "hglocal", "pikachu"];
    private static readonly string[] NewReleaseDefaults = ["hgnew", "hglocal"];
    private static readonly string[] RankingDefaults = ["hglocal", "pikachu"];
    private const string DownloadStateFileName = ".weixin-channel-download-state.json";
    private const string EpisodeNumberModeContinuous = "continuous";
    private const int DownloadBufferSize = 128 * 1024;
    private static readonly TimeSpan DownloadProgressInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif"];
    private static readonly ProductInfoHeaderValue UserAgentProduct = new("ShortDramaDesktop", "1.0");
    private static readonly string MobileUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    private readonly HttpClient _httpClient;
    private readonly IDramaSettingsProvider _settingsProvider;
    private readonly HongguoLocalApiService _hglocalApiService;
    private readonly HongguoNewApiService _hgnewApiService;
    private readonly HongguoDramaSearchService _hgnewSearchService;
    private readonly HongguoDramaDownloader _hgnewDownloader;
    private readonly HongguoMemoryReaderService _hongguoMemoryReaderService;

    public DramaSourceRouter(
        HttpClient httpClient,
        IDramaSettingsProvider settingsProvider,
        HongguoLocalApiService hglocalApiService,
        HongguoNewApiService hgnewApiService,
        HongguoDramaSearchService hgnewSearchService,
        HongguoDramaDownloader hgnewDownloader,
        HongguoMemoryReaderService hongguoMemoryReaderService)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _hglocalApiService = hglocalApiService;
        _hgnewApiService = hgnewApiService;
        _hgnewSearchService = hgnewSearchService;
        _hgnewDownloader = hgnewDownloader;
        _hongguoMemoryReaderService = hongguoMemoryReaderService;
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        Exception? lastError = null;

        foreach (var source in ResolveServiceOrder(settings.DramaServiceOrderSearch, SearchDefaults, settings.DramaSourceChain))
        {
            try
            {
                var result = source switch
                {
                    "hgnew" => await SearchHgnewAsync(keyword, page, settings, cancellationToken),
                    "hglocal" => await SearchLocalAsync(keyword, page, settings, cancellationToken),
                    "pikachu" => await SearchPikachuAsync(keyword, page, settings, cancellationToken),
                    _ => []
                };

                if (result.Count > 0)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        return [];
    }

    public async Task<int> ProbePikachuSearchAsync(DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var items = await SearchPikachuAsync("测试", 1, settings, cancellationToken);
        return items.Count;
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetTodayAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => _hgnewApiService.GetTodayNewAsync(settings, "djnew", ct),
            hglocalLoader: ct => GetLocalTodayAsync(settings, ct),
            cancellationToken);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetMangaTodayAsync(int days, CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewMangaTodayAsync(settings, days, ct),
            hglocalLoader: ct => GetLatestByGenreAsync(settings, "comic_series", days, ct),
            cancellationToken);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetAiTodayAsync(int days, CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewAiTodayAsync(settings, days, ct),
            hglocalLoader: ct => GetLatestByGenreAsync(settings, "ai_series", days, ct),
            cancellationToken);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetHistoryAsync(int days, CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewHistoryAsync(settings, days, ct),
            hglocalLoader: ct => GetLatestByGenreAsync(settings, "short_play", days, ct),
            cancellationToken);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchHgnewAsync(
        string keyword,
        int page,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _hgnewApiService.SearchAsync(settings, keyword, page, cancellationToken);
        }
        catch
        {
            // Fall back to the legacy proxy-based search when the authenticated path is unavailable.
            return await _hgnewSearchService.SearchAsync(keyword, page, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<DramaSearchItem>> LoadNewReleaseAsync(
        DramaSourceSettings settings,
        Func<CancellationToken, Task<IReadOnlyList<DramaSearchItem>>> hgnewLoader,
        Func<CancellationToken, Task<IReadOnlyList<DramaSearchItem>>> hglocalLoader,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        foreach (var source in ResolveServiceOrder(settings.DramaServiceOrderNewRelease, NewReleaseDefaults, settings.DramaSourceChain))
        {
            try
            {
                var result = source switch
                {
                    "hgnew" => await hgnewLoader(cancellationToken),
                    "hglocal" => await hglocalLoader(cancellationToken),
                    _ => []
                };

                if (result.Count > 0)
                {
                    return result;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        return [];
    }

    private async Task<IReadOnlyList<DramaSearchItem>> LoadHgnewMangaTodayAsync(
        DramaSourceSettings settings,
        int days,
        CancellationToken cancellationToken)
    {
        try
        {
            return await LoadHgnewDailyModeWithFallbackAsync(
                settings,
                "mjnew",
                days,
                cancellationToken);
        }
        catch
        {
            return await FilterByRecentDaysAsync(
                () => _hgnewSearchService.GetTodayAsync(cancellationToken),
                days);
        }
    }

    private async Task<IReadOnlyList<DramaSearchItem>> LoadHgnewAiTodayAsync(
        DramaSourceSettings settings,
        int days,
        CancellationToken cancellationToken)
    {
        try
        {
            return await LoadHgnewDailyModeWithFallbackAsync(
                settings,
                "aiju",
                days,
                cancellationToken);
        }
        catch
        {
            return await FilterByRecentDaysAsync(
                () => _hgnewSearchService.GetTodayAsync(cancellationToken),
                days);
        }
    }

    private async Task<IReadOnlyList<DramaSearchItem>> LoadHgnewHistoryAsync(
        DramaSourceSettings settings,
        int days,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _hgnewApiService.GetHistoryByDatesAsync(
                settings,
                "djnew",
                BuildRecentDateWindow(days),
                cancellationToken);
        }
        catch
        {
            return await FilterByRecentDaysAsync(
                () => _hgnewSearchService.GetTodayAsync(cancellationToken),
                days);
        }
    }

    private async Task<IReadOnlyList<DramaSearchItem>> LoadHgnewDailyModeWithFallbackAsync(
        DramaSourceSettings settings,
        string mode,
        int days,
        CancellationToken cancellationToken)
    {
        var window = BuildRecentDateWindow(days);
        if (window.Count == 0)
        {
            return [];
        }

        if (window.Count == 1)
        {
            var todayItems = await _hgnewApiService.GetDailyByDatesAsync(
                settings,
                mode,
                [window[0]],
                cancellationToken);
            if (todayItems.Count > 0)
            {
                return todayItems;
            }

            return await _hgnewApiService.GetDailyByDatesAsync(
                settings,
                mode,
                [window[0].AddDays(-1)],
                cancellationToken);
        }

        var merged = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var date in window)
        {
            var dayItems = await _hgnewApiService.GetDailyByDatesAsync(
                settings,
                mode,
                [date],
                cancellationToken);
            foreach (var item in dayItems)
            {
                if (string.IsNullOrWhiteSpace(item.BookId) || !seen.Add(item.BookId))
                {
                    continue;
                }

                merged.Add(item);
            }
        }

        return SortByPublishTimeDescending(merged);
    }

    public async Task<DramaDownloadResult> DownloadAsync(
        DramaDownloadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        var downloadTimeoutSeconds = ParsePositiveInt(settings.HongguoDownloadTimeoutSeconds, 60);
        var downloadAttempts = ParsePositiveInt(settings.HongguoEpisodeDownloadAttempts, 5);
        var bookId = request.BookId?.Trim() ?? string.Empty;

        if (bookId.StartsWith(HongguoLocalBookPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetLocalEpisodesAsync(bookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetLocalVideoUrlAsync(videoId, settings, ct),
                posterPrefix: HongguoLocalBookPrefix,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts);
        }

        if (bookId.StartsWith(PikachuBookPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetPikachuEpisodesAsync(bookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetPikachuVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: PikachuBookPrefix,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts);
        }

        try
        {
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetHgnewEpisodesAsync(bookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetHgnewVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: string.Empty,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts);
        }
        catch
        {
            return await _hgnewDownloader.DownloadAsync(request, progress, cancellationToken);
        }
    }

    private async Task<DramaDownloadResult> DownloadWithProviderAsync(
        DramaDownloadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IReadOnlyList<SourceEpisode>>> resolveEpisodes,
        Func<string, string, CancellationToken, Task<SourceVideoDetail>> resolveVideo,
        string posterPrefix,
        int downloadTimeoutSeconds,
        int downloadAttempts)
    {
        Directory.CreateDirectory(request.OutputDir);
        progress?.Report($"开始下载《{request.DisplayName}》...");

        IReadOnlyList<SourceEpisode> episodes;
        try
        {
            episodes = await resolveEpisodes(cancellationToken);
        }
        catch (Exception ex)
        {
            return new DramaDownloadResult(false, request.OutputDir, CountVideoFiles(request.OutputDir), ex.Message);
        }

        var episodeNumberMode = NormalizeEpisodeNumberMode(request.EpisodeNumberMode);
        var tasks = BuildEpisodeTasks(episodes, request.Episodes, episodeNumberMode);
        if (tasks.Count == 0)
        {
            return new DramaDownloadResult(false, request.OutputDir, CountVideoFiles(request.OutputDir), "没有可下载的剧集。");
        }

        var failures = new List<string>();
        var concurrency = Math.Clamp(request.Concurrent, 1, 8);
        using var semaphore = new SemaphoreSlim(concurrency);

        var downloads = tasks.Select(task => DownloadEpisodeAsync(
            request.OutputDir,
            request.Quality,
            task,
            tasks.Count,
            resolveVideo,
            progress,
            semaphore,
            failures,
            downloadTimeoutSeconds,
            downloadAttempts,
            cancellationToken));
        await Task.WhenAll(downloads);

        var posterUrl = ReadPosterUrlFromProject(request.ProjectDir);
        if (string.IsNullOrWhiteSpace(posterUrl))
        {
            posterUrl = tasks.Select(item => item.PosterUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            await EnsurePosterAsync(request.OutputDir, request.DisplayName, posterUrl, progress, cancellationToken);
        }

        var videoCount = CountVideoFiles(request.OutputDir);
        if (failures.Count > 0)
        {
            var failed = new DramaDownloadResult(false, request.OutputDir, videoCount, string.Join("；", failures.Distinct(StringComparer.Ordinal)));
            WriteDownloadState(request, failed, tasks, failures, episodeNumberMode);
            PersistEpisodeNumberMode(request.ProjectDir, episodeNumberMode);
            return failed;
        }

        var result = new DramaDownloadResult(videoCount > 0, request.OutputDir, videoCount, videoCount > 0
            ? $"下载完成，共 {videoCount} 个视频。"
            : "下载完成，但未发现视频文件。");
        WriteDownloadState(request, result, tasks, [], episodeNumberMode);
        PersistEpisodeNumberMode(request.ProjectDir, episodeNumberMode);
        return result;
    }

    private async Task DownloadEpisodeAsync(
        string outputDir,
        string quality,
        EpisodeTask task,
        int totalCount,
        Func<string, string, CancellationToken, Task<SourceVideoDetail>> resolveVideo,
        IProgress<string>? progress,
        SemaphoreSlim semaphore,
        ICollection<string> failures,
        int downloadTimeoutSeconds,
        int downloadAttempts,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var finalPath = Path.Combine(outputDir, BuildEpisodeFileName(task));
            var tempPath = $"{finalPath}.part";
            var existingVideo = FindExistingEpisodeVideo(outputDir, task);
            if (!string.IsNullOrWhiteSpace(existingVideo))
            {
                if (!string.Equals(Path.GetFullPath(existingVideo), Path.GetFullPath(finalPath), StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(finalPath))
                {
                    await DownloadFileOperations.SafeReplaceAsync(existingVideo, finalPath, cancellationToken);
                    existingVideo = finalPath;
                }

                progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集已存在，跳过");
                return;
            }

            if (HasValidVideoFile(finalPath))
            {
                progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集已存在，跳过");
                return;
            }

            CleanupDownloadArtifacts(finalPath, keepVideo: false);
            progress?.Report($"[{task.Order:00}/{totalCount:00}] 开始下载第{task.EpisodeNumber:00}集");

            var maxAttempts = Math.Clamp(downloadAttempts, 1, 20);
            for (var attempt = 1; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var detail = await resolveVideo(task.VideoId, quality, cancellationToken);
                    var stats = await DownloadVideoFileOnceAsync(
                        detail.Url,
                        tempPath,
                        finalPath,
                        downloadTimeoutSeconds,
                        cancellationToken,
                        downloadProgress => ReportEpisodeDownloadProgress(progress, task, totalCount, downloadProgress));
                    progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载完成（{FormatBytes(stats.Bytes)}, {stats.Elapsed.TotalSeconds:0.#}s, {FormatBytes(stats.BytesPerSecond)}/s）");
                    return;
                }
                catch (Exception ex) when (ShouldRetryDownload(ex))
                {
                    CleanupDownloadArtifacts(finalPath, keepVideo: false);
                    progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载重试 {attempt}/{maxAttempts}: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), cancellationToken);
                }
            }

            try
            {
                var detail = await resolveVideo(task.VideoId, quality, cancellationToken);
                var stats = await DownloadVideoFileOnceAsync(
                    detail.Url,
                    tempPath,
                    finalPath,
                    downloadTimeoutSeconds,
                    cancellationToken,
                    downloadProgress => ReportEpisodeDownloadProgress(progress, task, totalCount, downloadProgress));
                progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载完成（{FormatBytes(stats.Bytes)}, {stats.Elapsed.TotalSeconds:0.#}s, {FormatBytes(stats.BytesPerSecond)}/s）");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CleanupDownloadArtifacts(finalPath, keepVideo: false);
                lock (failures)
                {
                    failures.Add($"第{task.EpisodeNumber:00}集 {ex.Message}");
                }
                progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载失败: {ex.Message}");
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task DownloadVideoFileOnceAsync(
        string url,
        string tempPath,
        string finalPath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
        => await DownloadVideoFileOnceAsync(url, tempPath, finalPath, timeoutSeconds, cancellationToken, progress: null);

    private async Task<DownloadFileStats> DownloadVideoFileOnceAsync(
        string url,
        string tempPath,
        string finalPath,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        Action<DownloadFileProgress>? progress)
    {
        var clampedTimeoutSeconds = Math.Clamp(timeoutSeconds, 10, 600);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(clampedTimeoutSeconds));
        var token = timeoutCts.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", MobileUserAgent);
            request.Headers.UserAgent.Add(UserAgentProduct);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            var downloadedBytes = 0L;
            var stopwatch = Stopwatch.StartNew();
            var nextPercentToReport = 10d;
            var lastProgressAt = DateTime.UtcNow;

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, useAsync: true))
            {
                var buffer = new byte[DownloadBufferSize];
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                    if (read <= 0)
                        break;

                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    downloadedBytes += read;

                    var now = DateTime.UtcNow;
                    var percent = contentLength is > 0 ? downloadedBytes * 100d / contentLength.Value : (double?)null;
                    if (now - lastProgressAt >= DownloadProgressInterval ||
                        (percent is not null && percent >= nextPercentToReport))
                    {
                        var speed = downloadedBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d);
                        progress?.Invoke(new DownloadFileProgress(downloadedBytes, contentLength, stopwatch.Elapsed, speed));
                        if (percent is not null)
                            nextPercentToReport = Math.Floor(percent.Value / 10d) * 10d + 10d;
                        lastProgressAt = now;
                    }
                }

                await file.FlushAsync(token);
            }

            await DownloadFileOperations.DelayAfterWriteAsync(token);
            await DownloadFileOperations.SafeReplaceAsync(tempPath, finalPath, token);

            stopwatch.Stop();
            return new DownloadFileStats(
                downloadedBytes,
                stopwatch.Elapsed,
                downloadedBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"下载超过 {clampedTimeoutSeconds} 秒未完成，已中止并准备重试。", ex);
        }
    }

    private static void ReportEpisodeDownloadProgress(
        IProgress<string>? progress,
        EpisodeTask task,
        int totalCount,
        DownloadFileProgress downloadProgress)
    {
        if (progress is null)
            return;

        var total = downloadProgress.TotalBytes is > 0
            ? $" / {FormatBytes(downloadProgress.TotalBytes.Value)}"
            : "";
        var percent = downloadProgress.TotalBytes is > 0
            ? $"，{downloadProgress.Bytes * 100d / downloadProgress.TotalBytes.Value:0.#}%"
            : "";
        progress.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载中：{FormatBytes(downloadProgress.Bytes)}{total}{percent}，{FormatBytes(downloadProgress.BytesPerSecond)}/s");
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static bool ShouldRetryDownload(Exception exception)
    {
        if (exception is TaskCanceledException or TimeoutException or IOException or HttpRequestException)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("408", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("500", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("502", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("504", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchPikachuAsync(
        string keyword,
        int page,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.PikachuFanqieCookie))
        {
            throw new InvalidOperationException("未配置 pikachu 搜索 Cookie。");
        }

        var searchCtx = JsonSerializer.Serialize(new
        {
            type = 1,
            tab_type = 39,
            default_tab_type = 10,
            bottom_type = 1,
            search_tab_id = string.Equals(settings.PikachuDramaType, "manga", StringComparison.OrdinalIgnoreCase) ? 13 : 10
        });

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["limit"] = "20",
            ["offset"] = (Math.Max(0, page - 1) * 20).ToString(CultureInfo.InvariantCulture),
            ["query"] = keyword,
            ["search_ctx_info"] = searchCtx
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api5-sinfonlinea.novelfm.com/novelfm/bookmall/search/page/v1/?device_platform=android&aid=3040&manifest_version_code=628&update_version_code=62832")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("user-agent", "com.xs.fm/576 (Linux; U; Android 9; zh_CN; BVL-AN16; Build/PQ3B.190801.11191547;tt-ok/3.12.13.4-tiktok)");
        request.Headers.TryAddWithoutValidation("cookie", settings.PikachuFanqieCookie);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (GetInt(document.RootElement, "code") != 0)
        {
            throw new InvalidOperationException($"皮卡丘搜索失败: {GetString(document.RootElement, "message") ?? GetString(document.RootElement, "msg") ?? "unknown"}");
        }

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("search_data", out var searchData) ||
            searchData.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<DramaSearchItem>();
        foreach (var item in searchData.EnumerateArray())
        {
            if (!item.TryGetProperty("cell_slices", out var cells) || cells.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var cell in cells.EnumerateArray())
            {
                if (!cell.TryGetProperty("book_slice", out var bookSlice) ||
                    !bookSlice.TryGetProperty("book_info", out var info) ||
                    info.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var bookId = GetString(info, "book_id");
                if (string.IsNullOrWhiteSpace(bookId))
                {
                    continue;
                }

                results.Add(new DramaSearchItem(
                    BookId: EnsurePrefixed(bookId, PikachuBookPrefix),
                    Title: GetString(info, "book_name") ?? string.Empty,
                    Category: GetString(info, "category") ?? string.Empty,
                    EpisodeTotal: GetInt(info, "serial_count") ?? 0,
                    Intro: GetString(info, "abstract") ?? string.Empty,
                    PosterUrl: GetString(info, "thumb_url") ?? string.Empty,
                    Author: GetString(info, "author") ?? string.Empty,
                    PublishTime: GetString(info, "create_time") ?? string.Empty,
                    FavoriteCount: GetInt(info, "favorite_count") ?? GetInt(info, "collect_count") ?? 0));
            }
        }

        return results;
    }

    private static async Task<IReadOnlyList<DramaSearchItem>> FilterByRecentDaysAsync(
        Func<Task<IReadOnlyList<DramaSearchItem>>> loader,
        int days)
    {
        var items = await loader();
        return FilterByRecentDays(items, days);
    }

    private static IReadOnlyList<DateOnly> BuildRecentDateWindow(int days)
    {
        var window = Math.Clamp(days, 1, 30);
        return Enumerable.Range(0, window)
            .Select(offset => DateOnly.FromDateTime(DateTime.Today.AddDays(-offset)))
            .ToArray();
    }

    private static IReadOnlyList<DramaSearchItem> FilterByRecentDays(
        IReadOnlyList<DramaSearchItem> items,
        int days)
    {
        var queryDays = Math.Clamp(days, 1, 30);
        if (queryDays <= 1 || items.Count == 0)
        {
            return items;
        }

        var threshold = DateTime.Today.AddDays(-(queryDays - 1));
        var filtered = items
            .Where(item => !TryParsePublishDate(item.PublishTime, out var publishedAt) || publishedAt.Date >= threshold)
            .ToArray();

        return filtered.Length > 0 ? filtered : items;
    }

    private static IReadOnlyList<DramaSearchItem> SortByPublishTimeDescending(IEnumerable<DramaSearchItem> items)
    {
        return items
            .OrderByDescending(item => TryParsePublishDate(item.PublishTime, out var publishedAt) ? publishedAt : DateTime.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryParsePublishDate(string? value, out DateTime date)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(value) &&
            DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    private static IReadOnlyList<DramaSearchItem> MapLocalItems(IEnumerable<JsonElement> items)
    {
        return items
            .Select(item => new DramaSearchItem(
                BookId: EnsurePrefixed(GetString(item, "series_id") ?? GetString(item, "book_id") ?? GetString(item, "id"), HongguoLocalBookPrefix),
                Title: GetString(item, "title") ?? GetString(item, "name") ?? string.Empty,
                Category: GetString(item, "category") ?? GetString(item, "type") ?? string.Empty,
                EpisodeTotal: GetInt(item, "episode_cnt") ?? GetInt(item, "episode_total") ?? GetInt(item, "total") ?? 0,
                Intro: GetString(item, "intro") ?? GetString(item, "description") ?? GetString(item, "desc") ?? string.Empty,
                PosterUrl: GetString(item, "cover") ?? GetString(item, "poster") ?? GetString(item, "poster_url") ?? string.Empty,
                Author: GetString(item, "author") ?? GetString(item, "producer") ?? GetString(item, "copyright") ?? string.Empty,
                PublishTime: GetString(item, "publish_time") ?? GetString(item, "first_seen") ?? GetString(item, "created_at") ?? string.Empty,
                FavoriteCount: GetInt(item, "favorite_count") ?? GetInt(item, "collect_count") ?? 0))
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    private Task<IReadOnlyList<DramaSearchItem>> SearchLocalAsync(
        string keyword,
        int page,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        return _hglocalApiService.SearchAsync(settings, keyword, page, cancellationToken);
    }

    private Task<IReadOnlyList<DramaSearchItem>> GetLocalTodayAsync(
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        return _hglocalApiService.GetTodayNewAsync(settings, "short_play", cancellationToken);
    }

    private Task<IReadOnlyList<DramaSearchItem>> GetLatestByGenreAsync(
        DramaSourceSettings settings,
        string genre,
        int days,
        CancellationToken cancellationToken)
    {
        return _hglocalApiService.GetLatestByGenreAsync(settings, genre, days, cancellationToken);
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetLocalEpisodesAsync(
        string prefixedBookId,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var episodes = await _hglocalApiService.GetEpisodesAsync(settings, prefixedBookId, cancellationToken);
        return episodes
            .Select(item => new SourceEpisode(
                item.EpisodeNumber,
                item.Title,
                item.VideoId,
                item.PosterUrl))
            .ToArray();
    }

    private async Task<SourceVideoDetail> GetLocalVideoUrlAsync(
        string prefixedVideoId,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var detail = await _hglocalApiService.GetVideoPlaybackAsync(settings, prefixedVideoId, cancellationToken);
        return new SourceVideoDetail(detail.Url);
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetPikachuEpisodesAsync(string prefixedBookId, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var serverUrl = NormalizeServerUrl(settings.PikachuServerUrl);
        var bookId = StripPrefix(prefixedBookId, PikachuBookPrefix);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["bookId"] = PikachuEncrypt(bookId)
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/drama/hongguo/detail")
        {
            Content = content
        };
        ApplyPikachuHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if ((GetString(document.RootElement, "code") ?? string.Empty) != "200")
        {
            throw new InvalidOperationException($"皮卡丘 detail 失败: {GetString(document.RootElement, "msg") ?? "unknown"}");
        }

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("data", out var episodeList) ||
            episodeList.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var episodes = new List<SourceEpisode>();
        foreach (var item in episodeList.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var videoId = GetString(item, "videoId") ?? GetString(item, "video_id");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var episodeNumber = ExtractEpisodeNumber(GetString(item, "title"), episodes.Count + 1);
            episodes.Add(new SourceEpisode(
                episodeNumber,
                GetString(item, "title") ?? $"第{episodeNumber}集",
                EnsurePrefixed(videoId, PikachuEpisodePrefix),
                string.Empty));
        }

        return episodes;
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetHgnewEpisodesAsync(string bookId, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var items = await _hgnewApiService.GetEpisodesAsync(settings, bookId, cancellationToken);
        return items
            .Select(item => new SourceEpisode(
                item.EpisodeNumber,
                item.Title,
                item.VideoId,
                item.PosterUrl))
            .ToArray();
    }

    private async Task<SourceVideoDetail> GetHgnewVideoUrlAsync(string videoId, string quality, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var detail = await _hgnewApiService.GetVideoPlaybackAsync(settings, videoId, quality, cancellationToken);
        return new SourceVideoDetail(detail.Url);
    }

    private async Task<SourceVideoDetail> GetPikachuVideoUrlAsync(string prefixedVideoId, string quality, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var serverUrl = NormalizeServerUrl(settings.PikachuServerUrl);
        var videoId = StripPrefix(prefixedVideoId, PikachuEpisodePrefix);
        var deviceId = await ResolvePikachuDeviceIdAsync(settings, cancellationToken);
        var clientVersion = string.IsNullOrWhiteSpace(settings.PikachuClientVersion)
            ? "1.4.2"
            : settings.PikachuClientVersion.Trim();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["videoId"] = PikachuEncrypt(videoId),
            ["quality"] = PikachuEncrypt(MapPikachuQuality(quality)),
            ["deviceId"] = PikachuEncrypt(deviceId),
            ["version"] = PikachuEncrypt(clientVersion)
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/drama/hongguo/video")
        {
            Content = content
        };
        ApplyPikachuHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if ((GetString(document.RootElement, "code") ?? string.Empty) != "200")
        {
            throw new InvalidOperationException($"皮卡丘 video 失败: {GetString(document.RootElement, "msg") ?? "unknown"}");
        }

        var url = document.RootElement.TryGetProperty("data", out var data)
            ? GetString(data, "url")
            : null;
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("皮卡丘未返回可用播放链接。");
        }

        return new SourceVideoDetail(url);
    }

    private async Task<string> ResolvePikachuDeviceIdAsync(DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var configured = settings.PikachuDeviceId?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var readResult = await _hongguoMemoryReaderService.ReadRuntimeAsync(cancellationToken);
        var deviceId = readResult.DeviceId?.Trim();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException($"未配置 pikachu_device_id，且未能从红果进程读取 DeviceId：{readResult.Reason}");
        }

        _settingsProvider.SavePikachuDeviceId(deviceId);
        return deviceId;
    }

    private async Task EnsurePosterAsync(string outputDir, string displayName, string posterUrl, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!LooksLikeHttpUrl(posterUrl))
        {
            return;
        }

        var extension = ResolveImageExtensionFromUrl(posterUrl);
        var targetPath = Path.Combine(outputDir, $"{SanitizeFileStem(displayName)}{extension}");
        if (File.Exists(targetPath))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, posterUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", MobileUserAgent);
            request.Headers.UserAgent.Add(UserAgentProduct);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(targetPath);
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
            progress?.Report($"海报下载完成: {Path.GetFileName(targetPath)}");
        }
        catch
        {
            // Poster download is best-effort for routed sources.
        }
    }

    private static IReadOnlyList<EpisodeTask> BuildEpisodeTasks(
        IReadOnlyList<SourceEpisode> episodes,
        string selection,
        string episodeNumberMode)
    {
        var continuous = string.Equals(episodeNumberMode, EpisodeNumberModeContinuous, StringComparison.Ordinal);
        var selectedEpisodes = ParseEpisodeSelection(selection);
        var ordered = episodes
            .OrderBy(item => item.EpisodeNumber)
            .Select((item, index) =>
            {
                var sequenceNumber = index + 1;
                var sourceNumber = item.EpisodeNumber > 0 ? item.EpisodeNumber : sequenceNumber;
                var outputNumber = continuous ? sequenceNumber : sourceNumber;
                return new EpisodeTask(
                    Order: sequenceNumber,
                    EpisodeNumber: outputNumber,
                    SourceEpisodeNumber: sourceNumber,
                    SequenceEpisodeNumber: sequenceNumber,
                    Title: item.Title,
                    VideoId: item.VideoId,
                    PosterUrl: item.PosterUrl);
            })
            .ToArray();

        if (selectedEpisodes is null)
        {
            return ordered;
        }

        return ordered
            .Where(item => selectedEpisodes.Contains(continuous ? item.SequenceEpisodeNumber : item.SourceEpisodeNumber))
            .Select((item, index) => item with { Order = index + 1 })
            .ToArray();
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

    private static int CountVideoFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Count(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
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
        DeleteIfExists($"{path}.part");
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

    private static string NormalizeEpisodeNumberMode(string? value)
    {
        return string.Equals(value?.Trim(), EpisodeNumberModeContinuous, StringComparison.OrdinalIgnoreCase)
            ? EpisodeNumberModeContinuous
            : "source";
    }

    private static string BuildEpisodeFileName(EpisodeTask task) => $"第{task.EpisodeNumber}集.mp4";

    private static string? FindExistingEpisodeVideo(string outputDir, EpisodeTask task)
    {
        foreach (var directory in new[] { outputDir, Path.Combine(outputDir, "videos") })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(path);
                if (string.Equals(stem, Path.GetFileNameWithoutExtension(BuildEpisodeFileName(task)), StringComparison.OrdinalIgnoreCase) ||
                    BuildEpisodeMarkers(task).Any(marker => stem.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                {
                    if (HasValidVideoFile(path))
                    {
                        return path;
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildEpisodeMarkers(EpisodeTask task)
    {
        foreach (var number in new[] { task.EpisodeNumber, task.SourceEpisodeNumber, task.SequenceEpisodeNumber }.Where(value => value > 0).Distinct())
        {
            yield return $"第{number}集";
            yield return $"第{number:00}集";
        }
    }

    private static void WriteDownloadState(
        DramaDownloadRequest request,
        DramaDownloadResult result,
        IReadOnlyList<EpisodeTask> tasks,
        IReadOnlyList<string> failures,
        string episodeNumberMode)
    {
        try
        {
            var payload = new
            {
                ok = result.Ok,
                project_dir = request.OutputDir,
                video_count = result.VideoCount,
                message = result.Message ?? "",
                failures,
                stopped = false,
                episodes = string.IsNullOrWhiteSpace(request.Episodes) ? "all" : request.Episodes,
                quality = request.Quality,
                concurrent = Math.Clamp(request.Concurrent, 1, 10),
                episode_number_mode = episodeNumberMode,
                episode_mappings = tasks.Select(task => new
                {
                    source_episode_number = task.SourceEpisodeNumber,
                    sequence_episode_number = task.SequenceEpisodeNumber,
                    episode_title = task.Title
                }).ToArray()
            };
            File.WriteAllText(
                Path.Combine(request.OutputDir, DownloadStateFileName),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Download state is helpful for queue interoperability, but should not fail a completed download.
        }
    }

    private static void PersistEpisodeNumberMode(string projectDir, string episodeNumberMode)
    {
        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(document.RootElement.GetRawText())
                          ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            payload["episodeNumberMode"] = episodeNumberMode;
            payload["episode_number_mode"] = episodeNumberMode;
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignore metadata update failures.
        }
    }

    private static string ReadPosterUrlFromProject(string projectDir)
    {
        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return GetString(document.RootElement, "posterUrl") ?? GetString(document.RootElement, "poster_url") ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeLocalBaseUrl(string value)
    {
        var baseUrl = (value ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        return baseUrl.EndsWith("/api/hongguo", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/api/hongguo";
    }

    private static string NormalizeServerUrl(string value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized)
            ? "http://8.138.192.128/start-prod-api"
            : normalized;
    }

    private static int ParsePositiveInt(string value, int defaultValue)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private static IEnumerable<string> ResolveServiceOrder(string configured, IReadOnlyList<string> defaults, string legacyFirst)
    {
        var items = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant())
            .Where(item => defaults.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(legacyFirst) &&
            defaults.Contains(legacyFirst, StringComparer.OrdinalIgnoreCase))
        {
            var preferred = legacyFirst.Trim().ToLowerInvariant();
            items.RemoveAll(item => string.Equals(item, preferred, StringComparison.OrdinalIgnoreCase));
            items.Insert(0, preferred);
        }

        foreach (var item in defaults)
        {
            if (!items.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static string EnsurePrefixed(string? value, string prefix)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text : prefix + text;
    }

    private static string StripPrefix(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number)
            ? number
            : null;
    }

    private static int ExtractEpisodeNumber(string? title, int fallback)
    {
        var digits = new string((title ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static bool IsTodayItem(JsonElement item, string today)
    {
        if (item.TryGetProperty("today", out var todayProperty))
        {
            if (todayProperty.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (todayProperty.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }

        foreach (var propertyName in new[] { "publish_time", "first_seen", "last_seen", "created_at", "updated_at" })
        {
            var value = GetString(item, propertyName);
            if (!string.IsNullOrWhiteSpace(value) && value.StartsWith(today, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeHttpUrl(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
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

    private static void ApplyPikachuHeaders(HttpRequestMessage request)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var raw = $"{today}_{PikachuPassId}_{PikachuPassToken}";
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        request.Headers.TryAddWithoutValidation("auth-pass-id", PikachuPassId);
        request.Headers.TryAddWithoutValidation("auth-signature", signature);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
    }

    private static string PikachuEncrypt(string plaintext)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PikachuPublicKey);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(plaintext), RSAEncryptionPadding.Pkcs1));
    }

    private static string MapPikachuQuality(string quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "1080p+" or "1080p" or "1080" => "1080",
            "720p" or "720" => "720",
            "高清" or "hd" => "2",
            "标清" or "sd" => "1",
            _ => "0"
        };
    }

    private sealed record SourceEpisode(
        int EpisodeNumber,
        string Title,
        string VideoId,
        string PosterUrl);

    private sealed record SourceVideoDetail(string Url);

    private sealed record EpisodeTask(
        int Order,
        int EpisodeNumber,
        int SourceEpisodeNumber,
        int SequenceEpisodeNumber,
        string Title,
        string VideoId,
        string PosterUrl);

    private sealed record DownloadFileProgress(
        long Bytes,
        long? TotalBytes,
        TimeSpan Elapsed,
        double BytesPerSecond);

    private sealed record DownloadFileStats(
        long Bytes,
        TimeSpan Elapsed,
        double BytesPerSecond);

    private const string HongguoLocalBookPrefix = "hglocal:";
    private const string HongguoLocalEpisodePrefix = "hglocal_ep:";
    private const string PikachuBookPrefix = "pikachu:";
    private const string PikachuEpisodePrefix = "pikachu_ep:";
    private const string PikachuPassId = "start-prod-api";
    private const string PikachuPassToken = "MkYQyRrrD2iG5WuDEV7DjYcq2jq7";
    private const string PikachuPublicKey = """
-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC/EwSZCZTwnYhixLefB9Gvfa+X
o4uMnG35UiNdPd20/CpgMjw0a9Zy79WjvMH4oCRCOL81HMy5/o6Iuks5Nj4t0reN
KMHkDcrZdIgMW+DFaioJWEi4zfORC0amtHuDEMYaxfVQ1PxOfgnApbD+/3qzd4hr
4AzoGhyxwpyUXtX6wQIDAQAB
-----END PUBLIC KEY-----
""";
}
