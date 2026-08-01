using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public enum LiveActionClassification
{
    NonLiveAction,
    LiveAction,
    Uncertain,
}

public sealed record LiveActionDetectionResult(
    LiveActionClassification Classification,
    double Confidence,
    string Reason,
    string VideoFingerprint,
    bool FromCache = false);

/// <summary>
/// 下载完成后的真人实拍检测。复用“火山文本/视觉模型”配置，不持有第二套密钥。
/// </summary>
public static class TikTokLiveActionDetectionService
{
    private const string CacheFileName = "live-action-detection.json";
    private const string DetectorVersion = "v1-six-frames";
    private static readonly HttpClient VisionHttp = CreateHttpClient();

    public static async Task<LiveActionDetectionResult> DetectAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);

        var endpoint = (settings.AiTextEndpoint ?? string.Empty).Trim().TrimEnd('/');
        var apiKey = (settings.AiTextApiKey ?? string.Empty).Trim();
        var model = (settings.AiTextModel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                "真人检测需要先在系统设置中配置可识图的火山文本/视觉模型、API Key 和模型名称。");
        }

        var videos = ProjectVideoResolver.ResolveSourceVideos(item.ProjectDir, allowStagedFallback: true);
        if (videos.Count == 0)
            throw new InvalidOperationException("真人检测未找到可用视频，请先完成获取剧集步骤。");

        var fingerprint = BuildVideoFingerprint(videos);
        var workflow = ProjectWorkspaceService.ResolveWorkflowProjectDir(item.ProjectDir);
        if (string.IsNullOrWhiteSpace(workflow))
            workflow = Path.GetFullPath(item.ProjectDir);
        var cachePath = Path.Combine(workflow, CacheFileName);

        if (!forceRerun)
        {
            var cached = TryLoadCache(cachePath, fingerprint, model);
            if (cached is not null)
            {
                log?.Invoke(
                    $"真人检测：命中本地结果，{Describe(cached.Classification)}，" +
                    $"置信度 {cached.Confidence:P0}；{cached.Reason}");
                return cached with { FromCache = true };
            }
        }

        var selectedVideos = SelectRepresentativeVideos(videos);
        log?.Invoke(
            $"真人检测：从 {videos.Count} 集中选取 {selectedVideos.Count} 集，" +
            $"每集抽取 2 帧，准备调用视觉模型 {model}…");

        var tempDir = Path.Combine(workflow, $".live-action-detection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var frames = await ExtractFramesAsync(selectedVideos, tempDir, log, ct).ConfigureAwait(false);
            if (frames.Count < 2)
                throw new InvalidOperationException($"真人检测抽帧不足：仅得到 {frames.Count} 张有效画面。");

            var timeoutSeconds = Math.Clamp(
                settings.AiTextTimeoutSeconds <= 0 ? 120 : settings.AiTextTimeoutSeconds,
                30,
                300);
            LiveActionDetectionResult? result = null;
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    log?.Invoke($"真人检测：视觉请求已发送（第 {attempt}/3 次），上传 {frames.Count} 张抽帧…");
                    result = await AnalyzeAsync(
                            endpoint,
                            apiKey,
                            model,
                            item.Title,
                            frames,
                            fingerprint,
                            timeoutSeconds,
                            ct)
                        .ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    log?.Invoke($"真人检测：第 {attempt}/3 次识别失败：{ex.Message}");
                    if (attempt < 3)
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
                }
            }

            if (result is null)
            {
                throw new InvalidOperationException(
                    $"真人检测连续 3 次失败：{lastError?.Message ?? "未知错误"}",
                    lastError);
            }
            SaveCache(cachePath, model, result);
            log?.Invoke(
                $"真人检测完成：{Describe(result.Classification)}，" +
                $"置信度 {result.Confidence:P0}；{result.Reason}");
            return result;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static async Task<IReadOnlyList<string>> ExtractFramesAsync(
        IReadOnlyList<string> videos,
        string tempDir,
        Action<string>? log,
        CancellationToken ct)
    {
        var ffmpeg = MediaBinaryResolver.ResolveFfmpeg();
        var ffprobe = MediaBinaryResolver.ResolveFfprobe();
        var frames = new List<string>(videos.Count * 2);
        for (var videoIndex = 0; videoIndex < videos.Count; videoIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var video = videos[videoIndex];
            double duration;
            try
            {
                duration = await FfmpegRunner.ProbeDurationSecondsAsync(ffprobe, video, ct).ConfigureAwait(false);
            }
            catch
            {
                duration = 10;
            }

            foreach (var (ratio, suffix) in new[] { (0.25, "a"), (0.70, "b") })
            {
                var output = Path.Combine(tempDir, $"frame-{videoIndex + 1:00}-{suffix}.jpg");
                var second = Math.Clamp(duration * ratio, 0.2, Math.Max(0.2, duration - 0.2));
                await FfmpegRunner.RunAsync(
                        ffmpeg,
                        [
                            "-y", "-hide_banner", "-loglevel", "error",
                            "-ss", second.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                            "-i", video,
                            "-frames:v", "1",
                            "-vf", "scale=960:-2:force_original_aspect_ratio=decrease",
                            "-q:v", "4",
                            output,
                        ],
                        ct)
                    .ConfigureAwait(false);
                if (File.Exists(output) && new FileInfo(output).Length > 1024)
                    frames.Add(output);
            }

            log?.Invoke($"真人检测/抽帧 [{videoIndex + 1}/{videos.Count}]：{Path.GetFileName(video)}");
        }

        return frames;
    }

    private static async Task<LiveActionDetectionResult> AnalyzeAsync(
        string endpoint,
        string apiKey,
        string model,
        string title,
        IReadOnlyList<string> frames,
        string fingerprint,
        int timeoutSeconds,
        CancellationToken ct)
    {
        const string prompt =
            """
            你是短剧内容分类审核员。请综合所有抽帧判断该剧是否为真人实拍。
            live_action：真实演员在真实或搭建场景中拍摄的影视画面。
            non_live_action：动画、漫画、2D/3D CG、游戏录屏、绘本、AI生成动画；即使人物写实或像真人，也归此类。
            uncertain：画面不足、混合内容或无法可靠判断。
            只输出一个JSON对象，不要Markdown：
            {"classification":"live_action|non_live_action|uncertain","confidence":0.0,"reason":"不超过60字的中文依据"}
            confidence 为0到1。不要仅凭封面、字幕或单个人脸下结论，要看多帧的材质、运动和摄影特征。
            """;
        var content = new List<object>
        {
            new { type = "text", text = $"剧名：{title}\n{prompt}" },
        };
        foreach (var frame in frames)
        {
            var data = Convert.ToBase64String(await File.ReadAllBytesAsync(frame, ct).ConfigureAwait(false));
            content.Add(new
            {
                type = "image_url",
                image_url = new { url = $"data:image/jpeg;base64,{data}", detail = "high" },
            });
        }

        var payload = new
        {
            model,
            temperature = 0.1,
            messages = new object[]
            {
                new { role = "user", content = content.ToArray() },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await VisionHttp.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"视觉接口失败 {(int)response.StatusCode}: {Trim(body, 240)}");

        using var responseJson = JsonDocument.Parse(body);
        var raw = responseJson.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
        return ParseModelResponse(raw, fingerprint);
    }

    internal static LiveActionDetectionResult ParseModelResponse(string raw, string fingerprint)
    {
        var json = ExtractJsonObject(raw);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var classificationText = root.TryGetProperty("classification", out var classificationNode)
            ? (classificationNode.GetString() ?? string.Empty).Trim().ToLowerInvariant()
            : string.Empty;
        var classification = classificationText switch
        {
            "live_action" or "live" or "真人" or "真人实拍" => LiveActionClassification.LiveAction,
            "non_live_action" or "non_live" or "animation" or "非真人" => LiveActionClassification.NonLiveAction,
            _ => LiveActionClassification.Uncertain,
        };
        var confidence = root.TryGetProperty("confidence", out var confidenceNode) &&
                         confidenceNode.TryGetDouble(out var parsedConfidence)
            ? Math.Clamp(parsedConfidence, 0, 1)
            : 0;
        var reason = root.TryGetProperty("reason", out var reasonNode)
            ? (reasonNode.GetString() ?? string.Empty).Trim()
            : "模型未返回判断依据";
        if (classification == LiveActionClassification.LiveAction && confidence < 0.8)
            classification = LiveActionClassification.Uncertain;
        if (classification == LiveActionClassification.NonLiveAction && confidence < 0.65)
            classification = LiveActionClassification.Uncertain;
        return new LiveActionDetectionResult(classification, confidence, reason, fingerprint);
    }

    private static IReadOnlyList<string> SelectRepresentativeVideos(IReadOnlyList<string> videos)
    {
        if (videos.Count <= 3)
            return videos.ToArray();
        return new[] { videos[0], videos[videos.Count / 2], videos[^1] }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildVideoFingerprint(IReadOnlyList<string> videos)
    {
        var text = string.Join(
            "\n",
            videos.Select(path =>
            {
                var info = new FileInfo(path);
                return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static LiveActionDetectionResult? TryLoadCache(string path, string fingerprint, string model)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            if (!string.Equals(root.GetProperty("version").GetString(), DetectorVersion, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("video_fingerprint").GetString(), fingerprint, StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("model").GetString(), model, StringComparison.Ordinal))
            {
                return null;
            }

            var classification = Enum.TryParse<LiveActionClassification>(
                root.GetProperty("classification").GetString(),
                ignoreCase: true,
                out var parsed)
                ? parsed
                : LiveActionClassification.Uncertain;
            return new LiveActionDetectionResult(
                classification,
                root.GetProperty("confidence").GetDouble(),
                root.GetProperty("reason").GetString() ?? string.Empty,
                fingerprint,
                FromCache: true);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string path, string model, LiveActionDetectionResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new
        {
            version = DetectorVersion,
            model,
            classification = result.Classification.ToString(),
            confidence = result.Confidence,
            reason = result.Reason,
            video_fingerprint = result.VideoFingerprint,
            detected_at = DateTimeOffset.Now.ToString("o"),
        };
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException($"视觉模型返回内容不是JSON：{Trim(content, 240)}");
        return content[start..(end + 1)];
    }

    private static string Describe(LiveActionClassification classification) => classification switch
    {
        LiveActionClassification.LiveAction => "真人实拍",
        LiveActionClassification.NonLiveAction => "非真人剧",
        _ => "无法确认",
    };

    private static string Trim(string value, int length) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= length ? value : value[..length] + "…";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时抽帧清理失败不影响检测结果。
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
