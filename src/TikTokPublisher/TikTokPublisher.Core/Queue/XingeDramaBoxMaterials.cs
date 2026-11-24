using System.Net.Http.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

internal static class XingeDramaBoxMaterials
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static XingeAutoPublishMarker? ReadAssignment(string projectDir)
    {
        var marker = XingeAutoPublishMarkerStore.TryRead(projectDir);
        return marker is { AssignmentId: > 0 } && string.IsNullOrEmpty(marker.ClientDownloadChain) && IsDramaBoxSeries(marker.SeriesId) ? marker : null;
    }

    public static bool IsDramaBoxSeries(string? seriesId) =>
        seriesId?.StartsWith("dramabox:", StringComparison.OrdinalIgnoreCase) == true;

    public static async Task DownloadAsync(
        string projectDir,
        XingeAutoPublishMarker assignment,
        ClientSettings settings,
        IReadOnlyList<int> episodeNumbers,
        bool replace,
        Action<string> log,
        CancellationToken ct,
        HttpClient? client = null)
    {
        client ??= Http;
        var manifest = await ReadManifestAsync(client, assignment, settings, ct).ConfigureAwait(false);
        var byNumber = manifest.Items.ToDictionary(episode => episode.Number);
        var requested = episodeNumbers.Count == 0 ? byNumber.Keys.Order().ToArray() : episodeNumbers;
        if (requested.Count == 0 || requested.Any(number => !byNumber.ContainsKey(number)))
            throw new InvalidOperationException("XINGE 返回的 DramaBox 素材缺少所需分集。");

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Clamp(settings.DramaDownloadConcurrent, 1, 10),
        };
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.HongguoDownloadTimeoutSeconds, 10, 600));
        await Parallel.ForEachAsync(requested, options, async (number, token) =>
        {
            var path = Path.Combine(projectDir, $"第{number}集.mp4");
            if (replace || !HasFileContent(path))
                await DownloadFileAsync(client, byNumber[number].DownloadUrl, path, false, timeout, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var introPath = Path.Combine(projectDir, "简介.txt");
        if (!string.IsNullOrWhiteSpace(manifest.Intro) && !File.Exists(introPath))
            await File.WriteAllTextAsync(introPath, manifest.Intro, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(manifest.PosterUrl) && !Directory.EnumerateFiles(projectDir, "海报原图.*").Any())
        {
            try
            {
                await DownloadFileAsync(client, manifest.PosterUrl, Path.Combine(projectDir, "海报原图.jpg"), true, timeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or InvalidOperationException or TimeoutException)
            {
                log("XINGE DramaBox 海报下载失败，后续封面步骤可从视频抽帧。");
            }
        }
        log($"XINGE DramaBox 分集已就绪，共 {requested.Count} 集。");
    }

    private static async Task<Manifest> ReadManifestAsync(HttpClient client, XingeAutoPublishMarker assignment, ClientSettings settings, CancellationToken ct)
    {
        var connection = XingeRuntimeConnection.Resolve(settings)
            ?? throw new InvalidOperationException("请先连接 XINGE，再下载 DramaBox 派单素材。");
        if (!string.IsNullOrEmpty(assignment.ConnectionScope) && assignment.ConnectionScope != connection.Scope)
            throw new InvalidOperationException("此派单属于之前的 XINGE 连接，请恢复该账号后再下载素材。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var query = string.IsNullOrWhiteSpace(assignment.SeriesId) ? "" : "?series_id=" + Uri.EscapeDataString(assignment.SeriesId);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.BaseUrl}/client-api/dramabox/assignments/{assignment.AssignmentId}/episodes{query}");
        request.Headers.TryAddWithoutValidation("X-Remote-Client-ID", connection.ClientId);
        request.Headers.TryAddWithoutValidation("X-Remote-Client-Token", connection.ClientToken);
        try
        {
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"从 XINGE 读取 DramaBox 素材失败（HTTP {(int)response.StatusCode}）。");

            var manifest = await response.Content.ReadFromJsonAsync<Manifest>(cancellationToken: timeout.Token).ConfigureAwait(false);
            return manifest is { Items.Count: > 0 } ? manifest : throw new InvalidOperationException("XINGE 返回的 DramaBox 素材清单为空。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("读取 XINGE DramaBox 素材清单超时。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("无法从 XINGE 读取 DramaBox 素材清单。");
        }
    }

    private static async Task DownloadFileAsync(HttpClient client, string address, string target, bool image, TimeSpan timeout, CancellationToken ct)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != "https" || !string.IsNullOrEmpty(url.UserInfo))
            throw new InvalidOperationException("XINGE 返回的 DramaBox 素材地址无效。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"DramaBox 素材下载失败（HTTP {(int)response.StatusCode}）。");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not (null or "application/octet-stream") &&
                !mediaType.StartsWith(image ? "image/" : "video/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("DramaBox 素材类型与清单不符。");
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await response.Content.CopyToAsync(file, deadline.Token).ConfigureAwait(false);
            if (!HasFileContent(temp))
                throw new InvalidOperationException("DramaBox 素材下载结果为空。");
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("DramaBox 素材下载超时。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("连接 DramaBox 素材服务失败。");
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static bool HasFileContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private sealed record Manifest(
        IReadOnlyList<Episode> Items,
        string? Intro,
        [property: JsonPropertyName("poster_url")] string? PosterUrl);

    private sealed record Episode(int Number, [property: JsonPropertyName("download_url")] string DownloadUrl);
}
