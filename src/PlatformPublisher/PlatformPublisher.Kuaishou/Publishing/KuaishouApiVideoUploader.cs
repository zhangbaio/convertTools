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

    public async Task UploadAsync(
        KuaishouApiSession session,
        string seriesId,
        IReadOnlyList<string> videoPaths,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, Task>? afterEpisode = null)
    {
        var label = "快手分账";
        var episodes = await ReadEpisodesAsync(session, seriesId, cancellationToken);
        if (episodes.Count != videoPaths.Count)
            throw new InvalidOperationException($"短剧 {seriesId} 有 {episodes.Count} 集，本地有 {videoPaths.Count} 集，已停止上传。");
        for (var index = 0; index < videoPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = episodes[index];
            if (episode.Ready)
            {
                progress?.Report($"{label}：第 {index + 1} 集已在线上，跳过。");
                if (afterEpisode is not null)
                    await afterEpisode(index, cancellationToken);
                continue;
            }

            var path = videoPaths[index];
            if (!File.Exists(path))
                throw new FileNotFoundException($"缺少第 {index + 1} 集视频。", path);
            var ticket = await StartAsync(session, episode.Id, cancellationToken);
            var length = new FileInfo(path).Length;
            var uploaded = await ResumeIndexAsync(session, ticket, cancellationToken);
            var total = ChunkCount(length);
            if (uploaded < 0 || uploaded > total)
                throw new InvalidOperationException("快手视频断点无效。");
            progress?.Report($"{label}：第 {index + 1}/{videoPaths.Count} 集开始分片上传。");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (uploaded > 0)
                stream.Seek((long)uploaded * ChunkBytes, SeekOrigin.Begin);
            var buffer = new byte[ChunkBytes];
            for (var fragment = uploaded; fragment < total; fragment++)
            {
                var read = await ReadChunkAsync(stream, buffer, cancellationToken);
                if (read <= 0)
                    throw new InvalidOperationException($"第 {index + 1} 集视频在第 {fragment + 1} 片提前结束。");
                var body = read == buffer.Length ? buffer : buffer[..read];
                var start = (long)fragment * ChunkBytes;
                var range = $"bytes {start}-{start + body.Length - 1}/{length}";
                await session.PostBytesAsync(
                    FragmentUrl(ticket.Endpoint, ticket.UploadToken, fragment).AbsoluteUri,
                    body,
                    cancellationToken,
                    range);
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

    public async Task SubmitAsync(KuaishouApiSession session, string seriesId, CancellationToken cancellationToken)
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
        foreach (var name in new[] { "fragment_index", "fragmentIndex" })
        {
            if (document.RootElement.TryGetProperty(name, out var value) && value.TryGetInt32(out var index))
                return index;
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty(name, out var nested) &&
                nested.TryGetInt32(out index))
                return index;
        }

        return 0;
    }

    public static bool EpisodeReady(JsonElement row)
    {
        if (row.TryGetProperty("videoUploadStatus", out var status))
        {
            if (status.ValueKind == JsonValueKind.String)
            {
                var text = status.GetString()?.Trim() ?? string.Empty;
                if (text.Equals("FAIL", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("快手确认分集视频状态失败。");
                if (text.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (status.TryGetInt32(out var code) && code >= 3)
            {
                throw new InvalidOperationException("快手确认分集视频状态失败。");
            }
        }

        if (FirstString(row, "previewVideoPlayUrl", "videoId", "previewVideoId").Length > 0)
            return true;
        if (row.TryGetProperty("videoStatus", out var videoStatus) && videoStatus.TryGetInt32(out var videoCode))
            return videoCode == 1;
        return row.TryGetProperty("videoUploadStatus", out status) && status.TryGetInt32(out var ready) && ready == 2;
    }

    private async Task<UploadTicket> StartAsync(
        KuaishouApiSession session,
        string episodeId,
        CancellationToken cancellationToken)
    {
        var response = await session.PostJsonAsync(StartUrl, StartBody(episodeId), cancellationToken);
        return ReadTicket(response);
    }

    private async Task<int> ResumeIndexAsync(KuaishouApiSession session, UploadTicket ticket, CancellationToken cancellationToken)
    {
        try
        {
            var response = await session.GetAsync(ResumeUrl(ticket.Endpoint, ticket.UploadToken).AbsoluteUri, cancellationToken);
            return ReadFragmentIndex(response);
        }
        catch (InvalidOperationException ex) when (!ex.Message.Contains("登录", StringComparison.Ordinal))
        {
            return 0;
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
        var rows = new List<EpisodeSlot>();
        foreach (var row in Enumerate(document.RootElement))
        {
            var id = FirstString(row, "episodeId");
            if (id.Length == 0)
                continue;
            rows.Add(new EpisodeSlot(id, EpisodeReady(row)));
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("快手返回了无效的剧集列表。");
        return rows;
    }

    private static IEnumerable<JsonElement> Enumerate(JsonElement root)
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

    private readonly record struct EpisodeSlot(string Id, bool Ready);
}
