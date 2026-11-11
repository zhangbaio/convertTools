using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>
/// 接口直传：与剧集中心网页同一条链路（上传授权 → 点播 ApplyUploadInner → TOS 分片 → CommitUploadInner → 挂到剧集）。
/// 剧集中心接口带反爬签名，只能在已登录页面里 <c>fetch</c>；点播和 TOS 只认 STS 签名，由 HttpClient 并发上传。
/// </summary>
public static class TikTokApiUploadService
{
    internal const string SpaceName = "tiktok-copyright-platform";
    internal const int PartSize = 3 * 1024 * 1024;
    private const string ApiVersion = "2020-11-19";
    private const string AddVideoPath = "/api/content-partner/series/videos/add";

    private static readonly Regex SeriesIdPattern =
        new(@"/series/(?:draft/)?(\d{16,20})/?(?=[?#]|$)", RegexOptions.Compiled);
    private static readonly Regex EpisodePattern = new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);

    internal sealed record SeriesVideo(string VideoId, int OrderNum, string Title, int? Episode);

    internal sealed record CommittedVideo(string Vid, double DurationMs, string PosterUri);

    internal sealed record LocalEpisode(int Episode, string Path, string Title);

    public static string? TryReadSeriesIdFromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var match = SeriesIdPattern.Match(uri.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>页面在 www.tiktokdramacenter.com 时，点播与 TOS 走同域代理 vod./tos.。</summary>
    internal static string ResolveServiceBaseUrl(string pageUrl, string prefix)
    {
        var host = Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : "www.tiktokdramacenter.com";
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        return $"https://{prefix}.{host}";
    }

    internal static int? ParseEpisode(string? title)
    {
        var matches = EpisodePattern.Matches(title ?? "");
        if (matches.Count == 0)
            return null;
        return int.TryParse(matches[^1].Groups[1].Value, out var value) && value > 0 ? value : null;
    }

    internal static IReadOnlyList<LocalEpisode>? ResolveLocalEpisodes(IReadOnlyList<string> paths)
    {
        var episodes = new List<LocalEpisode>();
        foreach (var path in paths)
        {
            var title = System.IO.Path.GetFileNameWithoutExtension(path);
            if (ParseEpisode(title) is not { } episode)
                return null;
            episodes.Add(new LocalEpisode(episode, path, title));
        }

        return episodes.Select(item => item.Episode).Distinct().Count() == episodes.Count
            ? episodes.OrderBy(item => item.Episode).ToList()
            : null;
    }

    /// <summary>平台已有正片里有读不出集号或重复的集时不能只靠追加和排序修好，交给网页流程处理。</summary>
    internal static bool ExistingVideosUsable(IReadOnlyList<SeriesVideo> videos) =>
        videos.All(video => video.Episode is not null) &&
        videos.Select(video => video.Episode).Distinct().Count() == videos.Count;

    /// <summary>按集号排好的 videoId；已经是升序时返回 null。</summary>
    internal static IReadOnlyList<string>? PlanReorder(IReadOnlyList<SeriesVideo> videos)
    {
        var current = videos.OrderBy(video => video.OrderNum).ToList();
        var sorted = current.OrderBy(video => video.Episode ?? int.MaxValue).ToList();
        return current.Select(video => video.VideoId).SequenceEqual(sorted.Select(video => video.VideoId))
            ? null
            : sorted.Select(video => video.VideoId).ToList();
    }

    internal static string BuildAddVideoBody(string seriesId, CommittedVideo video, string title, int orderNum) =>
        JsonSerializer.Serialize(new
        {
            contractId = seriesId,
            video = new
            {
                videoType = 3,
                duration = video.DurationMs,
                vid = video.Vid,
                title,
                coverUri = video.PosterUri,
                orderNum,
            },
        });

    internal static string BuildFinishBody(IReadOnlyList<string> partCrcs) =>
        string.Join(",", partCrcs.Select((crc, index) => $"{index + 1}:{crc}"));

    /// <summary>
    /// 把本地缺的集直传并挂到剧集上，最后按集号排序并刷新页面。
    /// 返回 false 表示平台已有正片状态不适合接口处理（未做任何改动）；上传或挂集失败时抛出异常。
    /// </summary>
    public static async Task<bool> SyncEpisodesAsync(
        IPage page,
        string seriesId,
        IReadOnlyList<string> uploadPaths,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var local = ResolveLocalEpisodes(uploadPaths);
        if (local is null)
        {
            log?.Invoke("接口直传：本地视频文件名里有读不出集号或重复的集，改用网页上传。");
            return false;
        }

        var existing = await ReadSeriesVideosAsync(page, seriesId, ct);
        if (!ExistingVideosUsable(existing))
        {
            log?.Invoke($"接口直传：草稿已有 {existing.Count} 集里有重复或读不出集号的视频，改用网页流程处理。");
            return false;
        }

        var present = existing.Select(video => video.Episode!.Value).ToHashSet();
        var missing = local.Where(item => !present.Contains(item.Episode)).ToList();
        var changed = false;
        var failures = new List<string>();
        if (missing.Count == 0)
        {
            log?.Invoke($"接口直传：草稿已有 {existing.Count} 集，本地 {local.Count} 集都已挂上，无需上传。");
        }
        else
        {
            var concurrency = Math.Clamp(options.UploadApiConcurrency, 1, 6);
            log?.Invoke(
                $"接口直传：草稿已有 {existing.Count} 集，需上传 {missing.Count} 集" +
                $"（第 {FormatEpisodes(missing.Select(item => item.Episode))} 集），并发 {concurrency}。");

            using var http = await CreateHttpClientAsync(page, options);
            var sts = new StsCache(page);
            using var gate = new SemaphoreSlim(concurrency);
            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var uploaded = 0;
            var tasks = missing
                .Select(item => Task.Run(async () =>
                {
                    await gate.WaitAsync(uploadCts.Token);
                    try
                    {
                        return await UploadWithRetryAsync(
                            http, sts, page.Url, item, options, missing.Count, () => Interlocked.Increment(ref uploaded), log, uploadCts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException ||
                                               (!ct.IsCancellationRequested && !uploadCts.IsCancellationRequested))
                    {
                        log?.Invoke($"接口直传：第 {item.Episode} 集上传失败：{ex.Message}");
                        throw;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, uploadCts.Token))
                .ToList();

            var orderNum = existing.Count == 0 ? 0 : existing.Max(video => video.OrderNum);
            var mounted = 0;
            try
            {
                for (var index = 0; index < missing.Count; index++)
                {
                    var item = missing[index];
                    CommittedVideo committed;
                    try
                    {
                        committed = await tasks[index];
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        failures.Add($"第 {item.Episode} 集（{ex.Message}）");
                        continue;
                    }

                    orderNum++;
                    await PageFetchAsync(
                        page, "POST", AddVideoPath, BuildAddVideoBody(seriesId, committed, item.Title, orderNum), ct);
                    changed = true;
                    mounted++;
                    log?.Invoke(FormatMountedLog(item.Episode, mounted, missing.Count));
                }
            }
            catch
            {
                uploadCts.Cancel();
                if (changed)
                    await ReloadEditPageAsync(page, ct);
                throw;
            }
        }

        var after = await ReadSeriesVideosAsync(page, seriesId, ct);
        if (ExistingVideosUsable(after) && PlanReorder(after) is { } ordered)
        {
            await PageFetchAsync(
                page,
                "POST",
                "/api/content-partner/series/videos/reorder",
                JsonSerializer.Serialize(new { contractId = seriesId, orderedVideoIds = ordered }),
                ct);
            changed = true;
            log?.Invoke($"接口直传：已按集号重新排序 {ordered.Count} 集。");
        }

        if (changed)
            await ReloadEditPageAsync(page, ct);

        if (failures.Count > 0)
            throw new InvalidOperationException($"接口直传有 {failures.Count} 集未完成：{string.Join("；", failures)}");

        log?.Invoke($"接口直传完成：草稿正片 {after.Count} 集。");
        return true;
    }

    /// <summary>
    /// 新建页还没有剧集 ID：先用网页传第 1 个文件，从网页自己的挂集请求里取剧集 ID，再转到草稿编辑页，
    /// 剩下的集由编辑流程接口直传。
    /// </summary>
    public static async Task ContinueCreateAsDraftAsync(
        IPage page,
        TikTokPublishPayload payload,
        TikTokPublishOptions options,
        TikTokPublishRecommendation recommendation,
        string coverPath,
        IReadOnlyList<string> uploadPaths,
        Action<string>? log,
        CancellationToken ct)
    {
        var local = ResolveLocalEpisodes(uploadPaths)
                    ?? throw new InvalidOperationException("接口直传：本地视频文件名里有读不出集号或重复的集。");
        var first = local[0];
        log?.Invoke($"接口直传：新建页先用网页上传第 {first.Episode} 集，用来取得剧集 ID。");

        var timeoutMs = Math.Max(180, options.UploadStallSeconds) * 1000f;
        var response = await page.RunAndWaitForResponseAsync(
            () => TikTokBrowserActions.UploadLocalVideosAsync(page, new[] { first.Path }, waitForFinish: false, log, ct),
            candidate => candidate.Url.Contains(AddVideoPath, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(candidate.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions { Timeout = timeoutMs });

        var responseText = "";
        try { responseText = await response.TextAsync(); }
        catch { /* 只看请求体里的剧集 ID */ }
        if (!response.Ok || TikTokBrowserActions.ReorderResponseRejected(responseText))
            throw new InvalidOperationException($"接口直传：网页挂第 {first.Episode} 集失败（HTTP {response.Status}）。");

        var seriesId = ReadContractId(response.Request.PostData)
                       ?? throw new InvalidOperationException("接口直传：网页挂集请求里没有剧集 ID。");
        await ReadSeriesVideosAsync(page, seriesId, ct);

        var draftUrl = $"https://{new Uri(page.Url).Host}/series/draft/{seriesId}";
        log?.Invoke($"接口直传：已取得剧集 ID {seriesId}，转到草稿编辑页继续上传：{draftUrl}");
        await TikTokEditFlowService.OpenEditPublishFlowAsync(
            page, draftUrl, payload, options, recommendation, coverPath, log, ct);
    }

    internal static string? ReadContractId(string? postData)
    {
        if (string.IsNullOrWhiteSpace(postData))
            return null;
        try
        {
            using var document = JsonDocument.Parse(postData);
            if (!document.RootElement.TryGetProperty("contractId", out var value))
                return null;
            var text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<SeriesVideo> ParseSeriesVideos(JsonElement data)
    {
        var result = new List<SeriesVideo>();
        if (!data.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var video in videos.EnumerateArray())
        {
            var videoId = ReadString(video, "videoId");
            if (string.IsNullOrWhiteSpace(videoId))
                continue;
            var orderNum = video.TryGetProperty("orderNum", out var order) && order.TryGetInt32(out var number)
                ? number
                : result.Count + 1;
            var title = ReadString(video, "title");
            result.Add(new SeriesVideo(videoId, orderNum, title, ParseEpisode(title)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<SeriesVideo>> ReadSeriesVideosAsync(
        IPage page,
        string seriesId,
        CancellationToken ct)
    {
        using var document = await PageFetchAsync(page, "GET", $"/api/content-partner/series/{seriesId}", null, ct);
        return document.RootElement.TryGetProperty("data", out var data)
            ? ParseSeriesVideos(data)
            : Array.Empty<SeriesVideo>();
    }

    private static async Task<JsonDocument> PageFetchAsync(
        IPage page,
        string method,
        string path,
        string? body,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await page.EvaluateAsync<JsonElement>(
            """
            async ([method, path, body]) => {
              const init = { method, credentials: 'include' };
              if (body !== null) {
                init.headers = { 'content-type': 'application/json' };
                init.body = body;
              }
              const res = await fetch(path, init);
              return { status: res.status, text: await res.text() };
            }
            """,
            new object?[] { method, path, body });
        var status = result.GetProperty("status").GetInt32();
        var text = result.GetProperty("text").GetString() ?? "";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{path} 返回无法解析（HTTP {status}）：{Truncate(text)}");
        }

        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt64(out var value)
            ? value
            : -1;
        if (status is < 200 or >= 300 || code != 0)
        {
            var message = ReadString(root, "message");
            document.Dispose();
            throw new InvalidOperationException($"{path} 失败（HTTP {status}，code {code}）：{Truncate(message)}");
        }

        return document;
    }

    private static async Task ReloadEditPageAsync(IPage page, CancellationToken ct)
    {
        await page.ReloadAsync(new PageReloadOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60000,
        });
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20000 }); }
        catch { /* SPA */ }
        await TikTokBrowserActions.EnsureEditContentUploadTabAsync(page, ct);
    }

    private static async Task<CommittedVideo> UploadWithRetryAsync(
        HttpClient http,
        StsCache sts,
        string pageUrl,
        LocalEpisode item,
        TikTokPublishOptions options,
        int total,
        Func<int> markUploaded,
        Action<string>? log,
        CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var attempts = Math.Clamp(options.UploadBatchMaxRetries, 1, 10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var committed = await UploadFileAsync(http, sts, pageUrl, item.Path, ct);
                log?.Invoke(FormatUploadedLog(item.Episode, started.Elapsed, markUploaded(), total));
                return committed;
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                log?.Invoke($"接口直传：第 {item.Episode} 集第 {attempt} 次上传失败（{ex.Message}），{attempt * 3} 秒后重试。");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 3), ct);
            }
        }
    }

    internal static string FormatUploadedLog(int episode, TimeSpan elapsed, int uploaded, int total) =>
        $"接口直传：第 {episode} 集已上传（{FormatElapsed(elapsed)}，已上传 {uploaded}/{total}），按集号排队挂载。";

    internal static string FormatMountedLog(int episode, int mounted, int total) =>
        $"接口直传：第 {episode} 集已挂到剧集（已挂载 {mounted}/{total}）。";

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(1, (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
        if (seconds < 60)
            return $"{seconds} 秒";
        return $"{seconds / 60} 分 {seconds % 60} 秒";
    }

    private static async Task<CommittedVideo> UploadFileAsync(
        HttpClient http,
        StsCache sts,
        string pageUrl,
        string path,
        CancellationToken ct)
    {
        var fileSize = new FileInfo(path).Length;
        if (fileSize <= 0)
            throw new InvalidOperationException($"视频文件为空：{path}");

        var vodBase = ResolveServiceBaseUrl(pageUrl, "vod");
        var tosBase = ResolveServiceBaseUrl(pageUrl, "tos");
        var credentials = await sts.GetAsync(ct);

        var applyUri = new Uri(
            $"{vodBase}/?Action=ApplyUploadInner&Version={ApiVersion}&SpaceName={SpaceName}" +
            $"&FileType=video&IsInner=1&FileSize={fileSize}&s={RandomToken()}&device_platform=web");
        using var applyDocument = await SendVodAsync(http, HttpMethod.Get, applyUri, credentials, null, ct);
        var node = applyDocument.RootElement
            .GetProperty("Result").GetProperty("InnerUploadAddress").GetProperty("UploadNodes")[0];
        var store = node.GetProperty("StoreInfos")[0];
        var storeUri = ReadString(store, "StoreUri");
        var auth = ReadString(store, "Auth");
        var sessionKey = ReadString(node, "SessionKey");
        if (storeUri.Length == 0 || auth.Length == 0 || sessionKey.Length == 0)
            throw new InvalidOperationException("ApplyUploadInner 没有返回上传地址。");

        var objectUrl = $"{tosBase}/upload/v1/{storeUri}";
        var boundary = "----WebKitFormBoundary" + RandomToken(16);
        using (var init = new HttpRequestMessage(HttpMethod.Post, $"{objectUrl}?uploadmode=part&phase=init"))
        {
            init.Headers.TryAddWithoutValidation("Authorization", auth);
            init.Content = new StringContent($"--{boundary}--\r\n", Encoding.ASCII);
            init.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
            using var initDocument = await SendTosAsync(http, init, ct);
            var uploadId = ReadString(initDocument.RootElement.GetProperty("data"), "uploadid");
            if (uploadId.Length == 0)
                throw new InvalidOperationException("TOS 初始化没有返回 uploadid。");

            var crcs = new List<string>();
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                var buffer = new byte[PartSize];
                long offset = 0;
                for (var partNumber = 1; offset < fileSize; partNumber++)
                {
                    var length = await ReadChunkAsync(stream, buffer, ct);
                    if (length == 0)
                        break;
                    var crc = TikTokCrc32.Hex(buffer.AsSpan(0, length));
                    using var part = new HttpRequestMessage(
                        HttpMethod.Post,
                        $"{objectUrl}?uploadid={uploadId}&part_number={partNumber}&phase=transfer&part_offset={offset}");
                    part.Headers.TryAddWithoutValidation("Authorization", auth);
                    part.Content = new ByteArrayContent(buffer, 0, length);
                    part.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    part.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=\"undefined\"");
                    part.Content.Headers.TryAddWithoutValidation("Content-CRC32", crc);
                    using (await SendTosAsync(http, part, ct)) { }
                    crcs.Add(crc);
                    offset += length;
                }
            }

            using var finish = new HttpRequestMessage(
                HttpMethod.Post,
                $"{objectUrl}?uploadmode=part&phase=finish&uploadid={uploadId}&size={fileSize}");
            finish.Headers.TryAddWithoutValidation("Authorization", auth);
            finish.Content = new StringContent(BuildFinishBody(crcs), Encoding.UTF8);
            finish.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain;charset=UTF-8");
            using (await SendTosAsync(http, finish, ct)) { }
        }

        var commitBody = JsonSerializer.Serialize(new
        {
            SessionKey = sessionKey,
            Functions = new object[] { new { name = "Snapshot", input = new { SnapshotTime = 0 } } },
        });
        var commitUri = new Uri($"{vodBase}/?Action=CommitUploadInner&Version={ApiVersion}&SpaceName={SpaceName}");
        using var commitDocument = await SendVodAsync(
            http, HttpMethod.Post, commitUri, await sts.GetAsync(ct), commitBody, ct);
        var committed = commitDocument.RootElement.GetProperty("Result").GetProperty("Results")[0];
        var vid = ReadString(committed, "Vid");
        var poster = ReadString(committed, "PosterUri");
        var duration = committed.TryGetProperty("VideoMeta", out var meta) &&
                       meta.TryGetProperty("Duration", out var durationElement) &&
                       durationElement.TryGetDouble(out var seconds)
            ? seconds * 1000
            : 0;
        if (vid.Length == 0 || duration <= 0)
            throw new InvalidOperationException("CommitUploadInner 没有返回视频 ID 或时长。");
        return new CommittedVideo(vid, duration, poster);
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private static async Task<JsonDocument> SendVodAsync(
        HttpClient http,
        HttpMethod method,
        Uri uri,
        TikTokVodSigner.Credentials credentials,
        string? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain;charset=UTF-8");
        }

        TikTokVodSigner.Sign(request, credentials, body, DateTime.UtcNow);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var action = uri.Query.Contains("CommitUploadInner", StringComparison.Ordinal) ? "CommitUploadInner" : "ApplyUploadInner";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{action} 返回无法解析（HTTP {(int)response.StatusCode}）：{Truncate(text)}");
        }

        if (document.RootElement.TryGetProperty("ResponseMetadata", out var metadata) &&
            metadata.TryGetProperty("Error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            var message = $"{ReadString(error, "Code")} {ReadString(error, "Message")}".Trim();
            document.Dispose();
            throw new InvalidOperationException($"{action} 失败：{Truncate(message)}");
        }

        if (!response.IsSuccessStatusCode || !document.RootElement.TryGetProperty("Result", out _))
        {
            document.Dispose();
            throw new InvalidOperationException($"{action} 失败（HTTP {(int)response.StatusCode}）：{Truncate(text)}");
        }

        return document;
    }

    private static async Task<JsonDocument> SendTosAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"TOS 返回无法解析（HTTP {(int)response.StatusCode}）：{Truncate(text)}");
        }

        var code = document.RootElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
            ? value
            : -1;
        if (!response.IsSuccessStatusCode || code != 2000)
        {
            var message = ReadString(document.RootElement, "message");
            document.Dispose();
            throw new InvalidOperationException($"TOS 上传失败（HTTP {(int)response.StatusCode}，code {code}）：{Truncate(message)}");
        }

        return document;
    }

    private static async Task<HttpClient> CreateHttpClientAsync(IPage page, TikTokPublishOptions options)
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(options.NetworkProxyServer))
        {
            handler.Proxy = new WebProxy(new Uri(options.NetworkProxyServer))
            {
                Credentials = string.IsNullOrWhiteSpace(options.NetworkProxyUsername)
                    ? null
                    : new NetworkCredential(options.NetworkProxyUsername, options.NetworkProxyPassword),
            };
            handler.UseProxy = true;
        }

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            var userAgent = await page.EvaluateAsync<string>("() => navigator.userAgent");
            if (!string.IsNullOrWhiteSpace(userAgent))
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }
        catch
        {
            // 读不到 UA 时用默认值。
        }

        if (Uri.TryCreate(page.Url, UriKind.Absolute, out var pageUri))
        {
            var origin = $"{pageUri.Scheme}://{pageUri.Host}";
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", origin);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", origin + "/");
        }

        return http;
    }

    private sealed class StsCache(IPage page)
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private TikTokVodSigner.Credentials? _credentials;
        private DateTimeOffset _expiresAt;

        public async Task<TikTokVodSigner.Credentials> GetAsync(CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_credentials is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-5))
                    return _credentials;

                using var document = await PageFetchAsync(page, "POST", "/api/content-partner/tool/upload/auth/", "{}", ct);
                var ogc = document.RootElement.GetProperty("data").GetProperty("ogc");
                _credentials = new TikTokVodSigner.Credentials(
                    ReadString(ogc, "AccessKeyId"),
                    ReadString(ogc, "SecretAccessKey"),
                    ReadString(ogc, "SessionToken"));
                if (_credentials.AccessKeyId.Length == 0 || _credentials.SecretAccessKey.Length == 0)
                    throw new InvalidOperationException("上传授权没有返回 STS 密钥。");
                _expiresAt = DateTimeOffset.TryParse(
                    ReadString(ogc, "ExpiredTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
                    ? expires
                    : DateTimeOffset.UtcNow.AddMinutes(30);
                return _credentials;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string RandomToken(int length = 11)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        return string.Create(length, 0, (span, _) =>
        {
            for (var index = 0; index < span.Length; index++)
                span[index] = alphabet[Random.Shared.Next(alphabet.Length)];
        });
    }

    private static string FormatEpisodes(IEnumerable<int> episodes)
    {
        var list = episodes.ToList();
        return list.Count <= 12
            ? string.Join(", ", list)
            : string.Join(", ", list.Take(12)) + $" 等 {list.Count} 集";
    }

    private static string Truncate(string? text, int max = 200)
    {
        var value = (text ?? "").Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}
