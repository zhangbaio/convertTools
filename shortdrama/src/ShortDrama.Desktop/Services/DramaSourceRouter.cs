using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Desktop.Models;
using ShortDrama.Infrastructure.Automation;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Desktop.Services;

public sealed class DramaSourceRouter : IDramaSearchService, IDramaDownloader
{
    private const string SearchCapability = "search";
    private const string DownloadCapability = "download";
    private const string NewReleaseCapability = "new_release";
    private static readonly string[] SearchDefaults = ["hgnew", "hglocal", "pikachu"];
    private static readonly string[] DownloadDefaults = ["hgnew", "hglocal", "pikachu"];
    private static readonly string[] NewReleaseDefaults = ["hgnew", "hglocal"];
    private static readonly string[] RankingDefaults = ["hglocal", "pikachu"];
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif"];
    private static readonly ProductInfoHeaderValue UserAgentProduct = new("ShortDramaDesktop", "1.0");
    private static readonly string MobileUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    private readonly HttpClient _httpClient;
    private readonly GlobalSettingsService _globalSettingsService;
    private readonly HongguoDramaSearchService _hgnewSearchService;
    private readonly HongguoDramaDownloader _hgnewDownloader;

    public DramaSourceRouter(
        HttpClient httpClient,
        GlobalSettingsService globalSettingsService,
        HongguoDramaSearchService hgnewSearchService,
        HongguoDramaDownloader hgnewDownloader)
    {
        _httpClient = httpClient;
        _globalSettingsService = globalSettingsService;
        _hgnewSearchService = hgnewSearchService;
        _hgnewDownloader = hgnewDownloader;
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var settings = _globalSettingsService.Load();
        Exception? lastError = null;

        foreach (var source in ResolveServiceOrder(settings.DramaServiceOrderSearch, SearchDefaults, settings.DramaSourceChain))
        {
            try
            {
                var result = source switch
                {
                    "hgnew" => await _hgnewSearchService.SearchAsync(keyword, page, cancellationToken),
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

    public async Task<IReadOnlyList<DramaSearchItem>> GetTodayAsync(CancellationToken cancellationToken)
    {
        var settings = _globalSettingsService.Load();
        Exception? lastError = null;

        foreach (var source in ResolveServiceOrder(settings.DramaServiceOrderNewRelease, NewReleaseDefaults, settings.DramaSourceChain))
        {
            try
            {
                var result = source switch
                {
                    "hgnew" => await _hgnewSearchService.GetTodayAsync(cancellationToken),
                    "hglocal" => await GetLocalTodayAsync(settings, cancellationToken),
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

    public async Task<DramaDownloadResult> DownloadAsync(
        DramaDownloadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var settings = _globalSettingsService.Load();
        var bookId = request.BookId?.Trim() ?? string.Empty;

        if (bookId.StartsWith(HongguoLocalBookPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetLocalEpisodesAsync(bookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetLocalVideoUrlAsync(videoId, settings, ct),
                posterPrefix: HongguoLocalBookPrefix);
        }

        if (bookId.StartsWith(PikachuBookPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetPikachuEpisodesAsync(bookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetPikachuVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: PikachuBookPrefix);
        }

        return await _hgnewDownloader.DownloadAsync(request, progress, cancellationToken);
    }

    private async Task<DramaDownloadResult> DownloadWithProviderAsync(
        DramaDownloadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IReadOnlyList<SourceEpisode>>> resolveEpisodes,
        Func<string, string, CancellationToken, Task<SourceVideoDetail>> resolveVideo,
        string posterPrefix)
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

        var tasks = BuildEpisodeTasks(episodes, request.Episodes);
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
            return new DramaDownloadResult(false, request.OutputDir, videoCount, string.Join("；", failures.Distinct(StringComparer.Ordinal)));
        }

        return new DramaDownloadResult(videoCount > 0, request.OutputDir, videoCount, videoCount > 0
            ? $"下载完成，共 {videoCount} 个视频。"
            : "下载完成，但未发现视频文件。");
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
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var finalPath = Path.Combine(outputDir, $"第{task.EpisodeNumber}集.mp4");
            var tempPath = $"{finalPath}.part";

            if (HasValidVideoFile(finalPath))
            {
                progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集已存在，跳过");
                return;
            }

            CleanupDownloadArtifacts(finalPath, keepVideo: false);

            try
            {
                var detail = await resolveVideo(task.VideoId, quality, cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, detail.Url);
                request.Headers.TryAddWithoutValidation("User-Agent", MobileUserAgent);
                request.Headers.UserAgent.Add(UserAgentProduct);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Create(tempPath);
                await source.CopyToAsync(file, cancellationToken);
                await file.FlushAsync(cancellationToken);

                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }

                File.Move(tempPath, finalPath);
                progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载完成");
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

    private async Task<IReadOnlyList<DramaSearchItem>> SearchLocalAsync(
        string keyword,
        int page,
        GlobalConfigSnapshot settings,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeLocalBaseUrl(settings.HongguoLocalBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("未配置 hglocal 地址。");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/search?q={Uri.EscapeDataString(keyword)}&limit=40&page={Math.Max(1, page)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "ShortDramaDesktop/1.0");
        if (!string.IsNullOrWhiteSpace(settings.HongguoLocalApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.HongguoLocalApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new DramaSearchItem(
                BookId: EnsurePrefixed(GetString(item, "series_id") ?? GetString(item, "book_id") ?? GetString(item, "id"), HongguoLocalBookPrefix),
                Title: GetString(item, "title") ?? GetString(item, "name") ?? string.Empty,
                Category: GetString(item, "category") ?? GetString(item, "type") ?? string.Empty,
                EpisodeTotal: GetInt(item, "episode_cnt") ?? GetInt(item, "episode_total") ?? GetInt(item, "total") ?? 0,
                Intro: GetString(item, "intro") ?? GetString(item, "description") ?? GetString(item, "desc") ?? string.Empty,
                PosterUrl: GetString(item, "cover") ?? GetString(item, "poster") ?? GetString(item, "poster_url") ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    private async Task<IReadOnlyList<DramaSearchItem>> GetLocalTodayAsync(
        GlobalConfigSnapshot settings,
        CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeLocalBaseUrl(settings.HongguoLocalBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("未配置 hglocal 地址。");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/latest?genre=short_play&only_today=true&limit=1000");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "ShortDramaDesktop/1.0");
        if (!string.IsNullOrWhiteSpace(settings.HongguoLocalApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.HongguoLocalApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && IsTodayItem(item, today))
            .Select(item => new DramaSearchItem(
                BookId: EnsurePrefixed(GetString(item, "series_id") ?? GetString(item, "book_id") ?? GetString(item, "id"), HongguoLocalBookPrefix),
                Title: GetString(item, "title") ?? GetString(item, "name") ?? string.Empty,
                Category: GetString(item, "category") ?? GetString(item, "type") ?? string.Empty,
                EpisodeTotal: GetInt(item, "episode_cnt") ?? GetInt(item, "episode_total") ?? GetInt(item, "total") ?? 0,
                Intro: GetString(item, "intro") ?? GetString(item, "description") ?? string.Empty,
                PosterUrl: GetString(item, "cover") ?? GetString(item, "poster") ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId))
            .ToArray();
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchPikachuAsync(
        string keyword,
        int page,
        GlobalConfigSnapshot settings,
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
                    PosterUrl: GetString(info, "thumb_url") ?? string.Empty));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetLocalEpisodesAsync(string prefixedBookId, GlobalConfigSnapshot settings, CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeLocalBaseUrl(settings.HongguoLocalBaseUrl);
        var bookId = StripPrefix(prefixedBookId, HongguoLocalBookPrefix);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/episodes?series_id={Uri.EscapeDataString(bookId)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "ShortDramaDesktop/1.0");
        if (!string.IsNullOrWhiteSpace(settings.HongguoLocalApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.HongguoLocalApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("episodes", out var episodes) || episodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SourceEpisode>();
        var index = 1;
        foreach (var item in episodes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var videoId = GetString(item, "vid") ?? GetString(item, "video_id") ?? GetString(item, "id");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                continue;
            }

            var episodeNumber = GetInt(item, "index") ?? index;
            result.Add(new SourceEpisode(
                episodeNumber,
                GetString(item, "title") ?? $"第{episodeNumber}集",
                EnsurePrefixed(videoId, HongguoLocalEpisodePrefix),
                GetString(item, "cover") ?? string.Empty));
            index++;
        }

        return result;
    }

    private async Task<SourceVideoDetail> GetLocalVideoUrlAsync(string prefixedVideoId, GlobalConfigSnapshot settings, CancellationToken cancellationToken)
    {
        var baseUrl = NormalizeLocalBaseUrl(settings.HongguoLocalBaseUrl);
        var videoId = StripPrefix(prefixedVideoId, HongguoLocalEpisodePrefix);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/video_url?vid={Uri.EscapeDataString(videoId)}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "ShortDramaDesktop/1.0");
        if (!string.IsNullOrWhiteSpace(settings.HongguoLocalApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.HongguoLocalApiKey);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var url = GetString(root, "url") ?? GetString(root, "play_url") ?? GetString(root, "playUrl") ?? GetString(root, "video_url");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("hglocal 未返回可用播放链接。");
        }

        return new SourceVideoDetail(url);
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetPikachuEpisodesAsync(string prefixedBookId, GlobalConfigSnapshot settings, CancellationToken cancellationToken)
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

    private async Task<SourceVideoDetail> GetPikachuVideoUrlAsync(string prefixedVideoId, string quality, GlobalConfigSnapshot settings, CancellationToken cancellationToken)
    {
        var serverUrl = NormalizeServerUrl(settings.PikachuServerUrl);
        var videoId = StripPrefix(prefixedVideoId, PikachuEpisodePrefix);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["videoId"] = PikachuEncrypt(videoId),
            ["quality"] = PikachuEncrypt(MapPikachuQuality(quality))
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

    private static IReadOnlyList<EpisodeTask> BuildEpisodeTasks(IReadOnlyList<SourceEpisode> episodes, string selection)
    {
        var selectedEpisodes = ParseEpisodeSelection(selection);
        return episodes
            .Where(item => selectedEpisodes is null || selectedEpisodes.Contains(item.EpisodeNumber))
            .OrderBy(item => item.EpisodeNumber)
            .Select((item, index) => new EpisodeTask(index + 1, item.EpisodeNumber, item.Title, item.VideoId, item.PosterUrl))
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

    private static IEnumerable<string> ResolveServiceOrder(string configured, IReadOnlyList<string> defaults, string legacyFirst)
    {
        var items = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant())
            .Where(item => defaults.Contains(item, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (items.Count == 0 && !string.IsNullOrWhiteSpace(legacyFirst))
        {
            items.Add(legacyFirst.Trim().ToLowerInvariant());
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
        string Title,
        string VideoId,
        string PosterUrl);

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
