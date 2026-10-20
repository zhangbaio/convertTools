using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 海外短剧下载器 v2.4.0。会员登记与红果短剧下载器相同（start-prod-api、设备号、RSA、按日签名），
/// 会员类型是 drama。会员信息里的 streamApiKey 是各平台接口的访问码。
/// ReelShortV1 走官网页面；其余下拉源走下载器同一套平台接口。
/// </summary>
public static partial class ForeignPlayletClient
{
    public const string BookPrefix = "foreign:";
    public const string EpisodePrefix = "foreign_ep:";
    public const string DefaultServerUrl = "https://startvlog.cn/start-prod-api";
    public const string ClientVersion = "2.4.0";
    public const string MemberType = "drama";
    public const string DefaultSource = "reelshort";
    public const string DefaultLanguage = "en";
    public const string ReelShortOrigin = "https://www.reelshort.com";

    private const string MemberInfoPath = "/api/business/member/memberInfo";
    private const string PassId = "start-prod-api";
    private const string PassToken = "MkYQyRrrD2iG5WuDEV7DjYcq2jq7";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQC/EwSZCZTwnYhixLefB9Gvfa+X
        o4uMnG35UiNdPd20/CpgMjw0a9Zy79WjvMH4oCRCOL81HMy5/o6Iuks5Nj4t0reN
        KMHkDcrZdIgMW+DFaioJWEi4zfORC0amtHuDEMYaxfVQ1PxOfgnApbD+/3qzd4hr
        4AzoGhyxwpyUXtX6wQIDAQAB
        -----END PUBLIC KEY-----
        """;

    private static readonly Regex NextDataRegex = new(
        "<script id=\"__NEXT_DATA__\"[^>]*>(?<json>.*?)</script>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodeHrefRegex = new(
        "href=\"(?<path>/[^\"]*?/episodes/episode-(?<number>\\d+)-[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, string> MemberSessionDays = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> StreamApiKeys = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MemberSessionGates = new(StringComparer.Ordinal);

    public sealed record SourceOption(string Id, string Label);
    public sealed record LanguageOption(string Id, string Label);
    public sealed record EpisodeItem(int Number, string Title, string VideoId, string? DirectUrl = null);
    public sealed record VideoItem(string Url);
    public sealed record ConnectivityResult(bool MemberOk, bool SearchOk, bool VideoOk, string MemberMessage, string SearchMessage, string VideoMessage);

    public static IReadOnlyList<SourceOption> Sources { get; } =
    [
        new("reelshort", "ReelShortV1"),
        new("reelshortv2", "ReelShortV2"),
        new("dramabox", "DramaBox"),
        new("dramaboxv2", "DramaBoxV2"),
        new("dramawave", "DramaWave"),
        new("netshortv1", "NetShortV1"),
        new("netshort", "NetShortV2"),
        new("shortmax", "ShortMax"),
    ];

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("en", "英语"),
        new("ja", "日语"),
        new("zh", "中文"),
        new("ko", "韩语"),
        new("es", "西班牙语"),
        new("pt", "葡萄牙语"),
        new("th", "泰语"),
        new("id", "印尼语"),
        new("vi", "越南语"),
        new("fr", "法语"),
        new("de", "德语"),
        new("ar", "阿拉伯语"),
    ];

    public static string NormalizeSource(string? value)
    {
        var source = (value ?? string.Empty).Trim().ToLowerInvariant();
        return Sources.Any(item => string.Equals(item.Id, source, StringComparison.Ordinal))
            ? source
            : DefaultSource;
    }

    public static string SourceLabel(string? value)
    {
        var source = NormalizeSource(value);
        return Sources.First(item => string.Equals(item.Id, source, StringComparison.Ordinal)).Label;
    }

    public static string NormalizeLanguage(string? value)
    {
        var language = (value ?? string.Empty).Trim().ToLowerInvariant();
        return Languages.Any(item => string.Equals(item.Id, language, StringComparison.Ordinal))
            ? language
            : DefaultLanguage;
    }

    public static bool IsHlsUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.AbsolutePath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? deviceId,
        string? source,
        string? language,
        string keyword,
        int page,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeSource(source);
        var normalizedLanguage = NormalizeLanguage(language);
        await EnsureMemberSessionAsync(httpClient, serverUrl, deviceId, cancellationToken);
        if (!string.Equals(normalizedSource, "reelshort", StringComparison.Ordinal))
            return await SearchPlatformAsync(httpClient, serverUrl, deviceId, normalizedSource, normalizedLanguage, keyword, page, cancellationToken);
        var html = await GetTextAsync(
            httpClient,
            BuildReelShortSearchUrl(normalizedLanguage, keyword, page),
            normalizedLanguage,
            cancellationToken);
        return ReadSearchItems(html, normalizedSource, normalizedLanguage);
    }

    public static async Task<IReadOnlyList<EpisodeItem>> GetEpisodesAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? deviceId,
        string bookReference,
        CancellationToken cancellationToken)
    {
        var book = ParseBookReference(bookReference);
        await EnsureMemberSessionAsync(httpClient, serverUrl, deviceId, cancellationToken);
        if (!string.Equals(book.Source, "reelshort", StringComparison.Ordinal))
            return await GetPlatformEpisodesAsync(httpClient, serverUrl, deviceId, book, null, cancellationToken);
        using var response = await SendPageAsync(
            httpClient,
            $"{ReelShortOrigin}{LanguagePrefix(book.Language)}/movie/{Uri.EscapeDataString(book.BookId)}",
            book.Language,
            cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var moviePath = response.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        return ReadEpisodes(html, moviePath, book.Source, book.Language);
    }

    public static async Task<VideoItem> GetVideoAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? deviceId,
        string episodeReference,
        string? quality,
        CancellationToken cancellationToken)
    {
        var episode = ParseEpisodeReference(episodeReference);
        await EnsureMemberSessionAsync(httpClient, serverUrl, deviceId, cancellationToken);
        if (episode.EpisodeNumber is int episodeNumber)
        {
            var episodes = await GetPlatformEpisodesAsync(
                httpClient,
                serverUrl,
                deviceId,
                new BookReference(episode.Source, episode.Language, episode.BookId),
                quality,
                cancellationToken);
            var match = episodes.FirstOrDefault(item => item.Number == episodeNumber)
                        ?? throw new InvalidOperationException("海外短剧分集列表里没有这一集。");
            if (string.IsNullOrWhiteSpace(match.DirectUrl))
                throw new InvalidOperationException("海外短剧下载器没有返回这一集的播放地址。");
            return new VideoItem(match.DirectUrl);
        }

        var html = await GetTextAsync(httpClient, ReelShortOrigin + episode.Path, episode.Language, cancellationToken);
        var url = ReadVideoUrl(html);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("海外短剧下载器没有返回这一集的播放地址。");
        return new VideoItem(await PreferQualityAsync(httpClient, url, quality, cancellationToken));
    }

    public static async Task<ConnectivityResult> TestConnectivityAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? deviceId,
        string? source,
        string? language,
        CancellationToken cancellationToken)
    {
        string memberMessage;
        var memberOk = false;
        try
        {
            await EnsureMemberSessionAsync(httpClient, serverUrl, deviceId, cancellationToken, force: true);
            memberOk = true;
            memberMessage = "设备登记成功";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            memberMessage = $"设备登记失败：{ex.Message}";
        }

        string searchMessage;
        var searchOk = false;
        IReadOnlyList<DramaSearchItem> items = [];
        try
        {
            items = await SearchAsync(httpClient, serverUrl, deviceId, source, language, "ceo", 1, cancellationToken);
            searchOk = items.Count > 0;
            searchMessage = searchOk
                ? $"{SourceLabel(source)} 搜索到 {items.Count} 部，例如 {items[0].Title}"
                : $"{SourceLabel(source)} 搜索没有返回结果";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            searchMessage = $"搜索失败：{ex.Message}";
        }

        string videoMessage;
        var videoOk = false;
        try
        {
            if (items.Count == 0)
            {
                videoMessage = "未测试播放地址";
            }
            else
            {
                var episodes = await GetEpisodesAsync(httpClient, serverUrl, deviceId, items[0].BookId, cancellationToken);
                if (episodes.Count == 0)
                {
                    videoMessage = "剧集目录为空";
                }
                else
                {
                    var video = await GetVideoAsync(
                        httpClient,
                        serverUrl,
                        deviceId,
                        episodes[0].VideoId,
                        "720P",
                        cancellationToken);
                    videoOk = IsHlsUrl(video.Url) || !string.IsNullOrWhiteSpace(video.Url);
                    videoMessage = videoOk
                        ? $"剧集 {episodes.Count} 集，已拿到播放地址"
                        : "没有拿到播放地址";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            videoMessage = $"播放地址失败：{ex.Message}";
        }

        return new ConnectivityResult(memberOk, searchOk, videoOk, memberMessage, searchMessage, videoMessage);
    }

    internal static void ResetMemberSessionCache()
    {
        MemberSessionDays.Clear();
        StreamApiKeys.Clear();
    }

    internal static IReadOnlyList<DramaSearchItem> ReadSearchItems(string html, string source, string language)
    {
        var page = ReadNextData(html);
        if (!page.TryGetProperty("props", out var props) ||
            !props.TryGetProperty("pageProps", out var pageProps) ||
            !pageProps.TryGetProperty("books", out var books) ||
            books.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<DramaSearchItem>();
        foreach (var book in books.EnumerateArray())
        {
            var bookId = ReadString(book, "book_id");
            var title = ReadString(book, "book_title");
            if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(title))
                continue;
            items.Add(new DramaSearchItem(
                BookId: FormatBookId(source, language, bookId),
                Title: title,
                Category: ReadTags(book),
                EpisodeTotal: ReadInt(book, "chapter_count"),
                Intro: ReadString(book, "special_desc"),
                PosterUrl: ReadString(book, "book_pic"),
                PlayCount: ReadInt(book, "read_count")));
        }

        return items;
    }

    internal static IReadOnlyList<EpisodeItem> ReadEpisodes(string html, string moviePath, string source, string language)
    {
        var hrefs = new Dictionary<int, string>();
        foreach (Match match in EpisodeHrefRegex.Matches(html))
        {
            if (!int.TryParse(match.Groups["number"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                continue;
            hrefs.TryAdd(number, WebUtility.HtmlDecode(match.Groups["path"].Value));
        }

        var page = ReadNextData(html);
        JsonElement chapters = default;
        var hasChapters = page.TryGetProperty("props", out var props) &&
                          props.TryGetProperty("pageProps", out var pageProps) &&
                          pageProps.TryGetProperty("data", out var data) &&
                          data.TryGetProperty("online_base", out chapters) &&
                          chapters.ValueKind == JsonValueKind.Array;
        if (!hasChapters && hrefs.Count == 0)
            return [];

        var episodes = new List<EpisodeItem>();
        if (hasChapters)
        {
            foreach (var chapter in chapters.EnumerateArray())
            {
                var number = ReadInt(chapter, "serial_number");
                var chapterId = ReadString(chapter, "chapter_id");
                if (number <= 0 || string.IsNullOrWhiteSpace(chapterId))
                    continue;
                if (!hrefs.TryGetValue(number, out var path))
                    path = BuildEpisodePath(moviePath, number, chapterId);
                episodes.Add(new EpisodeItem(number, $"第{number}集", FormatEpisodeId(source, language, path)));
            }
        }
        else
        {
            foreach (var pair in hrefs.OrderBy(item => item.Key))
                episodes.Add(new EpisodeItem(pair.Key, $"第{pair.Key}集", FormatEpisodeId(source, language, pair.Value)));
        }

        return episodes
            .GroupBy(item => item.Number)
            .Select(group => group.First())
            .OrderBy(item => item.Number)
            .ToArray();
    }

    internal static string BuildEpisodePath(string moviePath, int serial, string chapterId)
    {
        const string marker = "/movie/";
        var index = moviePath.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0 || serial <= 0 || string.IsNullOrWhiteSpace(chapterId))
            throw new InvalidOperationException("海外短剧下载器剧集页地址无效。");
        var slug = moviePath[(index + marker.Length)..].Trim('/');
        return $"{moviePath[..index]}/episodes/episode-{serial}-{slug}-{chapterId.Trim()}";
    }

    internal static string? ReadVideoUrl(string html)
    {
        var page = ReadNextData(html);
        if (!page.TryGetProperty("props", out var props) ||
            !props.TryGetProperty("pageProps", out var pageProps) ||
            !pageProps.TryGetProperty("data", out var data))
        {
            return null;
        }

        return ReadString(data, "video_url");
    }

    internal static string FormatBookId(string source, string language, string bookId) =>
        $"{BookPrefix}{NormalizeSource(source)}|{NormalizeLanguage(language)}|{bookId.Trim()}";

    internal static string FormatEpisodeId(string source, string language, string path)
    {
        var normalized = path.StartsWith('/') ? path : "/" + path;
        return $"{EpisodePrefix}{NormalizeSource(source)}|{NormalizeLanguage(language)}|{normalized}";
    }

    private static async Task<string> PreferQualityAsync(
        HttpClient httpClient,
        string url,
        string? quality,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in BuildQualityCandidates(url, quality).Skip(1))
        {
            if (await PlaylistExistsAsync(httpClient, candidate, cancellationToken))
                return candidate;
        }

        return url;
    }

    internal static IReadOnlyList<string> BuildQualityCandidates(string url, string? quality)
    {
        if (!url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            return [url];
        var token = QualityToken(quality);
        if (token is null)
            return [url];
        var replaced = ReplaceQualityToken(url, token);
        return string.Equals(replaced, url, StringComparison.Ordinal)
            ? [url]
            : [url, replaced];
    }

    private static string? QualityToken(string? quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "1080p" or "1080" or "1" or "0" or "高清+" => "hd",
            "720p" or "720" or "2" or "高清" or "hd" => "sd",
            "540p" or "540" or "480p" or "480" or "3" or "4" or "标清" => "ld",
            _ => null
        };
    }

    private static string ReplaceQualityToken(string url, string token)
    {
        var match = Regex.Match(url, @"-(sd|hd|ld|fhd)(?=\.m3u8)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return url;
        return string.Concat(url.AsSpan(0, match.Index + 1), token, url.AsSpan(match.Index + match.Length));
    }

    private static async Task<bool> PlaylistExistsAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", ReelShortOrigin + "/");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return text.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BuildReelShortSearchUrl(string language, string keyword, int page)
    {
        var prefix = LanguagePrefix(language);
        var query = $"keywords={Uri.EscapeDataString(keyword.Trim())}&page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}";
        return $"{ReelShortOrigin}{prefix}/search?{query}";
    }

    private static string LanguagePrefix(string language) =>
        string.Equals(language, "en", StringComparison.Ordinal) ? "" : "/" + language;

    private static async Task EnsureMemberSessionAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? deviceId,
        CancellationToken cancellationToken,
        bool force = false)
    {
        var normalizedDeviceId = (deviceId ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedDeviceId))
            throw new InvalidOperationException("未配置海外短剧下载器设备号。请在下载器「软件设置」中复制设备信息后保存。");

        var server = NormalizeServerUrl(serverUrl);
        var day = ChinaToday().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cacheKey = server + "\n" + normalizedDeviceId;
        if (!force && MemberSessionDays.TryGetValue(cacheKey, out var cachedDay) && cachedDay == day)
            return;

        var gate = MemberSessionGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!force && MemberSessionDays.TryGetValue(cacheKey, out cachedDay) && cachedDay == day)
                return;
            StreamApiKeys[cacheKey] = await PostMemberSessionAsync(httpClient, server, normalizedDeviceId, cancellationToken);
            MemberSessionDays[cacheKey] = day;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string> GetStreamApiKeyAsync(
        HttpClient httpClient,
        string? serverUrl,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        await EnsureMemberSessionAsync(httpClient, serverUrl, deviceId, cancellationToken);
        var cacheKey = NormalizeServerUrl(serverUrl) + "\n" + (deviceId ?? string.Empty).Trim().ToUpperInvariant();
        if (!StreamApiKeys.TryGetValue(cacheKey, out var streamKey) || string.IsNullOrWhiteSpace(streamKey))
            throw new InvalidOperationException("海外短剧下载器没有返回平台访问码。");
        return streamKey;
    }

    private static async Task<string> PostMemberSessionAsync(
        HttpClient httpClient,
        string serverUrl,
        string deviceId,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["version"] = Encrypt(ClientVersion),
            ["deviceId"] = Encrypt(deviceId),
            ["memberType"] = Encrypt(MemberType)
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}{MemberInfoPath}")
        {
            Content = content
        };
        ApplyGatewayHeaders(request, deviceId);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"会员接口 HTTP {(int)response.StatusCode}");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("会员接口没有返回内容");
        using var document = JsonDocument.Parse(text);
        var code = ReadCode(document.RootElement);
        if (!string.Equals(code, "200", StringComparison.Ordinal))
            throw new InvalidOperationException(ReadMessage(document.RootElement) ?? $"会员接口 code={code}");
        var data = ReadString(document.RootElement, "data");
        if (string.IsNullOrWhiteSpace(data))
            throw new InvalidOperationException("会员接口没有返回会员数据。");
        using var member = JsonDocument.Parse(DecryptMemberData(data));
        var streamKey = ReadString(member.RootElement, "streamApiKey");
        if (string.IsNullOrWhiteSpace(streamKey))
            throw new InvalidOperationException("会员接口没有返回平台访问码。");
        return streamKey;
    }

    private static string DecryptMemberData(string data)
    {
        var blob = Convert.FromBase64String(data);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(MemberPrivateKey);
        var blockSize = rsa.KeySize / 8;
        if (blob.Length == 0 || blob.Length % blockSize != 0)
            throw new InvalidOperationException("会员数据无法解密。");
        using var output = new MemoryStream();
        for (var offset = 0; offset < blob.Length; offset += blockSize)
        {
            var chunk = rsa.Decrypt(blob.AsSpan(offset, blockSize), RSAEncryptionPadding.Pkcs1);
            output.Write(chunk);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static void ApplyGatewayHeaders(HttpRequestMessage request, string deviceId)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{today}_{PassId}_{PassToken}")));
        request.Headers.TryAddWithoutValidation("auth-pass-id", PassId);
        request.Headers.TryAddWithoutValidation("auth-signature", signature);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("memberType", MemberType);
        request.Headers.TryAddWithoutValidation("version", ClientVersion);
        request.Headers.TryAddWithoutValidation("deviceId", deviceId);
    }

    private static async Task<string> GetTextAsync(
        HttpClient httpClient,
        string url,
        string language,
        CancellationToken cancellationToken)
    {
        using var response = await SendPageAsync(httpClient, url, language, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendPageAsync(
        HttpClient httpClient,
        string url,
        string language,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", NormalizeLanguage(language));
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new InvalidOperationException($"海外短剧页面请求失败: HTTP {(int)response.StatusCode}");
        }

        return response;
    }

    private static JsonElement ReadNextData(string html)
    {
        var match = NextDataRegex.Match(html ?? string.Empty);
        if (!match.Success)
            throw new InvalidOperationException("海外短剧页面没有返回节目数据。");
        using var document = JsonDocument.Parse(match.Groups["json"].Value);
        return document.RootElement.Clone();
    }

    private static BookReference ParseBookReference(string value)
    {
        var text = StripPrefix(value, BookPrefix);
        var parts = text.Split('|', 3);
        if (parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2]))
            return new BookReference(NormalizeSource(parts[0]), NormalizeLanguage(parts[1]), parts[2].Trim());
        if (!string.IsNullOrWhiteSpace(text) && !text.Contains('|', StringComparison.Ordinal))
            return new BookReference(DefaultSource, DefaultLanguage, text.Trim());
        throw new InvalidOperationException("海外短剧编号无效。");
    }

    private static EpisodeReference ParseEpisodeReference(string value)
    {
        var text = StripPrefix(value, EpisodePrefix);
        var parts = text.Split('|', 4);
        if (parts.Length == 4 &&
            int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
            number > 0)
        {
            return new EpisodeReference(NormalizeSource(parts[0]), NormalizeLanguage(parts[1]), "", parts[2], number);
        }

        if (parts.Length >= 3 && parts[2].StartsWith('/'))
            return new EpisodeReference(NormalizeSource(parts[0]), NormalizeLanguage(parts[1]), parts[2], "", null);
        throw new InvalidOperationException("海外短剧分集号无效。");
    }

    private static string StripPrefix(string? value, string prefix)
    {
        var text = (value ?? string.Empty).Trim();
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? text[prefix.Length..] : text;
    }

    private static string NormalizeServerUrl(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? DefaultServerUrl : normalized;
    }

    private static string Encrypt(string plaintext)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKey);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(plaintext), RSAEncryptionPadding.Pkcs1));
    }

    private static DateOnly ChinaToday()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
    }

    private static string ReadTags(JsonElement book)
    {
        if (!book.TryGetProperty("tag", out var tag))
            return "";
        if (tag.ValueKind == JsonValueKind.String)
            return tag.GetString()?.Trim() ?? "";
        if (tag.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join(" / ", tag.EnumerateArray().Select(item => item.GetString()?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)));
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return "";
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? "",
            JsonValueKind.Number => property.GetRawText(),
            _ => ""
        };
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private static string? ReadCode(JsonElement element)
    {
        if (!element.TryGetProperty("code", out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static string? ReadMessage(JsonElement element)
    {
        foreach (var name in new[] { "msg", "message", "error" })
        {
            var value = ReadString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private sealed record BookReference(string Source, string Language, string BookId);
    private sealed record EpisodeReference(string Source, string Language, string Path, string BookId, int? EpisodeNumber);
}
