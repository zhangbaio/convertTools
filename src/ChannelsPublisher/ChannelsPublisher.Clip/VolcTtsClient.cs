using System.Text;
using System.Text.Json;

namespace ChannelsPublisher.Clip;

/// <summary>火山 TTS（一次性合成）客户端。移植自 material_clip/tts.py 的 volcengine 路径。
/// 注意鉴权头是 "Bearer;{token}"（分号，不是空格）。返回 mp3 字节。</summary>
public sealed class VolcTtsClient
{
    private const string Endpoint = "https://openspeech.bytedance.com/api/v1/tts";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<byte[]> SynthesizeAsync(string text, double speedRatio, ClipEngineOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.VolcAppId) || string.IsNullOrWhiteSpace(opts.VolcAccessToken))
            throw new Exception("未配置火山 TTS 凭据（复用 material_clip_volcengine_app_id/access_token）");

        var cluster = string.IsNullOrWhiteSpace(opts.TtsCluster) ? "volcano_tts" : opts.TtsCluster;
        var body = JsonSerializer.Serialize(new
        {
            app = new { appid = opts.VolcAppId, token = opts.VolcAccessToken, cluster },
            user = new { uid = opts.VolcAppId },
            audio = new
            {
                voice_type = string.IsNullOrWhiteSpace(opts.TtsVoiceType) ? "BV701_streaming" : opts.TtsVoiceType,
                encoding = "mp3",
                speed_ratio = Math.Round(Math.Clamp(speedRatio, 0.5, 2.0), 3),
            },
            request = new
            {
                reqid = Guid.NewGuid().ToString("N"),
                text,
                operation = "query",
                text_type = "plain",
            },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer;{opts.VolcAccessToken}");

        using var resp = await Http.SendAsync(req, ct);
        var payload = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new Exception($"火山 TTS HTTP {(int)resp.StatusCode}: {Trim(payload)}");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        int code = root.TryGetProperty("code", out var codeEl) && codeEl.TryGetInt32(out var cv) ? cv : -1;
        if (code != 3000)
        {
            var msg = root.TryGetProperty("message", out var mEl) ? mEl.GetString() : payload;
            throw new Exception($"火山 TTS 失败 code={code}: {Trim(msg ?? "")}");
        }
        var data = root.TryGetProperty("data", out var dEl) ? dEl.GetString() : null;
        if (string.IsNullOrEmpty(data)) throw new Exception("火山 TTS 返回空音频");
        return Convert.FromBase64String(data);
    }

    private static string Trim(string s) { s = (s ?? "").Trim(); return s.Length <= 300 ? s : s[..300]; }
}
