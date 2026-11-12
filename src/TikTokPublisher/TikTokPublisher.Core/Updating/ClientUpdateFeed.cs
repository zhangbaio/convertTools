using System.Net.Http.Headers;
using System.Text.Json;

namespace TikTokPublisher.Core.Updating;

public sealed record ClientUpdateOffer(string FeedUrl, string Version, string Notes, string SetupUrl);

public sealed record ClientUpdateDownloadResult(
    bool Found,
    bool Downloaded,
    bool NeedsInstaller,
    string Version,
    string Message);

public static class ClientUpdateRuntime
{
    public static Func<bool>? IsQueueRunning { get; set; }

    public static Func<string, CancellationToken, Task<ClientUpdateDownloadResult>>? DownloadAsync { get; set; }

    public static Action? Restart { get; set; }

    public static bool ShouldPromptRestart(bool downloaded, bool queueRunning) => downloaded && !queueRunning;
}

public static class ClientUpdateFeed
{
    public const string DefaultObjectPrefix = "client-releases/tiktok";

    public static string BuildFeedUrl(string publicBaseUrl, string? objectPrefix)
    {
        var baseUrl = (publicBaseUrl ?? "").Trim().TrimEnd('/');
        var prefix = string.IsNullOrWhiteSpace(objectPrefix) ? DefaultObjectPrefix : objectPrefix.Trim().Trim('/');
        return $"{baseUrl}/{prefix}/";
    }

    public static bool IsNewerVersion(string? current, string? remote)
    {
        if (!TryParseVersion(remote, out var remoteVersion))
            return false;
        if (!TryParseVersion(current, out var currentVersion))
            return true;
        return remoteVersion > currentVersion;
    }

    public static ClientUpdateOffer? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("available", out var available) &&
                available.ValueKind == JsonValueKind.False)
                return null;
            var feedUrl = ReadString(root, "feedUrl");
            var version = ReadString(root, "version");
            if (feedUrl.Length == 0 || version.Length == 0)
                return null;
            return new ClientUpdateOffer(feedUrl, version, ReadString(root, "notes"), ReadString(root, "setupUrl"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<ClientUpdateOffer?> FetchAsync(string? authServerUrl, CancellationToken cancellationToken)
    {
        var baseUrl = (authServerUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
            return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.GetAsync($"{baseUrl}/api/client-update/current", cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        var value = (text ?? "").Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];
        return Version.TryParse(value, out version!);
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
