using System.Diagnostics;
using System.Globalization;
using System.Net;
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
    public const string DefaultLanguage = "en";
    public const int FallbackNewChannelId = 41;

    private const string GatewayOrigin = "https://api.iusc.cc/api/dr";
    private const string AppId = "dr_downloader_cpp";
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static string? _token;
    private static DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private static string _tokenDeviceId = "";
    private static int? _resolvedNewChannelId;

    public sealed record ConnectivityResult(
        bool LicenseOk,
        bool NewOk,
        bool DownloadOk,
        string LicenseMessage,
        string NewMessage,
        string DownloadMessage);

    public sealed record NewChannelPage(
        IReadOnlyList<DramaSearchItem> Items,
        bool HasMore,
        int NextPage,
        int ChannelId);

    public async Task<ConnectivityResult> TestConnectivityAsync(string? deviceId, CancellationToken cancellationToken)
    {
        var settings = new DramaSourceSettings { DramaBoxDeviceId = deviceId ?? "" };
        await GetAccessTokenAsync(NormalizeDeviceId(settings.DramaBoxDeviceId), cancellationToken);
        var page = await GetNewChannelPageAsync(settings, 1, enrich: false, cancellationToken);
        if (page.Items.Count == 0)
        {
            return new ConnectivityResult(true, false, false, "设备已授权", "New 没有返回剧目", "未测试下载");
        }

        using var document = await PostSignedAsync(
            settings,
            "/drama-box/chapterv2/batch/load",
            new { bookId = page.Items[0].BookId[BookPrefix.Length..], boundaryIndex = 0, loadDirection = 0, preLoad = false },
            cancellationToken);
        var chapter = ReadChapters(DataObject(document.RootElement)).FirstOrDefault();
        var url = chapter is null ? null : PickUrl(chapter.Urls, "1080P");
        var newMessage = $"New 频道 {page.ChannelId} 拉到 {page.Items.Count} 部，例如 {page.Items[0].Title}";
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ConnectivityResult(true, true, false, "设备已授权", newMessage, "没有拿到分集播放地址");
        }

        var response = await SendTextAsync(
            HttpMethod.Get,
            url,
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Range"] = "bytes=0-15",
                ["User-Agent"] = "okhttp/4.10.0",
            },
            cancellationToken);
        var downloadOk = response.Status is >= 200 and < 300;
        return new ConnectivityResult(
            true,
            true,
            downloadOk,
            "设备已授权",
            newMessage,
            downloadOk
                ? $"第 {chapter!.EpisodeNumber} 集 1080P 可以下载"
                : $"播放地址 HTTP {response.Status}");
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
                language = DefaultLanguage,
                currentLanguage = DefaultLanguage,
            },
            cancellationToken);
        var books = ReadSearchList(document.RootElement);
        return await FillEpisodeCountsAsync(settings, books.Select(ToSearchItem).ToArray(), cancellationToken);
    }

    public async Task<IReadOnlyList<DramaSearchItem>> GetNewChannelAsync(CancellationToken cancellationToken)
        => await GetNewChannelAsync(new DramaSourceSettings { DramaBoxDeviceId = DefaultDeviceId }, cancellationToken);

    public async Task<IReadOnlyList<DramaSearchItem>> GetNewChannelAsync(
        DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        var items = new List<DramaSearchItem>();
        for (var page = 1; page <= 8; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await GetNewChannelPageAsync(settings, page, enrich: true, cancellationToken);
            var before = items.Count;
            foreach (var item in batch.Items)
            {
                if (items.Any(existing => string.Equals(existing.BookId, item.BookId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                items.Add(item);
            }

            if (!batch.HasMore || items.Count == before)
                break;
        }

        return items;
    }

    public async Task<NewChannelPage> GetNewChannelPageAsync(
        DramaSourceSettings settings,
        int page,
        bool enrich,
        CancellationToken cancellationToken)
    {
        var pageNo = Math.Max(1, page);
        var channelId = await ResolveNewChannelIdAsync(settings, cancellationToken);
        using var document = await PostSignedAsync(
            settings,
            "/drama-box/he001/theater",
            new
            {
                newChannelStyle = 1,
                isNeedRank = 1,
                pageNo,
                index = 1,
                channelId,
                language = DefaultLanguage,
                currentLanguage = DefaultLanguage,
            },
            cancellationToken);
        var parsed = ParseNewChannel(document.RootElement, pageNo);
        var items = parsed.Books.Select(ToSearchItem).ToArray();
        if (enrich)
            items = (await FillEpisodeCountsAsync(settings, items, cancellationToken)).ToArray();
        var hasMore = parsed.HasMore && items.Length > 0;
        return new NewChannelPage(items, hasMore, hasMore ? pageNo + 1 : pageNo, channelId);
    }

    private async Task<IReadOnlyList<DramaSearchItem>> FillEpisodeCountsAsync(
        DramaSourceSettings settings,
        DramaSearchItem[] items,
        CancellationToken cancellationToken)
    {
        if (items.All(item => item.EpisodeTotal > 0))
            return items;

        using var gate = new SemaphoreSlim(4);
        var filled = new DramaSearchItem[items.Length];
        await Task.WhenAll(items.Select(async (item, index) =>
        {
            if (item.EpisodeTotal > 0)
            {
                filled[index] = item;
                return;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                var count = await ReadDetailEpisodeCountAsync(settings, item.BookId, cancellationToken);
                filled[index] = count > 0 ? item with { EpisodeTotal = count } : item;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                filled[index] = item;
            }
            finally
            {
                gate.Release();
            }
        }));
        return filled;
    }

    private async Task<int> ReadDetailEpisodeCountAsync(
        DramaSourceSettings settings,
        string bookReference,
        CancellationToken cancellationToken)
    {
        var bookId = StripPrefix(bookReference, BookPrefix);
        using var document = await PostSignedAsync(
            settings,
            "/drama-box/chapterv2/detail",
            new { bookId },
            cancellationToken);
        var data = DataObject(document.RootElement);
        return data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.GetArrayLength()
            : 0;
    }

    public async Task<IReadOnlyList<GatewayEpisode>> GetEpisodesAsync(
        DramaSourceSettings settings, string bookReference, CancellationToken cancellationToken)
    {
        var bookId = StripPrefix(bookReference, BookPrefix);
        var (chapters, coverUrl) = await LoadChaptersAsync(settings, bookId, cancellationToken);
        return chapters
            .Select(chapter => new GatewayEpisode(
                chapter.EpisodeNumber,
                string.IsNullOrWhiteSpace(chapter.Title) ? $"第{chapter.EpisodeNumber}集" : chapter.Title,
                $"{EpisodePrefix}{bookId}|{chapter.ChapterId}",
                coverUrl))
            .ToArray();
    }

    public async Task<GatewayPlayback> GetPlaybackAsync(
        DramaSourceSettings settings, string episodeReference, string quality, CancellationToken cancellationToken)
    {
        var reference = StripPrefix(episodeReference, EpisodePrefix);
        var split = reference.Split('|', 2);
        if (split.Length != 2 || string.IsNullOrWhiteSpace(split[0]) || string.IsNullOrWhiteSpace(split[1]))
            throw new InvalidOperationException("DramaBox 分集号无效。");
        var (chapters, _) = await LoadChaptersAsync(settings, split[0], cancellationToken);
        var chapter = chapters.FirstOrDefault(item => string.Equals(item.ChapterId, split[1], StringComparison.Ordinal))
                      ?? throw new InvalidOperationException("DramaBox 分集列表里没有这一集。");
        var url = PickUrl(chapter.Urls, quality)
                  ?? throw new InvalidOperationException("DramaBox 没有返回该集的播放地址。");
        return new GatewayPlayback(url, string.Empty, false);
    }

    public async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.10.0");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode && !IsHtmlMedia(response))
            {
                await WriteResponseFileAsync(response, targetPath, cancellationToken);
                if (!IsDeniedFile(targetPath))
                    return;
            }
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
        }

        if (File.Exists(targetPath))
            File.Delete(targetPath);
        await DownloadFileWithImpersonationAsync(url, targetPath, cancellationToken);
        if (IsDeniedFile(targetPath))
            throw new InvalidOperationException("DramaBox 视频下载被 CDN 拒绝。");
    }

    private static bool IsHtmlMedia(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        return mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Contains("text", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteResponseFileAsync(
        HttpResponseMessage response, string targetPath, CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(file, cancellationToken);
    }

    private static async Task DownloadFileWithImpersonationAsync(
        string url, string targetPath, CancellationToken cancellationToken)
    {
        var curl = FindCurlImpersonate();
        if (!string.IsNullOrWhiteSpace(curl))
        {
            await DownloadFileWithCurlAsync(curl, url, targetPath, cancellationToken);
            return;
        }

        await DownloadFileWithPythonAsync(url, targetPath, cancellationToken);
    }

    private static async Task DownloadFileWithCurlAsync(
        string curl, string url, string targetPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = curl,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-sS");
        startInfo.ArgumentList.Add("-L");
        startInfo.ArgumentList.Add("--http1.1");
        startInfo.ArgumentList.Add("--max-time");
        startInfo.ArgumentList.Add("600");
        startInfo.ArgumentList.Add("-H");
        startInfo.ArgumentList.Add("User-Agent: okhttp/4.10.0");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add(url);
        using var process = global::System.Diagnostics.Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法启动 curl-impersonate。");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(targetPath))
            throw new InvalidOperationException("DramaBox 视频下载失败：" + Trim(error));
    }

    private static async Task DownloadFileWithPythonAsync(
        string url, string targetPath, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "dramabox-download.py");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            import json, sys, urllib.request, urllib.error
            req = json.load(sys.stdin)
            request = urllib.request.Request(req["url"], headers={"User-Agent": "okhttp/4.10.0"}, method="GET")
            try:
                with urllib.request.urlopen(request, timeout=600) as response:
                    open(req["path"], "wb").write(response.read())
            except urllib.error.HTTPError as error:
                open(req["path"], "wb").write(error.read())
                if error.code >= 400:
                    sys.exit(1)
            """,
            cancellationToken);
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{scriptPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = global::System.Diagnostics.Process.Start(startInfo)
                           ?? throw new InvalidOperationException("无法启动 Python，DramaBox 视频下载失败。");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { url, path = targetPath }));
        process.StandardInput.Close();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(targetPath))
            throw new InvalidOperationException("DramaBox 视频下载失败：" + Trim(error));
    }

    private static bool IsDeniedFile(string path)
    {
        if (!File.Exists(path))
            return true;
        var info = new FileInfo(path);
        if (info.Length == 0 || info.Length > 8192)
            return false;
        var text = File.ReadAllText(path);
        return IsCdnDenied(text);
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
        var episode = Math.Max(1, episodeNumber);
        var response = await SendTextAsync(
            HttpMethod.Post,
            $"{GatewayOrigin}/key",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                app_id = AppId,
                device = deviceId,
                private_key_uri = privateKeyUri,
                episode_number = episode,
                video_id = "",
            })),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer " + token,
                ["Content-Type"] = "application/json; charset=utf-8",
                ["User-Agent"] = "Mozilla/5.0",
            },
            cancellationToken);
        var text = response.Body;
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (response.Status is < 200 or >= 300 || !IsSuccess(document.RootElement))
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

    public sealed record GatewayEpisode(int EpisodeNumber, string Title, string VideoId, string CoverUrl = "");
    public sealed record GatewayPlayback(string Url, string SpadeA, bool Encrypted);

    private async Task<(IReadOnlyList<DramaChapter> Chapters, string CoverUrl)> LoadChaptersAsync(
        DramaSourceSettings settings, string bookId, CancellationToken cancellationToken)
    {
        var chapters = new List<DramaChapter>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 1;
        var expected = 0;
        var coverUrl = "";
        for (var page = 0; page < 40; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = await PostSignedAsync(
                settings,
                "/drama-box/chapterv2/batch/load",
                new
            {
                boundaryIndex = 0,
                index,
                loadDirection = 0,
                preLoad = false,
                bookId,
                language = DefaultLanguage,
                currentLanguage = DefaultLanguage,
            },
                cancellationToken);
            var data = DataObject(document.RootElement);
            if (coverUrl.Length == 0)
                coverUrl = ReadCoverUrl(data);
            if (expected <= 0)
                expected = ReadInt(data, "chapterCount") ?? 0;
            var batch = ReadChapters(data);
            if (batch.Count == 0)
                break;

            var added = 0;
            foreach (var chapter in batch)
            {
                if (seen.Add(chapter.ChapterId))
                {
                    chapters.Add(chapter);
                    added++;
                }
            }

            if (added == 0 || (expected > 0 && chapters.Count >= expected))
                break;
            var next = batch.Max(item => item.EpisodeNumber) + 1;
            if (next <= index)
                break;
            index = next;
        }

        if (chapters.Count > 0 && chapters.Min(item => item.EpisodeNumber) == 0)
        {
            chapters = chapters
                .Select(item => item with { EpisodeNumber = item.EpisodeNumber + 1 })
                .ToList();
        }

        return (chapters.OrderBy(item => item.EpisodeNumber).ToArray(), coverUrl);
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
            language = DefaultLanguage,
            currentLanguage = DefaultLanguage,
            content_language = DefaultLanguage,
        });
        var signResponse = await SendTextAsync(
            HttpMethod.Post,
            signUrl,
            Encoding.UTF8.GetBytes(signPayload),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer " + token,
                ["Content-Type"] = "application/json; charset=utf-8",
                ["User-Agent"] = "Mozilla/5.0",
            },
            cancellationToken);
        var signText = signResponse.Body;
        using var signed = JsonDocument.Parse(string.IsNullOrWhiteSpace(signText) ? "{}" : signText);
        if (signResponse.Status is < 200 or >= 300 || !IsSuccess(signed.RootElement))
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

        var upstream = await SendTextAsync(HttpMethod.Post, url, bodyBytes, upstreamHeaders, cancellationToken);
        var text = upstream.Body;
        if (upstream.Status is < 200 or >= 300 && !IsCdnDenied(text))
            throw new InvalidOperationException($"DramaBox 接口 HTTP {upstream.Status}：{Trim(text)}");
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

    internal readonly record struct HttpText(int Status, string Body);

    private async Task<HttpText> SendTextAsync(
        HttpMethod method,
        string url,
        byte[]? body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        try
        {
            var direct = await SendTextWithHttpClientAsync(method, url, body, headers, cancellationToken);
            if (!IsCdnDenied(direct.Body))
                return direct;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            // sapi.dramaboxdb.com 会拒绝 .NET 的 TLS 指纹，表现为握手失败或 HTML Access Denied。
        }

        return await SendTextWithImpersonationAsync(method, url, body, headers, cancellationToken);
    }

    private async Task<HttpText> SendTextWithHttpClientAsync(
        HttpMethod method,
        string url,
        byte[]? body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        using var upstream = new HttpRequestMessage(method, url);
        upstream.Version = HttpVersion.Version11;
        upstream.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        if (body is not null)
            upstream.Content = new ByteArrayContent(body);
        foreach (var header in headers)
        {
            if (!upstream.Headers.TryAddWithoutValidation(header.Key, header.Value) && upstream.Content is not null)
                upstream.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await httpClient.SendAsync(upstream, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return new HttpText((int)response.StatusCode, text);
    }

    private static async Task<HttpText> SendTextWithImpersonationAsync(
        HttpMethod method,
        string url,
        byte[]? body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var curl = FindCurlImpersonate();
        if (!string.IsNullOrWhiteSpace(curl))
            return await SendTextWithCurlAsync(curl, method, url, body, headers, cancellationToken);
        return await SendTextWithPythonAsync(method, url, body, headers, cancellationToken);
    }

    private static string? FindCurlImpersonate()
    {
        var names = new[]
        {
            "curl-impersonate-chrome.exe",
            "curl-impersonate.exe",
            "curl_chrome131.exe",
            "curl_chrome124.exe",
            "curl_chrome116.exe",
        };
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("DRAMABOX_CURL");
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured.Trim());
        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools"));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
            foreach (var name in names)
            {
                var path = Path.Combine(candidate, name);
                if (File.Exists(path))
                    return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var path = Path.Combine(directory.Trim(), name);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static async Task<HttpText> SendTextWithCurlAsync(
        string curl,
        HttpMethod method,
        string url,
        byte[]? body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var bodyPath = body is null ? null : Path.Combine(Path.GetTempPath(), $"dramabox-{Guid.NewGuid():N}.bin");
        var headerPath = Path.Combine(Path.GetTempPath(), $"dramabox-{Guid.NewGuid():N}.hdr");
        if (bodyPath is not null && body is not null)
            await File.WriteAllBytesAsync(bodyPath, body, cancellationToken);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = curl,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("-sS");
            startInfo.ArgumentList.Add("--http1.1");
            startInfo.ArgumentList.Add("--compressed");
            startInfo.ArgumentList.Add("--path-as-is");
            startInfo.ArgumentList.Add("--max-time");
            startInfo.ArgumentList.Add("40");
            startInfo.ArgumentList.Add("-X");
            startInfo.ArgumentList.Add(method.Method);
            startInfo.ArgumentList.Add("-D");
            startInfo.ArgumentList.Add(headerPath);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("-");
            foreach (var header in headers)
            {
                startInfo.ArgumentList.Add("-H");
                startInfo.ArgumentList.Add($"{header.Key}: {header.Value}");
            }

            if (bodyPath is not null)
            {
                startInfo.ArgumentList.Add("--data-binary");
                startInfo.ArgumentList.Add("@" + bodyPath);
            }

            startInfo.ArgumentList.Add(url);
            using var process = global::System.Diagnostics.Process.Start(startInfo)
                               ?? throw new InvalidOperationException("无法启动 curl-impersonate。");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("DramaBox 接口被 CDN 拒绝：" + Trim(error));
            var headerText = File.Exists(headerPath) ? await File.ReadAllTextAsync(headerPath, cancellationToken) : "";
            var status = SplitCurlDump(headerText + "\r\n\r\n").Status;
            return new HttpText(status, output);
        }
        finally
        {
            foreach (var path in new[] { bodyPath, headerPath })
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                try { File.Delete(path); } catch { /* 临时文件可以留到下次清理 */ }
            }
        }
    }

    internal static HttpText SplitCurlDump(string output)
    {
        var text = output ?? "";
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var width = 4;
        if (separator < 0)
        {
            separator = text.IndexOf("\n\n", StringComparison.Ordinal);
            width = 2;
        }

        if (separator < 0)
            return new HttpText(200, text);
        var header = text[..separator];
        var body = text[(separator + width)..];
        var status = 200;
        var first = header.Split('\n', 2)[0].Trim();
        var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            status = parsed;
        return new HttpText(status, body);
    }

    private static async Task<HttpText> SendTextWithPythonAsync(
        HttpMethod method,
        string url,
        byte[]? body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "dramabox-upstream.py");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            import json, sys, base64, urllib.request, urllib.error
            req = json.load(sys.stdin)
            payload = base64.b64decode(req["body"]) if req.get("body") else None
            request = urllib.request.Request(
                req["url"],
                data=payload,
                headers={key: str(value) for key, value in req["headers"].items()},
                method=req.get("method") or "POST")
            try:
                with urllib.request.urlopen(request, timeout=40) as response:
                    sys.stdout.buffer.write(str(response.status).encode("ascii") + b"\n")
                    sys.stdout.buffer.write(response.read())
            except urllib.error.HTTPError as error:
                sys.stdout.buffer.write(str(error.code).encode("ascii") + b"\n")
                sys.stdout.buffer.write(error.read())
            """,
            cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            url,
            method = method.Method,
            body = body is null ? null : Convert.ToBase64String(body),
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
        var newline = output.IndexOf('\n');
        if (newline <= 0 || !int.TryParse(output[..newline].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
            return new HttpText(200, output);
        return new HttpText(status, output[(newline + 1)..]);
    }

    private static bool IsTransportFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
                return false;
            if (current is HttpRequestException or IOException or System.Security.Authentication.AuthenticationException)
                return true;
        }

        return false;
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
            var response = await SendTextAsync(
                HttpMethod.Get,
                url,
                null,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["User-Agent"] = "Mozilla/5.0",
                },
                cancellationToken);
            var text = response.Body;
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            if (response.Status is < 200 or >= 300 || !IsSuccess(document.RootElement))
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

    internal static int CountSearchBooks(JsonElement root) => ReadSearchList(root).Count();

    private static IEnumerable<DramaBook> ReadSearchList(JsonElement root)
    {
        var data = DataObject(root);
        foreach (var name in new[] { "searchList", "bookList", "records", "list" })
        {
            if (!data.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            var found = false;
            foreach (var item in list.EnumerateArray())
            {
                var book = ReadBook(item);
                if (book is null)
                    continue;
                found = true;
                yield return book;
            }

            if (found)
                yield break;
        }
    }

    private async Task<int> ResolveNewChannelIdAsync(DramaSourceSettings settings, CancellationToken cancellationToken)
    {
        if (_resolvedNewChannelId is int cached && cached > 0)
            return cached;

        foreach (var path in new[] { "/drama-box/ap001/confload", "/drama-box/he001/recommendChannel" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = await PostSignedAsync(
                    settings,
                    path,
                    new { homePageStyle = 1, newChannelStyle = 1, language = DefaultLanguage },
                    cancellationToken);
                var id = FindNewChannelId(document.RootElement);
                if (id is > 0)
                {
                    _resolvedNewChannelId = id;
                    return id.Value;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 配置接口失败时继续用 New 频道的已知编号。
            }
        }

        return FallbackNewChannelId;
    }

    internal static int? FindNewChannelId(JsonElement root)
    {
        int? named = null;
        int? theater = null;
        Walk(root);
        return named ?? theater;

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var channelId = ReadInt(element, "channelId");
                    if (channelId is > 0)
                    {
                        if (IsExplicitNewTab(element))
                            named ??= channelId;
                        else if (PointsAtTheater(element))
                            theater ??= channelId;
                    }

                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item);
                    break;
            }
        }
    }

    private static bool IsExplicitNewTab(JsonElement element)
    {
        foreach (var name in new[] { "tabKey", "key", "contentKind", "channelType", "type" })
        {
            var value = ReadString(element, name);
            if (string.Equals(value, "new_drama", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "new", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var name in new[] { "tabName", "channelName", "name", "title", "channelTypeName" })
        {
            var value = ReadString(element, name);
            if (value is "New" or "新剧" or "新劇" or "Nuevo" or "Novo" or "Terbaru" or "신작" or "新作")
                return true;
        }

        return false;
    }

    private static bool PointsAtTheater(JsonElement element)
    {
        foreach (var name in new[] { "contentEndpoint", "path", "url", "requestPath" })
        {
            var value = ReadString(element, name);
            if (!string.IsNullOrWhiteSpace(value) &&
                value.Contains("/drama-box/he001/theater", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal readonly record struct NewChannelSnapshot(
        int Count,
        bool HasMore,
        string Title,
        int EpisodeCount,
        int PlayCount,
        string Category);

    internal static NewChannelSnapshot DescribeNewChannel(JsonElement root, int page)
    {
        var books = ReadNewTheater(root).ToArray();
        var first = books.FirstOrDefault();
        return new NewChannelSnapshot(
            books.Length,
            ReadHasMore(root, page, books.Length),
            first?.Title ?? "",
            first?.EpisodeCount ?? 0,
            first?.PlayCount ?? 0,
            first?.Category ?? "");
    }

    private readonly record struct NewChannelParse(IReadOnlyList<DramaBook> Books, bool HasMore);

    private static NewChannelParse ParseNewChannel(JsonElement root, int page)
    {
        var books = ReadNewTheater(root).ToArray();
        return new NewChannelParse(books, ReadHasMore(root, page, books.Length) && books.Length > 0);
    }

    private static bool ReadHasMore(JsonElement root, int page, int recordCount)
    {
        if (recordCount <= 0)
            return false;
        var data = DataObject(root);
        if (!data.TryGetProperty("newTheaterList", out var theater) || theater.ValueKind != JsonValueKind.Object)
            return true;
        if (ReadBool(theater, "isMore") is bool isMore)
            return isMore;
        if (ReadBool(theater, "hasMore") is bool hasMore)
            return hasMore;
        var pages = ReadInt(theater, "pages") ?? ReadInt(theater, "totalPages") ?? ReadInt(data, "pages");
        var current = ReadInt(theater, "current") ?? ReadInt(theater, "pageNo") ?? page;
        if (pages is > 0)
            return current < pages;
        return true;
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
            ReadCoverUrl(item),
            ReadEpisodeCount(item),
            JoinTags(item),
            ReadPublishTime(item),
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

    internal static int ReadEpisodeCount(JsonElement item)
    {
        foreach (var name in EpisodeCountFields)
        {
            var count = ReadInt(item, name);
            if (count is > 0)
                return count.Value;
        }

        foreach (var nestedName in new[] { "book", "bookInfo", "detail" })
        {
            if (!item.TryGetProperty(nestedName, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;
            var count = ReadEpisodeCount(nested);
            if (count > 0)
                return count;
        }

        return 0;
    }

    private static readonly string[] EpisodeCountFields =
    [
        "episodeCount", "episode_count", "episode_cnt",
        "chapterCount", "chapter_count", "chapterCnt", "chapter_cnt",
        "chapterNum", "chapter_num", "chapterTotal", "chapter_total",
        "totalChapter", "total_chapter", "totalChapterNum", "total_chapter_num",
        "totalEpisode", "total_episode", "episodeTotal", "episode_total",
        "chapterSize", "chapter_size", "sectionCount", "section_count",
        "updateChapterNum", "videoCount", "video_count", "epCount", "ep_count",
    ];

    private static string ReadPublishTime(JsonElement item)
    {
        foreach (var name in new[]
        {
            "bookShelfTime", "shelfTime", "publishTime", "firstShelfTime", "onlineTime", "utime", "ctime",
        })
        {
            if (!item.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var timestamp) && timestamp > 1_000_000_000)
            {
                var milliseconds = timestamp > 10_000_000_000 ? timestamp : timestamp * 1000;
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        return "";
    }

    private static int ReadPlayCount(JsonElement item)
    {
        foreach (var name in new[] { "playCount", "play_count", "viewCount", "view_count", "playNum", "inLibraryCount" })
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
        var tags = new List<string>();
        AddTags(item, tags);
        foreach (var nestedName in new[] { "book", "bookInfo" })
        {
            if (item.TryGetProperty(nestedName, out var nested) && nested.ValueKind == JsonValueKind.Object)
                AddTags(nested, tags);
        }

        return string.Join(" / ", tags.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static void AddTags(JsonElement item, List<string> tags)
    {
        foreach (var name in new[] { "tags", "tagV3s", "tagNames", "labels", "markNames" })
        {
            if (!item.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                foreach (var part in (value.GetString() ?? "").Split(['/', ',', '|', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    tags.Add(part);
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var tag in value.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String)
                {
                    var text = tag.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        tags.Add(text);
                    continue;
                }

                if (tag.ValueKind != JsonValueKind.Object)
                    continue;
                var nameText = ReadString(tag, "tagName") ?? ReadString(tag, "name") ?? ReadString(tag, "tagEnName");
                if (!string.IsNullOrWhiteSpace(nameText))
                    tags.Add(nameText);
            }
        }
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

    internal static string ReadCoverUrl(JsonElement item)
    {
        foreach (var name in new[] { "coverWap", "bookCover", "cover" })
        {
            var value = ReadString(item, name);
            if (!string.IsNullOrWhiteSpace(value) &&
                value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return "";
    }

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
