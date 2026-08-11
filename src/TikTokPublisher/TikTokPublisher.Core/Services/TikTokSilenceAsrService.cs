using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.Asr;

namespace TikTokPublisher.Core.Services;

/// <summary>ASR 引擎标识（对齐 Python <c>silence_asr_service</c>）。</summary>
public static class TikTokAsrEngine
{
    public const string Volcengine = "volcengine";
    public const string Local = "local";
    public const string Hybrid = "hybrid";

    public static string Label(string engine) => engine switch
    {
        Volcengine => "火山ASR(在线)",
        Local => "本地Paraformer(免费)",
        Hybrid => "混合(本地+火山复核)",
        _ => engine,
    };

    public static string Normalize(string? engine)
    {
        var n = (engine ?? "").Trim().ToLowerInvariant();
        return n is Volcengine or Local or Hybrid ? n : Local;
    }
}

/// <summary>识别到的一段有台词的语音区间（秒）。</summary>
public readonly record struct SpeechInterval(double StartSeconds, double EndSeconds);

/// <summary>某集视频的最长无台词间隔（对齐 Python <c>SilenceGap</c>）。</summary>
public sealed class SilenceGapReport
{
    public int EpisodeIndex { get; init; }
    public string Name { get; init; } = "";
    public double DurationSeconds { get; init; }
    public double MaxGapSeconds { get; init; }
    public double GapStartSeconds { get; init; }
    public double GapEndSeconds { get; init; }
    /// <summary>head / middle / tail</summary>
    public string Position { get; init; } = "middle";
}

/// <summary>基于 ASR 的无台词间隔检测（火山在线大模型极速版）。</summary>
public static class TikTokSilenceAsrService
{
    private const double EdgeEpsilonSeconds = 0.8;
    private const string VolcEndpoint =
        "https://openspeech.bytedance.com/api/v3/auc/bigmodel/recognize/flash";
    private const int VolcMaxAttempts = 5;
    private static readonly TimeSpan VolcRetryBase = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VolcRetryCap = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim VolcGlobalSemaphore = new(5, 5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    /// <summary>分析单集视频，返回最长无台词间隔（不足阈值也返回，用于统计）。</summary>
    public static async Task<SilenceGapReport> AnalyzeAsync(
        string videoPath,
        int episodeIndex,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        var ffprobe = MediaBinaryResolver.ResolveFfprobe();
        var duration = await FfmpegRunner.ProbeDurationSecondsAsync(ffprobe, videoPath, ct).ConfigureAwait(false);
        var intervals = await GetSpeechIntervalsAsync(videoPath, settings, duration, log, ct).ConfigureAwait(false);
        var (gap, gStart, gEnd) = ComputeMaxNoSpeechGap(intervals, duration);
        return new SilenceGapReport
        {
            EpisodeIndex = episodeIndex,
            Name = Path.GetFileName(videoPath),
            DurationSeconds = Math.Round(duration, 3),
            MaxGapSeconds = Math.Round(gap, 3),
            GapStartSeconds = Math.Round(gStart, 3),
            GapEndSeconds = Math.Round(gEnd, 3),
            Position = ClassifyPosition(gStart, gEnd, duration),
        };
    }

    /// <summary>批量检测（按并发度并行调用火山，本地/混合退回串行）。</summary>
    public static async Task<IReadOnlyList<SilenceGapReport>> DetectAsync(
        IReadOnlyList<string> uploadPaths,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        if (uploadPaths.Count == 0) return Array.Empty<SilenceGapReport>();
        var engine = TikTokAsrEngine.Normalize(settings.TiktokSilenceAsrEngine);
        var workers = engine == TikTokAsrEngine.Volcengine
            ? Math.Clamp(settings.TiktokSilenceDetectConcurrency, 1, 16)
            : Math.Min(2, uploadPaths.Count);
        workers = Math.Max(1, Math.Min(workers, uploadPaths.Count));

        var results = new SilenceGapReport[uploadPaths.Count];
        using var throttle = new SemaphoreSlim(Math.Max(1, workers));
        var tasks = new List<Task>(uploadPaths.Count);
        for (var i = 0; i < uploadPaths.Count; i++)
        {
            var index = i;
            var path = uploadPaths[i];
            tasks.Add(Task.Run(async () =>
            {
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    results[index] = await AnalyzeAsync(path, index + 1, settings, log: null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"⚠️ 第{index + 1}集 | {Path.GetFileName(path)} | 检测失败（{ex.Message}），已跳过。");
                    results[index] = new SilenceGapReport
                    {
                        EpisodeIndex = index + 1,
                        Name = Path.GetFileName(path),
                    };
                }
                finally
                {
                    throttle.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    public static string CacheKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Path.GetFileName(path);
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            return $"{info.Name}|{info.Length}|{mtime}";
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    public static void SaveReport(
        string sourceProjectDir,
        IReadOnlyList<string> uploadPaths,
        IReadOnlyList<SilenceGapReport> reports,
        double thresholdSeconds)
    {
        var context = Queue.ProjectWorkspaceService.LoadContext(sourceProjectDir);
        var items = new List<Dictionary<string, object?>>();
        for (var i = 0; i < reports.Count; i++)
        {
            var report = reports[i];
            if (report is null || report.EpisodeIndex <= 0) continue;
            var path = i < uploadPaths.Count ? uploadPaths[i] : report.Name;
            items.Add(new Dictionary<string, object?>
            {
                ["episode_index"] = report.EpisodeIndex,
                ["name"] = report.Name,
                ["duration_seconds"] = report.DurationSeconds,
                ["max_gap_seconds"] = report.MaxGapSeconds,
                ["gap_start_seconds"] = report.GapStartSeconds,
                ["gap_end_seconds"] = report.GapEndSeconds,
                ["position"] = report.Position,
                ["cache_key"] = CacheKey(path),
            });
        }

        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            "silence_asr_report",
            new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["threshold_seconds"] = thresholdSeconds,
                ["items"] = items,
            },
            context.WorkflowProjectDir);
    }

    /// <summary>按引擎调度：火山在线 / 本地 Paraformer / 混合复核。</summary>
    private static async Task<IReadOnlyList<SpeechInterval>> GetSpeechIntervalsAsync(
        string videoPath,
        ClientSettings settings,
        double durationSeconds,
        Action<string>? log,
        CancellationToken ct)
    {
        var engine = TikTokAsrEngine.Normalize(settings.TiktokSilenceAsrEngine);
        if (engine == TikTokAsrEngine.Volcengine)
            return await RecognizeWithVolcAsync(videoPath, settings, ct).ConfigureAwait(false);

        if (engine == TikTokAsrEngine.Local)
            return await RecognizeWithLocalAsync(videoPath, settings, log, ct).ConfigureAwait(false);

        // hybrid: 本地优先，临界带内用火山复核
        var local = await RecognizeWithLocalAsync(videoPath, settings, log, ct).ConfigureAwait(false);
        var (maxGap, _, _) = ComputeMaxNoSpeechGap(local, durationSeconds);
        var (low, high) = HybridBand(settings);
        if (maxGap < low || maxGap > high || !HasVolcCredentials(settings))
        {
            log?.Invoke($"混合ASR：采用本地结果（最长无台词 {maxGap:F1}s 未落临界带[{low:F0},{high:F0}]，免火山）。");
            return local;
        }

        log?.Invoke($"混合ASR：本地初筛最长无台词 {maxGap:F1}s 落入临界带[{low:F0},{high:F0}]，改用火山复核。");
        try
        {
            var volc = await RecognizeWithVolcAsync(videoPath, settings, ct).ConfigureAwait(false);
            log?.Invoke("混合ASR：火山复核完成（采用火山结果）。");
            return volc;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"混合ASR：火山复核失败（{ex.Message}），回退采用本地结果。");
            return local;
        }
    }

    private static async Task<IReadOnlyList<SpeechInterval>> RecognizeWithLocalAsync(
        string videoPath,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        var segments = await RecognizeLocalTranscriptAsync(videoPath, settings, log, ct)
            .ConfigureAwait(false);
        return LocalParaformerAsrClient.ToSpeechIntervals(segments);
    }

    /// <summary>
    /// 强制使用本地 Paraformer 识别视频台词。该入口负责抽取 16kHz 单声道 WAV，
    /// 并在识别完成或失败后清理临时文件。
    /// </summary>
    public static async Task<IReadOnlyList<TranscriptSegment>> RecognizeLocalTranscriptAsync(
        string videoPath,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        var (ok, reason) = SherpaOnnxModelResolver.CheckAvailable(settings);
        if (!ok)
            throw new InvalidOperationException(reason);

        log?.Invoke($"本地 Paraformer ASR：正在抽取音频（{Path.GetFileName(videoPath)}）…");
        var wavPath = await ExtractAsrWavAsync(videoPath, ct).ConfigureAwait(false);
        try
        {
            log?.Invoke("本地 Paraformer ASR 识别中…");
            var segments = await LocalParaformerAsrClient
                .RecognizeTranscriptSegmentsAsync(wavPath, settings, ct)
                .ConfigureAwait(false);
            log?.Invoke($"本地 Paraformer ASR 完成：识别到 {segments.Count} 段台词。");
            return segments;
        }
        finally
        {
            TryDelete(wavPath);
            TryDelete(Path.GetDirectoryName(wavPath));
        }
    }

    private static (double Low, double High) HybridBand(ClientSettings settings)
    {
        var low = settings.TiktokSilenceHybridLowSeconds;
        var high = settings.TiktokSilenceHybridHighSeconds;
        if (low > high)
            (low, high) = (high, low);
        return (low, high);
    }

    private static async Task<IReadOnlyList<SpeechInterval>> RecognizeWithVolcAsync(
        string videoPath,
        ClientSettings settings,
        CancellationToken ct)
    {
        var appId = (settings.TiktokSilenceAsrAppId ?? "").Trim();
        var accessToken = (settings.TiktokSilenceAsrAccessToken ?? "").Trim();
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("未配置火山 ASR（AppID / AccessToken）");

        var wavPath = await ExtractAsrWavAsync(videoPath, ct).ConfigureAwait(false);
        try
        {
            var audioBytes = await File.ReadAllBytesAsync(wavPath, ct).ConfigureAwait(false);
            var payload = new JsonObject
            {
                ["user"] = new JsonObject { ["uid"] = appId },
                ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(audioBytes) },
                ["request"] = new JsonObject
                {
                    ["model_name"] = "bigmodel",
                    ["model_version"] = "400",
                    ["enable_itn"] = true,
                    ["enable_punc"] = true,
                    ["enable_ddc"] = true,
                    ["show_utterances"] = true,
                },
            };
            var body = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

            await VolcGlobalSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (var attempt = 1; attempt <= VolcMaxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var request = new HttpRequestMessage(HttpMethod.Post, VolcEndpoint);
                    request.Headers.TryAddWithoutValidation("X-Api-Resource-Id", "volc.bigasr.auc_turbo");
                    request.Headers.TryAddWithoutValidation("X-Api-Sequence", "-1");
                    request.Headers.TryAddWithoutValidation("X-Api-App-Key", appId);
                    request.Headers.TryAddWithoutValidation("X-Api-Access-Key", accessToken);
                    request.Headers.TryAddWithoutValidation(
                        "X-Api-Request-Id",
                        $"tiktok-silence-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{attempt}");
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    HttpResponseMessage response;
                    try
                    {
                        response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex) when (attempt < VolcMaxAttempts)
                    {
                        await RetryDelayAsync(attempt, ct).ConfigureAwait(false);
                        _ = ex;
                        continue;
                    }

                    using (response)
                    {
                        var respText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            if (((int)response.StatusCode == 429 || IsThrottle(respText))
                                && attempt < VolcMaxAttempts)
                            {
                                await RetryDelayAsync(attempt, ct).ConfigureAwait(false);
                                continue;
                            }
                            throw new InvalidOperationException(
                                $"火山 STT 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}｜{Truncate(respText, 200)}");
                        }

                        var statusCode = FirstHeader(response, "X-Api-Status-Code");
                        if (!string.IsNullOrEmpty(statusCode) && statusCode != "20000000")
                        {
                            var message = FirstHeader(response, "X-Api-Message");
                            if (IsThrottle($"{statusCode} {message}") && attempt < VolcMaxAttempts)
                            {
                                await RetryDelayAsync(attempt, ct).ConfigureAwait(false);
                                continue;
                            }
                            throw new InvalidOperationException($"火山 STT 识别失败：code={statusCode} {message}");
                        }

                        return ParseVolcResponse(respText);
                    }
                }
                throw new InvalidOperationException("火山 STT 请求失败：并发超限重试已用尽");
            }
            finally
            {
                VolcGlobalSemaphore.Release();
            }
        }
        finally
        {
            TryDelete(wavPath);
            TryDelete(Path.GetDirectoryName(wavPath));
        }
    }

    private static IReadOnlyList<SpeechInterval> ParseVolcResponse(string responseText)
    {
        var doc = JsonNode.Parse(responseText) as JsonObject;
        var utterances = doc?["result"]?["utterances"] as JsonArray;
        if (utterances is null) return Array.Empty<SpeechInterval>();
        var result = new List<SpeechInterval>();
        foreach (var node in utterances)
        {
            if (node is not JsonObject obj) continue;
            var text = obj["text"]?.GetValue<string>()?.Trim() ?? "";
            if (string.IsNullOrEmpty(text)) continue;
            var start = obj["start_time"]?.GetValue<long>() ?? 0;
            var end = obj["end_time"]?.GetValue<long>() ?? 0;
            result.Add(new SpeechInterval(start / 1000.0, end / 1000.0));
        }
        return result;
    }

    private static async Task<string> ExtractAsrWavAsync(string videoPath, CancellationToken ct)
    {
        var ffmpeg = MediaBinaryResolver.ResolveFfmpeg();
        var tempDir = Path.Combine(Path.GetTempPath(), $"tiktok-silence-asr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var wav = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(videoPath) + ".16k.wav");
        try
        {
            await FfmpegRunner.RunAsync(ffmpeg, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", videoPath, "-vn", "-ac", "1", "-ar", "16000",
                "-c:a", "pcm_s16le", wav,
            }, ct).ConfigureAwait(false);
            if (!File.Exists(wav))
                throw new InvalidOperationException("音频抽取失败");
            return wav;
        }
        catch
        {
            TryDelete(tempDir);
            throw;
        }
    }

    public static (double MaxGapSeconds, double StartSeconds, double EndSeconds) ComputeMaxNoSpeechGap(
        IReadOnlyList<SpeechInterval> intervals, double durationSeconds)
    {
        var sorted = intervals
            .Select(i => new SpeechInterval(Math.Max(0, i.StartSeconds), Math.Max(0, i.EndSeconds)))
            .OrderBy(i => i.StartSeconds).ToList();
        double prev = 0, maxGap = 0, gStart = 0, gEnd = 0;
        foreach (var iv in sorted)
        {
            if (iv.StartSeconds - prev > maxGap)
            {
                maxGap = iv.StartSeconds - prev;
                gStart = prev;
                gEnd = iv.StartSeconds;
            }
            prev = Math.Max(prev, iv.EndSeconds);
        }
        if (durationSeconds - prev > maxGap)
        {
            maxGap = durationSeconds - prev;
            gStart = prev;
            gEnd = durationSeconds;
        }
        return (maxGap, gStart, gEnd);
    }

    public static string ClassifyPosition(double start, double end, double duration)
    {
        if (start <= EdgeEpsilonSeconds) return "head";
        if (duration - end <= EdgeEpsilonSeconds) return "tail";
        return "middle";
    }

    public static (bool Ok, string Reason) CheckAvailable(ClientSettings settings)
    {
        var engine = TikTokAsrEngine.Normalize(settings.TiktokSilenceAsrEngine);
        if (engine == TikTokAsrEngine.Volcengine)
        {
            return HasVolcCredentials(settings)
                ? (true, "")
                : (false, "未配置火山 ASR（系统设置 → ASR 配置 → AppID / AccessToken）。");
        }

        return SherpaOnnxModelResolver.CheckAvailable(settings);
    }

    private static bool HasVolcCredentials(ClientSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.TiktokSilenceAsrAppId)
        && !string.IsNullOrWhiteSpace(settings.TiktokSilenceAsrAccessToken);

    private static string FirstHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return string.Join(" ", values).Trim();
        if (response.Content.Headers.TryGetValues(name, out var contentValues))
            return string.Join(" ", contentValues).Trim();
        return "";
    }

    private static bool IsThrottle(string text)
    {
        var haystack = (text ?? "").ToLowerInvariant();
        return haystack.Contains("45000292")
            || haystack.Contains("concurrency")
            || haystack.Contains("too many requests")
            || haystack.Contains("quota exceeded");
    }

    private static async Task RetryDelayAsync(int attempt, CancellationToken ct)
    {
        var baseDelay = VolcRetryBase.TotalSeconds * Math.Pow(2, attempt - 1);
        var delay = Math.Min(baseDelay, VolcRetryCap.TotalSeconds) + new Random().NextDouble();
        await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
    }

    private static string Truncate(string text, int max) =>
        text is null ? "" : (text.Length <= max ? text : text.Substring(0, max));

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch { /* 忽略清理失败 */ }
    }

    public static string FormatTimestamp(double seconds)
    {
        var v = Math.Max(0, seconds);
        var minutes = (int)(v / 60);
        var rest = v - minutes * 60;
        return $"{minutes}:{rest.ToString("00.0", CultureInfo.InvariantCulture)}";
    }
}
