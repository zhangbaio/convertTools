using System.Net.Http.Json;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Kuaishou.Analytics;

public static class KuaishouWorksPlayApi
{
    private static readonly Uri Overview = new("https://cp.kuaishou.com/rest/cp/creator/analysis/pc/author/overview");
    private const string ApiCookieName = "kuaishou.web.cp.api_ph";

    public static async Task<IReadOnlyDictionary<DateOnly, long>> CollectAsync(
        string authStatePath, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cookies = KuaishouAuthCookies.Load(authStatePath);
        var body = new Dictionary<string, object> { ["timeType"] = 2 };
        if (cookies.GetCookies(Overview)[ApiCookieName] is { } apiCookie)
            body[ApiCookieName] = apiCookie.Value;

        using var client = new HttpClient(new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = false,
        }) { Timeout = TimeSpan.FromSeconds(12) };
        using var request = new HttpRequestMessage(HttpMethod.Post, Overview)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Referrer = new Uri(KuaishouWorksPlayCollector.WorksUrl);
        request.Headers.TryAddWithoutValidation("Origin", Overview.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return KuaishouWorksPlayParser.ParseOverview(json, start, end);
    }
}
