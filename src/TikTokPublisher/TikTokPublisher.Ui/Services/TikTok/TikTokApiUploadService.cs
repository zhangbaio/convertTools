using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
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
    internal const int PartsInFlightPerFile = 3;
    private const int MaxPartsInFlight = 12;
    private const string ApiVersion = "2020-11-19";
    private const string AddVideoPath = "/api/content-partner/series/videos/add";
    private const string DeleteVideosPath = "/api/content-partner/series/videos/batch-delete";

    private static readonly Regex SeriesIdPattern =
        new(@"/series/(?:draft/)?(\d{16,20})/?(?=[?#]|$)", RegexOptions.Compiled);
    private static readonly Regex SeriesIdValuePattern = new(@"^\d{16,20}$", RegexOptions.Compiled);
    private static readonly Regex EpisodePattern = new(@"第\s*(\d+)\s*集", RegexOptions.Compiled);

    internal sealed record SeriesVideo(string VideoId, int OrderNum, string Title, int? Episode);

    internal sealed record CommittedVideo(string Vid, double DurationMs, string PosterUri);

    internal sealed record UploadTiming(long Bytes, TimeSpan Transfer, TimeSpan Commit);

    /// <summary>传输名额按并发数限制；提交阶段不占名额。分片名额是所有集合计的上限。</summary>
    private sealed class UploadGates(int concurrency) : IDisposable
    {
        public SemaphoreSlim Transfer { get; } = new(concurrency, concurrency);

        public SemaphoreSlim Parts { get; } = new(
            Math.Min(MaxPartsInFlight, concurrency * PartsInFlightPerFile),
            Math.Min(MaxPartsInFlight, concurrency * PartsInFlightPerFile));

        public void Dispose()
        {
            Transfer.Dispose();
            Parts.Dispose();
        }
    }

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

    /// <summary>每一集都能读出且不重复。读不出或重复的行要先用删除接口去掉。</summary>
    internal static bool ExistingVideosUsable(IReadOnlyList<SeriesVideo> videos) =>
        videos.All(video => video.Episode is not null) &&
        videos.Select(video => video.Episode).Distinct().Count() == videos.Count;

    /// <summary>按出现顺序保留每个集号的第一行。后面的重复行和读不出集号的行要删除。</summary>
    internal static IReadOnlyList<SeriesVideo> SelectUsableExisting(IReadOnlyList<SeriesVideo> videos)
    {
        var seen = new HashSet<int>();
        var usable = new List<SeriesVideo>();
        foreach (var video in videos.OrderBy(video => video.OrderNum))
        {
            if (video.Episode is int episode && seen.Add(episode))
                usable.Add(video);
        }

        return usable;
    }

    /// <summary>把可用集按集号排到前面，读不出或重复的行排到末尾。已经是这个顺序时返回 null。</summary>
    internal static IReadOnlyList<string>? PlanDropRejected(IReadOnlyList<SeriesVideo> videos)
    {
        var usableIds = SelectUsableExisting(videos).Select(video => video.VideoId).ToHashSet(StringComparer.Ordinal);
        if (usableIds.Count == videos.Count)
            return null;

        var desired = SelectUsableExisting(videos)
            .OrderBy(video => video.Episode)
            .Select(video => video.VideoId)
            .Concat(videos.OrderBy(video => video.OrderNum).Where(video => !usableIds.Contains(video.VideoId)).Select(video => video.VideoId))
            .ToList();
        var current = videos.OrderBy(video => video.OrderNum).Select(video => video.VideoId).ToList();
        return current.SequenceEqual(desired) ? null : desired;
    }

    /// <summary>已有正片的最大 orderNum 之后，按缺失列表的位置连续编号。第 1 集从 1 开始。</summary>
    internal static int[] AssignOrderNums(int maxExistingOrderNum, int count)
    {
        var orderNums = new int[count];
        for (var index = 0; index < count; index++)
            orderNums[index] = maxExistingOrderNum + index + 1;
        return orderNums;
    }

    internal readonly record struct AddVideoResult(bool Succeeded, bool Retryable, string Error);

    /// <summary>HTTP 5xx 和网络失败可以重试；业务码拒绝不行。</summary>
    internal static AddVideoResult InspectAddVideoResponse(int status, string? body)
    {
        var text = body ?? "";
        var retryable = status == 0 || status >= 500;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt64(out var value)
                ? value
                : -1;
            if (status is >= 200 and < 300 && code == 0)
                return new AddVideoResult(true, false, "");
            var message = ReadString(root, "message");
            return new AddVideoResult(false, retryable, $"失败（HTTP {status}，code {code}）：{Truncate(message)}");
        }
        catch (JsonException)
        {
            return new AddVideoResult(false, retryable, $"返回无法解析（HTTP {status}）：{Truncate(text)}");
        }
    }

    /// <summary>每个集号只保留第一条。后面的重复条，以及读不出集号的条，返回它们的视频 ID。</summary>
    internal static IReadOnlyList<string> PlanDeleteExtras(IReadOnlyList<SeriesVideo> videos)
    {
        var keep = SelectUsableExisting(videos).Select(video => video.VideoId).ToHashSet(StringComparer.Ordinal);
        return videos
            .OrderBy(video => video.OrderNum)
            .Where(video => !keep.Contains(video.VideoId))
            .Select(video => video.VideoId)
            .ToList();
    }

    /// <summary>
    /// 正片条数等于总集数，且 1 到总集数每个集号恰好一条时返回 null。
    /// 否则返回要写进失败原因的说明，点名缺少、重复、超出总集数或读不出集号的行。
    /// </summary>
    internal static string? DescribeEpisodeMismatch(IReadOnlyList<SeriesVideo> videos, int expectedCount)
    {
        if (expectedCount <= 0)
            return "接口直传：本地总集数无效，已停止，不会提交。";

        var counts = new Dictionary<int, int>();
        var unreadable = 0;
        foreach (var video in videos)
        {
            if (video.Episode is int episode)
                counts[episode] = counts.GetValueOrDefault(episode) + 1;
            else
                unreadable++;
        }

        var missing = new List<int>();
        var duplicated = new List<int>();
        for (var episode = 1; episode <= expectedCount; episode++)
        {
            var count = counts.GetValueOrDefault(episode);
            if (count == 0)
                missing.Add(episode);
            else if (count > 1)
                duplicated.Add(episode);
        }

        var outside = counts.Keys
            .Where(episode => episode < 1 || episode > expectedCount)
            .OrderBy(episode => episode)
            .ToList();
        if (videos.Count == expectedCount &&
            missing.Count == 0 &&
            duplicated.Count == 0 &&
            outside.Count == 0 &&
            unreadable == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (missing.Count > 0)
            parts.Add($"缺少第 {FormatEpisodes(missing)} 集");
        if (duplicated.Count > 0)
            parts.Add($"第 {FormatEpisodes(duplicated)} 集重复");
        if (outside.Count > 0)
            parts.Add($"集号超出总集数：第 {FormatEpisodes(outside)} 集");
        if (unreadable > 0)
            parts.Add($"{unreadable} 行读不出集号");
        if (parts.Count == 0)
            parts.Add($"正片 {videos.Count} 条，总集数 {expectedCount}");

        return $"接口直传：正片和总集数不一致（正片 {videos.Count} 条，总集数 {expectedCount}），已停止，不会提交。{string.Join("；", parts)}。";
    }

    internal static string BuildBatchDeleteBody(string seriesId, IReadOnlyList<string> videoIds) =>
        JsonSerializer.Serialize(new { contractId = seriesId, videoIds });

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
    /// 按剧集详情里已经挂上的集号，先删掉重复和读不出集号的正片，再只直传缺少的集，最后按集号排序。
    /// 删除失败、删完仍在、或正片不是 1 到总集数各一条，都直接失败，不改走网页，也不提交。
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
        var local = ResolveLocalEpisodes(uploadPaths)
                    ?? throw new InvalidOperationException("接口直传：本地视频文件名里有读不出集号或重复的集。");

        var existing = await ReadSeriesVideosAsync(page, seriesId, ct, log);
        var changed = false;
        var extras = PlanDeleteExtras(existing);
        if (extras.Count > 0)
        {
            await DeleteExtraVideosAsync(page, seriesId, extras, options, log, ct);
            var afterDelete = existing;
            var stillThere = extras.ToList();
            for (var attempt = 1; attempt <= 3 && stillThere.Count > 0; attempt++)
            {
                if (attempt > 1)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                afterDelete = await ReadSeriesVideosAsync(page, seriesId, ct, log);
                stillThere = extras
                    .Where(id => afterDelete.Any(video => string.Equals(video.VideoId, id, StringComparison.Ordinal)))
                    .ToList();
            }

            if (stillThere.Count > 0)
            {
                var preview = string.Join(", ", stillThere.Take(5));
                if (stillThere.Count > 5)
                    preview += $" 等 {stillThere.Count} 条";
                throw new InvalidOperationException(
                    $"接口直传：删除后这些视频仍在草稿里（{preview}），已停止，不再补传。");
            }

            log?.Invoke($"接口直传：已删除 {extras.Count} 条多余正片，草稿还剩 {afterDelete.Count} 条。");
            existing = afterDelete;
            changed = true;
        }

        var usable = SelectUsableExisting(existing);

        var present = usable.Select(video => video.Episode!.Value).ToHashSet();
        var missing = local.Where(item => !present.Contains(item.Episode)).ToList();
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
            var sts = new StsCache(page, Math.Clamp(options.UploadBatchMaxRetries, 1, 10), log);
            using var gates = new UploadGates(concurrency);
            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var uploaded = 0;
            long uploadedBytes = 0;
            var uploadClock = Stopwatch.StartNew();
            var maxOrder = usable.Count == 0 ? 0 : usable.Max(video => video.OrderNum);
            var orderNums = AssignOrderNums(maxOrder, missing.Count);
            // 上传按集号从小到大领任务。挂载凑满 concurrency 集就在页面里一批发出，不等前面的集。
            var uploadedVideos = new CommittedVideo[missing.Count];
            var ready = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
            var settled = 0;
            var failureLock = new object();
            void MarkUploadSettled()
            {
                if (Interlocked.Increment(ref settled) == missing.Count)
                {
                    uploadClock.Stop();
                    ready.Writer.TryComplete();
                }
            }

            var nextIndex = 0;
            // 传完等提交的集不占传输名额，所以多开一倍任务，让下一集趁这时开始传。
            var workers = Enumerable.Range(0, concurrency * 2)
                .Select(_ => Task.Run(async () =>
                {
                    while (true)
                    {
                        if (uploadCts.IsCancellationRequested)
                            return;
                        var index = Interlocked.Increment(ref nextIndex) - 1;
                        if (index >= missing.Count)
                            return;

                        var item = missing[index];
                        try
                        {
                            var committed = await UploadWithRetryAsync(
                                http, sts, gates, page.Url, item, options, missing.Count,
                                () => Interlocked.Increment(ref uploaded),
                                bytes => Interlocked.Add(ref uploadedBytes, bytes),
                                log, uploadCts.Token);
                            uploadedVideos[index] = committed;
                            ready.Writer.TryWrite(index);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException &&
                                                   (ct.IsCancellationRequested || uploadCts.IsCancellationRequested))
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            log?.Invoke($"接口直传：第 {item.Episode} 集上传失败：{ex.Message}");
                            lock (failureLock)
                                failures.Add($"第 {item.Episode} 集（{ex.Message}）");
                        }

                        MarkUploadSettled();
                    }
                }))
                .ToArray();

            var mounted = 0;
            var batch = new List<int>(concurrency);
            try
            {
                while (await ready.Reader.WaitToReadAsync(ct))
                {
                    while (batch.Count < concurrency && ready.Reader.TryRead(out var index))
                        batch.Add(index);
                    if (batch.Count < concurrency)
                        continue;

                    var indexes = batch.ToList();
                    batch.Clear();
                    await MountBatchAsync(
                        page, seriesId, missing, orderNums, uploadedVideos, indexes,
                        () => ++mounted, () => changed = true, failures, failureLock, options, log, ct);
                }

                if (batch.Count > 0)
                {
                    await MountBatchAsync(
                        page, seriesId, missing, orderNums, uploadedVideos, batch,
                        () => ++mounted, () => changed = true, failures, failureLock, options, log, ct);
                }
            }
            catch
            {
                uploadCts.Cancel();
                if (changed)
                    await ReloadEditPageAsync(page, ct);
                throw;
            }
            finally
            {
                try { await Task.WhenAll(workers); }
                catch { /* 单集失败已经记在 failures 里 */ }
            }

            if (uploaded > 0)
                log?.Invoke(FormatUploadSummaryLog(uploaded, Interlocked.Read(ref uploadedBytes), uploadClock.Elapsed));
        }

        var after = await ReadSeriesVideosAsync(page, seriesId, ct, log);
        if (PlanReorder(after) is { Count: > 0 } ordered)
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

        if (DescribeEpisodeMismatch(after, local.Count) is { } mismatch)
            throw new InvalidOperationException(mismatch);

        log?.Invoke($"接口直传完成：草稿正片 {after.Count} 集。");
        return true;
    }

    /// <summary>提交前再读一次剧集详情。对不上就失败，调用方不要点提交。</summary>
    public static async Task EnsureReadyToSubmitAsync(
        IPage page,
        IReadOnlyList<string> uploadPaths,
        Action<string>? log,
        CancellationToken ct)
    {
        var local = ResolveLocalEpisodes(uploadPaths)
                    ?? throw new InvalidOperationException("接口直传：本地视频文件名里有读不出集号或重复的集，已停止，不会提交。");
        var seriesId = TryReadSeriesIdFromUrl(page.Url)
                       ?? throw new InvalidOperationException("接口直传：当前页面地址里没有剧集 ID，已停止，不会提交。");
        var videos = await ReadSeriesVideosAsync(page, seriesId, ct, log);
        if (DescribeEpisodeMismatch(videos, local.Count) is { } mismatch)
            throw new InvalidOperationException(mismatch);

        log?.Invoke($"接口直传：提交前核对通过，正片 {videos.Count} 集与总集数一致。");
    }

    /// <summary>
    /// 新建页还没有剧集 ID：先按剧名查已有草稿。查到就用那条剧集 ID 继续上传；
    /// 没有再保存剧壳，从创建接口、页面地址或保存后的列表取得剧集 ID，
    /// 再转到草稿编辑页，全部正片都由接口直传。
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
        if (ResolveLocalEpisodes(uploadPaths) is null)
            throw new InvalidOperationException("接口直传：本地视频文件名里有读不出集号或重复的集。");

        log?.Invoke("接口直传：先按剧名查找已有草稿，拿到剧集 ID 后再上传全部正片。");
        var seriesId = await SaveShellForSeriesIdAsync(page, payload.Title, log, ct);
        var draftUrl = $"https://{new Uri(page.Url).Host}/series/draft/{seriesId}";
        log?.Invoke($"接口直传：已取得剧集 ID {seriesId}，转到草稿编辑页继续上传：{draftUrl}");
        await TikTokEditFlowService.OpenEditPublishFlowAsync(
            page, draftUrl, payload, options, recommendation, coverPath, log, ct);
    }

    private static async Task<string> SaveShellForSeriesIdAsync(
        IPage page,
        string? title,
        Action<string>? log,
        CancellationToken ct)
    {
        var existing = TryReadSeriesIdFromUrl(page.Url);
        if (existing is not null)
            return existing;

        var listed = await TryReadExistingDraftIdAsync(page, title, log, ct);
        if (listed is not null)
        {
            log?.Invoke($"接口直传：平台已有同名草稿，直接使用剧集 ID {listed}。");
            return listed;
        }

        log?.Invoke("接口直传：新建页先保存剧壳，取得剧集 ID 后再上传全部正片。");
        IResponse? response = null;
        try
        {
            response = await page.RunAndWaitForResponseAsync(
                async () =>
                {
                    var button = page.Locator("button").Filter(new() { HasText = "保存" }).First;
                    await button.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
                    await button.ClickAsync(new() { Timeout = 15000 });
                },
                IsSeriesShellResponse,
                new PageRunAndWaitForResponseOptions { Timeout = 20000 });
        }
        catch (TimeoutException)
        {
            log?.Invoke("接口直传：保存剧壳没有等到创建接口，改为看页面地址。");
        }

        string? seriesId = null;
        if (response is not null)
        {
            var text = "";
            try { text = await response.TextAsync(); }
            catch { /* 下面再从地址里找 */ }
            seriesId = ReadSeriesIdFromJson(text) ?? TryReadSeriesIdFromUrl(response.Url);
        }

        seriesId ??= await WaitForSeriesIdInUrlAsync(page, ct);
        if (seriesId is null)
        {
            seriesId = await TryReadExistingDraftIdAsync(page, title, log, ct);
            if (seriesId is not null)
                log?.Invoke($"接口直传：保存剧壳后从剧集列表取得剧集 ID {seriesId}。");
        }

        return seriesId ?? throw new InvalidOperationException(
            "接口直传：保存剧壳后没有拿到剧集 ID，已停止，不会改用网页上传。");
    }

    private static async Task<string?> TryReadExistingDraftIdAsync(
        IPage page,
        string? title,
        Action<string>? log,
        CancellationToken ct)
    {
        var normalized = title?.Trim() ?? "";
        if (normalized.Length == 0)
            return null;

        var rows = await TikTokSeriesListLookupService.SearchExactAsync(page, normalized, ct, log);
        var lookup = FindDraftSeries(rows);
        var ids = lookup.SelectIds();
        if (ids.Count > 1)
        {
            throw new InvalidOperationException(
                $"接口直传：同名草稿有 {ids.Count} 个（{string.Join("、", ids)}），已停止，不会改用网页上传。");
        }

        return ids.Count == 1 ? ids[0] : null;
    }

    private static bool IsSeriesShellResponse(IResponse candidate)
    {
        if (!string.Equals(candidate.Request.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase))
            return false;
        var url = candidate.Url;
        return url.Contains("/api/content-partner/series", StringComparison.OrdinalIgnoreCase) &&
               !url.Contains("/videos/", StringComparison.OrdinalIgnoreCase) &&
               !url.Contains("/list", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> WaitForSeriesIdInUrlAsync(IPage page, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var seriesId = TryReadSeriesIdFromUrl(page.Url);
            if (seriesId is not null)
                return seriesId;
            await page.WaitForTimeoutAsync(500);
        }

        return TryReadSeriesIdFromUrl(page.Url);
    }

    internal static string? ReadSeriesIdFromJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return FindSeriesId(document.RootElement, 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindSeriesId(JsonElement element, int depth)
    {
        if (depth > 8)
            return null;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindSeriesId(item, depth + 1);
                if (found is not null)
                    return found;
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in new[] { "contractId", "seriesId", "dramaId", "id" })
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            var raw = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
            if (raw is not null && SeriesIdValuePattern.IsMatch(raw.Trim()))
                return raw.Trim();
        }

        foreach (var property in element.EnumerateObject())
        {
            var found = FindSeriesId(property.Value, depth + 1);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// 剧集列表里同名行的归类。草稿状态优先；只有明确的非草稿时不拿来继续上传；
    /// 列表没带状态时，按调用方的筛选结果使用。
    /// </summary>
    internal sealed record DraftSeriesLookup(
        IReadOnlyList<string> DraftIds,
        IReadOnlyList<string> OtherIds,
        IReadOnlyList<string> UntaggedIds)
    {
        public static DraftSeriesLookup Empty { get; } = new([], [], []);

        public IReadOnlyList<string> SelectIds()
        {
            if (DraftIds.Count > 0)
                return DraftIds;
            if (OtherIds.Count > 0)
                return [];
            return UntaggedIds;
        }
    }

    internal static DraftSeriesLookup FindDraftSeries(string? json, string? title)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(title))
            return DraftSeriesLookup.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindDraftSeries(document.RootElement, title);
        }
        catch (JsonException)
        {
            return DraftSeriesLookup.Empty;
        }
    }

    internal static DraftSeriesLookup FindDraftSeries(JsonElement root, string title) =>
        FindDraftSeries(TikTokSeriesListLookupService.ParseSeriesList(root).Rows, title);

    private static DraftSeriesLookup FindDraftSeries(IReadOnlyList<TikTokSeriesListRow> rows, string? title = null)
    {
        var wanted = title?.Trim();
        var drafts = new List<string>();
        var others = new List<string>();
        var untagged = new List<string>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SeriesId))
                continue;
            if (!string.IsNullOrEmpty(wanted) &&
                !string.Equals(row.Title.Trim(), wanted, StringComparison.Ordinal))
                continue;
            if (IsDraftLabel(row.PlatformStatus))
                drafts.Add(row.SeriesId);
            else if (string.IsNullOrWhiteSpace(row.PlatformStatus))
                untagged.Add(row.SeriesId);
            else
                others.Add(row.SeriesId);
        }

        return new DraftSeriesLookup(DistinctIds(drafts), DistinctIds(others), DistinctIds(untagged));
    }

    private static bool IsDraftLabel(string? status) =>
        !string.IsNullOrWhiteSpace(status) &&
        (status.Contains("草稿", StringComparison.Ordinal) ||
         status.Equals("Draft", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("InDraft", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> DistinctIds(List<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in ids)
        {
            if (seen.Add(id))
                result.Add(id);
        }

        return result;
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
            var videoId = ReadId(video, "videoId");
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

    /// <summary>刚由挂集请求建出来的剧集，详情接口会先返回一阵 HTTP 500，要等后台落库。</summary>
    private static async Task<IReadOnlyList<SeriesVideo>> ReadSeriesVideosAsync(
        IPage page,
        string seriesId,
        CancellationToken ct,
        Action<string>? log = null)
    {
        const int attempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var document = await PageFetchAsync(page, "GET", $"/api/content-partner/series/{seriesId}", null, ct);
                return document.RootElement.TryGetProperty("data", out var data)
                    ? ParseSeriesVideos(data)
                    : Array.Empty<SeriesVideo>();
            }
            catch (PageFetchException ex) when (ex.Status >= 500 && attempt < attempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(attempt * 2, 10));
                log?.Invoke($"接口直传：读取剧集详情暂时失败（HTTP {ex.Status}），{delay.TotalSeconds:0} 秒后重试（{attempt}/{attempts - 1}）。");
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// 剧集中心批量删除：POST /api/content-partner/series/videos/batch-delete，
    /// 请求体是 contractId 和 videoIds。失败就停，不再往上面叠传。
    /// </summary>
    private static async Task DeleteExtraVideosAsync(
        IPage page,
        string seriesId,
        IReadOnlyList<string> videoIds,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        log?.Invoke($"接口直传：草稿有 {videoIds.Count} 条重复或读不出集号的正片，先用接口删除。");
        var body = BuildBatchDeleteBody(seriesId, videoIds);
        try
        {
            using var response = await ExecuteWithRetryAsync(
                options.UploadBatchMaxRetries,
                "删除多余正片",
                log,
                token => PageFetchAsync(page, "POST", DeleteVideosPath, body, token),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"接口直传：删除多余正片失败，已停止，不再补传。{ex.Message}", ex);
        }
    }

    private sealed class PageFetchException(string message, int status) : InvalidOperationException(message)
    {
        public int Status { get; } = status;

        public bool IsTransient => IsTransientHttpStatus(Status);
    }

    private sealed class TransientUploadException(string message) : InvalidOperationException(message);

    internal static bool IsTransientHttpStatus(int status) =>
        status is 0 or 408 or 429 || status >= 500;

    private static bool IsTransientFailure(Exception ex) =>
        ex is TransientUploadException or HttpRequestException ||
        (ex is PageFetchException fetch && fetch.IsTransient);

    private static bool ShouldRetry(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && (IsTransientFailure(ex) || ex is TaskCanceledException);

    private static async Task<T> ExecuteWithRetryAsync<T>(
        int retries,
        string step,
        Action<string>? log,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        var attempts = Math.Clamp(retries, 1, 10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (Exception ex) when (ShouldRetry(ex, ct) && attempt < attempts)
            {
                var delay = TimeSpan.FromSeconds(attempt * 3);
                log?.Invoke($"接口直传：{step}暂时失败（{ex.Message}），{delay.TotalSeconds:0} 秒后重试（{attempt}/{attempts - 1}）。");
                await Task.Delay(delay, ct);
            }
        }
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
            throw new PageFetchException($"{path} 返回无法解析（HTTP {status}）：{Truncate(text)}", status);
        }

        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt64(out var value)
            ? value
            : -1;
        if (status is < 200 or >= 300 || code != 0)
        {
            var message = ReadString(root, "message");
            document.Dispose();
            throw new PageFetchException($"{path} 失败（HTTP {status}，code {code}）：{Truncate(message)}", status);
        }

        return document;
    }

    private static async Task MountBatchAsync(
        IPage page,
        string seriesId,
        IReadOnlyList<LocalEpisode> missing,
        IReadOnlyList<int> orderNums,
        IReadOnlyList<CommittedVideo> uploadedVideos,
        IReadOnlyList<int> indexes,
        Func<int> markMounted,
        Action markChanged,
        List<string> failures,
        object failureLock,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        var maxAttempts = Math.Clamp(options.UploadBatchMaxRetries, 1, 10);
        var pending = indexes.ToList();
        for (var attempt = 1; pending.Count > 0; attempt++)
        {
            var bodies = pending
                .Select(index => BuildAddVideoBody(
                    seriesId, uploadedVideos[index], missing[index].Title, orderNums[index]))
                .ToArray();
            var replies = await PostJsonManyAsync(page, AddVideoPath, bodies, ct);
            var retry = new List<int>();
            for (var i = 0; i < pending.Count; i++)
            {
                var index = pending[i];
                var episode = missing[index].Episode;
                var result = InspectAddVideoResponse(replies[i].Status, replies[i].Text);
                if (result.Succeeded)
                {
                    markChanged();
                    log?.Invoke(FormatMountedLog(episode, markMounted(), missing.Count));
                    continue;
                }

                if (result.Retryable && attempt < maxAttempts)
                {
                    var delaySeconds = attempt * 2;
                    log?.Invoke(
                        $"接口直传：第 {episode} 集挂载暂时失败（{result.Error}），{delaySeconds} 秒后重试（{attempt}/{maxAttempts - 1}）。");
                    retry.Add(index);
                    continue;
                }

                log?.Invoke($"接口直传：第 {episode} 集挂载失败：{result.Error}");
                lock (failureLock)
                    failures.Add($"第 {episode} 集（{result.Error}）");
            }

            pending = retry;
            if (pending.Count > 0)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
        }
    }

    private readonly record struct FetchReply(int Status, string Text);

    private static async Task<IReadOnlyList<FetchReply>> PostJsonManyAsync(
        IPage page,
        string path,
        IReadOnlyList<string> bodies,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = await page.EvaluateAsync<JsonElement>(
            """
            async ([path, bodies]) => {
              const replies = await Promise.allSettled(bodies.map(async (body) => {
                const res = await fetch(path, {
                  method: 'POST',
                  credentials: 'include',
                  headers: { 'content-type': 'application/json' },
                  body,
                });
                return { status: res.status, text: await res.text() };
              }));
              return replies.map((reply) => {
                if (reply.status === 'fulfilled') return reply.value;
                const reason = reply.reason;
                const message = reason && reason.message ? String(reason.message) : String(reason);
                return { status: 0, text: message };
              });
            }
            """,
            new object[] { path, bodies.ToArray() });

        var replies = new List<FetchReply>(bodies.Count);
        if (results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                replies.Add(new FetchReply(
                    item.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) ? code : 0,
                    item.TryGetProperty("text", out var text) ? text.GetString() ?? "" : ""));
            }
        }

        if (replies.Count != bodies.Count)
            throw new InvalidOperationException($"{path} 批量返回 {replies.Count} 条，期望 {bodies.Count} 条。");
        return replies;
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
        UploadGates gates,
        string pageUrl,
        LocalEpisode item,
        TikTokPublishOptions options,
        int total,
        Func<int> markUploaded,
        Action<long> addUploadedBytes,
        Action<string>? log,
        CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var retries = Math.Clamp(options.UploadBatchMaxRetries, 1, 10);
        var (committed, timing) = await UploadFileAsync(
            http, sts, gates, pageUrl, item.Path, log, item.Episode, retries, ct);
        addUploadedBytes(timing.Bytes);
        log?.Invoke(FormatUploadedLog(item.Episode, started.Elapsed, timing, markUploaded(), total));
        return committed;
    }

    internal static string FormatUploadedLog(int episode, TimeSpan elapsed, UploadTiming timing, int uploaded, int total) =>
        $"接口直传：第 {episode} 集已上传（{FormatElapsed(elapsed)}，{FormatSize(timing.Bytes)}，" +
        $"{FormatSpeed(timing.Bytes, timing.Transfer)}；传输 {FormatElapsed(timing.Transfer)}，" +
        $"提交 {FormatElapsed(timing.Commit)}，已上传 {uploaded}/{total}）。";

    internal static string FormatUploadSummaryLog(int uploaded, long bytes, TimeSpan elapsed) =>
        $"接口直传：{uploaded} 集共 {FormatSize(bytes)}，上传用时 {FormatElapsed(elapsed)}，平均 {FormatSpeed(bytes, elapsed)}。";

    private static string FormatSize(long bytes)
    {
        var mb = bytes / 1024d / 1024d;
        return mb >= 1024
            ? (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + "GB"
            : mb.ToString("0.0", CultureInfo.InvariantCulture) + "MB";
    }

    private static string FormatSpeed(long bytes, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        return (bytes / 1024d / 1024d / seconds).ToString("0.0", CultureInfo.InvariantCulture) + "MB/s";
    }

    internal static string FormatMountedLog(int episode, int mounted, int total) =>
        $"接口直传：第 {episode} 集已挂到剧集（已挂载 {mounted}/{total}）。";

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(1, (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
        if (seconds < 60)
            return $"{seconds} 秒";
        return $"{seconds / 60} 分 {seconds % 60} 秒";
    }

    private static async Task<(CommittedVideo Video, UploadTiming Timing)> UploadFileAsync(
        HttpClient http,
        StsCache sts,
        UploadGates gates,
        string pageUrl,
        string path,
        Action<string>? log,
        int episode,
        int retries,
        CancellationToken ct)
    {
        var fileSize = new FileInfo(path).Length;
        if (fileSize <= 0)
            throw new InvalidOperationException($"视频文件为空：{path}");

        var vodBase = ResolveServiceBaseUrl(pageUrl, "vod");
        var tosBase = ResolveServiceBaseUrl(pageUrl, "tos");
        string sessionKey;
        TimeSpan transferElapsed;
        await gates.Transfer.WaitAsync(ct);
        try
        {
            var transferClock = Stopwatch.StartNew();
            sessionKey = await TransferFileAsync(
                http, sts, gates, vodBase, tosBase, path, fileSize, log, episode, retries, ct);
            transferElapsed = transferClock.Elapsed;
        }
        finally
        {
            gates.Transfer.Release();
        }

        var commitClock = Stopwatch.StartNew();
        var video = await CommitFileAsync(http, sts, vodBase, sessionKey, log, episode, retries, ct);
        return (video, new UploadTiming(fileSize, transferElapsed, commitClock.Elapsed));
    }

    /// <summary>申请地址并把文件分片传到 TOS，返回提交用的 SessionKey。同一集最多 <see cref="PartsInFlightPerFile"/> 片同时在传。</summary>
    private static async Task<string> TransferFileAsync(
        HttpClient http,
        StsCache sts,
        UploadGates gates,
        string vodBase,
        string tosBase,
        string path,
        long fileSize,
        Action<string>? log,
        int episode,
        int retries,
        CancellationToken ct)
    {
        var credentials = await sts.GetAsync(ct);

        var applyUri = new Uri(
            $"{vodBase}/?Action=ApplyUploadInner&Version={ApiVersion}&SpaceName={SpaceName}" +
            $"&FileType=video&IsInner=1&FileSize={fileSize}&s={RandomToken()}&device_platform=web");
        using var applyDocument = await ExecuteWithRetryAsync(
            retries,
            $"第 {episode} 集初始化上传",
            log,
            token => SendVodAsync(http, HttpMethod.Get, applyUri, credentials, null, token),
            ct);
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
        using var initDocument = await ExecuteWithRetryAsync(
            retries,
            $"第 {episode} 集分片初始化",
            log,
            async token =>
            {
                using var init = new HttpRequestMessage(HttpMethod.Post, $"{objectUrl}?uploadmode=part&phase=init");
                init.Headers.TryAddWithoutValidation("Authorization", auth);
                init.Content = new StringContent($"--{boundary}--\r\n", Encoding.ASCII);
                init.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
                return await SendTosAsync(http, init, token);
            },
            ct);
        var uploadId = ReadString(initDocument.RootElement.GetProperty("data"), "uploadid");
        if (uploadId.Length == 0)
            throw new InvalidOperationException("TOS 初始化没有返回 uploadid。");

        var crcs = await SendPartsAsync(
            http, gates, objectUrl, uploadId, auth, path, fileSize, log, episode, retries, ct);

        using (await ExecuteWithRetryAsync(
            retries,
            $"第 {episode} 集分片提交",
            log,
            async token =>
            {
                using var finish = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{objectUrl}?uploadmode=part&phase=finish&uploadid={uploadId}&size={fileSize}");
                finish.Headers.TryAddWithoutValidation("Authorization", auth);
                finish.Content = new StringContent(BuildFinishBody(crcs), Encoding.UTF8);
                finish.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain;charset=UTF-8");
                return await SendTosAsync(http, finish, token);
            },
            ct)) { }

        return sessionKey;
    }

    private static async Task<IReadOnlyList<string>> SendPartsAsync(
        HttpClient http,
        UploadGates gates,
        string objectUrl,
        string uploadId,
        string auth,
        string path,
        long fileSize,
        Action<string>? log,
        int episode,
        int retries,
        CancellationToken ct)
    {
        var partCount = (int)((fileSize + PartSize - 1) / PartSize);
        var crcs = new string[partCount];
        using var partsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var perFile = new SemaphoreSlim(PartsInFlightPerFile, PartsInFlightPerFile);
        var sending = new List<Task>(partCount);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            long offset = 0;
            for (var index = 0; index < partCount; index++)
            {
                await perFile.WaitAsync(partsCts.Token);
                try
                {
                    await gates.Parts.WaitAsync(partsCts.Token);
                }
                catch
                {
                    perFile.Release();
                    throw;
                }

                var buffer = ArrayPool<byte>.Shared.Rent(PartSize);
                int length;
                try
                {
                    length = await ReadChunkAsync(stream, buffer.AsMemory(0, PartSize), partsCts.Token);
                    if (length == 0)
                        throw new InvalidOperationException($"视频文件读取不完整：{path}");
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    gates.Parts.Release();
                    perFile.Release();
                    throw;
                }

                var partIndex = index;
                var partOffset = offset;
                offset += length;
                sending.Add(Task.Run(async () =>
                {
                    try
                    {
                        var crc = TikTokCrc32.Hex(buffer.AsSpan(0, length));
                        await ExecuteWithRetryAsync(
                            retries,
                            $"第 {episode} 集分片 {partIndex + 1}",
                            log,
                            async token =>
                            {
                                using var part = new HttpRequestMessage(
                                    HttpMethod.Post,
                                    $"{objectUrl}?uploadid={uploadId}&part_number={partIndex + 1}&phase=transfer&part_offset={partOffset}");
                                part.Headers.TryAddWithoutValidation("Authorization", auth);
                                part.Content = new ByteArrayContent(buffer, 0, length);
                                part.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                                part.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename=\"undefined\"");
                                part.Content.Headers.TryAddWithoutValidation("Content-CRC32", crc);
                                using (await SendTosAsync(http, part, token)) { }
                                return 0;
                            },
                            partsCts.Token);
                        crcs[partIndex] = crc;
                    }
                    catch
                    {
                        partsCts.Cancel();
                        throw;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        gates.Parts.Release();
                        perFile.Release();
                    }
                }, CancellationToken.None));
            }

            await Task.WhenAll(sending);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 某一片失败后取消了其余分片：把真正的失败原因抛出去。
            try { await Task.WhenAll(sending); }
            catch { /* 下面挑出失败的那一片 */ }
            var failed = sending.FirstOrDefault(task => task.IsFaulted);
            if (failed is not null)
                await failed;
            throw;
        }
        finally
        {
            try { await Task.WhenAll(sending); }
            catch { /* 失败原因已在上面抛出 */ }
        }

        return crcs;
    }

    private static async Task<CommittedVideo> CommitFileAsync(
        HttpClient http,
        StsCache sts,
        string vodBase,
        string sessionKey,
        Action<string>? log,
        int episode,
        int retries,
        CancellationToken ct)
    {
        var commitBody = JsonSerializer.Serialize(new
        {
            SessionKey = sessionKey,
            Functions = new object[] { new { name = "Snapshot", input = new { SnapshotTime = 0 } } },
        });
        var commitUri = new Uri($"{vodBase}/?Action=CommitUploadInner&Version={ApiVersion}&SpaceName={SpaceName}");
        using var commitDocument = await ExecuteWithRetryAsync(
            retries,
            $"第 {episode} 集提交视频",
            log,
            async token => await SendVodAsync(
                http, HttpMethod.Post, commitUri, await sts.GetAsync(token), commitBody, token),
            ct);
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

    private static async Task<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
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
            var statusCode = (int)response.StatusCode;
            var message = $"{action} 返回无法解析（HTTP {statusCode}）：{Truncate(text)}";
            if (IsTransientHttpStatus(statusCode))
                throw new TransientUploadException(message);
            throw new InvalidOperationException(message);
        }

        var status = (int)response.StatusCode;
        if (IsTransientHttpStatus(status))
        {
            document.Dispose();
            throw new TransientUploadException($"{action} 失败（HTTP {status}）：{Truncate(text)}");
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
            throw new InvalidOperationException($"{action} 失败（HTTP {status}）：{Truncate(text)}");
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
            var statusCode = (int)response.StatusCode;
            var message = $"TOS 返回无法解析（HTTP {statusCode}）：{Truncate(text)}";
            if (IsTransientHttpStatus(statusCode))
                throw new TransientUploadException(message);
            throw new InvalidOperationException(message);
        }

        var status = (int)response.StatusCode;
        var code = document.RootElement.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value)
            ? value
            : -1;
        if (IsTransientHttpStatus(status))
        {
            var message = ReadString(document.RootElement, "message");
            document.Dispose();
            throw new TransientUploadException($"TOS 上传失败（HTTP {status}，code {code}）：{Truncate(message)}");
        }

        if (!response.IsSuccessStatusCode || code != 2000)
        {
            var message = ReadString(document.RootElement, "message");
            document.Dispose();
            throw new InvalidOperationException($"TOS 上传失败（HTTP {status}，code {code}）：{Truncate(message)}");
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

    private sealed class StsCache(IPage page, int retries, Action<string>? log)
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

                var attempts = Math.Clamp(retries, 1, 10);
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
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
                    catch (Exception ex) when (ShouldRetry(ex, ct) && attempt < attempts)
                    {
                        var delay = TimeSpan.FromSeconds(attempt * 3);
                        log?.Invoke($"接口直传：上传授权暂时失败（{ex.Message}），{delay.TotalSeconds:0} 秒后重试（{attempt}/{attempts - 1}）。");
                        await Task.Delay(delay, ct);
                    }
                }
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

    private static string ReadId(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

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
