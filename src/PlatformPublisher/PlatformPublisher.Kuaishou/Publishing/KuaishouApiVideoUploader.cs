using System.Globalization;
using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouApiVideoUploader
{
    public const int ChunkBytes = 5 * 1024 * 1024;
    public const string StartUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/episode/videoUpload/start";
    public const string FinishUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/episode/videoUpload/finish";
    public const string DetailUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/episode/videoUpload/detail";
    public const string SubmitUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/submit";

    public readonly record struct UploadTicket(string Endpoint, string UploadToken, string UploadTaskId);

    public readonly record struct ResumePoint(int NextFragment, long Offset);

    public async Task UploadAsync(
        KuaishouApiSession session,
        string seriesId,
        IReadOnlyList<string> videoPaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, Task>? afterEpisode = null,
        bool replaceExisting = false)
    {
        var label = "快手分账";
        var episodes = await ReadEpisodesAsync(session, seriesId, cancellationToken);
        if (episodes.Count != videoPaths.Count)
            throw new InvalidOperationException($"短剧 {seriesId} 有 {episodes.Count} 集，本地有 {videoPaths.Count} 集，已停止上传。");
        var ready = episodes.Count(episode => episode.Ready);
        if (replaceExisting)
            progress?.Report($"{label}：短剧 {seriesId} 的 {episodes.Count} 集视频都会重新上传。");
        else if (ready > 0)
            progress?.Report(ready == episodes.Count
                ? $"{label}：草稿 {seriesId} 的 {ready} 集视频都已在线上。"
                : $"{label}：草稿 {seriesId} 已有 {ready} 集视频，其余从断点续传。");
        for (var index = 0; index < videoPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = episodes[index];
            if (SkipUploadedEpisode(episode.Ready, replaceExisting))
            {
                progress?.Report($"{label}：第 {index + 1} 集已在线上，跳过。");
                if (afterEpisode is not null)
                    await afterEpisode(index, cancellationToken);
                continue;
            }
            if (episode.Processing && !replaceExisting)
            {
                await WaitUntilReadyAsync(session, seriesId, episode.Id, progress, index + 1, cancellationToken);
                if (afterEpisode is not null)
                    await afterEpisode(index, cancellationToken);
                continue;
            }

            var path = videoPaths[index];
            if (!File.Exists(path))
                throw new FileNotFoundException($"缺少第 {index + 1} 集视频。", path);
            var ticket = await StartAsync(session, episode.Id, cancellationToken);
            var length = new FileInfo(path).Length;
            var point = await ResumePointAsync(session, ticket, cancellationToken);
            var total = ChunkCount(length);
            if (point.NextFragment == total && point.Offset > length)
                point = point with { Offset = length };
            if (point.NextFragment > total || point.Offset > length || (point.NextFragment < total && point.Offset >= length && length > 0))
                throw new InvalidOperationException($"快手视频断点无效：fragment_index={point.NextFragment - 1}。");
            progress?.Report(point.NextFragment > 0
                ? $"{label}：第 {index + 1}/{videoPaths.Count} 集从第 {point.NextFragment + 1}/{total} 片续传。"
                : $"{label}：第 {index + 1}/{videoPaths.Count} 集开始分片上传。");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (point.Offset > 0)
                stream.Seek(point.Offset, SeekOrigin.Begin);
            var buffer = new byte[ChunkBytes];
            var offset = point.Offset;
            for (var fragment = point.NextFragment; fragment < total; fragment++)
            {
                var read = await ReadChunkAsync(stream, buffer, cancellationToken);
                if (read <= 0)
                    throw new InvalidOperationException($"第 {index + 1} 集视频在第 {fragment + 1} 片提前结束。");
                var body = read == buffer.Length ? buffer : buffer[..read];
                var range = $"bytes {offset}-{offset + body.Length - 1}/{length}";
                await session.PostBytesAsync(
                    FragmentUrl(ticket.Endpoint, ticket.UploadToken, fragment).AbsoluteUri,
                    body,
                    cancellationToken,
                    range);
                offset += body.Length;
                if (fragment == total - 1 || fragment % 10 == 0)
                    progress?.Report($"{label}：第 {index + 1} 集分片 {fragment + 1}/{total}。");
            }

            await session.PostBytesAsync(CompleteUrl(ticket.Endpoint, ticket.UploadToken, total).AbsoluteUri, [], cancellationToken);
            await session.PostJsonAsync(FinishUrl, FinishBody(ticket), cancellationToken);
            await WaitUntilReadyAsync(session, seriesId, episode.Id, progress, index + 1, cancellationToken);
            if (afterEpisode is not null)
                await afterEpisode(index, cancellationToken);
        }
    }

    public async Task SubmitAsync(IKuaishouKdjGateway session, string seriesId, CancellationToken cancellationToken)
    {
        await session.PostJsonAsync(
            SubmitUrl,
            JsonSerializer.Serialize(new { miniSeriesId = seriesId.Trim() }),
            cancellationToken);
    }

    public static string StartBody(string episodeId) =>
        JsonSerializer.Serialize(new { episodeId = long.Parse(episodeId, CultureInfo.InvariantCulture) });

    public static string FinishBody(UploadTicket ticket) =>
        JsonSerializer.Serialize(new
        {
            uploadToken = ticket.UploadToken,
            uploadTaskId = ticket.UploadTaskId,
            success = true,
        });

    public static int ChunkCount(long length) =>
        length <= 0 ? 1 : (int)((length + ChunkBytes - 1) / ChunkBytes);

    public static Uri FragmentUrl(string endpoint, string uploadToken, int fragmentId) =>
        UploadUri(endpoint, "/api/upload/fragment", uploadToken, ("fragment_id", fragmentId.ToString(CultureInfo.InvariantCulture)));

    public static Uri ResumeUrl(string endpoint, string uploadToken) =>
        UploadUri(endpoint, "/api/upload/resume", uploadToken);

    public static Uri CompleteUrl(string endpoint, string uploadToken, int fragmentCount) =>
        UploadUri(endpoint, "/api/upload/complete", uploadToken, ("fragment_count", fragmentCount.ToString(CultureInfo.InvariantCulture)));

    public static UploadTicket ReadTicket(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var endpoint = FirstEndpoint(data) is { Length: > 0 } listed ? listed : First(data, root, "endpoint", "upload_host");
        var token = First(data, root, "uploadToken", "upload_token");
        var task = First(data, root, "uploadTaskId", "upload_task_id");
        if (endpoint.Length == 0)
            throw new InvalidOperationException("快手视频上传启动响应缺少 endpoint。");
        if (token.Length == 0 || task.Length == 0)
            throw new InvalidOperationException("快手视频上传启动响应缺少 uploadToken 或 uploadTaskId。");
        return new UploadTicket(endpoint, token, task);
    }

    public static int ReadFragmentIndex(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return 0;
        using var document = JsonDocument.Parse(json);
        return ReadInt64(document.RootElement, "fragment_index", "fragmentIndex") is long index
            ? (int)index
            : 0;
    }

    public static ResumePoint ReadResumePoint(string json, int chunkBytes)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ResumePoint(0, 0);
        using var document = JsonDocument.Parse(json);
        var index = ReadInt64(document.RootElement, "fragment_index", "fragmentIndex");
        var bytes = ReadInt64(document.RootElement, "fragment_index_bytes", "fragmentIndexBytes");
        if (index is null or < 0)
            return new ResumePoint(0, 0);
        var next = (int)index.Value + 1;
        var offset = bytes is > 0 ? bytes.Value : (long)next * Math.Max(1, chunkBytes);
        return new ResumePoint(next, offset);
    }

    public static bool EpisodeReady(JsonElement row)
    {
        if (EpisodeFailed(row))
            throw new InvalidOperationException("快手确认分集视频状态失败。");
        if (EpisodeProcessing(row))
            return false;
        var status = FirstString(row, "videoUploadStatus");
        if (status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) || status == "2")
            return true;

        if (FirstString(row, "previewVideoPlayUrl", "videoId", "previewVideoId").Length > 0)
            return true;
        return FirstString(row, "videoStatus") == "1";
    }

    internal static bool EpisodeFailed(JsonElement row)
    {
        var status = FirstString(row, "videoUploadStatus");
        return status.Equals("FAIL", StringComparison.OrdinalIgnoreCase) || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
            int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && code >= 3 ||
            FirstString(row, "videoStatus") == "2";
    }

    internal static bool EpisodeProcessing(JsonElement row)
    {
        var status = FirstString(row, "videoUploadStatus");
        return status.Equals("PROCESSING", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("UPLOADING", StringComparison.OrdinalIgnoreCase) ||
            FirstString(row, "videoStatus") == "0" &&
            (status is "1" or "2" || status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) ||
             FirstString(row, "previewVideoPlayUrl", "videoId", "previewVideoId").Length > 0);
    }

    public static bool EpisodeUploaded(JsonElement row, bool replaceExisting)
    {
        try
        {
            return EpisodeReady(row);
        }
        catch (InvalidOperationException) when (replaceExisting)
        {
            return false;
        }
    }

    public static bool SkipUploadedEpisode(bool uploaded, bool replaceExisting) =>
        uploaded && !replaceExisting;

    private async Task<UploadTicket> StartAsync(
        KuaishouApiSession session,
        string episodeId,
        CancellationToken cancellationToken)
    {
        var response = await session.PostJsonAsync(StartUrl, StartBody(episodeId), cancellationToken);
        return ReadTicket(response);
    }

    private async Task<ResumePoint> ResumePointAsync(KuaishouApiSession session, UploadTicket ticket, CancellationToken cancellationToken)
    {
        try
        {
            var response = await session.GetAsync(ResumeUrl(ticket.Endpoint, ticket.UploadToken).AbsoluteUri, cancellationToken);
            return ReadResumePoint(response, ChunkBytes);
        }
        catch (InvalidOperationException ex) when (!ex.Message.Contains("登录", StringComparison.Ordinal))
        {
            return new ResumePoint(0, 0);
        }
    }

    private async Task WaitUntilReadyAsync(
        KuaishouApiSession session,
        string seriesId,
        string episodeId,
        IProgress<string>? progress,
        int number,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var episodes = await ReadEpisodesAsync(session, seriesId, cancellationToken);
            var match = episodes.FirstOrDefault(item => item.Id == episodeId);
            if (match.Ready)
                return;
            if (match.Failed)
                throw new InvalidOperationException($"快手确认第 {number} 集视频处理失败。");
            progress?.Report($"快手分账：第 {number} 集视频处理中。");
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        throw new InvalidOperationException("快手视频仍在处理中。");
    }

    private async Task<IReadOnlyList<EpisodeSlot>> ReadEpisodesAsync(
        KuaishouApiSession session,
        string seriesId,
        CancellationToken cancellationToken)
    {
        var detail = await session.PostJsonAsync(DetailUrl, "{\"miniSeriesId\":" + seriesId + "}", cancellationToken);
        using var document = JsonDocument.Parse(detail);
        var rows = new List<(int Order, int Index, EpisodeSlot Episode)>();
        var index = 0;
        foreach (var row in ReadEpisodeRows(document.RootElement))
        {
            var id = FirstString(row, "episodeId");
            if (id.Length == 0)
            {
                index++;
                continue;
            }

            var order = row.TryGetProperty("orderNo", out var orderNo) && orderNo.TryGetInt32(out var value)
                ? value
                : index;
            var failed = EpisodeFailed(row);
            rows.Add((order, index, new EpisodeSlot(id, !failed && EpisodeReady(row), failed, !failed && EpisodeProcessing(row))));
            index++;
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("快手返回了无效的剧集列表。");
        return rows
            .OrderBy(row => row.Order)
            .ThenBy(row => row.Index)
            .Select(row => row.Episode)
            .ToArray();
    }

    private static long? ReadInt64(JsonElement root, params string[] names)
    {
        if (TryReadInt64(root, names, out var value))
            return value;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            TryReadInt64(data, names, out value))
            return value;
        return null;
    }

    private static bool TryReadInt64(JsonElement element, string[] names, out long value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var node))
                continue;
            if (node.TryGetInt64(out value))
                return true;
            if (node.ValueKind == JsonValueKind.String &&
                long.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;
        }

        return false;
    }

    internal static IEnumerable<JsonElement> ReadEpisodeRows(JsonElement root)
    {
        foreach (var name in new[] { "episodeList", "seriesList" })
        {
            if (root.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array)
                return rows.EnumerateArray();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty(name, out rows) && rows.ValueKind == JsonValueKind.Array)
                return rows.EnumerateArray();
        }

        return [];
    }

    private static Uri UploadUri(string endpoint, string path, string uploadToken, params (string Name, string Value)[] extra)
    {
        var origin = EndpointOrigin(endpoint);
        var query = new List<string> { "upload_token=" + Uri.EscapeDataString(uploadToken) };
        foreach (var item in extra)
            query.Add(item.Name + "=" + Uri.EscapeDataString(item.Value));
        return new Uri(origin + path + "?" + string.Join("&", query));
    }

    public static string EndpointOrigin(string endpoint)
    {
        var text = endpoint.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text.Trim('/');
        var uri = new Uri(text);
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string FirstEndpoint(JsonElement data)
    {
        foreach (var name in new[] { "httpEndpointList", "endpointList", "endpoints" })
        {
            if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in value.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }

        return string.Empty;
    }

    private static string First(JsonElement data, JsonElement root, params string[] names)
    {
        var value = FirstString(data, names);
        return value.Length == 0 ? FirstString(root, names) : value;
    }

    private static string FirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return string.Empty;
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return string.Empty;
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
            if (read == 0)
                break;
            filled += read;
        }

        return filled;
    }

    private readonly record struct EpisodeSlot(string Id, bool Ready, bool Failed, bool Processing);
}
