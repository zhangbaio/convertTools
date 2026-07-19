using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

internal sealed class ElevenLabsDubbingClient
{
    private const string BaseUrl = "https://api.elevenlabs.io/v1";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ClientSettings _settings;

    public ElevenLabsDubbingClient(ClientSettings settings) => _settings = settings;

    public async Task<string> CreateAsync(string videoPath, CancellationToken ct)
    {
        await using var stream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "file", Path.GetFileName(videoPath));
        form.Add(new StringContent(_settings.ElevenLabsSourceLanguage), "source_lang");
        form.Add(new StringContent(_settings.ElevenLabsTargetLanguage), "target_lang");
        form.Add(new StringContent("0"), "num_speakers");
        form.Add(new StringContent("true"), "highest_resolution");
        form.Add(new StringContent(_settings.ElevenLabsDropBackgroundAudio ? "true" : "false"), "drop_background_audio");
        form.Add(new StringContent(_settings.ElevenLabsDisableVoiceCloning ? "true" : "false"), "disable_voice_cloning");
        using var response = await SendAsync(HttpMethod.Post, "/dubbing", form, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureSuccess(response, json, "创建配音任务");
        return JsonNode.Parse(json)?["dubbing_id"]?.GetValue<string>()
               ?? throw new InvalidOperationException("ElevenLabs 创建配音任务未返回 dubbing_id。");
    }

    public async Task<(string Status, string Error)> GetStatusAsync(string dubbingId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/dubbing/{Uri.EscapeDataString(dubbingId)}", null,
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureSuccess(response, json, "查询配音任务");
        var root = JsonNode.Parse(json);
        return (root?["status"]?.GetValue<string>() ?? "", root?["error"]?.GetValue<string>() ?? "");
    }

    public Task DownloadVideoAsync(string dubbingId, string outputPath, CancellationToken ct) =>
        DownloadAsync($"/dubbing/{Uri.EscapeDataString(dubbingId)}/audio/{Uri.EscapeDataString(_settings.ElevenLabsTargetLanguage)}", outputPath, ct);

    public async Task<string> GetSrtAsync(string dubbingId, CancellationToken ct)
    {
        var path = $"/dubbing/{Uri.EscapeDataString(dubbingId)}/transcripts/{Uri.EscapeDataString(_settings.ElevenLabsTargetLanguage)}/format/srt";
        using var response = await SendAsync(HttpMethod.Get, path, null, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureSuccess(response, json, "下载英文字幕");
        return JsonNode.Parse(json)?["srt"]?.GetValue<string>()
               ?? throw new InvalidOperationException("ElevenLabs 字幕响应中没有 srt 字段。");
    }

    private async Task DownloadAsync(string path, string outputPath, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            EnsureSuccess(response, error, "下载配音视频");
        }
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        HttpCompletionOption completion, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path) { Content = content };
        request.Headers.TryAddWithoutValidation("xi-api-key", _settings.ElevenLabsApiKey);
        try { return await Http.SendAsync(request, completion, ct).ConfigureAwait(false); }
        finally { request.Dispose(); }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var text = body.Length <= 500 ? body : body[..500];
        throw new InvalidOperationException($"ElevenLabs {operation}失败：{(int)response.StatusCode} {text}");
    }
}
