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
using System.Text.Json.Nodes;
using System.Threading;

namespace ShortDrama.Infrastructure.Automation;

public sealed class DramaSourceRouter : IDramaSearchService, IDramaDownloader
{
    private static readonly string[] SearchServices = ["hgnew", "pikachu", "playlet", "hongguoapp", "hghigh", "downloader", "dramabox", "foreign"];
    private static readonly string[] NewReleaseServices = ["hgnew", "hghigh", "downloader"];
    private const string HongguoAppBookPrefix = "hongguoapp:";
    private const string HongguoAppEpisodePrefix = "hongguoapp_ep:";
    private const string DownloadStateFileName = ".weixin-channel-download-state.json";
    private const string PlaybackCheckFailedKey = "playback_check_failed";
    private const string MissingVideosMessage = "没有视频文件，下载未完成。";
    private const string UnplayableVideosMessage = "无法播放，当前 FFmpeg 不支持该编码。下载未完成，请更换剧集或更换下载链路。";
    private const string EpisodeNumberModeContinuous = "continuous";
    private const int DownloadBufferSize = 128 * 1024;
    private const int DefaultDownloadFileSegments = 4;
    private const int MaxDownloadFileSegments = 16;
    private const int HghighMinimumDownloadConcurrency = 6;
    private const int HghighPieceSizeBytes = 512 * 1024;
    private const int HghighPiecePoolWorkers = 16;
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
    private readonly HongguoNewApiService _hgnewApiService;
    private readonly HongguoDramaSearchService _hgnewSearchService;
    private readonly HongguoDramaDownloader _hgnewDownloader;
    private readonly HongguoMemoryReaderService _hongguoMemoryReaderService;
    private readonly HongguoHighApiService _hghighApiService;
    private readonly HongguoAppDirectClient _hongguoApp;
    private readonly DownloaderGatewayApiService _downloaderApiService;
    private readonly DramaBoxApiService _dramaBoxApiService;

    public DramaSourceRouter(
        HttpClient httpClient,
        IDramaSettingsProvider settingsProvider,
        HongguoNewApiService hgnewApiService,
        HongguoDramaSearchService hgnewSearchService,
        HongguoDramaDownloader hgnewDownloader,
        HongguoMemoryReaderService hongguoMemoryReaderService)
        : this(
            httpClient,
            settingsProvider,
            hgnewApiService,
            hgnewSearchService,
            hgnewDownloader,
            hongguoMemoryReaderService,
            new HongguoHighApiService(httpClient))
    {
    }

    public DramaSourceRouter(
        HttpClient httpClient,
        IDramaSettingsProvider settingsProvider,
        HongguoNewApiService hgnewApiService,
        HongguoDramaSearchService hgnewSearchService,
        HongguoDramaDownloader hgnewDownloader,
        HongguoMemoryReaderService hongguoMemoryReaderService,
        HongguoHighApiService hghighApiService)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _hgnewApiService = hgnewApiService;
        _hgnewSearchService = hgnewSearchService;
        _hgnewDownloader = hgnewDownloader;
        _hongguoMemoryReaderService = hongguoMemoryReaderService;
        _hghighApiService = hghighApiService;
        _hongguoApp = new HongguoAppDirectClient(httpClient);
        _downloaderApiService = new DownloaderGatewayApiService(httpClient);
        _dramaBoxApiService = new DramaBoxApiService(httpClient);
    }

    public Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken) =>
        SearchAsync(keyword, page, cancellationToken, searchCategory: null);

    public Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken,
        string? searchCategory) =>
        SearchWithSettingsAsync(_settingsProvider.Get(), keyword, page, cancellationToken, searchCategory);

    public async Task<IReadOnlyList<DramaSearchItem>> SearchWithSettingsAsync(
        DramaSourceSettings settings,
        string keyword,
        int page,
        CancellationToken cancellationToken,
        string? searchCategory = null)
    {
        var source = ResolveSelectedService(settings.DramaSourceChain, SearchServices);
        return source switch
        {
            "hgnew" => await SearchHgnewAsync(keyword, page, settings, cancellationToken),
            "pikachu" => await SearchPikachuAsync(keyword, page, settings, cancellationToken, searchCategory),
            "playlet" => await SearchPlayletAsync(keyword, page, settings, cancellationToken, searchCategory),
            "hongguoapp" => await SearchHongguoAppAsync(keyword, page, settings, cancellationToken, searchCategory),
            "hghigh" => await _hghighApiService.SearchAsync(settings, keyword, page, cancellationToken, searchCategory),
            "downloader" => await _downloaderApiService.SearchAsync(settings, keyword, page, cancellationToken),
            "dramabox" => await _dramaBoxApiService.SearchAsync(settings, keyword, page, cancellationToken),
            "foreign" => await ForeignPlayletClient.SearchAsync(
                _httpClient,
                settings.ForeignServerUrl,
                settings.ForeignDeviceId,
                settings.ForeignSearchSource,
                settings.ForeignLanguage,
                keyword,
                page,
                cancellationToken),
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
        ThrowIfForeignCatalogUnsupported(settings);
        var source = ResolveSelectedService(settings.DramaSourceChain, NewReleaseServices);
        if (string.Equals(source, "hghigh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("红果高码率暂不支持短剧今日上新，请使用漫剧上新或关键词搜索。");
        }

        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => _hgnewApiService.GetTodayNewAsync(settings, "djnew", ct),
            cancellationToken,
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
        ThrowIfForeignCatalogUnsupported(settings);
        if (string.Equals(settings.DramaSourceChain?.Trim(), "playlet", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("正在读取漫剧上新，最新上架，7天内...");
            var items = await LoadPlayletShelfNewAsync(ai: false, cancellationToken);
            partialResults?.Report(items);
            return items;
        }
        if (string.Equals(settings.DramaSourceChain?.Trim(), "hongguoapp", StringComparison.OrdinalIgnoreCase))
            return await GetHongguoAppNewReleaseAsync("comic_series", partialResults, cancellationToken);

        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewMangaTodayAsync(settings, days, ct),
            cancellationToken: cancellationToken,
            hghighLoader: ct => _hghighApiService.GetManjuNewAsync(
                settings, days, enrich, progress, ct, partialResults, detailResults),
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
        ThrowIfForeignCatalogUnsupported(settings);
        if (string.Equals(settings.DramaSourceChain?.Trim(), "dramabox", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("正在读取 DramaBox New…");
            var items = await _dramaBoxApiService.GetNewChannelAsync(settings, cancellationToken);
            partialResults?.Report(items);
            return items;
        }

        if (string.Equals(settings.DramaSourceChain?.Trim(), "hongguoapp", StringComparison.OrdinalIgnoreCase))
            return await GetHongguoAppNewReleaseAsync("ai_series", partialResults, cancellationToken);
        if (string.Equals(settings.DramaSourceChain?.Trim(), "playlet", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("正在读取 AI短剧上新，最新上架，7天内...");
            var items = await LoadPlayletShelfNewAsync(ai: true, cancellationToken);
            partialResults?.Report(items);
            return items;
        }

        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewAiTodayAsync(settings, days, ct),
            cancellationToken: cancellationToken,
            hghighLoader: ct => _hghighApiService.GetAiNewAsync(
                settings, days, enrich, progress, ct, partialResults, detailResults),
            downloaderLoader: ct => _downloaderApiService.GetLatestAsync(settings, "ai", days, ct));
    }

    public async Task<HongguoAppReleasePage> GetHongguoNewReleasePageAsync(
        bool ai,
        int offset,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        var chain = settings.DramaSourceChain?.Trim();
        var playlet = string.Equals(chain, "playlet", StringComparison.OrdinalIgnoreCase);
        if (!playlet && !string.Equals(chain, "hongguoapp", StringComparison.OrdinalIgnoreCase))
            return new HongguoAppReleasePage([], false, Math.Max(0, offset), sessionId ?? "");

        var page = await _hongguoApp.GetNewReleasePageAsync(
            ai ? "ai_series" : "comic_series",
            offset,
            sessionId,
            cancellationToken,
            settings.HongguoAppDeviceId,
            settings.HongguoAppInstallId);
        return page with
        {
            // A full first page can be checked once for continuation even when the feed reports an end.
            HasMore = page.HasMore || (playlet && offset == 0 && page.NextOffset > 0 &&
                page.Items.Count == HongguoAppDirectClient.NewReleasePageSize),
            Items = playlet
                ? page.Items.Select(AsPlayletBook).ToArray()
                : page.Items.Select(item => RetargetBook(item, HongguoAppBookPrefix)).ToArray()
        };
    }

    public readonly record struct DramaBoxNewPage(
        IReadOnlyList<DramaSearchItem> Items,
        bool HasMore,
        int NextPage);

    public async Task<DramaBoxNewPage> GetDramaBoxNewPageAsync(int page, CancellationToken cancellationToken)
    {
        if (!IsDramaBoxSourceSelected())
            return new DramaBoxNewPage([], false, Math.Max(1, page));

        var pageResult = await _dramaBoxApiService.GetNewChannelPageAsync(
            _settingsProvider.Get(),
            Math.Max(1, page),
            enrich: true,
            cancellationToken);
        return new DramaBoxNewPage(pageResult.Items, pageResult.HasMore, pageResult.NextPage);
    }

    public bool IsDramaBoxSourceSelected() =>
        string.Equals(_settingsProvider.Get().DramaSourceChain?.Trim(), "dramabox", StringComparison.OrdinalIgnoreCase);

    public bool IsHongguoAppSourceSelected() =>
        string.Equals(_settingsProvider.Get().DramaSourceChain?.Trim(), "hongguoapp", StringComparison.OrdinalIgnoreCase);

    public bool IsHighSourceSelected() =>
        string.Equals(_settingsProvider.Get().DramaSourceChain?.Trim(), "hghigh", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<DramaSearchItem>> LoadPlayletShelfNewAsync(
        bool ai,
        CancellationToken cancellationToken)
    {
        var items = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        var sessionId = "";
        for (var page = 0; page < 8; page++)
        {
            var batch = await GetHongguoNewReleasePageAsync(ai, offset, sessionId, cancellationToken);
            foreach (var item in batch.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.BookId) && seen.Add(item.BookId))
                    items.Add(item);
            }

            if (!string.IsNullOrWhiteSpace(batch.SessionId))
                sessionId = batch.SessionId;
            if (!batch.HasMore || batch.NextOffset <= offset || batch.Items.Count == 0)
                break;
            offset = batch.NextOffset;
        }

        return items;
    }

    private static DramaSearchItem AsPlayletBook(DramaSearchItem item)
    {
        var raw = (item.BookId ?? "").Trim();
        var colon = raw.IndexOf(':');
        if (colon >= 0)
            raw = raw[(colon + 1)..];
        return item with
        {
            BookId = string.IsNullOrWhiteSpace(raw) ? "" : PlayletDramaClient.BookPrefix + raw,
        };
    }

    public bool IsPlayletSourceSelected() =>
        string.Equals(_settingsProvider.Get().DramaSourceChain?.Trim(), "playlet", StringComparison.OrdinalIgnoreCase);

    public bool IsForeignSourceSelected() =>
        string.Equals(_settingsProvider.Get().DramaSourceChain?.Trim(), "foreign", StringComparison.OrdinalIgnoreCase);

    public bool IsRankSourceSelected()
    {
        var chain = _settingsProvider.Get().DramaSourceChain?.Trim();
        return string.Equals(chain, "hghigh", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(chain, "playlet", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(chain, "hongguoapp", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetRankAsync(
        string category,
        string board,
        int page,
        CancellationToken cancellationToken)
    {
        var pageResult = await GetRankPageAsync(
            category,
            board,
            (Math.Max(1, page) - 1) * 20,
            cancellationToken);
        return pageResult.Items;
    }

    public Task<HongguoRankPage> GetRankPageAsync(
        string category,
        string board,
        int offset,
        CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        var chain = settings.DramaSourceChain?.Trim();
        if (string.Equals(chain, "playlet", StringComparison.OrdinalIgnoreCase))
            return PlayletDramaClient.GetRankPageAsync(_httpClient, category, board, offset, cancellationToken);
        if (string.Equals(chain, "hongguoapp", StringComparison.OrdinalIgnoreCase))
            return RetargetRankPageAsync(category, board, offset, cancellationToken);
        if (string.Equals(chain, "hghigh", StringComparison.OrdinalIgnoreCase))
            return _hghighApiService.GetRankPageAsync(settings, category, board, offset, cancellationToken);
        throw new InvalidOperationException("榜单接入红果高码率标准版、红果短剧下载器和红果APP。请先切换到其中一条数据链路。");
    }

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
        ThrowIfForeignCatalogUnsupported(settings);
        var source = ResolveSelectedService(settings.DramaSourceChain, NewReleaseServices);
        if (string.Equals(source, "hghigh", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("红果高码率暂不支持短剧历史上新，请使用漫剧上新或关键词搜索。");
        }

        return await LoadNewReleaseAsync(
            settings,
            hgnewLoader: ct => LoadHgnewHistoryAsync(settings, days, ct),
            cancellationToken,
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
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IReadOnlyList<DramaSearchItem>>>? hghighLoader = null,
        Func<CancellationToken, Task<IReadOnlyList<DramaSearchItem>>>? downloaderLoader = null)
    {
        var source = ResolveSelectedService(settings.DramaSourceChain, NewReleaseServices);
        return source switch
        {
            "hgnew" => await hgnewLoader(cancellationToken),
            "hghigh" => hghighLoader is null ? [] : await hghighLoader(cancellationToken),
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

    public async Task EnsureDownloadReadyAsync(
        string stateDirectory,
        IReadOnlyList<string> paths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var statePath = Path.Combine(stateDirectory, DownloadStateFileName);
        var state = ReadDownloadState(statePath);
        var retryPlayback = IsFailedPlaybackCheck(state);
        if (IsFailedDownloadState(state) && !retryPlayback)
            throw new InvalidOperationException(GetDownloadFailureMessage(state!) ?? "下载未完成。");

        if (retryPlayback)
            progress?.Report("上次视频播放校验失败，重新检查已有视频。");
        await EnsureVideosPlayableAsync(stateDirectory, paths, progress, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (retryPlayback && ReadDownloadState(statePath) is { } current && IsFailedPlaybackCheck(current))
        {
            current["ok"] = true;
            current["stopped"] = false;
            current["message"] = "已有视频通过播放校验，下载已完成。";
            current.Remove(PlaybackCheckFailedKey);
            WritePlaybackDownloadState(statePath, current);
        }
    }

    public async Task EnsureVideosPlayableAsync(
        string stateDirectory,
        IReadOnlyList<string> paths,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            throw PlaybackCheckFailed(stateDirectory, MissingVideosMessage);

        progress?.Report($"正在检查 {paths.Count} 个视频能否播放。");
        var failedEpisodes = new List<int>();
        var failedNames = new List<string>();
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index];
            if ((index + 1) % 10 == 0 || index + 1 == paths.Count)
                progress?.Report($"播放检查 {index + 1}/{paths.Count}");
            try
            {
                await ProbePrimaryVideoCodecAsync(path, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (InvalidDataException ex)
            {
                progress?.Report($"{Path.GetFileName(path)} 播放校验失败：{ex.Message}");
            }
            if (TryReadEpisodeNumber(path, out var episode))
                failedEpisodes.Add(episode);
            else
                failedNames.Add(Path.GetFileName(path));
        }

        if (failedEpisodes.Count == 0 && failedNames.Count == 0)
        {
            progress?.Report($"播放检查通过：{paths.Count} 个视频都可以播放。");
            return;
        }

        var labels = failedEpisodes
            .Distinct()
            .Order()
            .Select(episode => $"第{episode}集")
            .Concat(failedNames)
            .ToArray();
        throw PlaybackCheckFailed(
            stateDirectory,
            $"{string.Join("、", labels)}{UnplayableVideosMessage}");
    }

    public async Task<bool> HasFfmpegSupportedVideoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await ProbePrimaryVideoCodecAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public async Task<DramaDownloadResult> RedownloadEpisodesAsync(
        string metadataDirectory,
        string outputDirectory,
        IReadOnlyList<int> episodes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var selected = episodes.Where(episode => episode > 0).Distinct().Order().ToArray();
        if (selected.Length == 0)
            return new DramaDownloadResult(false, outputDirectory, 0, "没有需要重新下载的集。");
        var metadata = ProjectAutomationMetadata.Resolve(metadataDirectory);
        var quality = string.IsNullOrWhiteSpace(metadata.Quality) ? "1080P" : metadata.Quality;
        Directory.CreateDirectory(outputDirectory);
        var request = new DramaDownloadRequest(
            ProjectDir: outputDirectory,
            OutputDir: outputDirectory,
            DisplayName: FirstText(metadata.OriginalTitle, metadata.Title, metadata.DisplayName, Path.GetFileName(metadataDirectory)),
            BookId: metadata.BookId,
            Episodes: string.Join(",", selected),
            Quality: quality,
            Concurrent: Math.Clamp(metadata.Concurrent <= 0 ? 4 : metadata.Concurrent, 1, 10),
            EpisodeNumberMode: ReadEpisodeNumberMode(metadataDirectory),
            ExistingVideoPolicy: ExistingVideoPolicy.ReplaceAll,
            MetadataDirectory: metadataDirectory);
        return await DownloadAsync(request, progress, cancellationToken);
    }

    private static string ReadEpisodeNumberMode(string directory)
    {
        var path = Path.Combine(directory, "shortdrama-project.json");
        if (!File.Exists(path))
            return "source";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("episodeNumberMode", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        }
        catch (JsonException)
        {
            // 读不到集号方式时按片源集号下载。
        }

        return "source";
    }

    private static string FirstText(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private async Task<(string BookId, string? Error)> AlignBookIdToCurrentChainAsync(
        DramaDownloadRequest request,
        string bookId,
        DramaSourceSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var selected = DramaDownloadSourceSelector.Normalize(settings.DramaSourceChain);
        var directory = FirstProjectDirectory(request.MetadataDirectory, request.ProjectDir, request.OutputDir);
        var decided = DramaDownloadSourceSelector.TryRetarget(bookId, selected);
        if (decided is not null)
        {
            if (!string.Equals(decided, bookId, StringComparison.OrdinalIgnoreCase))
            {
                var title = ReadProjectTitle(directory, request.DisplayName);
                progress?.Report($"当前链路是{DramaDownloadSourceSelector.Label(selected)}，沿用同一书号：{decided}");
                DramaDownloadSourceSelector.Remember(directory, DramaDownloadSourceSelector.InferSource(bookId), bookId, title);
                DramaDownloadSourceSelector.Remember(directory, selected, decided, title);
            }

            return (decided, null);
        }

        var cached = DramaDownloadSourceSelector.ReadCachedBookId(directory, selected);
        if (!string.IsNullOrWhiteSpace(cached) &&
            string.Equals(DramaDownloadSourceSelector.InferSource(cached), selected, StringComparison.Ordinal))
        {
            progress?.Report($"当前链路是{DramaDownloadSourceSelector.Label(selected)}，使用已匹配书号：{cached}");
            return (cached, null);
        }

        var titles = ReadSearchTitles(directory, request.DisplayName);
        if (titles.Count == 0)
            return (bookId, $"切换到{DramaDownloadSourceSelector.Label(selected)}后，项目没有原剧名，无法重新匹配。");

        var expectedEpisodes = DramaDownloadSourceSelector.ReadEpisodeCount(directory);
        string? failure = null;
        foreach (var title in titles)
        {
            IReadOnlyList<DramaSearchItem> results;
            try
            {
                results = await SearchAsync(title, 1, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail = ex.Message.Contains("http", StringComparison.OrdinalIgnoreCase)
                    ? "搜索没有返回结果。"
                    : ex.Message;
                failure = $"切换到{DramaDownloadSourceSelector.Label(selected)}后，按原剧名《{title}》搜索失败：{detail}";
                continue;
            }

            var match = ChooseUniqueTitle(results, title, expectedEpisodes, selected);
            if (match.Item is not null)
            {
                var resolved = match.Item.BookId.Trim();
                var storedTitle = ReadProjectTitle(directory, title);
                progress?.Report($"当前链路是{DramaDownloadSourceSelector.Label(selected)}，按原剧名《{title}》匹配到 {resolved}");
                DramaDownloadSourceSelector.Remember(directory, DramaDownloadSourceSelector.InferSource(bookId), bookId, storedTitle);
                DramaDownloadSourceSelector.Remember(directory, selected, resolved, storedTitle);
                return (resolved, null);
            }

            failure = match.Error;
        }

        return (bookId, failure ?? $"切换到{DramaDownloadSourceSelector.Label(selected)}后，未能按原剧名找到唯一剧集。");
    }

    private static (DramaSearchItem? Item, string? Error) ChooseUniqueTitle(
        IReadOnlyList<DramaSearchItem> results,
        string title,
        int expectedEpisodes,
        string selected)
    {
        var exact = results
            .Where(item => !string.IsNullOrWhiteSpace(item.BookId) && SameTitle(item.Title, title))
            .ToArray();
        if (exact.Length == 1)
            return (exact[0], null);
        if (exact.Length > 1 && expectedEpisodes > 0)
        {
            var counted = exact.Where(item => item.EpisodeTotal == expectedEpisodes).ToArray();
            if (counted.Length == 1)
                return (counted[0], null);
        }

        var label = DramaDownloadSourceSelector.Label(selected);
        return exact.Length == 0
            ? (null, $"切换到{label}后，未能按原剧名《{title}》找到唯一剧集。")
            : (null, $"切换到{label}后，原剧名《{title}》匹配到多部剧，没有继续下载。");
    }

    private static bool SameTitle(string? left, string? right)
    {
        var a = (left ?? string.Empty).Trim();
        var b = (right ?? string.Empty).Trim();
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        return string.Equals(RemoveSpaces(a), RemoveSpaces(b), StringComparison.Ordinal);
    }

    private static string RemoveSpaces(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\u3000", string.Empty, StringComparison.Ordinal);

    private static string? FirstProjectDirectory(params string?[] directories)
    {
        foreach (var directory in directories)
        {
            if (!string.IsNullOrWhiteSpace(directory) &&
                File.Exists(Path.Combine(directory, "shortdrama-project.json")))
                return directory;
        }

        return directories.FirstOrDefault(directory => !string.IsNullOrWhiteSpace(directory));
    }

    private static string ReadProjectTitle(string? directory, string? fallback)
    {
        var metadata = string.IsNullOrWhiteSpace(directory)
            ? null
            : ProjectAutomationMetadata.Resolve(directory);
        return FirstText(metadata?.OriginalTitle, metadata?.Title, metadata?.DisplayName, fallback);
    }

    private static List<string> ReadSearchTitles(string? directory, string? fallback)
    {
        var metadata = string.IsNullOrWhiteSpace(directory)
            ? null
            : ProjectAutomationMetadata.Resolve(directory);
        var titles = new List<string>();
        foreach (var title in new[] { metadata?.OriginalTitle, metadata?.Title, metadata?.DisplayName, fallback })
        {
            var text = (title ?? string.Empty).Trim();
            if (text.Length == 0 || titles.Contains(text, StringComparer.Ordinal))
                continue;
            titles.Add(text);
        }

        return titles;
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
        if (request.ExpectedEpisodeTotal > 0 &&
            (bookId.Length == 0 || DramaDownloadSourceSelector.IsRemovedBookId(bookId) ||
             DramaDownloadSourceSelector.InferSource(bookId) != DramaDownloadSourceSelector.Normalize(settings.DramaSourceChain)))
        {
            return new DramaDownloadResult(false, request.OutputDir, 0, "剧集标识与客户端配置的下载链路不一致，请重新匹配。");
        }
        if (bookId.Length > 0 && request.ExpectedEpisodeTotal == 0)
        {
            var aligned = await AlignBookIdToCurrentChainAsync(request, bookId, settings, progress, cancellationToken);
            if (aligned.Error is not null)
                return new DramaDownloadResult(false, request.OutputDir, 0, aligned.Error);
            bookId = aligned.BookId;
        }

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
                downloadFileSegments: Math.Max(downloadFileSegments, 8),
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts,
                separateResolveConcurrency: Math.Min(4, Math.Clamp(request.Concurrent, 1, 10)));
        }


        if (bookId.StartsWith(HongguoHighCrypto.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
            bookId.StartsWith(HongguoHighCrypto.EpisodePrefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.DramaSourceChain?.Trim(), "hghigh", StringComparison.OrdinalIgnoreCase))
        {
            var highBookId = HongguoHighCrypto.EnsureBookPrefix(bookId);
            var standardSingle = HongguoClientProfile.NormalizeEdition(settings.HghighEdition) == HongguoClientProfile.StandardEdition
                && !HongguoClientProfile.IsStandardBatchPlayback(settings.HghighStandardPlaybackMode);
            var resolveParallel = standardSingle
                ? Math.Clamp(Math.Max(request.Concurrent, HghighMinimumDownloadConcurrency), 1, 8)
                : Math.Min(4, Math.Clamp(request.Concurrent, 1, 10));
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetHghighEpisodesAsync(highBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetHghighVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: HongguoHighCrypto.BookPrefix,
                downloadFileSegments: Math.Max(downloadFileSegments, 8),
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts,
                separateResolveConcurrency: resolveParallel,
                registerResolvePlan: (videoIds, batchSize) =>
                {
                    var standardEdition = HongguoClientProfile.NormalizeEdition(settings.HghighEdition) == HongguoClientProfile.StandardEdition;
                    var single = standardEdition && !HongguoClientProfile.IsStandardBatchPlayback(settings.HghighStandardPlaybackMode);
                    var mode = standardEdition ? "标准版明文直链" : "高码率";
                    var effectiveBatch = single ? 1 : batchSize;
                    progress?.Report(single
                        ? $"{mode}播放地址按单集解析：同时 {resolveParallel} 集，共 {videoIds.Count} 集"
                        : $"{mode}播放地址启用批量解析：每批 {effectiveBatch} 集，共 {videoIds.Count} 集");
                    return _hghighApiService.RegisterBatchParsePlan(settings, videoIds, request.Quality, effectiveBatch);
                },
                minConcurrency: HghighMinimumDownloadConcurrency,
                pieceSize: HghighPieceSizeBytes,
                pieceWorkers: HghighPiecePoolWorkers);
        }

        if (bookId.StartsWith(DramaBoxApiService.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
            bookId.StartsWith(DramaBoxApiService.EpisodePrefix, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settings.DramaSourceChain?.Trim(), "dramabox", StringComparison.OrdinalIgnoreCase))
        {
            var dramaBoxBookId = bookId.StartsWith(DramaBoxApiService.BookPrefix, StringComparison.OrdinalIgnoreCase)
                ? bookId
                : DramaBoxApiService.BookPrefix + bookId;
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetDramaBoxEpisodesAsync(dramaBoxBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetDramaBoxVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: DramaBoxApiService.BookPrefix,
                downloadFileSegments: downloadFileSegments,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts,
                prepareDownloadedFile: (path, episode, ct) => _dramaBoxApiService.DecryptPrivateMp4Async(path, settings, episode, ct),
                dramaBoxMedia: true);
        }

        if (bookId.StartsWith(ForeignPlayletClient.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
            bookId.StartsWith(ForeignPlayletClient.EpisodePrefix, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(settings.DramaSourceChain?.Trim(), "foreign", StringComparison.OrdinalIgnoreCase) &&
             string.IsNullOrWhiteSpace(bookId)))
        {
            var foreignBookId = EnsurePrefixed(bookId, ForeignPlayletClient.BookPrefix);
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetForeignEpisodesAsync(foreignBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetForeignVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: ForeignPlayletClient.BookPrefix,
                downloadFileSegments: 1,
                downloadTimeoutSeconds: Math.Max(downloadTimeoutSeconds, 180),
                downloadAttempts: downloadAttempts);
        }

        if (bookId.StartsWith(HongguoAppBookPrefix, StringComparison.OrdinalIgnoreCase) ||
            bookId.StartsWith(HongguoAppEpisodePrefix, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(settings.DramaSourceChain?.Trim(), "hongguoapp", StringComparison.OrdinalIgnoreCase) &&
             string.IsNullOrWhiteSpace(bookId)))
        {
            var appBookId = bookId.StartsWith(HongguoAppBookPrefix, StringComparison.OrdinalIgnoreCase)
                ? bookId
                : HongguoAppBookPrefix + bookId;
            var playbackModels = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetHongguoAppEpisodesAsync(appBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetHongguoAppVideoUrlAsync(
                    videoId,
                    quality,
                    settings,
                    ct,
                    playbackModels,
                    lockDefinition: true),
                posterPrefix: HongguoAppBookPrefix,
                downloadFileSegments: downloadFileSegments,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts,
                separateResolveConcurrency: 1,
                adjustQuality: (tasks, quality, report, ct) => ChooseHongguoAppDownloadQualityAsync(
                    tasks,
                    quality,
                    playbackModels,
                    report,
                    ct));
        }

        if (bookId.StartsWith(PlayletDramaClient.BookPrefix, StringComparison.OrdinalIgnoreCase) ||
            bookId.StartsWith(PlayletDramaClient.EpisodePrefix, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(settings.DramaSourceChain?.Trim(), "playlet", StringComparison.OrdinalIgnoreCase) &&
             string.IsNullOrWhiteSpace(bookId)))
        {
            var playletBookId = EnsurePrefixed(bookId, PlayletDramaClient.BookPrefix);
            return await DownloadWithProviderAsync(
                request,
                progress,
                cancellationToken,
                resolveEpisodes: ct => GetPlayletEpisodesAsync(playletBookId, settings, ct),
                resolveVideo: (videoId, quality, ct) => GetPlayletVideoUrlAsync(videoId, quality, settings, ct),
                posterPrefix: PlayletDramaClient.BookPrefix,
                downloadFileSegments: downloadFileSegments,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts);
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
                downloadFileSegments: downloadFileSegments,
                downloadTimeoutSeconds: downloadTimeoutSeconds,
                downloadAttempts: downloadAttempts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (request.ExpectedEpisodeTotal == 0)
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
        int downloadFileSegments,
        int downloadTimeoutSeconds,
        int downloadAttempts,
        int separateResolveConcurrency = 0,
        Func<IReadOnlyList<string>, int, IDisposable>? registerResolvePlan = null,
        int minConcurrency = 0,
        int pieceSize = 0,
        int pieceWorkers = 0,
        Func<string, int, CancellationToken, Task>? prepareDownloadedFile = null,
        Func<IReadOnlyList<EpisodeTask>, string, IProgress<string>?, CancellationToken, Task<string>>? adjustQuality = null,
        bool dramaBoxMedia = false)
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

        if (request.ExpectedEpisodeTotal > 0 &&
            !episodes.Select(episode => episode.EpisodeNumber).Order().SequenceEqual(Enumerable.Range(1, request.ExpectedEpisodeTotal)))
        {
            return new DramaDownloadResult(false, request.OutputDir, 0,
                $"分集清单与派单的 {request.ExpectedEpisodeTotal} 集不一致，可能缺集、重复或超出集数");
        }

        var episodeNumberMode = NormalizeEpisodeNumberMode(request.EpisodeNumberMode);
        var tasks = BuildEpisodeTasks(episodes, request.Episodes, episodeNumberMode);
        if (tasks.Count == 0)
        {
            return new DramaDownloadResult(false, request.OutputDir, CountVideoFiles(request.OutputDir), "没有可下载的剧集。");
        }

        var failures = new List<string>();
        var concurrency = ResolveDownloadConcurrency(request.Concurrent, minConcurrency);
        var resolveConcurrency = Math.Clamp(separateResolveConcurrency, 0, 10);
        var quality = request.Quality;
        var existingVideoPolicy = request.ExistingVideoPolicy;
        if (adjustQuality is not null)
        {
            try
            {
                var chosen = await adjustQuality(tasks, request.Quality, progress, cancellationToken);
                if (!string.Equals(
                        HongguoHighCrypto.NormalizeQuality(request.Quality),
                        HongguoHighCrypto.NormalizeQuality(chosen),
                        StringComparison.Ordinal))
                    existingVideoPolicy = ExistingVideoPolicy.ReplaceAll;
                quality = chosen;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failed = new DramaDownloadResult(false, request.OutputDir, CountVideoFiles(request.OutputDir), ex.Message);
                WriteDownloadState(request, failed, tasks, [ex.Message], episodeNumberMode);
                return failed;
            }
        }

        var plannedVideoIds = new List<string>(tasks.Count);
        if (registerResolvePlan is not null)
        {
            foreach (var task in tasks)
            {
                var existing = FindExistingEpisodeVideo(
                    request.OutputDir,
                    task.EpisodeNumber,
                    report: null,
                    existingVideoPolicy,
                    replacementCandidates: null);
                if (string.IsNullOrWhiteSpace(existing))
                    plannedVideoIds.Add(task.VideoId);
            }
        }

        using var resolvePlan = registerResolvePlan?.Invoke(
            plannedVideoIds,
            concurrency);
        using var downloadSemaphore = new SemaphoreSlim(concurrency);
        using var resolveSemaphore = resolveConcurrency > 0 ? new SemaphoreSlim(resolveConcurrency) : null;
        using var accountFailureStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var downloads = tasks.Select(task => DownloadEpisodeAsync(
            request.OutputDir,
            quality,
            task,
            tasks.Count,
            resolveVideo,
            progress,
            downloadSemaphore,
            resolveSemaphore,
            failures,
            existingVideoPolicy,
            downloadFileSegments,
            downloadTimeoutSeconds,
            downloadAttempts,
            accountFailureStop.Token,
            pieceSize,
            pieceWorkers,
            prepareDownloadedFile,
            accountFailureStop,
            dramaBoxMedia));
        try
        {
            await Task.WhenAll(downloads);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        var posterUrl = ReadPosterUrlFromProject(request.OutputDir);
        if (!LooksLikeHttpUrl(posterUrl))
            posterUrl = ReadPosterUrlFromProject(request.ProjectDir);
        if (!LooksLikeHttpUrl(posterUrl))
            posterUrl = tasks.Select(item => item.PosterUrl).FirstOrDefault(url => LooksLikeHttpUrl(url)) ?? string.Empty;

        WriteIntroFile(request.OutputDir, progress);
        if (!string.Equals(request.ProjectDir, request.OutputDir, StringComparison.OrdinalIgnoreCase))
            WriteIntroFile(request.ProjectDir, progress);

        if (!LooksLikeHttpUrl(posterUrl))
        {
            progress?.Report("未找到海报 URL，跳过海报下载。");
        }
        else
        {
            RememberPosterUrl(request.OutputDir, posterUrl);
            RememberPosterUrl(request.ProjectDir, posterUrl);
            try
            {
                await EnsurePosterAsync(request.OutputDir, request.DisplayName, posterUrl, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex.Message);
            }
        }

        var videoCount = CountVideoFiles(request.OutputDir);
        if (failures.Count > 0)
        {
            var accountFailure = failures.FirstOrDefault(IsAccountLevelDownloadFailure);
            var failureMessage = accountFailure ?? string.Join("；", failures.Distinct(StringComparer.Ordinal));
            var failed = new DramaDownloadResult(false, request.OutputDir, videoCount, failureMessage);
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
        SemaphoreSlim downloadSemaphore,
        SemaphoreSlim? resolveSemaphore,
        ICollection<string> failures,
        ExistingVideoPolicy existingVideoPolicy,
        int downloadFileSegments,
        int downloadTimeoutSeconds,
        int downloadAttempts,
        CancellationToken cancellationToken,
        int pieceSize = 0,
        int pieceWorkers = 0,
        Func<string, int, CancellationToken, Task>? prepareDownloadedFile = null,
        CancellationTokenSource? accountFailureStop = null,
        bool dramaBoxMedia = false)
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
            var existingVideo = FindExistingEpisodeVideo(
                outputDir,
                task.EpisodeNumber,
                message => progress?.Report($"[{task.Order:00}/{totalCount:00}] {message}"),
                existingVideoPolicy,
                replacementCandidates);
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
                        downloadFileSegments,
                        message => progress?.Report($"[{task.Order:00}/{totalCount:00}] {message}"),
                        pieceSize,
                        pieceWorkers,
                        prepareDownloadedFile is null
                            ? null
                            : (path, ct) => prepareDownloadedFile(path, task.SourceEpisodeNumber, ct),
                        dramaBoxMedia);
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
                    ReportLowerHevc(detail, quality, task, totalCount, progress);
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
                    RememberEpisodeFailure(failures, task, ex, accountFailureStop);
                    progress?.Report(DescribeEpisodeDownloadFailure(task, totalCount, ex));
                    return;
                }
            }

            try
            {
                var detail = await ResolveAsync();
                ReportLowerHevc(detail, quality, task, totalCount, progress);
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
                RememberEpisodeFailure(failures, task, ex, accountFailureStop);
                progress?.Report(DescribeEpisodeDownloadFailure(task, totalCount, ex));
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
            downloadFileSegments: DefaultDownloadFileSegments,
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
        int downloadFileSegments,
        Action<string>? report,
        int pieceSize = 0,
        int pieceWorkers = 0,
        Func<string, CancellationToken, Task>? prepareDownloadedFile = null,
        bool dramaBoxMedia = false)
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
                    report?.Invoke(pieceSize > 0
                        ? "使用 CDN 直连 + 本地解密"
                        : $"使用 CDN 直连 + 本地解密（单文件最多 {downloadFileSegments} 路分块）");
                    await DownloadAndDecryptHongguoCdnAsync(
                        hongguoCdn,
                        tempPath,
                        downloadFileSegments,
                        report,
                        token,
                        pieceSize,
                        pieceWorkers);
                    usedHongguoCdn = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DeleteIfExists(BuildHongguoEncryptedTempPath(tempPath));
                    DeleteIfExists(tempPath);
                    report?.Invoke($"CDN 本地解密失败，回退服务器流：{ex.Message}");
                }
            }

            var usedHls = false;
            if (!usedHongguoCdn && ForeignPlayletClient.IsHlsUrl(url))
            {
                report?.Invoke("正在合并 HLS 分片");
                await DownloadHlsPlaylistAsync(url, tempPath, token);
                usedHls = true;
            }

            if (!usedHongguoCdn && !usedHls)
            {
                await DownloadHttpContentAsync(
                    url,
                    downloadTargetPath,
                    downloadFileSegments,
                    message => report?.Invoke(message),
                    token,
                    pieceSize,
                    pieceWorkers,
                    dramaBoxMedia);
            }

            if (!usedHongguoCdn && hasPikachuDecryptKey)
            {
                if (expectedSize > 0 && new FileInfo(downloadTargetPath).Length != expectedSize)
                    throw new InvalidDataException("CENC 密文下载长度不完整。");
                await DecryptPikachuCencVideoAsync(pikachuDecryptKey!.Trim(), downloadTargetPath, tempPath, timeoutSeconds, cancellationToken);
            }

            var actualSize = new FileInfo(tempPath).Length;
            // CENC 的大小是密文大小，已在解密前校验；重封装会改变成品大小。
            if (!hasPikachuDecryptKey && expectedSize > 0 && actualSize != expectedSize)
            {
                throw new InvalidDataException($"下载文件长度不完整：预期 {expectedSize} 字节，实际 {actualSize} 字节。");
            }

            if (LooksLikeMp4(tempPath) && !HasCompleteMp4Structure(tempPath))
            {
                throw new InvalidDataException("下载的 MP4 结构不完整。");
            }

            if (prepareDownloadedFile is not null &&
                LooksLikeMp4(tempPath) &&
                DramaBoxPrivateMp4Decryptor.ContainsPrivateKey(tempPath))
            {
                report?.Invoke("正在解密 DramaBox 私有视频");
                await prepareDownloadedFile(tempPath, cancellationToken);
            }

            if (!usedHongguoCdn && ContainsEncryptedMp4SampleEntry(tempPath))
            {
                throw new InvalidDataException("下载结果仍为加密 MP4，未获得可直接播放的明文视频。");
            }

            if (!HasValidVideoFile(tempPath))
            {
                throw new InvalidDataException("下载内容不是有效的视频文件。");
            }

            await DownloadFileOperations.DelayAfterWriteAsync(cancellationToken);
            await DownloadFileOperations.SafeReplaceAsync(tempPath, finalPath, cancellationToken);

            stopwatch.Stop();
            var finalBytes = new FileInfo(finalPath).Length;
            return new DownloadFileStats(
                finalBytes,
                stopwatch.Elapsed,
                finalBytes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d),
                "保留源文件");
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

    private async Task DownloadHlsPlaylistAsync(
        string url,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = ResolveFfmpegBinaryForTests.Value?.Invoke() ?? ResolveFfmpegBinary();
        var runner = RunProcessAsyncForTests.Value ?? RunProcessAsyncDefault;
        string[][] extraArguments =
        [
            ["-c", "copy", "-bsf:a", "aac_adtstoasc"],
            ["-c", "copy"]
        ];
        ProcessRunResult? last = null;
        foreach (var extra in extraArguments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteIfExists(outputPath);
            var arguments = new List<string>
            {
                "-y",
                "-hide_banner",
                "-loglevel", "error",
                "-user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "-headers", "Referer: https://www.reelshort.com/\r\nOrigin: https://www.reelshort.com\r\n",
                "-i", url
            };
            arguments.AddRange(extra);
            arguments.Add("-movflags");
            arguments.Add("+faststart");
            arguments.Add("-f");
            arguments.Add("mp4");
            arguments.Add(outputPath);
            last = await runner(CreateFfmpegStartInfo(ffmpegPath, arguments), cancellationToken);
            if (last.ExitCode == 0 && HasValidVideoFile(outputPath))
                return;
            DeleteIfExists(outputPath);
        }

        var detail = last is null
            ? ""
            : string.IsNullOrWhiteSpace(last.StandardError) ? last.StandardOutput : last.StandardError;
        throw new InvalidOperationException("HLS 合并失败：" + TrimProcessOutput(detail));
    }

    private async Task DownloadAndDecryptHongguoCdnAsync(
        HongguoCdnDownload cdn,
        string outputPath,
        int segments,
        Action<string>? report,
        CancellationToken cancellationToken,
        int pieceSize = 0,
        int pieceWorkers = 0)
    {
        var encryptedPath = BuildHongguoEncryptedTempPath(outputPath);
        Exception? lastDownloadError = null;
        var downloaded = false;
        foreach (var candidateUrl in cdn.EncryptedUrls)
        {
            try
            {
                DeleteIfExists(encryptedPath);
                await DownloadHttpContentAsync(
                    candidateUrl,
                    encryptedPath,
                    segments,
                    report,
                    cancellationToken,
                    pieceSize,
                    pieceWorkers);
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
        CancellationToken cancellationToken,
        int pieceSize = 0,
        int pieceWorkers = 0,
        bool dramaBoxMedia = false)
    {
        if (dramaBoxMedia)
        {
            var library = DramaBoxLibcurl.Find();
            if (!string.IsNullOrWhiteSpace(library))
            {
                try
                {
                    await Task.Run(
                        () => DramaBoxLibcurl.DownloadSegmented(library, url, targetPath, 16, report),
                        cancellationToken);
                    if (!IsDramaBoxDeniedFile(targetPath))
                        return;
                    report?.Invoke("直连分块被 CDN 拒绝，改用代理分块");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    report?.Invoke($"直连分块失败，改用代理分块：{ex.Message}");
                }

                DeleteIfExists(targetPath);
            }

            try
            {
                await DownloadHttpContentAsync(
                    url,
                    targetPath,
                    requestedSegments,
                    report,
                    cancellationToken,
                    pieceSize,
                    pieceWorkers);
                if (!IsDramaBoxDeniedFile(targetPath))
                    return;
                report?.Invoke("CDN 拒绝了 .NET 下载，改用备用通道");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report?.Invoke($"直连下载失败，改用备用通道：{ex.Message}");
            }

            DeleteIfExists(targetPath);
            await _dramaBoxApiService.DownloadFileAsync(url, targetPath, cancellationToken);
            return;
        }

        var segments = Math.Clamp(
            requestedSegments <= 0 ? DefaultDownloadFileSegments : requestedSegments,
            1,
            MaxDownloadFileSegments);
        if (segments <= 1 && pieceSize <= 0)
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

        if (pieceSize > 0 && totalBytes >= MinSegmentedDownloadSize)
        {
            try
            {
                await DownloadPooledPiecesAsync(
                    url,
                    targetPath,
                    totalBytes,
                    pieceSize,
                    pieceWorkers > 0 ? pieceWorkers : HghighPiecePoolWorkers,
                    cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                DeleteIfExists(targetPath);
                throw;
            }
            catch (Exception ex)
            {
                DeleteIfExists(targetPath);
                report?.Invoke($"固定分片下载不可用，已回退等分分块：{ex.Message}");
            }
        }

        var segmentCount = PlanDownloadSegmentCount(totalBytes, segments);
        if (segmentCount > 1)
        {
            try
            {
                if (pieceSize <= 0)
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

    private async Task DownloadPooledPiecesAsync(
        string url,
        string targetPath,
        long totalBytes,
        int pieceSize,
        int workers,
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

        var ranges = BuildPooledPieceRanges(totalBytes, pieceSize);
        var workerCount = Math.Max(1, Math.Min(workers, ranges.Count));
        using var gate = new SemaphoreSlim(workerCount);
        await Task.WhenAll(ranges.Select(async range =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await DownloadRangeAsync(url, targetPath, range.Start, range.End, totalBytes, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));
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

    private static bool IsDramaBoxDeniedFile(string path)
    {
        if (!File.Exists(path))
            return true;
        var info = new FileInfo(path);
        if (info.Length == 0 || info.Length > 8192)
            return false;
        var text = File.ReadAllText(path);
        return text.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("<HTML", StringComparison.OrdinalIgnoreCase);
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

    internal static int ResolveDownloadConcurrency(int requested, int minimum = 0)
    {
        return Math.Max(Math.Clamp(requested, 1, 10), Math.Clamp(minimum, 0, 10));
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

    internal static IReadOnlyList<(long Start, long End)> BuildPooledPieceRanges(long totalBytes, int pieceSize)
    {
        if (totalBytes <= 0 || pieceSize <= 0)
            return [];

        var ranges = new List<(long Start, long End)>();
        long start = 0;
        while (start < totalBytes)
        {
            var end = Math.Min(start + pieceSize - 1, totalBytes - 1);
            ranges.Add((start, end));
            start = end + 1;
        }

        return ranges;
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

    internal static bool IsAccountLevelDownloadFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("未授权", StringComparison.Ordinal)
               || message.Contains("积分不足", StringComparison.Ordinal)
               || message.Contains("请授权账号", StringComparison.Ordinal);
    }

    private static void RememberEpisodeFailure(
        ICollection<string> failures,
        EpisodeTask task,
        Exception exception,
        CancellationTokenSource? accountFailureStop)
    {
        var accountLevel = IsAccountLevelDownloadFailure(exception.Message);
        lock (failures)
        {
            if (!(accountLevel && failures.Any(IsAccountLevelDownloadFailure)))
                failures.Add($"第{task.EpisodeNumber:00}集 {exception.Message}");
        }

        if (accountLevel)
            accountFailureStop?.Cancel();
    }

    private static void ReportLowerHevc(
        SourceVideoDetail detail,
        string quality,
        EpisodeTask task,
        int totalCount,
        IProgress<string>? progress)
    {
        if (!int.TryParse(HongguoHighCrypto.NormalizeQuality(quality), out var requested))
            return;
        var note = HongguoGuestPlayback.LowerQualityNote(requested, detail.SelectedDefinition);
        if (note is null)
            return;
        progress?.Report($"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集 {note}");
    }

    private static string DescribeEpisodeDownloadFailure(EpisodeTask task, int totalCount, Exception exception)
    {
        var prefix = $"[{task.Order:00}/{totalCount:00}] 第{task.EpisodeNumber:00}集";
        return HongguoGuestPlayback.IsUnsupportedCodec(exception)
            ? $"{prefix}未下载: {exception.Message}"
            : $"{prefix}下载失败: {exception.Message}";
    }

    internal static bool ShouldRetryDownload(Exception exception)
    {
        if (IsAccountLevelDownloadFailure(exception.Message))
            return false;
        if (HongguoGuestPlayback.IsUnsupportedCodec(exception))
            return false;
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
               message.Contains("解析超过", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("empty body", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("播放地址为空", StringComparison.Ordinal) ||
               message.Contains("设备注册失败", StringComparison.Ordinal);
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
        CancellationToken cancellationToken,
        string? searchCategory = null)
    {
        using var document = await PikachuDramaClient.RequestFanqieSearchAsync(
            _httpClient,
            settings.PikachuFanqieCookie,
            ResolvePikachuDramaType(settings, searchCategory),
            keyword,
            page,
            cancellationToken);

        return ReadFanqieSearchItems(document, PikachuBookPrefix);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchPlayletAsync(
        string keyword,
        int page,
        DramaSourceSettings settings,
        CancellationToken cancellationToken,
        string? searchCategory = null)
    {
        using var document = await PikachuDramaClient.RequestFanqieSearchAsync(
            _httpClient,
            fanqieCookie: "",
            ResolvePikachuDramaType(settings, searchCategory),
            keyword,
            page,
            cancellationToken,
            allowMissingCookie: true);
        return ReadFanqieSearchItems(document, PlayletDramaClient.BookPrefix);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> GetHongguoAppNewReleaseAsync(
        string genre,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults,
        CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        var page = await _hongguoApp.GetNewReleasePageAsync(
            genre,
            0,
            null,
            cancellationToken,
            settings.HongguoAppDeviceId,
            settings.HongguoAppInstallId);
        var items = page.Items.Select(item => RetargetBook(item, HongguoAppBookPrefix)).ToArray();
        partialResults?.Report(items);
        return items;
    }

    private async Task<IReadOnlyList<DramaSearchItem>> SearchHongguoAppAsync(
        string keyword,
        int page,
        DramaSourceSettings settings,
        CancellationToken cancellationToken,
        string? searchCategory = null)
    {
        var items = await _hongguoApp.SearchAsync(
            keyword,
            page,
            cancellationToken,
            searchCategory,
            settings.HongguoAppDeviceId,
            settings.HongguoAppInstallId,
            (deviceId, installId) => _settingsProvider.SaveHongguoAppPlaybackIdentity(deviceId, installId),
            settings.HongguoAppPlan);
        return items.Select(item => RetargetBook(item, HongguoAppBookPrefix)).ToArray();
    }

    private async Task<HongguoRankPage> RetargetRankPageAsync(
        string category,
        string board,
        int offset,
        CancellationToken cancellationToken)
    {
        var settings = _settingsProvider.Get();
        var page = await _hongguoApp.GetRankPageAsync(
            category,
            board,
            offset,
            cancellationToken,
            settings.HongguoAppDeviceId,
            settings.HongguoAppInstallId,
            (deviceId, installId) => _settingsProvider.SaveHongguoAppPlaybackIdentity(deviceId, installId),
            settings.HongguoAppPlan);
        return new HongguoRankPage(
            page.Items.Select(item => RetargetBook(item, HongguoAppBookPrefix)).ToArray(),
            page.HasMore,
            page.NextOffset);
    }

    private static DramaSearchItem RetargetBook(DramaSearchItem item, string bookPrefix)
    {
        var id = item.BookId ?? string.Empty;
        var colon = id.IndexOf(':');
        var raw = colon >= 0 ? id[(colon + 1)..] : id;
        return item with { BookId = bookPrefix + raw };
    }

    private static IReadOnlyList<DramaSearchItem> ReadFanqieSearchItems(JsonDocument document, string bookPrefix)
    {
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
                    BookId: EnsurePrefixed(bookId, bookPrefix),
                    Title: GetString(info, "book_name") ?? string.Empty,
                    Category: GetString(info, "category") ?? string.Empty,
                    EpisodeTotal: GetInt(info, "serial_count") ?? 0,
                    Intro: GetString(info, "abstract") ?? string.Empty,
                    PosterUrl: GetString(info, "thumb_url") ?? string.Empty,
                    Author: GetString(info, "author") ?? string.Empty,
                    PublishTime: GetString(info, "create_time") ?? string.Empty,
                    FavoriteCount: GetInt(info, "favorite_count") ?? GetInt(info, "collect_count") ?? 0,
                    PlayCount: DramaPlayCount.Read(info, GetInt)));
            }
        }

        return results;
    }

    private static string ResolvePikachuDramaType(DramaSourceSettings settings, string? searchCategory)
    {
        if (string.IsNullOrWhiteSpace(searchCategory))
        {
            return settings.PikachuDramaType;
        }

        return DramaSearchCategory.Normalize(searchCategory) switch
        {
            DramaSearchCategory.Comic => "manga",
            DramaSearchCategory.Short => "short",
            _ => settings.PikachuDramaType
        };
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

    private async Task<IReadOnlyList<SourceEpisode>> GetDramaBoxEpisodesAsync(
        string bookId, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var items = await _dramaBoxApiService.GetEpisodesAsync(settings, bookId, cancellationToken);
        var coverUrl = items.Select(item => item.CoverUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;
        return items.Select(item => new SourceEpisode(
            item.EpisodeNumber, item.Title, item.VideoId, coverUrl)).ToArray();
    }

    private async Task<SourceVideoDetail> GetDramaBoxVideoUrlAsync(
        string videoId, string quality, DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var detail = await _dramaBoxApiService.GetPlaybackAsync(settings, videoId, quality, cancellationToken);
        return new SourceVideoDetail(detail.Url);
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

    private async Task<IReadOnlyList<SourceEpisode>> GetForeignEpisodesAsync(
        string prefixedBookId,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var episodes = await ForeignPlayletClient.GetEpisodesAsync(
            _httpClient,
            settings.ForeignServerUrl,
            settings.ForeignDeviceId,
            prefixedBookId,
            cancellationToken);
        return episodes
            .Select(item => new SourceEpisode(item.Number, item.Title, item.VideoId, string.Empty))
            .ToArray();
    }

    private async Task<SourceVideoDetail> GetForeignVideoUrlAsync(
        string videoId,
        string quality,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var video = await ForeignPlayletClient.GetVideoAsync(
            _httpClient,
            settings.ForeignServerUrl,
            settings.ForeignDeviceId,
            videoId,
            quality,
            cancellationToken);
        return new SourceVideoDetail(video.Url);
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetPlayletEpisodesAsync(
        string prefixedBookId,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var episodes = await PlayletDramaClient.GetEpisodesAsync(
            _httpClient,
            settings.PlayletServerUrl,
            prefixedBookId,
            cancellationToken);
        return episodes
            .Select(item => new SourceEpisode(
                item.Number,
                item.Title,
                EnsurePrefixed(item.VideoId, PlayletDramaClient.EpisodePrefix),
                string.Empty))
            .ToArray();
    }

    private async Task<SourceVideoDetail> GetPlayletVideoUrlAsync(
        string prefixedVideoId,
        string quality,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var video = await PlayletDramaClient.GetVideoAsync(
            _httpClient,
            settings.PlayletServerUrl,
            prefixedVideoId,
            quality,
            settings.PlayletDeviceId,
            cancellationToken);
        return new SourceVideoDetail(video.Url, video.Key);
    }

    private async Task<IReadOnlyList<SourceEpisode>> GetHongguoAppEpisodesAsync(
        string prefixedBookId,
        DramaSourceSettings settings,
        CancellationToken cancellationToken)
    {
        var bookId = prefixedBookId.StartsWith(HongguoAppBookPrefix, StringComparison.OrdinalIgnoreCase)
            ? prefixedBookId[HongguoAppBookPrefix.Length..]
            : prefixedBookId;
        var items = await _hongguoApp.GetEpisodesAsync(
            bookId,
            cancellationToken,
            settings.HongguoAppDeviceId,
            settings.HongguoAppInstallId,
            (deviceId, installId) => _settingsProvider.SaveHongguoAppPlaybackIdentity(deviceId, installId),
            settings.HongguoAppPlan);
        return items
            .Select(item => new SourceEpisode(item.EpisodeNumber, item.Title, item.VideoId, item.PosterUrl))
            .ToArray();
    }

    private async Task<string> ChooseHongguoAppDownloadQualityAsync(
        IReadOnlyList<EpisodeTask> tasks,
        string quality,
        Dictionary<string, JsonObject> models,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var current = _settingsProvider.Get();
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (models.ContainsKey(task.VideoId))
                continue;
            models[task.VideoId] = await _hongguoApp.GetPlaybackModelAsync(
                task.VideoId,
                cancellationToken,
                current.HongguoAppDeviceId,
                current.HongguoAppInstallId,
                (deviceId, installId) => _settingsProvider.SaveHongguoAppPlaybackIdentity(deviceId, installId),
                current.HongguoAppStreamLink1,
                current.HongguoAppStreamLink2,
                current.HongguoAppPlan);
        }

        var requested = HongguoHighCrypto.NormalizeQuality(quality);
        var chosen = HongguoGuestPlayback.ChooseUniformQuality(quality, models.Values.ToArray());
        if (!string.Equals(requested, chosen, StringComparison.Ordinal))
            progress?.Report($"有剧集的 {requested}P 没有 FFmpeg 可播放的编码，全部改用 {chosen}P。");
        var blocked = HongguoGuestPlayback.MissingPlayableDownloadMessage(
            tasks.Select(task => (
                task.EpisodeNumber,
                models.TryGetValue(task.VideoId, out var model) ? model : null)).ToArray(),
            chosen);
        if (blocked is not null)
        {
            progress?.Report(blocked);
            throw new HongguoHighException(blocked);
        }

        return chosen;
    }

    private async Task<SourceVideoDetail> GetHongguoAppVideoUrlAsync(
        string prefixedVideoId,
        string quality,
        DramaSourceSettings settings,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, JsonObject>? models = null,
        bool lockDefinition = false)
    {
        HongguoHighVideoPlayback detail;
        if (models is not null && models.TryGetValue(prefixedVideoId, out var model))
        {
            detail = HongguoGuestPlayback.Pick(model, quality, lockDefinition);
        }
        else
        {
            var current = _settingsProvider.Get();
            detail = await _hongguoApp.GetVideoPlaybackAsync(
                prefixedVideoId,
                quality,
                cancellationToken,
                current.HongguoAppDeviceId,
                current.HongguoAppInstallId,
                (deviceId, installId) => _settingsProvider.SaveHongguoAppPlaybackIdentity(deviceId, installId),
                current.HongguoAppStreamLink1,
                current.HongguoAppStreamLink2,
                current.HongguoAppPlan);
        }

        return new SourceVideoDetail(
            detail.Url,
            HongguoCdn: new HongguoCdnDownload(detail.EncryptedUrls, detail.SpadeA, detail.Encrypted),
            ExpectedSize: detail.Size,
            SelectedDefinition: detail.Definition);
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

    public async Task DownloadMissingPosterAsync(
        string projectDirectory,
        string displayName,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            throw new InvalidOperationException("没有项目目录，无法补下海报原图。");
        var posterUrl = ReadPosterUrlFromProject(projectDirectory);
        if (!LooksLikeHttpUrl(posterUrl))
            posterUrl = ReadPosterUrlFromLinkedWorkflow(projectDirectory);
        if (!LooksLikeHttpUrl(posterUrl))
            throw new InvalidOperationException("没有已保存的海报地址，无法只补下海报原图。");
        progress?.Report("开始补下海报原图。");
        WriteIntroFile(projectDirectory, progress);
        await EnsurePosterAsync(projectDirectory, displayName, posterUrl, progress, cancellationToken);
    }

    public async Task EnsurePosterAndIntroAsync(
        string projectDirectory,
        string displayName,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<string, bool>? validatePoster = null,
        bool downloadPoster = true)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            return;
        WriteIntroFile(projectDirectory, progress);

        if (!downloadPoster)
            return;

        var posterUrl = ReadPosterUrlFromProject(projectDirectory);
        if (!LooksLikeHttpUrl(posterUrl))
            posterUrl = ReadPosterUrlFromLinkedWorkflow(projectDirectory);
        if (!LooksLikeHttpUrl(posterUrl) || (validatePoster is null && PosterFileExists(projectDirectory)))
            return;
        await EnsurePosterAsync(projectDirectory, displayName, posterUrl, progress, cancellationToken, validatePoster);
    }

    public Task<(string Intro, string PosterUrl)> GetDramaBoxBookMaterialsAsync(
        string bookReference,
        CancellationToken cancellationToken) =>
        _dramaBoxApiService.GetBookMaterialsAsync(_settingsProvider.Get(), bookReference, cancellationToken);

    private async Task EnsurePosterAsync(
        string outputDir,
        string displayName,
        string posterUrl,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<string, bool>? validatePoster = null)
    {
        if (!LooksLikeHttpUrl(posterUrl))
        {
            return;
        }

        var extension = ResolveImageExtensionFromUrl(posterUrl);
        var targetPath = Path.Combine(outputDir, "海报原图" + extension);
        if (File.Exists(targetPath) && (validatePoster?.Invoke(targetPath) ?? new FileInfo(targetPath).Length > 0))
        {
            progress?.Report($"海报已存在，跳过: {Path.GetFileName(targetPath)}");
            return;
        }

        var downloadPath = validatePoster is null
            ? targetPath
            : Path.Combine(outputDir, $".poster-download-{Guid.NewGuid():N}.tmp");
        progress?.Report("开始下载海报...");
        try
        {
            await DownloadPosterFileAsync(posterUrl, downloadPath, cancellationToken);
            if (validatePoster is not null)
            {
                if (!validatePoster(downloadPath))
                    throw new InvalidDataException("下载内容不是可用的海报图片。");

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(downloadPath, targetPath, overwrite: true);
            }
            progress?.Report($"海报下载完成: {Path.GetFileName(targetPath)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"海报下载失败：{ex.Message}", ex);
        }
        finally
        {
            if (validatePoster is not null)
            {
                try { File.Delete(downloadPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Preserve the download result if the temporary file is held open.
                }
            }
        }
    }

    private async Task DownloadPosterFileAsync(string posterUrl, string targetPath, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, posterUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", MobileUserAgent);
            request.Headers.UserAgent.Add(UserAgentProduct);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await WriteReleasedFileAsync(targetPath, source, cancellationToken);
            if (new FileInfo(targetPath).Length > 0)
                return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        var library = DramaBoxLibcurl.Find();
        if (string.IsNullOrWhiteSpace(library))
            throw new InvalidOperationException("海报图片下载失败。");
        if (File.Exists(targetPath))
            File.Delete(targetPath);
        await Task.Run(
            () => DramaBoxLibcurl.Download(library, posterUrl, targetPath, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
                ["Accept"] = "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
            }, 60),
            cancellationToken);
        if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
            throw new InvalidOperationException("海报图片下载失败。");
    }

    private static bool PosterFileExists(string projectDirectory)
    {
        foreach (var name in new[] { "海报原图.jpg", "海报原图.jpeg", "海报原图.png", "海报原图.webp", "cover.jpg", "cover.png" })
        {
            var path = Path.Combine(projectDirectory, name);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
                return true;
        }

        return false;
    }

    private static void WriteIntroFile(string projectDirectory, IProgress<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            return;
        var target = Path.Combine(projectDirectory, "简介.txt");
        if (File.Exists(target) && new FileInfo(target).Length > 0)
            return;
        var intro = ReadProjectIntro(projectDirectory);
        if (string.IsNullOrWhiteSpace(intro))
            return;
        File.WriteAllText(target, intro.Trim() + Environment.NewLine);
        progress?.Report("简介已保存: 简介.txt");
    }

    private static string ReadProjectIntro(string projectDirectory)
    {
        var metadataPath = Path.Combine(projectDirectory, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
            return "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            foreach (var name in new[] { "intro", "introduction", "description", "desc" })
            {
                var value = GetString(document.RootElement, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task WriteReleasedFileAsync(string targetPath, Stream source, CancellationToken cancellationToken)
    {
        var tempPath = targetPath + ".part";
        try
        {
            await using (var target = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // 临时文件清不掉时保留现场，正式海报文件没有被独占打开。
            }

            throw;
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

    public static HashSet<int>? ParseEpisodeSelection(string? selection)
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
            return ContainsEncryptedSampleEntry(stream, 0, stream.Length);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsEncryptedSampleEntry(Stream stream, long start, long end)
    {
        Span<byte> header = stackalloc byte[16];
        var offset = start;
        while (offset + 8 <= end)
        {
            stream.Position = offset;
            if (stream.Read(header[..8]) != 8)
                return false;

            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = header.Slice(4, 4);
            long headerSize = 8;
            long resolvedSize;
            if (boxSize == 1)
            {
                if (offset + 16 > end || stream.Read(header[8..16]) != 8)
                    return false;
                resolvedSize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]));
                headerSize = 16;
            }
            else
            {
                resolvedSize = boxSize == 0 ? end - offset : boxSize;
            }

            if (resolvedSize < headerSize || offset + resolvedSize > end)
                return false;

            var boxEnd = offset + resolvedSize;
            if (type.SequenceEqual("stsd"u8))
            {
                if (StsdContainsEncryptedEntry(stream, offset + headerSize, boxEnd))
                    return true;
            }
            else if (IsMp4ContainerBox(type) &&
                     ContainsEncryptedSampleEntry(stream, offset + headerSize, boxEnd))
            {
                return true;
            }

            offset = boxEnd;
        }

        return false;
    }

    private static bool StsdContainsEncryptedEntry(Stream stream, long contentStart, long boxEnd)
    {
        if (contentStart + 8 > boxEnd)
            return false;

        Span<byte> header = stackalloc byte[8];
        stream.Position = contentStart;
        if (stream.Read(header) != 8)
            return false;

        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        var offset = contentStart + 8;
        for (var index = 0; index < entryCount && offset + 8 <= boxEnd; index++)
        {
            stream.Position = offset;
            if (stream.Read(header) != 8)
                return false;

            var entrySize32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var encrypted = header.Slice(4, 4).SequenceEqual("encv"u8) ||
                            header.Slice(4, 4).SequenceEqual("enca"u8);
            long headerSize = 8;
            long entrySize = entrySize32;
            if (entrySize32 == 1)
            {
                if (stream.Read(header) != 8)
                    return false;
                entrySize = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header));
                headerSize = 16;
            }
            else if (entrySize32 == 0)
            {
                entrySize = boxEnd - offset;
            }

            if (entrySize < headerSize || offset + entrySize > boxEnd)
                return false;
            if (encrypted)
                return true;
            offset += entrySize;
        }

        return false;
    }

    private static bool IsMp4ContainerBox(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("moov"u8) ||
        type.SequenceEqual("trak"u8) ||
        type.SequenceEqual("mdia"u8) ||
        type.SequenceEqual("minf"u8) ||
        type.SequenceEqual("stbl"u8);

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

    private static string? FindExistingEpisodeVideo(
        string outputDir,
        int outputEpisodeNumber,
        Action<string>? report,
        ExistingVideoPolicy existingVideoPolicy,
        ICollection<string>? replacementCandidates)
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

                    return path;
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

    private static InvalidOperationException PlaybackCheckFailed(string stateDirectory, string message)
    {
        RecordUnplayableDownload(stateDirectory, message);
        return new InvalidOperationException(message);
    }

    private static void RecordUnplayableDownload(string directory, string message)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DownloadStateFileName);
            var root = ReadDownloadState(path) ?? new JsonObject();
            root["ok"] = false;
            root["stopped"] = false;
            root["message"] = message;
            root[PlaybackCheckFailedKey] = true;
            WritePlaybackDownloadState(path, root);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private static JsonObject? ReadDownloadState(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsFailedDownloadState(JsonObject? state) =>
        string.Equals(state?["ok"]?.ToString(), "false", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedPlaybackCheck(JsonObject? state)
    {
        if (state is null || !IsFailedDownloadState(state))
            return false;
        if (state[PlaybackCheckFailedKey] is JsonValue flag && flag.TryGetValue<bool>(out var failed))
            return failed;

        var message = GetDownloadFailureMessage(state);
        return message == MissingVideosMessage || message?.EndsWith(UnplayableVideosMessage, StringComparison.Ordinal) == true;
    }

    private static string? GetDownloadFailureMessage(JsonObject state) =>
        state["message"] is JsonValue value && value.TryGetValue<string>(out var message) &&
        !string.IsNullOrWhiteSpace(message) ? message.Trim() : null;

    private static void WritePlaybackDownloadState(string path, JsonObject state)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool TryReadEpisodeNumber(string path, out int episode)
    {
        episode = 0;
        var name = Path.GetFileNameWithoutExtension(path);
        var start = name.IndexOf('第');
        var end = start < 0 ? -1 : name.IndexOf('集', start + 1);
        return start >= 0 &&
               end > start &&
               int.TryParse(name[(start + 1)..end].Trim(), out episode) &&
               episode > 0;
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

    private static void RememberPosterUrl(string projectDir, string posterUrl)
    {
        if (!LooksLikeHttpUrl(posterUrl))
            return;
        var metadataPath = Path.Combine(projectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
            return;

        try
        {
            var json = File.ReadAllText(metadataPath);
            using var document = JsonDocument.Parse(json);
            var current = GetString(document.RootElement, "posterUrl") ?? GetString(document.RootElement, "poster_url") ?? "";
            if (LooksLikeHttpUrl(current))
                return;
            var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(document.RootElement.GetRawText())
                          ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            payload["posterUrl"] = posterUrl;
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // The downloaded image is what the poster step needs.
        }
    }

    private static string ReadPosterUrlFromLinkedWorkflow(string projectDirectory)
    {
        var metadataPath = Path.Combine(projectDirectory, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
            return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var workflow = GetString(document.RootElement, "workflowProjectDir");
            if (string.IsNullOrWhiteSpace(workflow))
                return string.Empty;
            return ReadPosterUrlFromProject(workflow);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return string.Empty;
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

    private static void ThrowIfForeignCatalogUnsupported(DramaSourceSettings settings)
    {
        if (string.Equals(settings.DramaSourceChain?.Trim(), "foreign", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("海外短剧下载器当前只支持关键词搜索和下载。");
    }

    private static string? ResolveSelectedService(string selected, IReadOnlyList<string> supportedServices)
    {
        var normalized = (selected ?? "hgnew").Trim().ToLowerInvariant();
        if (normalized is "hglocal" or "mapleleaf")
            normalized = DramaDownloadSourceSelector.DefaultChain;
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
        long ExpectedSize = 0,
        int SelectedDefinition = 0);

    private sealed record HongguoCdnDownload(
        IReadOnlyList<string> EncryptedUrls,
        string SpadeA,
        bool Encrypted);

    internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError);


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
