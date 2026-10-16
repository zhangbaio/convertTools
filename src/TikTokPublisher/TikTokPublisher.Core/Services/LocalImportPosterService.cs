using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 本地导入缺海报时的 URL 解析与下载：简介封面链接优先，否则走下载链路同款搜索匹配。
/// </summary>
public static class LocalImportPosterService
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 " +
        "TikTokPublisher/1.0";

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic", ".heif"];

    private static readonly Dictionary<string, string> ImageContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/bmp"] = ".bmp",
        ["image/heic"] = ".heic",
        ["image/heif"] = ".heif",
    };

    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttpClient);

    public static async Task<(string PosterUrl, double MatchRatio)> ResolvePosterUrlAsync(
        string projectDirectory,
        string title,
        JsonObject? metadata = null,
        ClientSettings? settings = null,
        Action<string>? log = null,
        Func<string, CancellationToken, Task<IReadOnlyList<DramaSearchItem>>>? searchAsync = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectDirectory);
        metadata ??= new JsonObject();
        settings ??= new ClientSettings();
        var external = ExternalDramaInfoReader.Read(root, metadata);

        var direct = FirstNonEmpty(
            ReadString(metadata, "posterUrl"),
            ReadString(metadata, "poster_url"),
            ReadString(metadata, "cover_url"),
            external.PosterUrl);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            var normalized = HongguoLocalMediaUrl.Normalize(direct, settings.HongguoLocalBaseUrl);
            return (string.IsNullOrWhiteSpace(normalized) ? direct : normalized, 1.0);
        }

        var searchTitle = FirstNonEmpty(title, external.Title, Path.GetFileName(root));
        if (string.IsNullOrWhiteSpace(searchTitle))
            return ("", 0);

        log?.Invoke($"按剧名搜索海报：{searchTitle}");
        IReadOnlyList<DramaSearchItem> results;
        try
        {
            var search = searchAsync ?? ((keyword, ct) => ShortDramaDramaServices.SearchAsync(keyword, 1, ct));
            ShortDramaDramaServices.RefreshSettings(settings);
            results = await search(searchTitle, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Invoke($"搜索海报失败：{ex.Message}");
            return ("", 0);
        }

        if (results.Count == 0)
        {
            log?.Invoke($"未搜到可匹配海报的短剧：{searchTitle}");
            return ("", 0);
        }

        var (match, ratio) = DramaSearchMatch.Pick(results, searchTitle);
        if (match is null)
        {
            var topTitle = (results[0].Title ?? "").Trim();
            log?.Invoke(
                $"搜索结果与剧名不符，跳过海报下载：{searchTitle}" +
                $"（最接近：{(string.IsNullOrWhiteSpace(topTitle) ? "-" : topTitle)} 相似度 {ratio:P0}）");
            return ("", ratio);
        }

        var posterUrl = (match.PosterUrl ?? "").Trim();
        var matchedTitle = FirstNonEmpty(match.Title, searchTitle);
        if (string.IsNullOrWhiteSpace(posterUrl))
        {
            log?.Invoke($"匹配到短剧但未返回海报地址：{matchedTitle}");
            return ("", ratio);
        }

        posterUrl = HongguoLocalMediaUrl.Normalize(posterUrl, settings.HongguoLocalBaseUrl);
        log?.Invoke($"已匹配海报：{matchedTitle}（相似度 {ratio:P0}）");
        return (posterUrl, ratio);
    }

    public static async Task<string?> EnsurePosterAsync(
        string projectDirectory,
        string displayName,
        string posterUrl,
        Action<string>? log = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectDirectory);
        var existing = FindExistingPoster(root, displayName);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            log?.Invoke($"海报已存在，跳过：{Path.GetFileName(existing)}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(posterUrl))
        {
            log?.Invoke("未找到海报 URL，跳过海报下载。");
            return null;
        }

        try
        {
            var http = httpClient ?? SharedHttp.Value;
            using var request = new HttpRequestMessage(HttpMethod.Get, posterUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var extension = ResolveImageExtension(posterUrl, response.Content.Headers.ContentType?.MediaType);
            var targetPath = Path.Combine(root, $"{SanitizeFileStem(displayName)}{extension}");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = File.Create(targetPath);
            await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);

            log?.Invoke($"海报下载完成：{Path.GetFileName(targetPath)}");
            return null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"海报下载失败：{ex.Message}");
            return $"海报下载失败: {ex.Message}";
        }
    }

    private static string? FindExistingPoster(string directory, string displayName)
    {
        var stem = SanitizeFileStem(displayName);
        foreach (var extension in ImageExtensions)
        {
            var candidate = Path.Combine(directory, $"{stem}{extension}");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string ResolveImageExtension(string url, string? mediaType)
    {
        var normalizedType = (mediaType ?? "").Split(';', 2)[0].Trim();
        if (ImageContentTypes.TryGetValue(normalizedType, out var extension))
            return extension;

        try
        {
            var fromUrl = Path.GetExtension(new Uri(url).AbsolutePath);
            if (ImageExtensions.Contains(fromUrl, StringComparer.OrdinalIgnoreCase))
                return string.Equals(fromUrl, ".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : fromUrl.ToLowerInvariant();
        }
        catch
        {
            // fall through
        }

        return ".jpg";
    }

    private static string SanitizeFileStem(string value)
    {
        var text = (value ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            text = text.Replace(c, '_');
        return string.IsNullOrWhiteSpace(text) ? "海报" : text;
    }

    private static string ReadString(JsonObject metadata, string key)
    {
        if (!metadata.TryGetPropertyValue(key, out var node) || node is null)
            return "";
        try { return node.GetValue<string>()?.Trim() ?? ""; }
        catch { return node.ToJsonString().Trim('"').Trim(); }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return "";
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
    }
}
