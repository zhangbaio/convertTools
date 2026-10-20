using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public sealed class DramaBoxApiService(HttpClient httpClient)
{
    public const string BookPrefix = "dramabox:";
    public const string EpisodePrefix = "dramabox_ep:";
    public const string DefaultDeviceId = "d001277c5c31829ceba9d49c";
    public const string DefaultBaseUrl = "http://127.0.0.1:17865";

    private const string GatewayOrigin = "https://api.iusc.cc/api/dr";
    private const string AppId = "dr_downloader_cpp";
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static string? _token;
    private static DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private static string _tokenDeviceId = "";

    public sealed record ConnectivityResult(
        bool LicenseOk,
        bool SearchOk,
        bool DownloadOk,
        string LicenseMessage,
        string SearchMessage,
        string DownloadMessage);

    public async Task<ConnectivityResult> TestConnectivityAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var settings = new DramaSourceSettings { DramaBoxDeviceId = deviceId ?? "" };
        await GetAccessTokenAsync(NormalizeDeviceId(settings.DramaBoxDeviceId), cancellationToken);
        var search = await SearchAsync(settings, "love", 1, cancellationToken);
        if (search.Count == 0)
        {
            return new ConnectivityResult(true, false, false, "设备已授权", "搜索没有返回结果", "未测试下载");
        }

        using var document = await PostSignedAsync(
            settings,
            "/drama-box/chapterv2/batch/load",
            new { bookId = search[0].BookId[BookPrefix.Length..], boundaryIndex = 0, loadDirection = 0, preLoad = false },
            cancellationToken);
        var chapter = ReadChapters(DataObject(document.RootElement)).FirstOrDefault();
        var url = chapter is null ? null : PickUrl(chapter.Urls, "1080P");
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ConnectivityResult(
                true,
                true,
                false,
                "设备已授权",
                $"搜索到 {search.Count} 部，例如 {search[0].Title}",
                "没有拿到分集播放地址");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 15);
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.10.0");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var downloadOk = response.IsSuccessStatusCode;
        return new ConnectivityResult(
            true,
            true,
            downloadOk,
            "设备已授权",
            $"搜索到 {search.Count} 部，例如 {search[0].Title}",
            downloadOk
                ? $"第 {chapter!.EpisodeNumber} 集 1080P 可以下载"
                : $"播放地址 HTTP {(int)response.StatusCode}");
    }

    public async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        DramaSourceSettings settings, string keyword, int page, CancellationToken cancellationToken)
    {
        using var document = await PostSignedAsync(
            settings,
            "/drama-box/search/search",
            new
            {
                keyword = keyword.Trim(),
                pageNo = Math.Max(1, page),
                pageSize = 20,
                from = "search_home",
                searchSource = "his",
            },
            cancellationToken);
        var books = ReadSearchList(document.RootElement);
        return books.Select(ToSearchItem).ToArray();
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetNewChannelAsync(CancellationToken cancellationToken)
        => await GetNewChannelAsync(new DramaSourceSettings { DramaBoxDeviceId = DefaultDeviceId }, cancellationToken);

    public async Task<IReadOnlyList<DramaSearchItem>> GetNewChannelAsync(
        DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var books = new List<DramaBook>();
        for (var page = 1; page <= 8; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = await PostSignedAsync(
                settings,
                "/drama-box/he001/theater",
                new
                {
                    newChannelStyle = 1,
                    isNeedRank = 1,
                    pageNo = page,
                    index = 1,
                    channelId = 41,
                },
                cancellationToken);
            var before = books.Count;
            books.AddRange(ReadNewTheater(document.RootElement));
            books = books
                .GroupBy(item => item.BookId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (books.Count == before)
                break;
        }

        return books.Select(ToSearchItem).ToArray();
    }

    public async Task<IReadOnlyList<GatewayEpisode>> GetEpisodesAsync(
        DramaSourceSettings settings, string bookReference, CancellationToken cancellationToken)
    {
        var bookId = StripPrefix(bookReference, BookPrefix);
        var chapters = await LoadChaptersAsync(settings, bookId, cancellationToken);
        return chapters
            .Select(chapter => new GatewayEpisode(
                chapter.EpisodeNumber,
                string.IsNullOrWhiteSpace(chapter.Title) ? $"第{chapter.EpisodeNumber}集" : chapter.Title,
                $"{EpisodePrefix}{bookId}|{chapter.ChapterId}"))
            .ToArray();
    }

    public async Task<GatewayPlayback> GetPlaybackAsync(
        DramaSourceSettings settings, string episodeReference, string quality, CancellationToken cancellationToken)
    {
        var reference = StripPrefix(episodeReference, EpisodePrefix);
        var split = reference.Split('|', 2);
        if (split.Length != 2 || string.IsNullOrWhiteSpace(split[0]) || string.IsNullOrWhiteSpace(split[1]))
            throw new InvalidOperationException("DramaBox 分集号无效。");
        var chapters = await LoadChaptersAsync(settings, split[0], cancellationToken);
        var chapter = chapters.FirstOrDefault(item => string.Equals(item.ChapterId, split[1], StringComparison.Ordinal))
                      ?? throw new InvalidOperationException("DramaBox 分集列表里没有这一集。");
        var url = PickUrl(chapter.Urls, quality)
                  ?? throw new InvalidOperationException("DramaBox 没有返回该集的播放地址。");
        return new GatewayPlayback(url, string.Empty, false);
    }

    public async Task DecryptPrivateMp4Async(
        string path, DramaSourceSettings settings, int episodeNumber, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var privateKeyUri = DramaBoxPrivateMp4Decryptor.ReadPrivateKeyUri(bytes);
        if (privateKeyUri is null)
            return;
        var deviceId = NormalizeDeviceId(settings.DramaBoxDeviceId);
        var token = await GetAccessTokenAsync(deviceId, cancellationToken);
        var package = await RequestPrivateKeyAsync(deviceId, token, privateKeyUri, Math.Max(1, episodeNumber), cancellationToken);
        if (!DramaBoxPrivateMp4Decryptor.TryDecrypt(bytes, package, AppId, deviceId))
            throw new InvalidDataException("DramaBox 私有视频解密失败。");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    private async Task<DramaBoxPrivateMp4Decryptor.KeyPackage> RequestPrivateKeyAsync(
        string deviceId, string token, string privateKeyUri, int episodeNumber, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GatewayOrigin}/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        var episode = Math.Max(1, episodeNumber);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            app_id = AppId,
            device = deviceId,
            private_key_uri = privateKeyUri,
            episode_number = episode,
            video_id = "",
        }), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (!response.IsSuccessStatusCode || !IsSuccess(document.RootElement))
            throw new InvalidOperationException("DramaBox 密钥请求失败：" + ReadMessage(document.RootElement));
        var data = DataObject(document.RootElement);
        var nonceText = ReadString(data, "nonce") ?? "";
        var cipherText = ReadString(data, "ciphertext") ?? "";
        var tagText = ReadString(data, "tag") ?? "";
        var hash = ReadString(data, "private_key_uri_hash") ?? "";
        var entitlement = ReadString(data, "entitlement") ?? "";
        if (nonceText.Length == 0 || cipherText.Length == 0 || tagText.Length == 0 || hash.Length == 0)
            throw new InvalidOperationException("DramaBox 密钥响应缺少解密材料。");
        if (!data.TryGetProperty("expires_at", out var expiresValue) || !TryReadInt64(expiresValue, out var expiresAt))
            throw new InvalidOperationException("DramaBox 密钥没有返回过期时间。");
        return new DramaBoxPrivateMp4Decryptor.KeyPackage(
            Convert.FromBase64String(nonceText),
            Convert.FromBase64String(cipherText),
            Convert.FromBase64String(tagText),
            nonceText,
            privateKeyUri,
            hash,
            entitlement,
            ReadInt(data, "version") ?? 2,
            episode,
            expiresAt,
            "");
    }

    public sealed record GatewayEpisode(int EpisodeNumber, string Title, string VideoId);
    public sealed record GatewayPlayback(string Url, string SpadeA, bool Encrypted);

    private async Task<IReadOnlyList<DramaChapter>> LoadChaptersAsync(
        DramaSourceSettings settings, string bookId, CancellationToken cancellationToken)
    {
        var chapters = new List<DramaChapter>();
        string? pullCid = null;
        var expected = 0;
        for (var page = 0; page < 30; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object payload = pullCid is null
                ? new { bookId, boundaryIndex = 0, loadDirection = 0, preLoad = false }
                : new { bookId, pullCid, loadDirection = 1 };
            using var document = await PostSignedAsync(
                settings, "/drama-box/chapterv2/batch/load", payload, cancellationToken);
            var data = DataObject(document.RootElement);
            if (expected <= 0)
                expected = ReadInt(data, "chapterCount") ?? 0;
            var batch = ReadChapters(data);
            var before = chapters.Count;
            foreach (var chapter in batch)
            {
                if (chapters.All(item => item.ChapterId != chapter.ChapterId))
                    chapters.Add(chapter);
            }

            if (batch.Count == 0 || chapters.Count == before)
                break;
            if (expected > 0 && chapters.Count >= expected)
                break;
            pullCid = batch[^1].ChapterId;
        }

        return chapters
            .OrderBy(item => item.EpisodeNumber)
            .ToArray();
    }

    private async Task<JsonDocument> PostSignedAsync(
        DramaSourceSettings settings, string path, object payload, CancellationToken cancellationToken)
    {
        var deviceId = NormalizeDeviceId(settings.DramaBoxDeviceId);
        var token = await GetAccessTokenAsync(deviceId, cancellationToken);
        var signUrl = $"{GatewayOrigin}/sign?app_id={AppId}&device={Uri.EscapeDataString(deviceId)}";
        var signPayload = JsonSerializer.Serialize(new
        {
            method = "POST",
            path,
            body = payload,
            language = "en",
            currentLanguage = "en",
            content_language = "en",
        });
        using var signRequest = new HttpRequestMessage(HttpMethod.Post, signUrl);
        signRequest.Content = new StringContent(signPayload, Encoding.UTF8, "application/json");
        signRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        signRequest.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        using var signResponse = await httpClient.SendAsync(signRequest, cancellationToken);
        var signText = await signResponse.Content.ReadAsStringAsync(cancellationToken);
        using var signed = JsonDocument.Parse(string.IsNullOrWhiteSpace(signText) ? "{}" : signText);
        if (!signResponse.IsSuccessStatusCode || !IsSuccess(signed.RootElement))
            throw new InvalidOperationException("DramaBox 签名失败：" + ReadMessage(signed.RootElement));
        if (!signed.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("DramaBox 签名没有返回请求。");
        var url = ReadString(data, "url") ?? throw new InvalidOperationException("DramaBox 签名没有返回地址。");
        var bodyBytes = Convert.FromBase64String(ReadString(data, "body_base64") ?? "");
        var upstreamHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
            {
                var value = header.Value.ValueKind == JsonValueKind.String
                    ? header.Value.GetString() ?? ""
                    : header.Value.ToString();
                upstreamHeaders[header.Name] = value;
            }
        }

        var text = await PostDramaBoxAsync(url, bodyBytes, upstreamHeaders, cancellationToken);
        var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (document.RootElement.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.False)
        {
            var message = ReadString(document.RootElement, "message") ?? "DramaBox 请求失败";
            document.Dispose();
            throw new InvalidOperationException(message);
        }

        return document;
    }

    private async Task<string> PostDramaBoxAsync(
        string url,
        byte[] body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var httpText = await PostDramaBoxWithHttpClientAsync(url, body, headers, cancellationToken);
        if (!IsCdnDenied(httpText))
            return httpText;

        // sapi.dramaboxdb.com 拒绝 .NET 的 TLS 指纹，返回 HTML Access Denied。
        // 同一份签名请求用 Python 的 TLS 栈可以拿到 JSON。
        return await PostDramaBoxWithPythonAsync(url, body, headers, cancellationToken);
    }

    private async Task<string> PostDramaBoxWithHttpClientAsync(
        string url,
        byte[] body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        using var upstream = new HttpRequestMessage(HttpMethod.Post, url);
        upstream.Version = HttpVersion.Version11;
        upstream.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        upstream.Content = new ByteArrayContent(body);
        foreach (var header in headers)
        {
            if (!upstream.Headers.TryAddWithoutValidation(header.Key, header.Value))
                upstream.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await httpClient.SendAsync(upstream, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode && !IsCdnDenied(text))
            throw new InvalidOperationException($"DramaBox 接口 HTTP {(int)response.StatusCode}：{Trim(text)}");
        return text;
    }

    private static async Task<string> PostDramaBoxWithPythonAsync(
        string url,
        byte[] body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "dramabox-upstream.py");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            import json, sys, base64, urllib.request
            req = json.load(sys.stdin)
            request = urllib.request.Request(
                req["url"],
                data=base64.b64decode(req["body"]),
                headers={key: str(value) for key, value in req["headers"].items()},
                method="POST")
            with urllib.request.urlopen(request, timeout=40) as response:
                sys.stdout.buffer.write(response.read())
            """,
            cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            url,
            body = Convert.ToBase64String(body),
            headers,
        });
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{scriptPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = global::System.Diagnostics.Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法启动 Python，DramaBox 接口被 CDN 拒绝。");
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("DramaBox 接口被 CDN 拒绝：" + Trim(error));
        return output;
    }

    private static bool IsCdnDenied(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) &&
        text.Contains("<HTML", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string? text)
    {
        var value = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 180 ? value : value[..180];
    }

    private async Task<string> GetAccessTokenAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token) &&
            string.Equals(_tokenDeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow < _tokenExpiresAt.AddMinutes(-1))
        {
            return _token;
        }

        await TokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token) &&
                string.Equals(_tokenDeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow < _tokenExpiresAt.AddMinutes(-1))
            {
                return _token;
            }

            var url = $"{GatewayOrigin}/license_verify.php?action=device_status&app_id={AppId}&device={Uri.EscapeDataString(deviceId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            if (!response.IsSuccessStatusCode || !IsSuccess(document.RootElement))
                throw new InvalidOperationException("DramaBox 设备授权失败：" + ReadMessage(document.RootElement));
            var data = DataObject(document.RootElement);
            var token = ReadString(data, "access_token")
                        ?? throw new InvalidOperationException("DramaBox 设备授权没有返回访问令牌。");
            if (ReadBool(data, "licensed") == false)
                throw new InvalidOperationException("DramaBox 设备未授权。");
            _token = token;
            _tokenDeviceId = deviceId;
            _tokenExpiresAt = ReadDate(data, "token_expires_at") ?? DateTimeOffset.UtcNow.AddMinutes(20);
            return token;
        }
        finally
        {
            TokenGate.Release();
        }
    }

    private static string NormalizeDeviceId(string? deviceId)
    {
        var value = (deviceId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            value = DefaultDeviceId;
        return value;
    }

    private static DramaSearchItem ToSearchItem(DramaBook item) => new(
        BookPrefix + item.BookId,
        item.Title,
        item.Category,
        item.EpisodeCount,
        item.Intro,
        item.Cover,
        "",
        item.PublishTime,
        PlayCount: item.PlayCount);

    private static IEnumerable<DramaBook> ReadSearchList(JsonElement root)
    {
        var data = DataObject(root);
        if (!data.TryGetProperty("searchList", out var list) || list.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in list.EnumerateArray())
        {
            var book = ReadBook(item);
            if (book is not null)
                yield return book;
        }
    }

    private static IEnumerable<DramaBook> ReadNewTheater(JsonElement root)
    {
        var data = DataObject(root);
        if (!data.TryGetProperty("newTheaterList", out var theater) || theater.ValueKind != JsonValueKind.Object)
            yield break;
        if (!theater.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in records.EnumerateArray())
        {
            var book = ReadBook(item);
            if (book is not null)
                yield return book;
        }
    }

    private static DramaBook? ReadBook(JsonElement item)
    {
        var bookId = ReadString(item, "bookId");
        var title = ReadString(item, "bookName");
        if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(title))
            return null;
        return new DramaBook(
            bookId,
            title,
            ReadString(item, "introduction") ?? "",
            ReadString(item, "coverWap") ?? ReadString(item, "bookCover") ?? "",
            ReadInt(item, "chapterCount") ?? 0,
            JoinTags(item),
            ReadString(item, "bookShelfTime") ?? ReadString(item, "shelfTime") ?? "",
            ReadPlayCount(item));
    }

    private static IReadOnlyList<DramaChapter> ReadChapters(JsonElement data)
    {
        if (!data.TryGetProperty("chapterList", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var chapters = new List<DramaChapter>();
        foreach (var item in list.EnumerateArray())
        {
            var chapterId = ReadString(item, "chapterId");
            var index = ReadInt(item, "chapterIndex");
            if (string.IsNullOrWhiteSpace(chapterId) || index is null)
                continue;
            chapters.Add(new DramaChapter(
                chapterId,
                index.Value,
                ReadString(item, "chapterName") ?? "",
                ReadUrls(item)));
        }

        if (chapters.Count > 0 && chapters.Min(item => item.EpisodeNumber) == 0)
        {
            chapters = chapters
                .Select(item => item with { EpisodeNumber = item.EpisodeNumber + 1 })
                .ToList();
        }

        return chapters;
    }

    private static IReadOnlyList<(int Quality, string Url)> ReadUrls(JsonElement chapter)
    {
        var urls = new List<(int Quality, string Url)>();
        if (!chapter.TryGetProperty("cdnList", out var cdnList) || cdnList.ValueKind != JsonValueKind.Array)
            return urls;
        foreach (var cdn in cdnList.EnumerateArray())
        {
            if (!cdn.TryGetProperty("videoPathList", out var paths) || paths.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var path in paths.EnumerateArray())
            {
                var url = ReadString(path, "videoPath");
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;
                urls.Add((ReadInt(path, "quality") ?? 0, url));
            }
        }

        return urls;
    }

    private static string? PickUrl(IReadOnlyList<(int Quality, string Url)> urls, string quality)
    {
        if (urls.Count == 0)
            return null;
        var preferred = quality.Contains("720", StringComparison.OrdinalIgnoreCase) ? 720
            : quality.Contains("540", StringComparison.OrdinalIgnoreCase) ? 540
            : 1080;
        return urls.OrderBy(item => Math.Abs(item.Quality - preferred)).Select(item => item.Url).FirstOrDefault();
    }

    private static int ReadPlayCount(JsonElement item)
    {
        foreach (var name in new[] { "playCount", "play_count", "viewCount", "view_count" })
        {
            if (!item.TryGetProperty(name, out var value))
                continue;
            var count = ParsePlayCount(value);
            if (count > 0)
                return count;
        }

        return 0;
    }

    internal static int ParsePlayCount(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var number))
                return ClampPlayCount(number);
            if (value.TryGetDouble(out var real))
                return ClampPlayCount((long)Math.Round(real));
            return 0;
        }

        return value.ValueKind == JsonValueKind.String ? ParsePlayCount(value.GetString()) : 0;
    }

    internal static int ParsePlayCount(string? text)
    {
        var value = (text ?? "").Trim().Replace(",", "").Replace(" ", "");
        if (value.Length == 0)
            return 0;
        var multiplier = 1d;
        var suffix = value[^1];
        if (suffix is 'K' or 'k')
        {
            multiplier = 1_000;
            value = value[..^1];
        }
        else if (suffix is 'M' or 'm')
        {
            multiplier = 1_000_000;
            value = value[..^1];
        }
        else if (suffix is 'B' or 'b')
        {
            multiplier = 1_000_000_000;
            value = value[..^1];
        }
        else if (suffix is 'W' or 'w' or '万')
        {
            multiplier = 10_000;
            value = value[..^1];
        }
        else if (suffix is '亿')
        {
            multiplier = 100_000_000;
            value = value[..^1];
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return 0;
        return ClampPlayCount((long)Math.Round(amount * multiplier));
    }

    private static int ClampPlayCount(long value) =>
        value <= 0 ? 0 : value >= int.MaxValue ? int.MaxValue : (int)value;

    private static string JoinTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join(" / ", tags.EnumerateArray()
            .Select(tag => tag.ValueKind == JsonValueKind.String ? tag.GetString() : null)
            .Where(tag => !string.IsNullOrWhiteSpace(tag)));
    }

    private static JsonElement DataObject(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;

    private static bool IsSuccess(JsonElement root)
    {
        var code = ReadInt(root, "code");
        if (code is not null)
            return code is 0 or 200;
        return true;
    }

    private static string ReadMessage(JsonElement root) =>
        ReadString(root, "msg") ?? ReadString(root, "message") ?? "未知错误";

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool TryReadInt64(JsonElement value, out long number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number))
            return true;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return true;
        number = 0;
        return false;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        var text = ReadString(element, name);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private static string StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

    private sealed record DramaBook(
        string BookId,
        string Title,
        string Intro,
        string Cover,
        int EpisodeCount,
        string Category,
        string PublishTime,
        int PlayCount);

    private sealed record DramaChapter(
        string ChapterId,
        int EpisodeNumber,
        string Title,
        IReadOnlyList<(int Quality, string Url)> Urls);
}
