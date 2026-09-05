using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure;
using System.Buffers.Binary;
using System.Diagnostics;
using ShortDrama.Infrastructure.Automation;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ShortDrama.Infrastructure.Automation;

public sealed class DramaSourceRouter : IDramaSearchService, IDramaDownloader
{
    private static readonly string[] SearchServices = ["hgnew", "hglocal", "pikachu", "hghigh", "mapleleaf", "downloader"];
    private static readonly string[] NewReleaseServices = ["hgnew", "hglocal", "hghigh", "mapleleaf", "downloader"];
    private const string DownloadStateFileName = ".weixin-channel-download-state.json";
    private const string EpisodeNumberModeContinuous = "continuous";
    private const int DownloadBufferSize = 128 * 1024;
    private const int DefaultDownloadFileSegments = 4;
    private const int MaxDownloadFileSegments = 16;
    private const int MapleleafMinimumDownloadTimeoutSeconds = 15 * 60;
    private const int DefaultPlayUrlTimeoutSeconds = 15;
    private const int DefaultPlayUrlResolveConcurrency = 4;
    private const long MinSegmentedDownloadSize = 4L * 1024 * 1024;
    private const string DownloadUserAgent = "Mozilla/5.0";
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif"];
    private static readonly ProductInfoHeaderValue UserAgentProduct = new("ShortDramaDesktop", "1.0");
    private static readonly string MobileUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
    internal static AsyncLocal<Func<string>?> ResolveFfmpegBinaryForTests { get; } = new();
    internal static AsyncLocal<Func<string>?> ResolveFfprobeBinaryForTests { get; } = new();
    internal static AsyncLocal<Func<ProcessStartInfo, CancellationToken, Task<ProcessRunResult>>?> RunProcessAsyncForTests { get; } = new();

    private readonly HttpClient _httpClient;
    private readonly IDramaSettingsProvider _settingsProvider;
    private readonly HongguoLocalApiService _hglocalApiService;
    private readonly HongguoNewApiService _hgnewApiService;
    private readonly HongguoDramaSearchService _hgnewSearchService;
    private readonly HongguoDramaDownloader _hgnewDownloader;
    private readonly HongguoMemoryReaderService _hongguoMemoryReaderService;
    private readonly HongguoHighApiService _hghighApiService;
    private readonly MapleleafApiService _mapleleafApiService;
    private readonly DownloaderGatewayApiService _downloaderApiService;

    public DramaSourceRouter(
        HttpClient httpClient,
        IDramaSettingsProvider settingsProvider,
        HongguoLocalApiService hglocalApiService,
        HongguoNewApiService hgnewApiService,
        HongguoDramaSearchService hgnewSearchService,
        HongguoDramaDownloader hgnewDownloader,
        HongguoMemoryReaderService hongguoMemoryReaderService)
        : this(
            httpClient,
            settingsProvider,
            hglocalApiService,
            hgnewApiService,
            hgnewSearchService,
            hgnewDownloader,
            hongguoMemoryReaderService,
            new HongguoHighApiService(httpClient),
            new MapleleafApiService(httpClient))
    {
    }

    public DramaSourceRouter(
        HttpClient httpClient,
        IDramaSettingsProvider settingsProvider,
        HongguoLocalApiService hglocalApiService,
        HongguoNewApiService hgnewApiService,
        HongguoDramaSearchService hgnewSearchService,
        HongguoDramaDownloader hgnewDownloader,
        HongguoMemoryReaderService hongguoMemoryReaderService,
        HongguoHighApiService hghighApiService)
        : this(
            httpClient,
            settingsProvider,
            hglocalApiService,
            hgnewApiService,
            hgnewSearchService,
            hgnewDownloader,
            hongguoMemoryReaderService,
            hghighApiService,
            new MapleleafApiService(httpClient))
    {
    }

    public DramaSourceRouter(
        HttpClient httpClient,
        IDramaSettingsProvider settingsProvider,
        HongguoLocalApiService hglocalApiService,
        HongguoNewApiService hgnewApiService,
        HongguoDramaSearchService hgnewSearchService,
        HongguoDramaDownloader hgnewDownloader,
        HongguoMemoryReaderService hongguoMemoryReaderService,
        HongguoHighApiService hghighApiService,
        MapleleafApiService mapleleafApiService)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _hglocalApiService = hglocalApiService;
        _hgnewApiService = hgnewApiService;
        _hgnewSearchService = hgnewSearchService;
        _hgnewDownloader = hgnewDownloader;
        _hongguoMemoryReaderService = hongguoMemoryReaderService;
        _hghighApiService = hghighApiService;
        _mapleleafApiService = mapleleafApiService;
        _downloaderApiService = new DownloaderGatewayApiService(httpClient);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        var source = ResolveSelectedService(settings.DramaSourceChain, SearchServices);
        return source switch
        {
            "hgnew" => await SearchHgnewAsync(keyword, page, settings, cancellationToken),
            "hglocal" => await SearchLocalAsync(keyword, page, settings, cancellationToken),
            "pikachu" => await SearchPikachuAsync(keyword, page, settings, cancellationToken),
            "hghigh" => await _hghighApiService.SearchAsync(settings, keyword, page, cancellationToken),
            "mapleleaf" => await _mapleleafApiService.SearchAsync(settings, keyword, page, cancellationToken),
            "downloader" => await _downloaderApiService.SearchAsync(settings, keyword, page, cancellationToken),
            _ => []
        };
    }

    public async Task<int> ProbePikachuSearchAsync(DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var items = await SearchPikachuAsync("测试", 1, settings, cancellationToken);
        return items.Count;
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetTodayAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        var source = ResolveSelectedService(settings.DramaSourceChain, NewReleaseServices);
        if (string.Equals(source, "hghigh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("红果高码率暂不支持短剧今日上新，请使用漫剧上新或关键词搜索。");
        }

        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => _hgnewApiService.GetTodayNewAsync(settings, "djnew", ct),
            hglocalLoader: ct => GetLocalTodayAsync(settings, ct),
            cancellationToken,
            mapleleafLoader: ct => _mapleleafApiService.GetLatestAsync(settings, "djnew", 1, ct),
            downloaderLoader: ct => _downloaderApiService.GetLatestAsync(settings, "short", 1, ct));
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetMangaTodayAsync(int days, CancellationToken cancellationToken)
        => await GetMangaTodayAsync(days, enrich: true, progress: null, cancellationToken);

    public async Task<IReadOnlyList<DramaSearchItem>> GetMangaTodayAsync(
        int days,
        bool enrich,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        var settings = _settingsProvider.Get();
        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewMangaTodayAsync(settings, days, ct),
            hglocalLoader: ct => GetLatestByGenreAsync(settings, "comic_series", days, ct),
            cancellationToken: cancellationToken,
            hghighLoader: ct => _hghighApiService.GetManjuNewAsync(
                settings, days, enrich, progress, ct, partialResults, detailResults),
            mapleleafLoader: ct => _mapleleafApiService.GetLatestAsync(settings, "mjnew", days, ct),
            downloaderLoader: ct => _downloaderApiService.GetLatestAsync(settings, "comic", days, ct));
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetAiTodayAsync(int days, CancellationToken cancellationToken)
        => await GetAiTodayAsync(days, enrich: true, progress: null, cancellationToken);

    public async Task<IReadOnlyList<DramaSearchItem>> GetAiTodayAsync(
        int days,
        bool enrich,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        var settings = _settingsProvider.Get();
        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewAiTodayAsync(settings, days, ct),
            hglocalLoader: ct => GetLatestByGenreAsync(settings, "ai_series", days, ct),
            cancellationToken: cancellationToken,
            hghighLoader: ct => _hghighApiService.GetAiNewAsync(
                settings, days, enrich, progress, ct, partialResults, detailResults),
            mapleleafLoader: ct => _mapleleafApiService.GetLatestAsync(settings, "aiju", days, ct),
            downloaderLoader: ct => _downloaderApiService.GetLatestAsync(settings, "ai", days, ct));
    }

    public bool IsHighSourceSelected() =>
        string.Equals(_settingsProvider.Get().DramaSourceChain?.Trim(), "hghigh", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DramaSearchItem>> EnrichHighNewReleaseItemsAsync(
        IReadOnlyList<DramaSearchItem> items,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        var settings = _settingsProvider.Get();
        if (!string.Equals(
                settings.DramaSourceChain?.Trim(),
                "hghigh",
                StringComparison.OrdinalIgnoreCase))
        {
            return items;
        }
        return await _hghighApiService.EnrichNewReleaseItemsAsync(
            settings,
            items,
            progress,
            cancellationToken,
            detailResults);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetHistoryAsync(int days, CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        var source = ResolveSelectedService(settings.DramaSourceChain, NewReleaseServices);
        if (string.Equals(source, "hghigh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("红果高码率暂不支持短剧历史上新，请使用漫剧上新或关键词搜索。");
        }

        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewHistoryAsync(settings, days, ct),
            hglocalLoader: ct => GetLatestByGenreAsync(settings, "short_play", days, ct),
            cancellationToken,
            mapleleafLoader: ct => _mapleleafApiService.GetLatestAsync(settings, "djnew", days, ct),
            downloaderLoader: ct => _downloaderApiService.GetLatestAsync(settings, "short", days, ct));
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
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IReadOnlyList<DramaSearchItem>>>? hghighLoader = null,
        Func<CancellationToken, Task<IReadOnlyList<DramaSearchItem>>>? mapleleafLoader = null,
        Func<CancellationToken, Task<IReadOnlyList<DramaSearchItem>>>? downloaderLoader = null)
    {
        var source = ResolveSelectedService(settings.DramaSourceChain, NewReleaseServices);
        return source switch
        {
            "hgnew" => await hgnewLoader(cancellationToken),
            "hglocal" => await hglocalLoader(cancellationToken),
            "hghigh" => hghighLoader is null ? [] : await hghighLoader(cancellationToken),
            "mapleleaf" => mapleleafLoader is null ? [] : await mapleleafLoader(cancellationToken),
            "downloader" => downloaderLoader is null ? [] : await downloaderLoader(cancellationToken),
            _ => []
        };
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
        var downloadFileSegments = ParseDownloadFileSegments(settings.DownloadFileSegments);
        var bookId = request.BookId?.Trim() ?? string.Empty;

        if (bookId.StartsWith(DownloaderGatewayApiService.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
            bookId.StartsWith(DownloaderGatewayApiService.EpisodePrefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.DramaSourceChain?.Trim(), "downloader", StringComparison.OrdinalIgnoreCase))
        {
            var downloaderBookId = bookId.StartsWith(DownloaderGatewayApiService.BookPrefix, StringComparison.OrdinalIgnoreCase)
                ? bookId
                : DownloaderGatewayApiService.BookPrefix + bookId;
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetDownloaderEpisodesAsync(downloaderBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetDownloaderVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: DownloaderGatewayApiService.BookPrefix,
                validateVideoEncoding: ShouldValidateVideoEncodingForSource("downloader"),
                downloadFileSegments: Math.Max(downloadFileSegments, 8),
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts,
                separateResolveConcurrency: Math.Min(4, Math.Clamp(request.Concurrent, 1, 10)));
        }

        if (bookId.StartsWith(MapleleafApiService.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
            bookId.StartsWith(MapleleafApiService.EpisodePrefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.DramaSourceChain?.Trim(), "mapleleaf", StringComparison.OrdinalIgnoreCase))
        {
            var mapleleafBookId = MapleleafApiService.EnsureBookPrefix(bookId);
            var mapleleafTimeoutSeconds = ResolveProviderDownloadTimeoutSeconds(
                "mapleleaf",
                downloadTimeoutSeconds);
            var mapleleafPlayUrlTimeoutSeconds = ResolvePlayUrlTimeoutSeconds(downloadTimeoutSeconds);
            if (mapleleafTimeoutSeconds > downloadTimeoutSeconds)
            {
                progress?.Report(
                    $"Mapleleaf 慢速 CDN 保护：整集下载时限由 {downloadTimeoutSeconds} 秒" +
                    $"延长到 {mapleleafTimeoutSeconds} 秒；继续使用最多 16 路分块。");
            }
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetMapleleafEpisodesAsync(mapleleafBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetMapleleafVideoUrlAsync(
                    videoId,
                    quality,
                    settings,
                    mapleleafPlayUrlTimeoutSeconds,
                    ct),
                posterPrefix: MapleleafApiService.BookPrefix,
                validateVideoEncoding: ShouldValidateVideoEncodingForSource("mapleleaf"),
                downloadFileSegments: Math.Max(downloadFileSegments, 16),
                downloadTimeoutSeconds: mapleleafTimeoutSeconds,
                downloadAttempts: downloadAttempts,
                separateResolveConcurrency: DefaultPlayUrlResolveConcurrency);
        }

        if (bookId.StartsWith(HongguoLocalBookPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetLocalEpisodesAsync(bookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetLocalVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: HongguoLocalBookPrefix,
                validateVideoEncoding: ShouldValidateVideoEncodingForSource("hglocal"),
                downloadFileSegments: downloadFileSegments,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts);
        }

        if (bookId.StartsWith(HongguoHighCrypto.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
            bookId.StartsWith(HongguoHighCrypto.EpisodePrefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.DramaSourceChain?.Trim(), "hghigh", StringComparison.OrdinalIgnoreCase))
        {
            var highBookId = HongguoHighCrypto.EnsureBookPrefix(bookId);
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetHghighEpisodesAsync(highBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetHghighVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: HongguoHighCrypto.BookPrefix,
                validateVideoEncoding: ShouldValidateVideoEncodingForSource("hghigh"),
                downloadFileSegments: Math.Max(downloadFileSegments, 8),
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts,
                separateResolveConcurrency: Math.Min(4, Math.Clamp(request.Concurrent, 1, 10)),
                registerResolvePlan: (videoIds, batchSize) =>
                {
                    var mode = HongguoClientProfile.NormalizeEdition(settings.HghighEdition) == HongguoClientProfile.StandardEdition
                        ? "标准版明文直链"
                        : "高码率";
                    progress?.Report($"{mode}播放地址启用批量解析：每批 {batchSize} 集，共 {videoIds.Count} 集");
                    return _hghighApiService.RegisterBatchParsePlan(settings, videoIds, request.Quality, batchSize);
                });
        }

        if (bookId.StartsWith(PikachuBookPrefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.DramaSourceChain?.Trim(), "pikachu", StringComparison.OrdinalIgnoreCase))
        {
            var pikachuBookId = EnsurePrefixed(bookId, PikachuBookPrefix);
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetPikachuEpisodesAsync(pikachuBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetPikachuVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: PikachuBookPrefix,
                validateVideoEncoding: ShouldValidateVideoEncodingForSource("pikachu"),
                downloadFileSegments: downloadFileSegments,
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
                validateVideoEncoding: ShouldValidateVideoEncodingForSource("hgnew"),
                downloadFileSegments: downloadFileSegments,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            var existingResult = await TryBuildSuccessfulResultWhenVideosExistAsync(request, progress, cancellationToken);
            if (existingResult is not null)
            {
                progress?.Report(existingResult.Message ?? "已存在视频文件，跳过 legacy 红果重复下载。");
                return existingResult;
            }

            return await _hgnewDownloader.DownloadAsync(request, progress, cancellationToken);
        }
    }

    internal static int ResolveProviderDownloadTimeoutSeconds(
        string? source,
        int configuredTimeoutSeconds)
    {
        var configured = Math.Clamp(configuredTimeoutSeconds, 10, 600);
        return string.Equals(
            (source ?? string.Empty).Trim(),
            "mapleleaf",
            StringComparison.OrdinalIgnoreCase)
            ? Math.Max(configured, MapleleafMinimumDownloadTimeoutSeconds)
            : configured;
    }

    internal static int ResolvePlayUrlTimeoutSeconds(int configuredSeconds)
    {
        var configured = configuredSeconds <= 0 ? DefaultPlayUrlTimeoutSeconds : configuredSeconds;
        return Math.Clamp(configured, 5, DefaultPlayUrlTimeoutSeconds);
    }

    internal static Task<DramaDownloadResult?> TryBuildSuccessfulResultWhenVideosExistAsync(
        DramaDownloadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var videoCount = 0;
        if (Directory.Exists(request.OutputDir))
        {
            foreach (var path in Directory.EnumerateFiles(request.OutputDir, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
            {
                if (HasValidVideoFile(path))
                {
                    videoCount++;
                }
                else
                {
                    DeleteIfExists(path);
                    progress?.Report($"检测到无效或空视频文件并已清理：{Path.GetFileName(path)}");
                }
            }
        }

        if (videoCount <= 0)
        {
            return Task.FromResult<DramaDownloadResult?>(null);
        }

        return Task.FromResult<DramaDownloadResult?>(new DramaDownloadResult(
            Ok: true,
            OutputDir: request.OutputDir,
            VideoCount: videoCount,
            Message: $"已存在 {videoCount} 个视频文件，跳过 legacy 红果下载重试。"));
    }

    private async Task<DramaDownloadResult> DownloadWithProviderAsync(
        DramaDownloadRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IReadOnlyList<SourceEpisode>>> resolveEpisodes,
        Func<string, string, CancellationToken, Task<SourceVideoDetail>> resolveVideo,
        string posterPrefix,
        bool validateVideoEncoding,
        int downloadFileSegments,
        int downloadTimeoutSeconds,
        int downloadAttempts,
        int separateResolveConcurrency = 0,
        Func<IReadOnlyList<string>, int, IDisposable>? registerResolvePlan = null)
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
        var concurrency = Math.Clamp(request.Concurrent, 1, 10);
        var resolveConcurrency = Math.Clamp(separateResolveConcurrency, 0, 10);
        var validateReplacement = RequiresVideoEncodingValidation(
            validateVideoEncoding,
            request.ExistingVideoPolicy);
        var plannedVideoIds = new List<string>(tasks.Count);
        if (registerResolvePlan is not null)
        {
            foreach (var task in tasks)
            {
                var existing = await FindExistingEpisodeVideoAsync(
                    request.OutputDir,
                    task.EpisodeNumber,
                    report: null,
                    validateReplacement,
                    request.ExistingVideoPolicy,
                    replacementCandidates: null,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(existing))
                    plannedVideoIds.Add(task.VideoId);
            }
        }

        using var resolvePlan = registerResolvePlan?.Invoke(
            plannedVideoIds,
            concurrency);
        using var downloadSemaphore = new SemaphoreSlim(concurrency);
        using var resolveSemaphore = resolveConcurrency > 0 ? new SemaphoreSlim(resolveConcurrency) : null;

        var downloads = tasks.Select(task => DownloadEpisodeAsync(
            request.OutputDir,
            request.Quality,
            task,
            tasks.Count,
            resolveVideo,
            progress,
            downloadSemaphore,
            resolveSemaphore,
            failures,
            validateReplacement,
            request.ExistingVideoPolicy,
            downloadFileSegments,
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

    internal static bool RequiresVideoEncodingValidation(
        bool sourceDefault,
        ExistingVideoPolicy existingVideoPolicy) => sourceDefault;

    internal static bool ShouldValidateVideoEncodingForSource(string? source) =>
        string.Equals(source?.Trim(), "hglocal", StringComparison.OrdinalIgnoreCase);

    private async Task DownloadEpisodeAsync(
        string outputDir,
        string quality,
        EpisodeTask task,
        int totalCount,
        Func<string, string, CancellationToken, Task<SourceVideoDetail>> resolveVideo,
        IProgress<string>? progress,
        SemaphoreSlim downloadSemaphore,
        SemaphoreSlim? resolveSemaphore,
        ICollection<string> failures,
        bool validateVideoEncoding,
        ExistingVideoPolicy existingVideoPolicy,
        int downloadFileSegments,
        int downloadTimeoutSeconds,
        int downloadAttempts,
        CancellationToken cancellationToken)
    {
        var lifecycleSlotHeld = false;
        if (resolveSemaphore is null)
        {
            await downloadSemaphore.WaitAsync(cancellationToken);
            lifecycleSlotHeld = true;
        }
        try
        {
            var finalPath = Path.Combine(outputDir, BuildEpisodeFileName(task));
            var tempPath = $"{finalPath}.part";
            var replacementCandidates = new List<string>();
            var existingVideo = await FindExistingEpisodeVideoAsync(
                outputDir,
                task.EpisodeNumber,
                message => progress?.Report($"[{task.Order:00}/{totalCount:00}] {message}"),
                validateVideoEncoding,
                existingVideoPolicy,
                replacementCandidates,
                cancellationToken);
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

            // Preserve an existing final file until the replacement has downloaded,
            // decrypted and passed validation. SafeReplaceAsync swaps it only after
            // the new temporary file is ready.
            CleanupDownloadArtifacts(finalPath, keepVideo: true);
            var started = false;

            async Task<SourceVideoDetail> ResolveAsync()
            {
                if (resolveSemaphore is null)
                {
                    if (!started)
                    {
                        progress?.Report($"[{task.Order:00}/{totalCount:00}] 开始下载第{task.EpisodeNumber:00}集");
                        started = true;
                    }
                    return await resolveVideo(task.VideoId, quality, cancellationToken);
                }

                await resolveSemaphore.WaitAsync(cancellationToken);
                try
                {
                    if (!started)
                    {
                        progress?.Report($"[{task.Order:00}/{totalCount:00}] 开始下载第{task.EpisodeNumber:00}集");
                        started = true;
                    }
                    return await resolveVideo(task.VideoId, quality, cancellationToken);
                }
                finally
                {
                    resolveSemaphore.Release();
                }
            }

            async Task<DownloadFileStats> DownloadAsync(SourceVideoDetail detail)
            {
                if (resolveSemaphore is not null)
                    await downloadSemaphore.WaitAsync(cancellationToken);
                try
                {
                    return await DownloadVideoFileOnceAsync(
                        detail.Url,
                        tempPath,
                        finalPath,
                        detail.ExpectedSize,
                        downloadTimeoutSeconds,
                        cancellationToken,
                        detail.PikachuDecryptKey,
                        detail.HongguoCdn,
                        validateVideoEncoding,
                        downloadFileSegments,
                        detail.EnsureWindowsCompatible,
                        detail.TranscodeEngine,
                        message => progress?.Report($"[{task.Order:00}/{totalCount:00}] {message}"));
                }
                finally
                {
                    if (resolveSemaphore is not null)
                        downloadSemaphore.Release();
                }
            }

            var maxAttempts = Math.Clamp(downloadAttempts, 1, 20);
            for (var attempt = 1; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var detail = await ResolveAsync();
                    var stats = await DownloadAsync(detail);
                    DeleteReplacedAlternateFiles(finalPath, replacementCandidates);
                    progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载完成（{FormatBytes(stats.Bytes)}, {stats.Elapsed.TotalSeconds:0.#}s, {FormatBytes(stats.BytesPerSecond)}/s；{stats.MediaSummary}）");
                    return;
                }
                catch (Exception ex) when (ShouldRetryDownload(ex))
                {
                    CleanupDownloadArtifacts(finalPath, keepVideo: true);
                    progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载重试 {attempt}/{maxAttempts}: {ex.Message}");
                    await Task.Delay(ResolveDownloadRetryDelay(ex, attempt), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    CleanupDownloadArtifacts(finalPath, keepVideo: true);
                    lock (failures)
                    {
                        failures.Add($"第{task.EpisodeNumber:00}集 {ex.Message}");
                    }
                    progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载失败: {ex.Message}");
                    return;
                }
            }

            try
            {
                var detail = await ResolveAsync();
                var stats = await DownloadAsync(detail);
                DeleteReplacedAlternateFiles(finalPath, replacementCandidates);
                progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载完成（{FormatBytes(stats.Bytes)}, {stats.Elapsed.TotalSeconds:0.#}s, {FormatBytes(stats.BytesPerSecond)}/s；{stats.MediaSummary}）");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CleanupDownloadArtifacts(finalPath, keepVideo: true);
                lock (failures)
                {
                    failures.Add($"第{task.EpisodeNumber:00}集 {ex.Message}");
                }
                progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集下载失败: {ex.Message}");
            }
        }
        finally
        {
            if (lifecycleSlotHeld)
                downloadSemaphore.Release();
        }
    }

    private async Task DownloadVideoFileOnceAsync(
        string url,
        string tempPath,
        string finalPath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
        => await DownloadVideoFileOnceAsync(
            url,
            tempPath,
            finalPath,
            expectedSize: 0,
            timeoutSeconds,
            cancellationToken,
            pikachuDecryptKey: null,
            hongguoCdn: null,
            validateVideoEncoding: false,
            downloadFileSegments: DefaultDownloadFileSegments,
            ensureWindowsCompatible: false,
            transcodeEngine: "auto",
            report: null);

    private async Task<DownloadFileStats> DownloadVideoFileOnceAsync(
        string url,
        string tempPath,
        string finalPath,
        long expectedSize,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? pikachuDecryptKey,
        HongguoCdnDownload? hongguoCdn,
        bool validateVideoEncoding,
        int downloadFileSegments,
        bool ensureWindowsCompatible,
        string transcodeEngine,
        Action<string>? report)
    {
        var hasPikachuDecryptKey = !string.IsNullOrWhiteSpace(pikachuDecryptKey);
        var encryptedTempPath = hasPikachuDecryptKey ? BuildEncryptedTempPath(tempPath) : null;
        var downloadTargetPath = encryptedTempPath ?? tempPath;
        if (encryptedTempPath is not null)
        {
            DeleteIfExists(encryptedTempPath);
            DeleteIfExists(tempPath);
        }

        var clampedTimeoutSeconds = Math.Clamp(timeoutSeconds, 10, 600);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(clampedTimeoutSeconds));
        var token = timeoutCts.Token;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var usedHongguoCdn = false;
            if (hongguoCdn is { EncryptedUrls.Count: > 0 } && !string.IsNullOrWhiteSpace(hongguoCdn.SpadeA))
            {
                try
                {
                    report?.Invoke($"使用 CDN 直连 + 本地解密（单文件最多 {downloadFileSegments} 路分块）");
                    await DownloadAndDecryptHongguoCdnAsync(
                        hongguoCdn,
                        tempPath,
                        downloadFileSegments,
                        report,
                        token);
                    usedHongguoCdn = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DeleteIfExists(BuildHongguoEncryptedTempPath(tempPath));
                    DeleteIfExists(tempPath);
                    report?.Invoke($"CDN 本地解密失败，回退服务器流：{ex.Message}");
                }
            }

            if (!usedHongguoCdn)
            {
                await DownloadHttpContentAsync(
                    url,
                    downloadTargetPath,
                    downloadFileSegments,
                    message => report?.Invoke(message),
                    token);
            }

            if (!usedHongguoCdn && hasPikachuDecryptKey)
            {
                await DecryptPikachuCencVideoAsync(pikachuDecryptKey!.Trim(), downloadTargetPath, tempPath, timeoutSeconds, cancellationToken);
            }

            var actualSize = new FileInfo(tempPath).Length;
            if (expectedSize > 0 && actualSize != expectedSize)
            {
                throw new InvalidDataException($"下载文件长度不完整：预期 {expectedSize} 字节，实际 {actualSize} 字节。");
            }

            if (LooksLikeMp4(tempPath) && !HasCompleteMp4Structure(tempPath))
            {
                throw new InvalidDataException("下载的 MP4 结构不完整。");
            }

            if (!usedHongguoCdn && ContainsEncryptedMp4SampleEntry(tempPath))
            {
                throw new InvalidDataException("下载结果仍为加密 MP4，未获得可直接播放的明文视频。");
            }

            if (!HasValidVideoFile(tempPath))
            {
                throw new InvalidDataException("下载内容不是有效的视频文件。");
            }

            VideoProcessingResult? processing = null;
            if (validateVideoEncoding && ensureWindowsCompatible)
            {
                processing = await EnsureWindowsCompatibleMp4Async(
                    tempPath,
                    timeoutSeconds,
                    cancellationToken,
                    transcodeEngine,
                    report);
            }
            else if (validateVideoEncoding)
            {
                var codec = await ProbePrimaryVideoCodecAsync(tempPath, cancellationToken).ConfigureAwait(false);
                processing = new VideoProcessingResult(codec, Transcoded: false, TranscodeEngine: null);
                report?.Invoke($"视频校验通过，编码 {codec.ToUpperInvariant()}，快速模式保留原文件");
            }

            await DownloadFileOperations.DelayAfterWriteAsync(cancellationToken);
            await DownloadFileOperations.SafeReplaceAsync(tempPath, finalPath, cancellationToken);

            stopwatch.Stop();
            var finalBytes = new FileInfo(finalPath).Length;
            return new DownloadFileStats(
                finalBytes,
                stopwatch.Elapsed,
                finalBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d),
                processing is null
                    ? "保留源文件"
                    : processing.Transcoded
                    ? $"{processing.TranscodeEngine} 已转为 H.264"
                    : $"视频编码 {processing.Codec.ToUpperInvariant()}，无需转码");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"下载超过 {clampedTimeoutSeconds} 秒未完成，已中止并准备重试。", ex);
        }
        finally
        {
            if (encryptedTempPath is not null)
            {
                DeleteIfExists(encryptedTempPath);
            }

            DeleteIfExists(tempPath);
            DeleteIfExists(BuildHongguoEncryptedTempPath(tempPath));
        }
    }

    private async Task DownloadAndDecryptHongguoCdnAsync(
        HongguoCdnDownload cdn,
        string outputPath,
        int segments,
        Action<string>? report,
        CancellationToken cancellationToken)
    {
        var encryptedPath = BuildHongguoEncryptedTempPath(outputPath);
        Exception? lastDownloadError = null;
        var downloaded = false;
        foreach (var candidateUrl in cdn.EncryptedUrls)
        {
            try
            {
                DeleteIfExists(encryptedPath);
                await DownloadHttpContentAsync(candidateUrl, encryptedPath, segments, report, cancellationToken);
                downloaded = true;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastDownloadError = ex;
            }
        }

        if (!downloaded || !HasValidVideoFile(encryptedPath))
            throw new InvalidDataException($"加密 CDN 下载失败：{lastDownloadError?.Message ?? "文件无效"}", lastDownloadError);

        if (!cdn.Encrypted)
        {
            await DownloadFileOperations.SafeReplaceAsync(encryptedPath, outputPath, cancellationToken);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => HongguoCdnDecryptor.Decrypt(cdn.SpadeA, encryptedPath, outputPath), cancellationToken);
        DeleteIfExists(encryptedPath);
    }

    private async Task DownloadHttpContentAsync(
        string url,
        string targetPath,
        int requestedSegments,
        Action<string>? report,
        CancellationToken cancellationToken)
    {
        var segments = Math.Clamp(
            requestedSegments <= 0 ? DefaultDownloadFileSegments : requestedSegments,
            1,
            MaxDownloadFileSegments);
        if (segments <= 1)
        {
            await DownloadSingleStreamAsync(url, targetPath, cancellationToken);
            return;
        }

        long totalBytes = 0;
        try
        {
            using var probeRequest = CreateDownloadRequest(url);
            probeRequest.Headers.Range = new RangeHeaderValue(0, 0);
            using var probeResponse = await _httpClient.SendAsync(
                probeRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (probeResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                EnsureMediaResponse(probeResponse);
                var contentRange = probeResponse.Content.Headers.ContentRange;
                if (contentRange?.From != 0 || contentRange.To != 0 || contentRange.Length is null or <= 0)
                {
                    throw new InvalidDataException("Range 探测返回的 Content-Range 无效。");
                }

                totalBytes = contentRange.Length.Value;
            }
            else if (probeResponse.IsSuccessStatusCode)
            {
                EnsureMediaResponse(probeResponse);
                report?.Invoke("CDN 未返回 HTTP 206，已改用单流下载");
                await WriteResponseBodyAsync(probeResponse, targetPath, cancellationToken);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Range 探测失败时由单流下载兜底。
        }

        var segmentCount = PlanDownloadSegmentCount(totalBytes, segments);
        if (segmentCount > 1)
        {
            try
            {
                report?.Invoke($"启用 {segmentCount} 路分块下载（{FormatBytes(totalBytes)}）");
                await DownloadSegmentedAsync(
                    url,
                    targetPath,
                    totalBytes,
                    segmentCount,
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DeleteIfExists(targetPath);
                if (ex is InvalidDataException)
                {
                    report?.Invoke($"{segmentCount} 路分块响应范围不匹配，将尝试备用地址或刷新地址后重试：{ex.Message}");
                    throw;
                }

                report?.Invoke($"{segmentCount} 路分块下载不可用，已自动切换单流下载：{ex.Message}");
            }
        }

        await DownloadSingleStreamAsync(url, targetPath, cancellationToken);
    }

    private async Task DownloadSingleStreamAsync(
        string url,
        string targetPath,
        CancellationToken cancellationToken)
    {
        using var request = CreateDownloadRequest(url);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException("单流下载意外返回 HTTP 206，拒绝保存不完整分片。");
        }
        EnsureMediaResponse(response);
        await WriteResponseBodyAsync(response, targetPath, cancellationToken);
    }

    private async Task DownloadSegmentedAsync(
        string url,
        string targetPath,
        long totalBytes,
        int segmentCount,
        CancellationToken cancellationToken)
    {
        await using (var file = new FileStream(
                         targetPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.ReadWrite,
                         DownloadBufferSize,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            file.SetLength(totalBytes);
            await file.FlushAsync(cancellationToken);
        }

        var ranges = BuildDownloadRanges(totalBytes, segmentCount);
        await Task.WhenAll(ranges.Select(range => DownloadRangeAsync(
            url,
            targetPath,
            range.Start,
            range.End,
            totalBytes,
            cancellationToken)));
    }

    private async Task DownloadRangeAsync(
        string url,
        string targetPath,
        long start,
        long end,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        const int maxRangeAttempts = 3;
        for (var attempt = 1; attempt <= maxRangeAttempts; attempt++)
        {
            try
            {
                await DownloadRangeOnceAsync(url, targetPath, start, end, totalBytes, cancellationToken);
                return;
            }
            catch (InvalidDataException) when (attempt < maxRangeAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
            }
        }
    }

    private async Task DownloadRangeOnceAsync(
        string url,
        string targetPath,
        long start,
        long end,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateDownloadRequest(url);
        request.Headers.Range = new RangeHeaderValue(start, end);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException($"分块下载要求 HTTP 206，实际为 {(int)response.StatusCode}。");
        }

        EnsureMediaResponse(response);
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.From != start ||
            contentRange.To != end ||
            (contentRange.Length.HasValue && contentRange.Length.Value != totalBytes))
        {
            throw new InvalidDataException("分块下载返回的 Content-Range 与请求范围不一致。");
        }

        var expectedBytes = end - start + 1;
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != expectedBytes)
        {
            throw new InvalidDataException("分块下载返回的 Content-Length 与请求范围不一致。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            DownloadBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        file.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[DownloadBufferSize];
        var remaining = expectedBytes;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read <= 0)
            {
                throw new EndOfStreamException("分块下载提前结束，数据不完整。");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }

        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) > 0)
        {
            throw new InvalidDataException("分块下载返回的数据超过请求范围。");
        }

        await file.FlushAsync(cancellationToken);
    }

    private static HttpRequestMessage CreateDownloadRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DownloadUserAgent);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        return request;
    }

    private static void EnsureMediaResponse(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (IsClearlyNonMediaContentType(mediaType))
        {
            throw new InvalidDataException($"下载响应不是视频（Content-Type: {mediaType}）。");
        }
    }

    private static async Task WriteResponseBodyAsync(
        HttpResponseMessage response,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DownloadBufferSize,
            useAsync: true);
        await source.CopyToAsync(file, DownloadBufferSize, cancellationToken);
        await file.FlushAsync(cancellationToken);
    }

    private static int PlanDownloadSegmentCount(long totalBytes, int requestedSegments)
    {
        if (totalBytes < MinSegmentedDownloadSize || requestedSegments <= 1)
        {
            return 1;
        }

        var byMinimumPartSize = (int)Math.Max(1, totalBytes / (1024 * 1024));
        return Math.Clamp(Math.Min(requestedSegments, byMinimumPartSize), 1, MaxDownloadFileSegments);
    }

    private static IReadOnlyList<(long Start, long End)> BuildDownloadRanges(long totalBytes, int segmentCount)
    {
        var ranges = new List<(long Start, long End)>(segmentCount);
        var partSize = totalBytes / segmentCount;
        long start = 0;
        for (var index = 0; index < segmentCount; index++)
        {
            var end = index == segmentCount - 1 ? totalBytes - 1 : start + partSize - 1;
            ranges.Add((start, end));
            start = end + 1;
        }

        return ranges;
    }

    private static async Task DecryptPikachuCencVideoAsync(
        string decryptKey,
        string encryptedPath,
        string outputPath,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = ResolveFfmpegBinaryForTests.Value?.Invoke() ?? ResolveFfmpegBinary();
        var clampedTimeoutSeconds = Math.Clamp(timeoutSeconds + 120, 60, 900);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(clampedTimeoutSeconds));

        DeleteIfExists(outputPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-decryption_key");
        startInfo.ArgumentList.Add(decryptKey);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(encryptedPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add(outputPath);

        try
        {
            var runner = RunProcessAsyncForTests.Value ?? RunProcessAsyncDefault;
            var result = await runner(startInfo, timeoutCts.Token);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Pikachu CENC decrypt failed: {TrimProcessOutput(result.StandardError)}");
            }

            if (!HasValidVideoFile(outputPath))
            {
                throw new InvalidOperationException("Pikachu CENC decrypt did not produce a playable mp4 file.");
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Pikachu CENC decrypt timed out after {clampedTimeoutSeconds} seconds.", ex);
        }
    }

    private static async Task<VideoProcessingResult> EnsureWindowsCompatibleMp4Async(
        string path,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string transcodeEngine,
        Action<string>? report)
    {
        var codec = await ProbePrimaryVideoCodecAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase))
        {
            report?.Invoke($"视频校验通过，编码 {codec.ToUpperInvariant()}，无需转码");
            return new VideoProcessingResult(codec, Transcoded: false, TranscodeEngine: null);
        }

        var ffmpegPath = ResolveFfmpegBinaryForTests.Value?.Invoke() ?? ResolveFfmpegBinary();
        var outputPath = $"{path}.h264.mp4";
        DeleteIfExists(outputPath);

        var clampedTimeoutSeconds = Math.Clamp(timeoutSeconds + 300, 300, 1800);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(clampedTimeoutSeconds));

        try
        {
            var runner = RunProcessAsyncForTests.Value ?? RunProcessAsyncDefault;
            var plans = await ResolveH264TranscodePlansAsync(
                ffmpegPath,
                path,
                outputPath,
                NormalizeHongguoLocalTranscodeEngine(transcodeEngine),
                runner,
                timeoutCts.Token).ConfigureAwait(false);

            for (var index = 0; index < plans.Count; index++)
            {
                DeleteIfExists(outputPath);
                var plan = plans[index];
                try
                {
                    report?.Invoke($"检测到 HEVC，开始使用 {plan.Name} 转码为 H.264");
                    var result = await runner(plan.StartInfo, timeoutCts.Token).ConfigureAwait(false);
                    if (result.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"{plan.Name} HEVC to H.264 transcode failed: {TrimProcessOutput(result.StandardError)}");
                    }

                    var outputCodec = await ProbePrimaryVideoCodecAsync(outputPath, timeoutCts.Token).ConfigureAwait(false);
                    if (!string.Equals(outputCodec, "h264", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(outputCodec, "avc", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(outputCodec, "avc1", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"{plan.Name} 转码结果编码异常：{outputCodec}。");
                    }

                    await DownloadFileOperations.SafeReplaceAsync(outputPath, path, cancellationToken).ConfigureAwait(false);
                    report?.Invoke($"{plan.Name} 转码完成，输出编码 H.264");
                    return new VideoProcessingResult("h264", Transcoded: true, TranscodeEngine: plan.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && index + 1 < plans.Count)
                {
                    report?.Invoke($"{plan.Name} 转码失败，改用下一转码引擎：{ex.Message}");
                    // Auto mode falls back from NVENC to CPU when the local driver/runtime rejects hardware transcode.
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"HEVC to H.264 transcode timed out after {clampedTimeoutSeconds} seconds.", ex);
        }
        finally
        {
            DeleteIfExists(outputPath);
        }

        throw new InvalidOperationException("HEVC 转码失败：没有可用的转码方案。");
    }

    private static async Task<IReadOnlyList<H264TranscodePlan>> ResolveH264TranscodePlansAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        string transcodeEngine,
        Func<ProcessStartInfo, CancellationToken, Task<ProcessRunResult>> runner,
        CancellationToken cancellationToken)
    {
        if (transcodeEngine == "cpu")
        {
            return [BuildCpuH264TranscodePlan(ffmpegPath, inputPath, outputPath)];
        }

        if (transcodeEngine == "nvenc")
        {
            if (await FfmpegSupportsEncoderAsync(ffmpegPath, "h264_nvenc", runner, cancellationToken).ConfigureAwait(false))
            {
                return
                [
                    BuildNvencH264TranscodePlan(ffmpegPath, inputPath, outputPath),
                    BuildCpuH264TranscodePlan(ffmpegPath, inputPath, outputPath)
                ];
            }

            return [BuildCpuH264TranscodePlan(ffmpegPath, inputPath, outputPath)];
        }

        if (await FfmpegSupportsEncoderAsync(ffmpegPath, "h264_nvenc", runner, cancellationToken).ConfigureAwait(false))
        {
            return
            [
                BuildNvencH264TranscodePlan(ffmpegPath, inputPath, outputPath),
                BuildCpuH264TranscodePlan(ffmpegPath, inputPath, outputPath)
            ];
        }

        return [BuildCpuH264TranscodePlan(ffmpegPath, inputPath, outputPath)];
    }

    private static H264TranscodePlan BuildNvencH264TranscodePlan(string ffmpegPath, string inputPath, string outputPath) =>
        new("NVIDIA NVENC", CreateFfmpegStartInfo(ffmpegPath,
        [
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-hwaccel",
            "cuda",
            "-hwaccel_output_format",
            "cuda",
            "-c:v",
            "hevc_cuvid",
            "-i",
            inputPath,
            "-map",
            "0:v:0",
            "-map",
            "0:a?",
            "-c:v",
            "h264_nvenc",
            "-preset",
            "p4",
            "-cq",
            "26",
            "-b:v",
            "0",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            "-f",
            "mp4",
            outputPath
        ]));

    private static H264TranscodePlan BuildCpuH264TranscodePlan(string ffmpegPath, string inputPath, string outputPath) =>
        new("CPU libx264", CreateFfmpegStartInfo(ffmpegPath,
        [
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-xerror",
            "-i",
            inputPath,
            "-map",
            "0:v:0",
            "-map",
            "0:a?",
            "-c:v",
            "libx264",
            "-preset",
            "veryfast",
            "-crf",
            "20",
            "-pix_fmt",
            "yuv420p",
            "-c:a",
            "aac",
            "-b:a",
            "128k",
            "-movflags",
            "+faststart",
            "-f",
            "mp4",
            outputPath
        ]));

    private static ProcessStartInfo CreateFfmpegStartInfo(string ffmpegPath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<bool> FfmpegSupportsEncoderAsync(
        string ffmpegPath,
        string encoderName,
        Func<ProcessStartInfo, CancellationToken, Task<ProcessRunResult>> runner,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateFfmpegStartInfo(ffmpegPath, ["-hide_banner", "-encoders"]);
        try
        {
            var result = await runner(startInfo, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return false;
            }

            var output = $"{result.StandardOutput}\n{result.StandardError}";
            return output.Contains(encoderName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ProbePrimaryVideoCodecAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"视频校验失败：文件不存在（{Path.GetFileName(path)}）。");
        }

        var ffprobePath = ResolveFfprobeBinaryForTests.Value?.Invoke() ?? ResolveFfprobeBinary();
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(path);

        ProcessRunResult result;
        try
        {
            var runner = RunProcessAsyncForTests.Value ?? RunProcessAsyncDefault;
            result = await runner(startInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法运行 ffprobe 校验视频：{ex.Message}", ex);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"视频校验失败，ffprobe 无法识别媒体：{TrimProcessOutput(result.StandardError)}");
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidDataException("视频校验失败：ffprobe 未返回媒体流信息。");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("视频校验失败：媒体中没有视频流。");
            }

            foreach (var stream in streams.EnumerateArray())
            {
                if (!string.Equals(GetString(stream, "codec_type"), "video", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var codec = GetString(stream, "codec_name")?.Trim().ToLowerInvariant();
                if (!IsRecognizedVideoCodec(codec))
                {
                    throw new InvalidDataException(
                        $"视频校验失败：视频流编码无效（{(string.IsNullOrWhiteSpace(codec) ? "缺失" : codec)}）。" +
                        "下载内容可能仍处于加密状态、封装不完整，或当前 FFmpeg 不支持该编码。");
                }

                return codec!;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"视频校验失败：ffprobe 返回内容无效（{ex.Message}）。", ex);
        }

        throw new InvalidDataException("视频校验失败：媒体中没有视频流。");
    }

    internal static bool IsRecognizedVideoCodec(string? codec) =>
        !string.IsNullOrWhiteSpace(codec) &&
        !string.Equals(codec.Trim(), "none", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(codec.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);

    private static async Task<ProcessRunResult> RunProcessAsyncDefault(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            TryKillProcess(process);
            throw;
        }

        var standardOutput = await outputTask;
        var standardError = await errorTask;
        return new ProcessRunResult(process.ExitCode, standardOutput, standardError);
    }

    private static void TryKillProcess(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore process cleanup failures.
        }
    }

    private static string ResolveFfmpegBinary() => BundledToolResolver.TryResolveBinary("ffmpeg") ?? "ffmpeg";

    private static string ResolveFfprobeBinary() => BundledToolResolver.TryResolveBinary("ffprobe") ?? "ffprobe";

    private static bool IsClearlyNonMediaContentType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimProcessOutput(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "no stderr";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
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

    internal static bool ShouldRetryDownload(Exception exception)
    {
        if (exception is HongguoHighException highException)
        {
            if (highException.Code is 401 or 403)
                return false;
            if (highException.Code is 408 or 425 or 429 or 500 or 502 or 503 or 504)
                return true;
        }

        if (exception is TaskCanceledException or TimeoutException or IOException or HttpRequestException or InvalidDataException)
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
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("解析超过", StringComparison.OrdinalIgnoreCase);
    }

    internal static TimeSpan ResolveDownloadRetryDelay(Exception exception, int attempt)
    {
        var text = exception.Message ?? string.Empty;
        var serviceBusy = text.Contains("解析服务繁忙", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("服务繁忙", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("稍后重试", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("service busy", StringComparison.OrdinalIgnoreCase);
        if (serviceBusy)
            return TimeSpan.FromSeconds(Math.Min(30, 5 * Math.Pow(2, Math.Max(0, attempt - 1))));
        return TimeSpan.FromSeconds(Math.Min(5, 1.5 * Math.Max(1, attempt)));
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchPikachuAsync(
        string keyword,
        int page,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        using var document = await PikachuDramaClient.RequestFanqieSearchAsync(
            _httpClient,
            settings.PikachuFanqieCookie,
            settings.PikachuDramaType,
            keyword,
            page,
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("search_data", out var searchData) ||
            searchData.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<DramaSearchItem>();
        var seenBookIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in searchData.EnumerateArray())
        {
            var bookInfos = new List<JsonElement>();
            if (item.TryGetProperty("books", out var books) && books.ValueKind == JsonValueKind.Array)
            {
                bookInfos.AddRange(books.EnumerateArray().Where(book => book.ValueKind == JsonValueKind.Object));
            }

            if (item.TryGetProperty("cell_slices", out var cells) && cells.ValueKind == JsonValueKind.Array)
            {
                foreach (var cell in cells.EnumerateArray())
                {
                    if (cell.TryGetProperty("book_slice", out var bookSlice) &&
                        bookSlice.TryGetProperty("book_info", out var info) &&
                        info.ValueKind == JsonValueKind.Object)
                    {
                        bookInfos.Add(info);
                    }
                }
            }

            foreach (var info in bookInfos)
            {
                var bookId = GetString(info, "book_id");
                if (string.IsNullOrWhiteSpace(bookId) || !seenBookIds.Add(bookId))
                {
                    continue;
                }

                if (!IsPikachuDramaBookInfo(info))
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

    private static bool IsPikachuDramaBookInfo(JsonElement info)
    {
        var superCategory = GetString(info, "super_category")?.Trim();
        if (string.Equals(superCategory, "9", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(superCategory))
        {
            return false;
        }

        var genre = GetString(info, "genre")?.Trim();
        return genre is not ("10" or "262");
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

    private async Task<IReadOnlyList<SourceEpisode>> GetDownloaderEpisodesAsync(
        string bookId, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var items = await _downloaderApiService.GetEpisodesAsync(settings, bookId, cancellationToken);
        return items.Select(item => new SourceEpisode(
            item.EpisodeNumber, item.Title, item.VideoId, string.Empty)).ToArray();
    }

    private async Task<SourceVideoDetail> GetDownloaderVideoUrlAsync(
        string videoId, string quality, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var detail = await _downloaderApiService.GetPlaybackAsync(settings, videoId, quality, cancellationToken);
        return new SourceVideoDetail(
            detail.Url,
            HongguoCdn: new HongguoCdnDownload([detail.Url], detail.SpadeA, detail.Encrypted));
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
        string quality,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var detail = await _hglocalApiService.GetVideoPlaybackAsync(settings, prefixedVideoId, quality, cancellationToken);
        return new SourceVideoDetail(
            detail.Url,
            EnsureWindowsCompatible: IsHongguoLocalCompatibleMode(settings.HongguoLocalDownloadMode),
            TranscodeEngine: NormalizeHongguoLocalTranscodeEngine(settings.HongguoLocalTranscodeEngine));
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
        var code = GetString(document.RootElement, "code") ?? string.Empty;
        if (code != "200")
        {
            throw new InvalidOperationException(
                $"皮卡丘 detail 失败 (code={NonEmpty(code, "unknown")}): {DescribePikachuFailure(document.RootElement)}");
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

    private async Task<IReadOnlyList<SourceEpisode>> GetHghighEpisodesAsync(string bookId, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var items = await _hghighApiService.GetEpisodesAsync(settings, bookId, cancellationToken);
        return items
            .Select(item => new SourceEpisode(
                item.EpisodeNumber,
                item.Title,
                item.VideoId,
                item.PosterUrl))
            .ToArray();
    }

    private async Task<SourceVideoDetail> GetHghighVideoUrlAsync(string videoId, string quality, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var detail = await _hghighApiService.GetVideoPlaybackAsync(settings, videoId, quality, cancellationToken);
        return new SourceVideoDetail(
            detail.Url,
            HongguoCdn: new HongguoCdnDownload(detail.EncryptedUrls, detail.SpadeA, detail.Encrypted),
            ExpectedSize: detail.Size);
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetMapleleafEpisodesAsync(
        string bookId,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var items = await _mapleleafApiService.GetEpisodesAsync(settings, bookId, cancellationToken);
        return items.Select(item => new SourceEpisode(
            item.EpisodeNumber,
            item.Title,
            item.VideoId,
            item.PosterUrl)).ToArray();
    }

    private async Task<SourceVideoDetail> GetMapleleafVideoUrlAsync(
        string videoId,
        string quality,
        DramaSourceSettings settings,
        int playUrlTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var detail = await _mapleleafApiService.GetVideoPlaybackAsync(
            settings,
            videoId,
            quality,
            playUrlTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
        return new SourceVideoDetail(
            detail.Url,
            HongguoCdn: new HongguoCdnDownload(detail.CdnUrls, detail.SpadeA, detail.Encrypted));
    }

    private async Task<SourceVideoDetail> GetPikachuVideoUrlAsync(string prefixedVideoId, string quality, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var serverUrl = NormalizeServerUrl(settings.PikachuServerUrl);
        var videoId = StripPrefix(prefixedVideoId, PikachuEpisodePrefix);
        var deviceId = await ResolvePikachuDeviceIdAsync(settings, cancellationToken);
        var clientVersion = string.IsNullOrWhiteSpace(settings.PikachuClientVersion)
            ? "1.4.4"
            : settings.PikachuClientVersion.Trim();
        var qualityCodes = BuildPikachuQualityFallbackCodes(quality);
        string? lastCode = null;
        string? lastMessage = null;
        foreach (var qualityCode in qualityCodes)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["videoId"] = PikachuEncrypt(videoId),
                ["quality"] = PikachuEncrypt(qualityCode),
                ["deviceId"] = PikachuEncrypt(deviceId),
                ["version"] = PikachuEncrypt(clientVersion)
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/api/drama/hongguo/decryptVideo")
            {
                Content = content
            };
            ApplyPikachuHeaders(request);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var code = GetString(document.RootElement, "code") ?? string.Empty;
            if (code != "200")
            {
                lastCode = code;
                lastMessage = DescribePikachuFailure(document.RootElement);
                if (code == "500" && qualityCode != qualityCodes[^1])
                    continue;
                throw new InvalidOperationException(
                    $"皮卡丘 video 失败 (code={NonEmpty(code, "unknown")}): {lastMessage}；" +
                    $"已尝试清晰度 {string.Join(" -> ", qualityCodes.TakeWhile(value => value != qualityCode).Append(qualityCode))}");
            }

            var url = document.RootElement.TryGetProperty("data", out var data)
                ? GetString(data, "url")
                : null;
            var decryptKey = document.RootElement.TryGetProperty("data", out data)
                ? GetString(data, "key")
                : null;
            if (!string.IsNullOrWhiteSpace(url))
                return new SourceVideoDetail(url, decryptKey);
            lastCode = "200";
            lastMessage = "未返回可用播放链接";
        }
        throw new InvalidOperationException(
            $"皮卡丘 video 失败 (code={NonEmpty(lastCode, "unknown")}): {NonEmpty(lastMessage, "未知错误")}；" +
            $"已尝试清晰度 {string.Join(" -> ", qualityCodes)}");
    }

    internal static IReadOnlyList<string> BuildPikachuQualityFallbackCodes(string quality)
    {
        var requested = MapPikachuQuality(quality);
        var ordered = new[] { "1080", "720", "2", "1", "0" };
        var start = Array.IndexOf(ordered, requested);
        return start < 0 ? ["0"] : ordered[start..];
    }

    internal static string DescribePikachuFailure(JsonElement root)
    {
        foreach (var key in new[] { "msg", "message", "error", "reason" })
        {
            var value = GetString(root, key);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "msg", "message", "error", "reason" })
            {
                var value = GetString(data, key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return "未知错误";
    }

    private static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

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
            if (!File.Exists(path) || new FileInfo(path).Length <= 0)
            {
                return false;
            }

            if (LooksLikeMp4(path) && !HasCompleteMp4Structure(path))
            {
                return false;
            }

            Span<byte> header = stackalloc byte[512];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var read = stream.Read(header);
            if (read <= 0)
            {
                return false;
            }

            var prefix = Encoding.UTF8.GetString(header[..read])
                .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return prefix.Length > 0 &&
                   !prefix.StartsWith('<') &&
                   !prefix.StartsWith('{') &&
                   !prefix.StartsWith('[') &&
                   !prefix.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasCompleteMp4Structure(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            if (length < 8)
                return false;

            var offset = 0L;
            var hasFtyp = false;
            var hasMoov = false;
            var hasMdat = false;
            Span<byte> header = stackalloc byte[16];
            while (offset + 8 <= length)
            {
                stream.Position = offset;
                if (stream.Read(header[..8]) != 8)
                    return false;
                var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                var type = header.Slice(4, 4);
                var headerSize = 8L;
                long resolvedSize;
                if (boxSize == 1)
                {
                    if (offset + 16 > length || stream.Read(header[8..16]) != 8)
                        return false;
                    resolvedSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]));
                    headerSize = 16;
                }
                else
                {
                    resolvedSize = boxSize == 0 ? length - offset : boxSize;
                }

                if (resolvedSize < headerSize || resolvedSize > length - offset)
                    return false;

                hasFtyp |= type.SequenceEqual("ftyp"u8);
                hasMoov |= type.SequenceEqual("moov"u8);
                hasMdat |= type.SequenceEqual("mdat"u8);
                offset += resolvedSize;
            }

            return offset == length && hasFtyp && hasMoov && hasMdat;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeMp4(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[8];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(header) == header.Length && header[4..8].SequenceEqual("ftyp"u8);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ContainsEncryptedMp4SampleEntry(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var count = (int)Math.Min(stream.Length, 8L * 1024 * 1024);
            if (count <= 0)
                return false;
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var current = stream.Read(buffer, read, count - read);
                if (current <= 0)
                    break;
                read += current;
            }
            var data = buffer.AsSpan(0, read);
            return data.IndexOf("encv"u8) >= 0 || data.IndexOf("enca"u8) >= 0;
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
        DeleteIfExists(BuildEncryptedTempPath(tempPath));
        if (!keepVideo)
        {
            DeleteIfExists(path);
        }
    }

    private static string BuildEncryptedTempPath(string tempPath) => $"{tempPath}.enc.part";

    private static string BuildHongguoEncryptedTempPath(string tempPath) => $"{tempPath}.hgenc";

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

    private static async Task<string?> FindExistingEpisodeVideoAsync(
        string outputDir,
        int outputEpisodeNumber,
        Action<string>? report,
        bool validateVideoEncoding,
        ExistingVideoPolicy existingVideoPolicy,
        ICollection<string>? replacementCandidates,
        CancellationToken cancellationToken)
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

                if (IsEpisodeFileForOutput(path, outputEpisodeNumber))
                {
                    if (existingVideoPolicy == ExistingVideoPolicy.ReplaceAll)
                    {
                        replacementCandidates?.Add(path);
                        return null;
                    }

                    if (!HasValidVideoFile(path))
                    {
                        replacementCandidates?.Add(path);
                        report?.Invoke(
                            $"发现无效的已有视频，将重新下载并在成功后替换：{Path.GetFileName(path)}");
                        continue;
                    }

                    if (!validateVideoEncoding)
                    {
                        return path;
                    }

                    if (ContainsEncryptedMp4SampleEntry(path))
                    {
                        replacementCandidates?.Add(path);
                        report?.Invoke(
                            $"发现旧版加密 MP4，将重新获取明文直链并替换：{Path.GetFileName(path)}");
                        continue;
                    }

                    try
                    {
                        await ProbePrimaryVideoCodecAsync(path, cancellationToken).ConfigureAwait(false);
                        return path;
                    }
                    catch (InvalidDataException ex)
                    {
                        replacementCandidates?.Add(path);
                        report?.Invoke(
                            $"发现无效的已有视频，将重新下载并在成功后替换：{Path.GetFileName(path)}（{ex.Message}）");
                    }
                }
            }
        }

        return null;
    }

    private static void DeleteReplacedAlternateFiles(
        string finalPath,
        IEnumerable<string> replacementCandidates)
    {
        var finalFullPath = Path.GetFullPath(finalPath);
        foreach (var candidate in replacementCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(Path.GetFullPath(candidate), finalFullPath, StringComparison.OrdinalIgnoreCase))
                DeleteIfExists(candidate);
        }
    }

    internal static bool IsEpisodeFileForOutput(string path, int outputEpisodeNumber)
    {
        if (outputEpisodeNumber <= 0)
            return false;

        var stem = Path.GetFileNameWithoutExtension(path).Trim();
        if (stem.Length < 3 || stem[0] != '第' || stem[^1] != '集')
            return false;

        return int.TryParse(stem[1..^1].Trim(), out var fileEpisodeNumber) &&
               fileEpisodeNumber == outputEpisodeNumber;
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
            ? "https://startvlog.cn/start-prod-api"
            : normalized;
    }

    private static int ParsePositiveInt(string value, int defaultValue)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private static int ParseDownloadFileSegments(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? Math.Clamp(parsed, 1, MaxDownloadFileSegments)
            : DefaultDownloadFileSegments;
    }

    private static bool IsHongguoLocalCompatibleMode(string? value)
    {
        return string.Equals((value ?? "fast").Trim(), "compatible", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHongguoLocalTranscodeEngine(string? value)
    {
        var normalized = (value ?? "auto").Trim().ToLowerInvariant();
        return normalized is "auto" or "nvenc" or "cpu" ? normalized : "auto";
    }

    private static string? ResolveSelectedService(string selected, IReadOnlyList<string> supportedServices)
    {
        var normalized = (selected ?? "hgnew").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "hgnew";
        }

        return supportedServices.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : null;
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

    private sealed record SourceVideoDetail(
        string Url,
        string? PikachuDecryptKey = null,
        HongguoCdnDownload? HongguoCdn = null,
        bool EnsureWindowsCompatible = false,
        string TranscodeEngine = "auto",
        long ExpectedSize = 0);

    private sealed record HongguoCdnDownload(
        IReadOnlyList<string> EncryptedUrls,
        string SpadeA,
        bool Encrypted);

    internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record H264TranscodePlan(string Name, ProcessStartInfo StartInfo);

    private sealed record VideoProcessingResult(
        string Codec,
        bool Transcoded,
        string? TranscodeEngine);

    private sealed record EpisodeTask(
        int Order,
        int EpisodeNumber,
        int SourceEpisodeNumber,
        int SequenceEpisodeNumber,
        string Title,
        string VideoId,
        string PosterUrl);

    private sealed record DownloadFileStats(
        long Bytes,
        TimeSpan Elapsed,
        double BytesPerSecond,
        string MediaSummary);

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
