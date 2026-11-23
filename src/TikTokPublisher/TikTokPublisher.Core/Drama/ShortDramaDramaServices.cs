using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Models;
using System.Net;
using CoreSearchItem = ShortDrama.Core.Models.DramaSearchItem;

namespace TikTokPublisher.Core.Drama;

/// <summary>
/// 桥接 <c>ShortDrama.Infrastructure</c> 多链路短剧搜索/下载（hgnew / pikachu / hghigh）。
/// </summary>
public static class ShortDramaDramaServices
{
    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttpClient);
    private static readonly Lazy<ClientSettingsDramaSettingsProvider> SettingsProvider = new(() => new ClientSettingsDramaSettingsProvider());
    private static readonly Lazy<DramaSourceRouter> Router = new(() => new DramaSourceRouter(
        SharedHttp.Value,
        SettingsProvider.Value,
        new HongguoNewApiService(SharedHttp.Value),
        new HongguoDramaSearchService(SharedHttp.Value),
        new HongguoDramaDownloader(SharedHttp.Value),
        new HongguoMemoryReaderService()));

    public static IDramaSearchService Search => Router.Value;
    public static IDramaDownloader Downloader => Router.Value;

    public static void UseSettingsDatabase(string? databasePath) =>
        SettingsProvider.Value.UseDatabase(databasePath);

    private static readonly AsyncLocal<ClientSettings?> HeldSettings = new();

    internal static ClientSettings? OperationSettings => HeldSettings.Value;

    public static IDisposable HoldSettings(ClientSettings settings)
    {
        var previous = HeldSettings.Value;
        HeldSettings.Value = settings.Clone();
        return new SettingsHoldScope(previous);
    }

    public static void RefreshSettings(ClientSettings? settings = null)
    {
        if (settings is not null)
        {
            SettingsProvider.Value.Replace(settings);
            return;
        }

        if (HeldSettings.Value is not null)
            return;

        SettingsProvider.Value.Reload();
    }

    private sealed class SettingsHoldScope(ClientSettings? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            HeldSettings.Value = previous;
        }
    }

    public static Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken) =>
        SearchAsync(keyword, page, cancellationToken, searchCategory: null);

    public static async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken,
        string? searchCategory)
    {
        RefreshSettings();
        var items = await Router.Value.SearchAsync(keyword, page, cancellationToken, searchCategory);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> GetTodayAsync(CancellationToken cancellationToken)
    {
        RefreshSettings();
        var items = await Search.GetTodayAsync(cancellationToken);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> GetMangaTodayAsync(int days, CancellationToken cancellationToken)
        => await GetMangaTodayAsync(days, enrich: true, progress: null, cancellationToken);

    public static async Task<IReadOnlyList<DramaSearchItem>> GetMangaTodayAsync(
        int days,
        bool enrich,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        RefreshSettings();
        if (Search is not DramaSourceRouter router)
        {
            return [];
        }

        var corePartial = partialResults is null
            ? null
            : new ForwardProgress<IReadOnlyList<CoreSearchItem>>(items =>
                partialResults.Report(items.Select(FromCore).ToArray()));
        var coreDetails = detailResults is null
            ? null
            : new ForwardProgress<IReadOnlyList<CoreSearchItem>>(items =>
                detailResults.Report(items.Select(FromCore).ToArray()));
        var items = await router.GetMangaTodayAsync(
            days, enrich, progress, cancellationToken, corePartial, coreDetails);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> GetAiTodayAsync(int days, CancellationToken cancellationToken)
        => await GetAiTodayAsync(days, enrich: true, progress: null, cancellationToken);

    public static async Task<IReadOnlyList<DramaSearchItem>> GetAiTodayAsync(
        int days,
        bool enrich,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? partialResults = null,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        RefreshSettings();
        if (Search is not DramaSourceRouter router)
        {
            return [];
        }

        var corePartial = partialResults is null
            ? null
            : new ForwardProgress<IReadOnlyList<CoreSearchItem>>(items =>
                partialResults.Report(items.Select(FromCore).ToArray()));
        var coreDetails = detailResults is null
            ? null
            : new ForwardProgress<IReadOnlyList<CoreSearchItem>>(items =>
                detailResults.Report(items.Select(FromCore).ToArray()));
        var items = await router.GetAiTodayAsync(
            days, enrich, progress, cancellationToken, corePartial, coreDetails);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> EnrichHighNewReleaseItemsAsync(
        IReadOnlyList<DramaSearchItem> items,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<IReadOnlyList<DramaSearchItem>>? detailResults = null)
    {
        RefreshSettings();
        if (Search is not DramaSourceRouter router)
            return items;
        var coreItems = items.Select(ToCore).ToArray();
        var coreDetails = detailResults is null
            ? null
            : new ForwardProgress<IReadOnlyList<CoreSearchItem>>(batch =>
                detailResults.Report(batch.Select(FromCore).ToArray()));
        var enriched = await router.EnrichHighNewReleaseItemsAsync(
            coreItems,
            progress,
            cancellationToken,
            coreDetails);
        return enriched.Select(FromCore).ToArray();
    }

    public readonly record struct HongguoAppNewReleasePage(
        IReadOnlyList<DramaSearchItem> Items,
        bool HasMore,
        int NextOffset,
        string SessionId);

    public static async Task<HongguoAppNewReleasePage> GetHongguoAppNewReleasePageAsync(
        bool ai,
        int offset,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        RefreshSettings();
        var page = await Router.Value.GetHongguoAppNewReleasePageAsync(ai, offset, sessionId, cancellationToken);
        return new HongguoAppNewReleasePage(
            page.Items.Select(FromCore).ToArray(),
            page.HasMore,
            page.NextOffset,
            page.SessionId);
    }

    public readonly record struct DramaBoxNewPage(
        IReadOnlyList<DramaSearchItem> Items,
        bool HasMore,
        int NextPage);

    public static async Task<DramaBoxNewPage> GetDramaBoxNewPageAsync(int page, CancellationToken cancellationToken)
    {
        RefreshSettings();
        var result = await Router.Value.GetDramaBoxNewPageAsync(page, cancellationToken);
        return new DramaBoxNewPage(result.Items.Select(FromCore).ToArray(), result.HasMore, result.NextPage);
    }

    public static bool IsDramaBoxSourceSelected()
    {
        RefreshSettings();
        return Router.Value.IsDramaBoxSourceSelected();
    }

    public static bool IsHongguoAppSourceSelected()
    {
        RefreshSettings();
        return Router.Value.IsHongguoAppSourceSelected();
    }

    public static bool IsPlayletSourceSelected()
    {
        RefreshSettings();
        return Router.Value.IsPlayletSourceSelected();
    }

    public static bool IsHighSourceSelected()
    {
        RefreshSettings();
        return Router.Value.IsHighSourceSelected();
    }

    public static IReadOnlyList<string> RankBoardsFor(string category) =>
        HongguoRankCatalog.BoardsFor(category);

    public static string? RankBlockReason(string category, string board)
    {
        RefreshSettings();
        if (!Router.Value.IsRankSourceSelected())
            return "榜单接入红果高码率标准版、红果短剧下载器和红果APP。请先切换到其中一条数据链路。";
        return HongguoRankCatalog.UnavailableReason(category, board);
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> GetRankAsync(
        string category,
        string board,
        int page,
        CancellationToken cancellationToken)
    {
        RefreshSettings();
        var items = await Router.Value.GetRankAsync(category, board, page, cancellationToken);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<DramaRankPage> GetRankPageAsync(
        string category,
        string board,
        int offset,
        CancellationToken cancellationToken)
    {
        RefreshSettings();
        var page = await Router.Value.GetRankPageAsync(category, board, offset, cancellationToken);
        return new DramaRankPage(page.Items.Select(FromCore).ToArray(), page.HasMore, page.NextOffset);
    }

    public static string GetHighNewReleaseDateWindowDisplay(int days) =>
        HongguoHighCalendarMapper.ResolveRecentDateWindow(days).DisplayText;

    public static IReadOnlyList<DramaSearchItem> FilterEnrichedHighNewReleaseItems(
        IEnumerable<DramaSearchItem> items,
        int days) =>
        HongguoHighCalendarMapper
            .FilterEnrichedByRecentDays(items.Select(ToCore), days)
            .Select(FromCore)
            .ToArray();

    public static async Task<IReadOnlyList<DramaSearchItem>> GetHistoryAsync(int days, CancellationToken cancellationToken)
    {
        RefreshSettings();
        if (Search is not DramaSourceRouter router)
        {
            return [];
        }

        var items = await router.GetHistoryAsync(days, cancellationToken);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<string> BootstrapAsync(
        string rootDir,
        DramaSearchItem item,
        string episodes,
        string quality,
        int concurrent,
        string episodeNumberMode,
        string queueEntryDramaType,
        CancellationToken cancellationToken,
        Action<string>? beforeWrite = null)
    {
        var bootstrapper = new DramaProjectBootstrapper();
        var result = await bootstrapper.BootstrapAsync(
            new DramaProjectBootstrapRequest(
                RootDir: rootDir,
                Drama: ToCore(item),
                CompanyName: null,
                Episodes: string.IsNullOrWhiteSpace(episodes) ? "all" : episodes.Trim(),
                Quality: string.IsNullOrWhiteSpace(quality) ? "1080P" : quality.Trim(),
                Concurrent: Math.Clamp(concurrent, 1, 10),
                EpisodeNumberMode: NormalizeEpisodeNumberMode(episodeNumberMode),
                QueueEntryDramaType: queueEntryDramaType),
            cancellationToken,
            beforeWrite);
        return result.SourceProjectDir;
    }

    public static DramaSearchItem FromCore(CoreSearchItem core) => new()
    {
        BookId = core.BookId,
        Title = core.Title,
        Category = core.Category,
        EpisodeTotal = core.EpisodeTotal,
        Intro = core.Intro,
        PosterUrl = core.PosterUrl,
        Author = core.Author,
        PublishTime = core.PublishTime,
        FavoriteCount = core.FavoriteCount,
        PlayCount = core.PlayCount,
    };

    private static CoreSearchItem ToCore(DramaSearchItem item) => new(
        BookId: item.BookId,
        Title: item.Title,
        Category: item.Category,
        EpisodeTotal: item.EpisodeTotal,
        Intro: item.Intro,
        PosterUrl: item.PosterUrl,
        Author: item.Author,
        PublishTime: item.PublishTime,
        FavoriteCount: item.FavoriteCount,
        PlayCount: item.PlayCount);

    private static string NormalizeEpisodeNumberMode(string? value)
    {
        return string.Equals(value?.Trim(), "continuous", StringComparison.OrdinalIgnoreCase)
            ? "continuous"
            : "source";
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    private sealed class ForwardProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
