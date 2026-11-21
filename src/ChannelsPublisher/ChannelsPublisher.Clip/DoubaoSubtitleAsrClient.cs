using System.Text.Json;

namespace ChannelsPublisher.Clip;

internal static class DoubaoSubtitleAsrClient
{
    private const string Endpoint = "https://openspeech.bytedance.com/api/v1/vc";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static async Task<List<SubtitleSegment>> TranscribeAsync(string wavPath, ClipEngineOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.DoubaoAppId) || string.IsNullOrWhiteSpace(options.DoubaoAccessToken))
            throw new InvalidOperationException("请在剪辑服务配置填写豆包字幕 ASR 的 APP ID 和 Access Token。");

        var language = options.AsrLanguage.ToLowerInvariant() switch
        {
            "en" or "en-us" => "en-US",
            "ja" or "ja-jp" => "ja-JP",
            "ko" or "ko-kr" => "ko-KR",
            _ => "zh-CN",
        };
        var appId = Uri.EscapeDataString(options.DoubaoAppId);
        using var submit = new HttpRequestMessage(HttpMethod.Post,
            $"{Endpoint}/submit?appid={appId}&language={language}&use_itn=True&caption_type=speech&max_lines=1");
        submit.Content = new StreamContent(File.OpenRead(wavPath));
        submit.Content.Headers.ContentType = new("audio/wav");
        using var submitted = await SendAsync(submit, options.DoubaoAccessToken, ct);
        var ticket = submitted.RootElement;
        if (ticket.GetProperty("code").GetInt32() != 0 || !ticket.TryGetProperty("id", out var job) || string.IsNullOrWhiteSpace(job.ToString()))
            throw new InvalidOperationException("豆包字幕 ASR 未接受识别任务，请检查凭据和服务额度。");

        var id = Uri.EscapeDataString(job.ToString());
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            using var query = new HttpRequestMessage(HttpMethod.Get, $"{Endpoint}/query?appid={appId}&id={id}&blocking=0");
            using var response = await SendAsync(query, options.DoubaoAccessToken, ct);
            var result = response.RootElement;
            var code = result.GetProperty("code").GetInt32();
            if (code == 2000)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }
            if (code != 0)
                throw new InvalidOperationException($"豆包字幕 ASR 识别失败（{code}）。");

            return result.TryGetProperty("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.Array
                ? utterances.EnumerateArray().Select(u => new SubtitleSegment(
                    u.GetProperty("start_time").GetInt32(), u.GetProperty("end_time").GetInt32(),
                    u.GetProperty("text").GetString()?.Trim() ?? "")).ToList()
                : [];
        }
        throw new TimeoutException("豆包字幕 ASR 识别超时。");
    }

    private static async Task<JsonDocument> SendAsync(HttpRequestMessage request, string token, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer; {token}");
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"豆包字幕 ASR 请求失败（HTTP {(int)response.StatusCode}）。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
