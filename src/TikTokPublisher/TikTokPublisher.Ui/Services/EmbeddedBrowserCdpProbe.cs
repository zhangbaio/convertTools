using System.Net.Http;

namespace TikTokPublisher.Ui.Services;

internal static class EmbeddedBrowserCdpProbe
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }

    public static async Task<bool> IsReachableAsync(string? endpoint, CancellationToken ct)
    {
        var normalized = (endpoint ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(normalized))
            return false;

        try
        {
            using var response = await Http.GetAsync($"{normalized}/json/version", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
