using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Models;
using System.Net;
using CoreSearchItem = ShortDrama.Core.Models.DramaSearchItem;

namespace TikTokPublisher.Core.Drama;

/// <summary>
/// 桥接 <c>ShortDrama.Infrastructure</c> 多链路短剧搜索/下载（hgnew / hglocal / pikachu / hghigh）。
/// </summary>
public static class ShortDramaDramaServices
{
    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttpClient);
    private static readonly Lazy<ClientSettingsDramaSettingsProvider> SettingsProvider = new(() => new ClientSettingsDramaSettingsProvider());
    private static readonly Lazy<DramaSourceRouter> Router = new(() => new DramaSourceRouter(
        SharedHttp.Value,
        SettingsProvider.Value,
        new HongguoLocalApiService(SharedHttp.Value),
        new HongguoNewApiService(SharedHttp.Value),
        new HongguoDramaSearchService(SharedHttp.Value),
        new HongguoDramaDownloader(SharedHttp.Value),
        new HongguoMemoryReaderService()));

    public static IDramaSearchService Search => Router.Value;
    public static IDramaDownloader Downloader => Router.Value;

    public static void RefreshSettings(ClientSettings? settings = null)
    {
        if (settings is not null)
        {
            SettingsProvider.Value.Replace(settings);
        }
        else
        {
            SettingsProvider.Value.Replace(Services.ClientSettingsStore.Load());
        }
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        RefreshSettings();
        var items = await Search.SearchAsync(keyword, page, cancellationToken);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> GetTodayAsync(CancellationToken cancellationToken)
    {
        RefreshSettings();
        var items = await Search.GetTodayAsync(cancellationToken);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> GetMangaTodayAsync(int days, CancellationToken cancellationToken)
    {
        RefreshSettings();
        if (Search is not DramaSourceRouter router)
        {
            return [];
        }

        var items = await router.GetMangaTodayAsync(days, cancellationToken);
        return items.Select(FromCore).ToArray();
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> GetAiTodayAsync(int days, CancellationToken cancellationToken)
    {
        RefreshSettings();
        if (Search is not DramaSourceRouter router)
        {
            return [];
        }

        var items = await router.GetAiTodayAsync(days, cancellationToken);
        return items.Select(FromCore).ToArray();
    }

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
        CancellationToken cancellationToken)
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
            cancellationToken);
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
        FavoriteCount: item.FavoriteCount);

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
}
