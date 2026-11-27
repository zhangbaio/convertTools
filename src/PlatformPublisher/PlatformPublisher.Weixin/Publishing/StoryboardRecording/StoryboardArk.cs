using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SixLabors.ImageSharp;

namespace PlatformPublisher.Weixin.Publishing;

internal sealed record StoryboardFrame(double Seconds, string Path);
internal sealed record StoryboardAsset(string Kind, string Label, string Name, string Description, string? Image,
    string? ReferenceSource = null, int? ReferenceShot = null, int? ReferenceFrame = null, double? ReferenceSeconds = null);
internal sealed record StoryboardAnalysis(string[] Prompts, IReadOnlyList<StoryboardAsset> Assets);

internal sealed class StoryboardArk(StoryboardArkSettings settings, HttpClient http)
{
    private sealed record Reference(string Name, string Description, int Number, StoryboardFrame Frame, bool Partial = false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Uri _visionUri = Endpoint(settings.VisionEndpoint, "chat/completions");
    private readonly Uri _imageUri = Endpoint(settings.ImageEndpoint, "images/generations");
    private readonly string _visionKey = First(settings.VisionKey, Environment.GetEnvironmentVariable("ARK_API_KEY"), settings.ImageKey);
    private readonly string _imageKey = First(settings.ImageKey, Environment.GetEnvironmentVariable("ARK_API_KEY"), settings.VisionKey);

    public object Signature => new { Vision = _visionUri, settings.VisionModel, Image = _imageUri, settings.ImageModel };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(settings.VisionModel) || string.IsNullOrWhiteSpace(settings.ImageModel))
            throw new InvalidOperationException("请在系统设置中填写方舟视觉模型和 Seedream 图片模型。");
        if (_visionKey.Length == 0 || _imageKey.Length == 0)
            throw new InvalidOperationException("请在系统设置中填写火山方舟 API Key。");
    }

    public async Task<StoryboardAnalysis> GenerateAsync(
        int episode, string source, string stage, IReadOnlyList<StoryboardFrame> shots, double duration,
        Func<IReadOnlyList<double>, CancellationToken, Task<IReadOnlyList<StoryboardFrame>>> sample,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"第{episode}集 · 方舟反推 {shots.Count} 个镜头");
        var root = await ChatAsync(StoryboardArkPrompts.Analysis, "镜头", shots, 2500, 0.2, ct).ConfigureAwait(false);
        var prompts = new string[shots.Count];
        if (root["shots"] is not JsonArray shotArray || root["assets"] is not JsonObject assetObject)
            throw new InvalidDataException($"第{episode}集方舟返回的分镜结构无效。");
        foreach (var item in shotArray)
        {
            var number = Number(item?["shot"], shots.Count);
            if (number == 0 || prompts[number - 1] is not null || Text(item?["prompt"]) is not { Length: > 0 } prompt)
                throw new InvalidDataException($"第{episode}集方舟返回的镜号或提示词无效。");
            prompts[number - 1] = prompt;
        }
        if (prompts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"第{episode}集方舟漏掉了镜头提示词。");

        var prop = await FindPropAsync(episode, duration, sample, progress, ct).ConfigureAwait(false);
        var assets = new List<StoryboardAsset>();
        foreach (var (kind, label, size, instruction) in StoryboardArkPrompts.Assets)
        {
            var scanned = kind == "prop" ? prop : null;
            var reference = scanned ?? ParseReference(assetObject[kind], "reference_shot", shots);
            if (reference is null)
            {
                progress?.Report($"第{episode}集 · 抽帧未确认可用的{label}参考");
                assets.Add(new StoryboardAsset(kind, label, "", "", null));
                continue;
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report($"第{episode}集 · Seedream 正在生成{label}");
            var response = await PostAsync(_imageUri, _imageKey, new
            {
                model = settings.ImageModel,
                prompt = $"{instruction} 保留参考画面的美术风格。不要字幕、文字、台标或水印。{reference.Name}：{reference.Description}",
                image = await ImageDataAsync(reference.Frame, ct).ConfigureAwait(false),
                size, response_format = "b64_json", watermark = false,
            }, ct).ConfigureAwait(false);
            var imageName = $"第{episode}集{label}.png";
            if (response["data"] is not JsonArray images || images.Count == 0 ||
                Text(images[0]?["b64_json"]) is not { Length: > 0 } encoded)
                throw new InvalidDataException($"第{episode}集{label}未返回可用图片。");
            using var image = Image.Load(Convert.FromBase64String(encoded));
            await image.SaveAsPngAsync(Path.Combine(stage, imageName), ct).ConfigureAwait(false);
            assets.Add(new StoryboardAsset(kind, label, reference.Name, reference.Description, imageName,
                scanned is null ? "storyboard" : "prop_scan",
                scanned is null ? reference.Number : null, scanned?.Number, Math.Round(reference.Frame.Seconds, 3)));
        }

        var report = new
        {
            episode, source = Path.GetFileName(source), settings.VisionModel, settings.ImageModel,
            shots = shots.Select((shot, i) => new { shot = i + 1, sourceSeconds = Math.Round(shot.Seconds, 3), prompt = prompts[i] }),
            assets = assets.ToDictionary(item => item.Kind, item => item.Image is null ? null : item),
        };
        await File.WriteAllTextAsync(Path.Combine(stage, $"第{episode}集分镜提示词.json"),
            JsonSerializer.Serialize(report, Json), ct).ConfigureAwait(false);
        return new StoryboardAnalysis(prompts, assets);
    }

    private async Task<Reference?> FindPropAsync(int episode, double duration,
        Func<IReadOnlyList<double>, CancellationToken, Task<IReadOnlyList<StoryboardFrame>>> sample,
        IProgress<string>? progress, CancellationToken ct)
    {
        async Task<Reference?> ScanAsync(IReadOnlyList<StoryboardFrame> frames, string? target = null)
        {
            if (frames.Count == 0) return null;
            progress?.Report($"第{episode}集 · 方舟{(target is null ? "搜索" : "复核")}道具，共 {frames.Count} 张候选帧");
            var prompt = StoryboardArkPrompts.Prop + (target is null ? "" : $"本轮只复核与「{target}」相同的物件，不切换道具。");
            var response = await ChatAsync(prompt, "候选帧", frames, 500, 0.1, ct).ConfigureAwait(false);
            return ParseReference(response["prop"], "reference_frame", frames);
        }

        Reference? prop = null;
        try
        {
            prop = await ScanAsync(await sample(SampleTimes(duration, .5), ct).ConfigureAwait(false)).ConfigureAwait(false);
            if (prop is null)
                prop = await ScanAsync(await sample(SampleTimes(duration, .25), ct).ConfigureAwait(false)).ConfigureAwait(false);
            else if (prop.Partial)
            {
                var anchor = prop;
                var nearby = new[] { -2, -1, -.5, .5, 1, 2 }.Select(offset => anchor.Frame.Seconds + offset)
                    .Where(time => time >= 0 && time < duration).ToArray();
                var frames = await sample(nearby, ct).ConfigureAwait(false);
                var refined = await ScanAsync(frames.Append(anchor.Frame).OrderBy(frame => frame.Seconds).ToArray(),
                    anchor.Name).ConfigureAwait(false);
                if (refined is { Partial: false }) prop = refined;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or TimeoutException)
        {
            progress?.Report($"第{episode}集 · 道具选帧暂不可用，使用已有参考。{ex.Message}");
        }
        if (prop is not null)
            progress?.Report($"第{episode}集 · 道具「{prop.Name}」参考画面 {prop.Frame.Seconds:F2} 秒");
        return prop;
    }

    internal static double[] SampleTimes(double duration, double offset) =>
        Enumerable.Range(0, 12).Select(index => (index + offset) * duration / 12).ToArray();

    private static Reference? ParseReference(JsonNode? node, string key, IReadOnlyList<StoryboardFrame> frames)
    {
        if (node is not JsonObject item) return null;
        var name = Text(item["name"]);
        var description = Text(item["description"]);
        var number = Number(item[key], frames.Count);
        if (number == 0 || (name.Length == 0 && description.Length == 0)) return null;
        return new Reference(name.Length == 0 ? description[..Math.Min(28, description.Length)] : name,
            description.Length == 0 ? name : description, number, frames[number - 1],
            key == "reference_frame" && Text(item["visibility"]) != "clear");
    }

    private async Task<JsonObject> ChatAsync(string prompt, string label, IReadOnlyList<StoryboardFrame> frames,
        int maxTokens, double temperature, CancellationToken ct)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        for (var i = 0; i < frames.Count; i++)
        {
            content.Add(new { type = "text", text = FormattableString.Invariant($"{label} {i + 1}，源视频 {frames[i].Seconds:F2} 秒") });
            content.Add(new { type = "image_url", image_url = new { url = await ImageDataAsync(frames[i], ct).ConfigureAwait(false), detail = "high" } });
        }
        var response = await PostAsync(_visionUri, _visionKey, new
        {
            model = settings.VisionModel, temperature, max_tokens = maxTokens,
            thinking = new { type = "disabled" }, messages = new[] { new { role = "user", content } },
        }, ct).ConfigureAwait(false);
        if (response["choices"] is not JsonArray choices || choices.Count == 0)
            throw new InvalidDataException("方舟没有返回视觉分析结果。");
        var text = Text(choices[0]?["message"]?["content"]);
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start || JsonNode.Parse(text[start..(end + 1)]) is not JsonObject result)
            throw new InvalidDataException("方舟视觉分析没有返回有效的 JSON 对象。");
        return result;
    }

    private async Task<JsonObject> PostAsync(Uri uri, string key, object data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(data) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"火山方舟调用失败 (HTTP {(int)response.StatusCode})。");
            return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("火山方舟返回空结果。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("火山方舟请求超时。");
        }
    }

    private static Uri Endpoint(string endpoint, string action)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.StartsWith("ark.", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.EndsWith(".volces.com", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("请在系统设置中填写火山方舟的 HTTPS 视觉和图片接口地址。");
        return new Uri($"{uri.AbsoluteUri.TrimEnd('/')}/{action}");
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
    private static int Number(JsonNode? node, int max) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) && number >= 1 && number <= max ? number : 0;
    private static async Task<string> ImageDataAsync(StoryboardFrame frame, CancellationToken ct) =>
        "data:image/jpeg;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(frame.Path, ct).ConfigureAwait(false));
}
