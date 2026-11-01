using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 红果 APP 下载的搜索、剧集和播放地址。搜索与剧集用进程内 PureSign，播放地址用匿名注册。
/// </summary>
internal sealed class HongguoAppDirectClient
{
    private readonly HttpClient _httpClient;
    private readonly HongguoGuestPlayback _playback;

    internal HongguoAppDirectClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _playback = new HongguoGuestPlayback(httpClient);
    }

    internal async Task<IReadOnlyList<DramaSearchItem>> SearchAsync(
        string keyword,
        int page,
        CancellationToken cancellationToken,
        string? searchCategory = null)
    {
        var trimmed = (keyword ?? "").Trim();
        if (trimmed.Length == 0)
            return [];
        if (IsBookIdQuery(trimmed, searchCategory))
        {
            return page > 1
                ? []
                : [new DramaSearchItem(trimmed, trimmed, "", 0, "", "")];
        }

        var offset = Math.Max(0, (Math.Max(1, page) - 1) * 10);
        var extra = new KeyValuePair<string, string>[]
        {
            new("query", trimmed),
            new("tab_name", "feed"),
            new("search_source", "1"),
            new("offset", offset.ToString()),
            new("count", "10"),
            new("use_correct", "true"),
        };
        JsonNode root;
        try
        {
            root = await SendAsync(HttpMethod.Get, "/reading/bookapi/search/tab/v", null, extra, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HongguoHighException("红果搜索失败", 0, ex);
        }

        var results = new List<DramaSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in SearchCells(root))
        {
            foreach (var item in MapCell(cell))
            {
                if (item.BookId.Length > 0 && seen.Add(item.BookId))
                    results.Add(item);
            }
        }
        return results;
    }

    internal async Task<IReadOnlyList<HongguoNewApiService.HongguoEpisodeInfo>> GetEpisodesAsync(
        string bookId,
        CancellationToken cancellationToken)
    {
        var seriesId = RawSeriesId(bookId);
        if (seriesId.Length == 0)
            return [];

        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var root = await SendAsync(
                    HttpMethod.Post,
                    "/novel/player/multi_video_detail/preload/v1",
                    HongguoPureSign.EpisodesBody(seriesId),
                    null,
                    cancellationToken);
                var episodes = ReadEpisodes(root, seriesId);
                if (episodes.Count > 0)
                    return episodes;
                last = new HongguoHighException("剧集列表为空");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (attempt < 2)
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }

        throw last switch
        {
            HongguoHighException high => high,
            _ => new HongguoHighException("剧集列表获取失败", 0, last)
        };
    }

    internal Task<HongguoHighVideoPlayback> GetVideoPlaybackAsync(
        string videoId,
        string quality,
        CancellationToken cancellationToken)
    {
        if (!HongguoHighCrypto.TryDecodeEpisodeId(videoId, out _, out _, out var rawVideoId))
            throw new HongguoHighException("剧集标识无效");
        return _playback.GetAsync(rawVideoId, quality, cancellationToken);
    }

    internal Task<HongguoGuestPlayback.TrackProbe> ProbePlaybackAsync(string videoId, CancellationToken cancellationToken) =>
        _playback.ProbeAsync(videoId, cancellationToken);

    private async Task<JsonNode> SendAsync(
        HttpMethod method,
        string path,
        byte[]? body,
        IReadOnlyList<KeyValuePair<string, string>>? extra,
        CancellationToken cancellationToken)
    {
        var deviceId = HongguoPureSign.LoadDeviceId(HongguoPureSign.DefaultDevicePath());
        Exception? last = null;
        var rticket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var khronos = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var built = HongguoPureSign.BuildUrl(path, extra, deviceId, rticket);
            var url = built.Url;
            rticket = built.Rticket;
            for (var guard = 0; HongguoPureSign.BranchOf(url, body, khronos) == 1 && guard < 32; guard++)
            {
                rticket++;
                built = HongguoPureSign.BuildUrl(path, extra, deviceId, rticket);
                url = built.Url;
                rticket = built.Rticket;
            }

            using var request = new HttpRequestMessage(method, url);
            foreach (var header in HongguoPureSign.SessionHeaders())
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (body is { Length: > 0 })
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                request.Headers.TryAddWithoutValidation("x-ss-stub", Convert.ToHexString(MD5.HashData(body)));
            }
            else
            {
                request.Headers.TryAddWithoutValidation("content-type", "application/json; charset=utf-8");
            }

            foreach (var header in HongguoPureSign.Sign(url, body, khronos))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0 || bytes.All(b => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t'))
                    throw new InvalidOperationException("empty body");
                var node = JsonNode.Parse(bytes) ?? throw new InvalidOperationException("empty body");
                if (node is JsonObject obj && CodeOf(obj) is not (0 or null))
                    throw new InvalidOperationException("rejected");
                return node;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                rticket++;
                if (attempt < 2)
                    await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            }
        }

        throw last ?? new InvalidOperationException("红果请求失败");
    }

    private static IEnumerable<DramaSearchItem> MapCell(JsonObject cell)
    {
        var mapped = HongguoHighCalendarMapper.ExtractSearchTabItems(cell)
            .Select(HongguoHighCalendarMapper.TryMapItem)
            .OfType<DramaSearchItem>()
            .ToArray();
        if (mapped.Length > 0)
        {
            foreach (var item in mapped)
                yield return item;
            yield break;
        }

        var bookId = Text(cell, "book_id") ?? Text(cell, "search_result_id");
        if (string.IsNullOrWhiteSpace(bookId))
            yield break;
        var video = FirstVideo(cell);
        var detail = cell["video_detail"] as JsonObject ?? video?["video_detail"] as JsonObject;
        var title = Text(cell["search_high_light"]?["title"] as JsonObject, "text")
                    ?? Text(detail, "series_title")
                    ?? Text(video, "title")
                    ?? bookId;
        title = StripTags(title);
        yield return new DramaSearchItem(
            HongguoHighCrypto.EnsureBookPrefix(bookId),
            title.Length == 0 ? bookId : title,
            "",
            Positive(detail, "episode_cnt") is var fromDetail and > 0
                ? fromDetail
                : Positive(video, "episode_cnt"),
            Text(detail, "series_intro") ?? "",
            Text(video, "cover") ?? Text(detail, "series_cover") ?? "");
    }

    private static IEnumerable<JsonObject> SearchCells(JsonNode root)
    {
        if (root["search_tabs"] is JsonArray tabs && tabs.FirstOrDefault() is JsonObject tab && tab["data"] is JsonArray data)
        {
            foreach (var cell in data.OfType<JsonObject>())
                yield return cell;
        }
    }

    private static List<HongguoNewApiService.HongguoEpisodeInfo> ReadEpisodes(JsonNode root, string seriesId)
    {
        var videoData = root["data"]?[seriesId]?["video_data"] as JsonObject;
        var list = videoData?["video_list"];
        var rows = new List<JsonObject>();
        if (list is JsonArray array)
            rows.AddRange(array.OfType<JsonObject>());
        else if (list is JsonObject obj)
            rows.AddRange(obj.Select(pair => pair.Value).OfType<JsonObject>());
        if (rows.Count == 0)
            return [];

        var episodes = new List<HongguoNewApiService.HongguoEpisodeInfo>();
        var fallback = 0;
        foreach (var row in rows.OrderBy(IndexOf))
        {
            var videoId = Text(row, "vid");
            if (string.IsNullOrWhiteSpace(videoId))
                continue;
            fallback++;
            var number = IndexOf(row);
            if (number <= 0)
                number = fallback;
            var title = Text(row, "title");
            if (string.IsNullOrWhiteSpace(title))
                title = $"第{number}集";
            episodes.Add(new HongguoNewApiService.HongguoEpisodeInfo(
                number,
                title,
                HongguoHighCrypto.EncodeEpisodeId(seriesId, number, videoId),
                Text(row, "episode_cover") ?? Text(row, "cover") ?? ""));
        }
        return episodes;
    }

    private static int IndexOf(JsonObject row)
    {
        if (row["vid_index"] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static JsonObject? FirstVideo(JsonObject cell)
    {
        var video = cell["video_data"];
        if (video is JsonArray array)
            return array.OfType<JsonObject>().FirstOrDefault();
        return video as JsonObject;
    }

    private static bool IsBookIdQuery(string keyword, string? searchCategory)
    {
        var digits = keyword.All(char.IsDigit);
        if (string.Equals(searchCategory?.Trim(), DramaSearchCategory.BookId, StringComparison.Ordinal))
            return digits && keyword.Length > 0;
        return digits && keyword.Length is >= 10 and <= 20;
    }

    private static string RawSeriesId(string bookId)
    {
        var text = (bookId ?? "").Trim();
        var colon = text.IndexOf(':');
        if (colon >= 0)
            text = text[(colon + 1)..];
        return HongguoHighCrypto.StripBookPrefix(text);
    }

    private static int? CodeOf(JsonObject obj)
    {
        var node = obj["code"] ?? obj["Code"];
        if (node is null)
            return null;
        if (node is JsonValue value && value.TryGetValue<int>(out var number))
            return number;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : -1;
    }

    private static int Positive(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return Math.Max(0, number);
        return int.TryParse(value.ToString(), out var parsed) ? Math.Max(0, parsed) : 0;
    }

    private static string? Text(JsonObject? obj, string name)
    {
        if (obj is null || obj[name] is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text) ? null : text;
        var raw = value.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static string StripTags(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", "");
}
