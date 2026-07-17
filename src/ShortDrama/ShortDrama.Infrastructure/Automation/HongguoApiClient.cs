using ShortDrama.Infrastructure.Config;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation;

internal sealed class HongguoApiClient
{
    private const int MaxRequestAttempts = 3;
    private const string ApiUrl = "https://orz.icic.icu/api/hguo/api.php";
    private const string PreviewUrl = "https://orz.icic.icu/api/hguo/hgm.php";
    private const string UserAgentString =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    private readonly HttpClient _httpClient;
    private readonly HongguoAccessOptions _accessOptions;

    public HongguoApiClient(HttpClient httpClient, HongguoAccessOptions accessOptions)
    {
        _httpClient = httpClient;
        _accessOptions = accessOptions;
    }

    public async Task<IReadOnlyList<JsonElement>> SearchAsync(string keyword, int page, CancellationToken cancellationToken)
    {
        var (data, message) = await RequestAsync(
            ApiUrl,
            new Dictionary<string, string>
            {
                ["action"] = "search",
                ["name"] = keyword,
                ["page"] = Math.Max(1, page).ToString()
            },
            cancellationToken);

        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "搜索短剧失败" : message);
        }

        return data.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    public async Task<IReadOnlyList<JsonElement>> GetTodayNewAsync(CancellationToken cancellationToken)
    {
        var (data, message) = await RequestAsync(
            ApiUrl,
            new Dictionary<string, string>
            {
                ["action"] = "today_new"
            },
            cancellationToken);

        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "获取今日上新失败" : message);
        }

        return data.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    public async Task<IReadOnlyList<JsonElement>> GetEpisodesAsync(string bookId, CancellationToken cancellationToken)
    {
        JsonElement data;
        string message;

        if (_accessOptions.HasKey)
        {
            (data, message) = await RequestAsync(
                ApiUrl,
                new Dictionary<string, string>
                {
                    ["action"] = "get_list",
                    ["key"] = _accessOptions.Key!,
                    ["machine_id"] = _accessOptions.MachineId,
                    ["book_id"] = bookId
                },
                cancellationToken);
        }
        else
        {
            (data, message) = await RequestAsync(
                PreviewUrl,
                new Dictionary<string, string>
                {
                    ["book_id"] = bookId
                },
                cancellationToken);
        }

        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "获取剧集列表失败" : message);
        }

        return data.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    public async Task<JsonElement> GetVideoDetailAsync(string videoId, string quality, CancellationToken cancellationToken)
    {
        JsonElement data;
        string message;

        if (_accessOptions.HasKey)
        {
            (data, message) = await RequestAsync(
                ApiUrl,
                new Dictionary<string, string>
                {
                    ["action"] = "get_detail",
                    ["key"] = _accessOptions.Key!,
                    ["machine_id"] = _accessOptions.MachineId,
                    ["video_id"] = videoId,
                    ["level"] = quality
                },
                cancellationToken);
        }
        else
        {
            (data, message) = await RequestAsync(
                PreviewUrl,
                new Dictionary<string, string>
                {
                    ["video_id"] = videoId,
                    ["level"] = ToPreviewLevel(quality)
                },
                cancellationToken);
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "获取播放链接失败" : message);
        }

        return data;
    }

    private async Task<(JsonElement Data, string Message)> RequestAsync(
        string url,
        IReadOnlyDictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(url, query);
        for (var attempt = 1; attempt <= MaxRequestAttempts; attempt++)
        {
            try
            {
                using var request = CreateRequest(uri);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (attempt < MaxRequestAttempts && IsTransientStatusCode(response.StatusCode))
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = document.RootElement;

                var code = root.TryGetProperty("code", out var codeElement) ? codeElement : default;
                var message = root.TryGetProperty("msg", out var msgElement) && msgElement.ValueKind == JsonValueKind.String
                    ? msgElement.GetString() ?? string.Empty
                    : string.Empty;
                var ok = IsSuccessCode(code);
                var data = root.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : default;

                if (!ok)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "红果接口请求失败" : message);
                }

                return (data, message);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt == MaxRequestAttempts)
            {
                throw new InvalidOperationException($"红果接口请求超时（已重试 {MaxRequestAttempts - 1} 次）: {uri}", ex);
            }
            catch (Exception ex) when (attempt < MaxRequestAttempts && IsTransientTransportFailure(ex, cancellationToken))
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(BuildRequestErrorMessage(uri, ex, attempt), ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(BuildRequestErrorMessage(uri, ex, attempt), ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(BuildRequestErrorMessage(uri, ex, attempt), ex);
            }
        }

        throw new InvalidOperationException($"红果接口请求失败（已尝试 {MaxRequestAttempts} 次）: {uri}");
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentString);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return request;
    }

    private static bool IsTransientTransportFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception switch
        {
            TaskCanceledException => !cancellationToken.IsCancellationRequested,
            HttpRequestException httpException =>
                httpException.StatusCode is null || IsTransientStatusCode(httpException.StatusCode.Value),
            IOException => true,
            JsonException => true,
            _ => false
        };
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
               (int)statusCode >= 500;
    }

    private static Task DelayBeforeRetryAsync(int failedAttempt, CancellationToken cancellationToken)
    {
        var delay = failedAttempt == 1 ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromMilliseconds(600);
        return Task.Delay(delay, cancellationToken);
    }

    private static string BuildRequestErrorMessage(Uri uri, Exception ex, int attempts)
    {
        var parts = new List<string>
        {
            attempts > 1
                ? $"红果接口请求失败（已尝试 {attempts} 次）: {uri}"
                : $"红果接口请求失败: {uri}"
        };

        if (ex is HttpRequestException { StatusCode: not null } httpException)
        {
            parts.Add($"HTTP {(int)httpException.StatusCode.Value}");
        }

        Exception? current = ex;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                parts.Add(current.Message.Trim());
            }

            current = current.InnerException;
        }

        return string.Join(" | ", parts.Distinct(StringComparer.Ordinal));
    }

    private static Uri BuildUri(string baseUrl, IReadOnlyDictionary<string, string> query)
    {
        var builder = new UriBuilder(baseUrl);
        var queryString = string.Join("&", query.Select(item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        builder.Query = queryString;
        return builder.Uri;
    }

    private static bool IsSuccessCode(JsonElement code)
    {
        return code.ValueKind switch
        {
            JsonValueKind.Number => code.TryGetInt32(out var numeric) && numeric == 200,
            JsonValueKind.String => string.Equals(code.GetString(), "200", StringComparison.Ordinal),
            JsonValueKind.True => true,
            _ => false
        };
    }

    private static string ToPreviewLevel(string quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "1080P+" or "1080P" => "3",
            "720P" => "2",
            _ => "1"
        };
    }
}

internal static class HongguoAccessOptionsResolver
{
    public static HongguoAccessOptions Resolve(string? anchorDirectory)
    {
        var configMap = FindConfig(anchorDirectory);
        var key = ReadFirstNonEmpty(
            Environment.GetEnvironmentVariable("HONGGUO_KEY"),
            TryGet(configMap, "HongguoKey"),
            TryGet(configMap, "HongguoApiKey"));

        var machineId = ReadFirstNonEmpty(
            Environment.GetEnvironmentVariable("HONGGUO_MACHINE_ID"),
            TryGet(configMap, "HongguoMachineId"),
            BuildMachineId());

        return new HongguoAccessOptions(key, machineId);
    }

    private static IReadOnlyDictionary<string, string>? FindConfig(string? anchorDirectory)
    {
        foreach (var root in EnumerateSearchRoots(anchorDirectory))
        {
            var path = Path.Combine(root, "config", "config.json");
            if (File.Exists(path))
            {
                return KeyValueConfigReader.Read(path);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchRoots(string? anchorDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { anchorDirectory, AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var current = Path.GetFullPath(start);
            while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
            {
                yield return current;
                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }
    }

    private static string BuildMachineId()
    {
        var raw = $"{Environment.MachineName}|{Environment.UserName}|{Environment.OSVersion.VersionString}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

    private static string? TryGet(IReadOnlyDictionary<string, string>? map, string key)
    {
        return map is not null && map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static string ReadFirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}

internal sealed record HongguoAccessOptions(string? Key, string MachineId)
{
    public bool HasKey => !string.IsNullOrWhiteSpace(Key);
}
