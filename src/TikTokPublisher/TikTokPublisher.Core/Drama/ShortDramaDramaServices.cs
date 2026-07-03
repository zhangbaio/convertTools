using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure.Automation;
using TikTokPublisher.Core.Models;
using System.Net;
using CoreSearchItem = ShortDrama.Core.Models.DramaSearchItem;

namespace TikTokPublisher.Core.Drama;

/// <summary>
/// 桥接 <c>ShortDrama.Infrastructure</c> 多链路短剧搜索/下载（hgnew / hglocal / pikachu）。
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
