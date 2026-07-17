using System.Net;
using System.Text;
using System.Text.Json;

namespace ChannelsPublisher.Clip;

/// <summary>火山在线 ASR（大模型极速版 flash，一段式同步）。移植自 material_clip/asr.py 的 volcengine 路径。
/// 输入 16k 单声道 WAV，返回带毫秒时间戳的分句字幕。</summary>
public sealed class VolcengineAsrClient
{
    private const string Endpoint = "https://openspeech.bytedance.com/api/v3/auc/bigmodel/recognize/flash";
    private const int MaxAttempts = 5;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    public async Task<List<SubtitleSegment>> TranscribeAsync(string wavPath, ClipEngineOptions opts, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.VolcAppId))
            throw new Exception("未配置火山 AppId（material_clip_volcengine_app_id）");

        var audioB64 = Convert.ToBase64String(await File.ReadAllBytesAsync(wavPath, ct));
        var json = JsonSerializer.Serialize(new
        {
            user = new { uid = opts.VolcAppId },
            audio = new { data = audioB64 },
            request = new
            {
                model_name = "bigmodel",
                model_version = "400",
                enable_itn = true,
                enable_punc = true,
                enable_ddc = true,
                show_utterances = true,
            },
        });

        var rng = new Random();
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Api-Resource-Id", "volc.bigasr.auc_turbo");
            req.Headers.TryAddWithoutValidation("X-Api-Sequence", "-1");
            req.Headers.TryAddWithoutValidation("X-Api-Request-Id", $"clip-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{attempt}");
            if (!string.IsNullOrWhiteSpace(opts.VolcAccessToken))
            {
                req.Headers.TryAddWithoutValidation("X-Api-App-Key", opts.VolcAppId);
                req.Headers.TryAddWithoutValidation("X-Api-Access-Key", opts.VolcAccessToken);
            }
            else
            {
                req.Headers.TryAddWithoutValidation("X-Api-Key", opts.VolcAppId);
            }

            HttpResponseMessage resp;
            try { resp = await Http.SendAsync(req, ct); }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                log?.Invoke($"ASR 请求异常，退避重试 {attempt}：{ex.Message}");
                await BackoffAsync(rng, attempt, ct);
                continue;
            }

            var statusCode = resp.Headers.TryGetValues("X-Api-Status-Code", out var sv) ? string.Concat(sv) : "";
            var payload = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode && statusCode == "20000000")
                return ParseUtterances(payload);

            bool concurrency = resp.StatusCode == (HttpStatusCode)429
                || statusCode == "45000292"
                || payload.Contains("concurrency", StringComparison.OrdinalIgnoreCase)
                || payload.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase);
            if (concurrency && attempt < MaxAttempts)
            {
                log?.Invoke($"ASR 并发/额度受限，退避重试 {attempt}");
                await BackoffAsync(rng, attempt, ct);
                continue;
            }

            var msg = resp.Headers.TryGetValues("X-Api-Message", out var mv) ? string.Concat(mv) : payload;
            throw new Exception($"火山 ASR 失败（HTTP {(int)resp.StatusCode}, status {statusCode}）：{TrimMsg(msg)}");
        }
        throw new Exception("火山 ASR 重试次数用尽");
    }

    private static async Task BackoffAsync(Random rng, int attempt, CancellationToken ct)
    {
        double sec = Math.Min(2.0 * Math.Pow(2, attempt - 1), 30.0) + rng.NextDouble();
        await Task.Delay(TimeSpan.FromSeconds(sec), ct);
    }

    private static List<SubtitleSegment> ParseUtterances(string payload)
    {
        var list = new List<SubtitleSegment>();
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return list;
        if (!result.TryGetProperty("utterances", out var utts) || utts.ValueKind != JsonValueKind.Array) return list;
        foreach (var u in utts.EnumerateArray())
        {
            var text = u.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            int start = ReadMs(u, "start_time");
            int end = ReadMs(u, "end_time");
            if (!string.IsNullOrWhiteSpace(text)) list.Add(new SubtitleSegment(start, Math.Max(start, end), text.Trim()));
        }
        return list;
    }

    private static int ReadMs(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number) return 0;
        if (el.TryGetInt32(out var i)) return i;
        return (int)Math.Round(el.GetDouble());
    }

    private static string TrimMsg(string s) { s = (s ?? "").Trim(); return s.Length <= 300 ? s : s[..300]; }
}
