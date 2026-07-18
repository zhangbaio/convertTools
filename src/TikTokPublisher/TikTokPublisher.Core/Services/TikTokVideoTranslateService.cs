using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>视频翻译：火山 ASR → 火山/LLM 翻译 → ASS 中文字幕烧录 → 校验后原子替换。</summary>
public static class TikTokVideoTranslateService
{
    private const string AsrEndpoint = "https://openspeech.bytedance.com/api/v3/auc/bigmodel/recognize/flash";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(8) };
    private sealed record Cue(double Start, double End, string Source, string Translation = "");

    public static async Task TranslateAsync(string projectDir, string title, string originalTitle,
        ClientSettings settings, Action<string>? log, CancellationToken ct)
    {
        ValidateSettings(settings);
        var payload = TikTokUploadStagingService.BuildPayload(
            projectDir, title, originalTitle, rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0) throw new InvalidOperationException("视频翻译失败：未找到上传视频。");
        log?.Invoke($"开始视频翻译：共 {payload.UploadPaths.Count} 集，翻译引擎 {settings.VideoTranslateEngine}。");
        for (var i = 0; i < payload.UploadPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = payload.UploadPaths[i];
            log?.Invoke($"第 {i + 1} 集：正在识别外语台词…");
            var cues = await RecognizeAsync(path, settings, ct).ConfigureAwait(false);
            if (cues.Count == 0) throw new InvalidOperationException($"第 {i + 1} 集 ASR 未识别到台词。");
            var translated = await TranslateTextsAsync(cues.Select(x => x.Source).ToList(), settings, ct).ConfigureAwait(false);
            var final = cues.Select((c, n) => c with { Translation = translated[n] }).ToList();
            await BurnAndReplaceAsync(path, final, settings, ct).ConfigureAwait(false);
            log?.Invoke($"第 {i + 1} 集：翻译及中文字幕烧录完成（{cues.Count} 条）。");
        }
        log?.Invoke("视频翻译完成：所有输出均已校验并覆盖上传副本。");
    }

    private static void ValidateSettings(ClientSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.TiktokSilenceAsrAppId) || string.IsNullOrWhiteSpace(s.TiktokSilenceAsrAccessToken))
            throw new InvalidOperationException("视频翻译需要火山 ASR AppID / AccessToken（系统设置 → ASR 配置）。");
        if (s.VideoTranslateEngine == "volc" && (string.IsNullOrWhiteSpace(s.VideoTranslateVolcAccessKeyId) || string.IsNullOrWhiteSpace(s.VideoTranslateVolcSecretAccessKey)))
            throw new InvalidOperationException("未配置火山翻译 AccessKeyId / SecretAccessKey（系统服务 → 翻译配置）。");
        if (s.VideoTranslateEngine != "volc" && string.IsNullOrWhiteSpace(s.VideoTranslateLlmApiKey))
            throw new InvalidOperationException("未配置大模型翻译 API Key（系统服务 → 翻译配置）。");
    }

    private static async Task<List<Cue>> RecognizeAsync(string video, ClientSettings s, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"tiktok-translate-{Guid.NewGuid():N}"); Directory.CreateDirectory(temp);
        var wav = Path.Combine(temp, "audio.wav");
        try
        {
            await FfmpegRunner.RunAsync(MediaBinaryResolver.ResolveFfmpeg(), ["-y", "-hide_banner", "-loglevel", "error", "-i", video, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav], ct).ConfigureAwait(false);
            var audio = await File.ReadAllBytesAsync(wav, ct).ConfigureAwait(false);
            var body = new JsonObject {
                ["user"] = new JsonObject { ["uid"] = s.TiktokSilenceAsrAppId },
                ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(audio) },
                ["request"] = new JsonObject { ["model_name"]="bigmodel", ["model_version"]="400", ["enable_itn"]=true, ["enable_punc"]=true, ["show_utterances"]=true, ["language"]=s.VideoTranslateSourceLanguage }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, AsrEndpoint);
            req.Headers.TryAddWithoutValidation("X-Api-Resource-Id", "volc.bigasr.auc_turbo");
            req.Headers.TryAddWithoutValidation("X-Api-Sequence", "-1");
            req.Headers.TryAddWithoutValidation("X-Api-App-Key", s.TiktokSilenceAsrAppId);
            req.Headers.TryAddWithoutValidation("X-Api-Access-Key", s.TiktokSilenceAsrAccessToken);
            req.Headers.TryAddWithoutValidation("X-Api-Request-Id", Guid.NewGuid().ToString());
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"火山 ASR 请求失败：{(int)resp.StatusCode} {Truncate(json)}");
            var items = JsonNode.Parse(json)?["result"]?["utterances"] as JsonArray;
            return items?.OfType<JsonObject>().Select(x => new Cue((x["start_time"]?.GetValue<double>() ?? 0) / 1000d, (x["end_time"]?.GetValue<double>() ?? 0) / 1000d, x["text"]?.GetValue<string>()?.Trim() ?? "")).Where(x => x.Source.Length > 0).ToList() ?? [];
        }
        finally { try { Directory.Delete(temp, true); } catch { } }
    }

    private static async Task<List<string>> TranslateTextsAsync(List<string> texts, ClientSettings s, CancellationToken ct)
    {
        var result = new List<string>(texts.Count);
        foreach (var batch in texts.Chunk(s.VideoTranslateEngine == "volc" ? 16 : 40))
        {
            var translated = s.VideoTranslateEngine == "volc"
                ? await TranslateVolcAsync(batch, s, ct).ConfigureAwait(false)
                : await TranslateLlmAsync(batch, s, ct).ConfigureAwait(false);
            if (translated.Count != batch.Length) throw new InvalidOperationException("翻译返回条数与输入不一致。");
            result.AddRange(translated);
        }
        return result;
    }

    private static async Task<List<string>> TranslateLlmAsync(string[] texts, ClientSettings s, CancellationToken ct)
    {
        var numbered = string.Join('\n', texts.Select((x, i) => $"{i + 1}|{x}"));
        var payload = JsonSerializer.Serialize(new { model=s.VideoTranslateLlmModel, temperature=0.3, stream=false, messages=new[] { new { role="system", content=$"你是专业短剧字幕翻译。将每行翻译成自然简洁的{s.VideoTranslateTargetLanguage}字幕。严格按 序号|译文 逐行输出，不增删。" }, new { role="user", content=numbered } } });
        using var req = new HttpRequestMessage(HttpMethod.Post, s.VideoTranslateLlmBaseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.VideoTranslateLlmApiKey); req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false); var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"大模型翻译失败：{(int)resp.StatusCode} {Truncate(json)}");
        var content = JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var map = content.Split('\n').Select(x => x.Trim()).Select(x => x.Split('|', 2)).Where(x => x.Length == 2 && int.TryParse(x[0], out _)).ToDictionary(x => int.Parse(x[0]), x => x[1].Trim());
        return Enumerable.Range(1, texts.Length).Select(i => map.GetValueOrDefault(i) ?? texts[i - 1]).ToList();
    }

    private static async Task<List<string>> TranslateVolcAsync(string[] texts, ClientSettings s, CancellationToken ct)
    {
        const string host="translate.volcengineapi.com", region="cn-north-1", service="translate", action="TranslateText", version="2020-06-01";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { TargetLanguage=s.VideoTranslateTargetLanguage, SourceLanguage=s.VideoTranslateSourceLanguage, TextList=texts });
        var now=DateTime.UtcNow; var xdate=now.ToString("yyyyMMdd'T'HHmmss'Z'"); var date=now.ToString("yyyyMMdd"); var hash=Hex(SHA256.HashData(body));
        var query=$"Action={action}&Version={version}"; var ctype="application/json; charset=utf-8"; var headers=$"content-type:{ctype}\nhost:{host}\nx-content-sha256:{hash}\nx-date:{xdate}\n"; var signed="content-type;host;x-content-sha256;x-date";
        var canonical=$"POST\n/\n{query}\n{headers}\n{signed}\n{hash}"; var scope=$"{date}/{region}/{service}/request"; var toSign=$"HMAC-SHA256\n{xdate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key=Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes(s.VideoTranslateVolcSecretAccessKey), date), region), service), "request"); var sig=Hex(Hmac(key,toSign));
        using var req=new HttpRequestMessage(HttpMethod.Post,$"https://{host}/?{query}"); req.Content=new ByteArrayContent(body); req.Content.Headers.ContentType=MediaTypeHeaderValue.Parse(ctype);
        req.Headers.TryAddWithoutValidation("Host",host); req.Headers.TryAddWithoutValidation("X-Date",xdate); req.Headers.TryAddWithoutValidation("X-Content-Sha256",hash); req.Headers.TryAddWithoutValidation("Authorization",$"HMAC-SHA256 Credential={s.VideoTranslateVolcAccessKeyId}/{scope}, SignedHeaders={signed}, Signature={sig}");
        using var resp=await Http.SendAsync(req,ct).ConfigureAwait(false); var json=await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); if(!resp.IsSuccessStatusCode) throw new InvalidOperationException($"火山翻译失败：{Truncate(json)}");
        var root=JsonNode.Parse(json); if(root?["ResponseMetadata"]?["Error"] is not null) throw new InvalidOperationException($"火山翻译失败：{root["ResponseMetadata"]!["Error"]}");
        return (root?["TranslationList"] as JsonArray)?.Select(x=>x?["Translation"]?.GetValue<string>()??"").ToList() ?? [];
    }

    private static async Task BurnAndReplaceAsync(string video, List<Cue> cues, ClientSettings s, CancellationToken ct)
    {
        var dir=Path.GetDirectoryName(video)!; var stem=Path.GetFileNameWithoutExtension(video); var ass=Path.Combine(dir,$".{stem}.translate.ass"); var output=Path.Combine(dir,$".{stem}.translated.mp4");
        try
        {
            var duration=await FfmpegRunner.ProbeDurationSecondsAsync(MediaBinaryResolver.ResolveFfprobe(),video,ct).ConfigureAwait(false);
            var text=BuildAss(cues,s); await File.WriteAllTextAsync(ass,text,new UTF8Encoding(false),ct).ConfigureAwait(false);
            var filter=$"ass='{ass.Replace("\\","/").Replace(":","\\:").Replace("'","\\'")}'";
            await FfmpegRunner.RunAsync(MediaBinaryResolver.ResolveFfmpeg(),["-y","-hide_banner","-loglevel","error","-i",video,"-vf",filter,"-c:v","libx264","-preset","veryfast","-crf","18","-pix_fmt","yuv420p","-c:a","copy",output],ct).ConfigureAwait(false);
            var outDuration=await FfmpegRunner.ProbeDurationSecondsAsync(MediaBinaryResolver.ResolveFfprobe(),output,ct).ConfigureAwait(false); if(Math.Abs(duration-outDuration)>1.5) throw new InvalidOperationException("翻译成片时长校验失败。");
            File.Move(output,video,true);
        }
        finally { TryDelete(ass); TryDelete(output); }
    }

    private static string BuildAss(List<Cue> cues, ClientSettings s)
    {
        var b=new StringBuilder($"[Script Info]\nScriptType: v4.00+\nPlayResX: 720\nPlayResY: 1280\nWrapStyle: 0\n\n[V4+ Styles]\nFormat: Name,Fontname,Fontsize,PrimaryColour,SecondaryColour,OutlineColour,BackColour,Bold,Italic,Underline,StrikeOut,ScaleX,ScaleY,Spacing,Angle,BorderStyle,Outline,Shadow,Alignment,MarginL,MarginR,MarginV,Encoding\nStyle: Default,{s.VideoTranslateFont},{Math.Clamp(s.VideoTranslateFontSize,12,120)},&H00FFFFFF,&H000000FF,&H00202020,&H64000000,1,0,0,0,100,100,0,0,1,2,1,2,40,40,{Math.Clamp(s.VideoTranslateMarginV,0,600)},1\n\n[Events]\nFormat: Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text\n");
        foreach(var c in cues){var t=Escape(s.VideoTranslateBilingual?$"{c.Translation}\\N{c.Source}":c.Translation); b.AppendLine($"Dialogue: 0,{Ts(c.Start)},{Ts(c.End)},Default,,0,0,0,,{t}");} return b.ToString();
    }
    private static string Ts(double x)=>TimeSpan.FromSeconds(x).ToString(@"h\:mm\:ss\.ff",CultureInfo.InvariantCulture);
    private static string Escape(string x)=>x.Replace("{","（").Replace("}","）").Replace("\r","").Replace("\n","\\N");
    private static byte[] Hmac(byte[] key,string value)=>new HMACSHA256(key).ComputeHash(Encoding.UTF8.GetBytes(value));
    private static string Hex(byte[] value)=>Convert.ToHexString(value).ToLowerInvariant();
    private static string Truncate(string x)=>x.Length<=300?x:x[..300];
    private static void TryDelete(string p){try{if(File.Exists(p))File.Delete(p);}catch{}}
}
