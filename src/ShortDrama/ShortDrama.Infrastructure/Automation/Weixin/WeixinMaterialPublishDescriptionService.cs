using ShortDrama.Core.Models;
using ChannelsPublisher.Clip;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation.Weixin;

public sealed class WeixinMaterialPublishDescriptionService
{
    private const string PromptVersion = "csharp-publish-ai-description-v1";
    private const int MaxDescriptionLength = 500;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly Regex SrtIndexRegex = new(@"^\s*\d+\s*$", RegexOptions.Compiled);
    private static readonly Regex SrtClockRegex = new(@"-->|^\s*\d{1,2}:\d{2}:\d{2}", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex HashtagJoinRegex = new(@"(?<=[^\s#])#", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public WeixinMaterialPublishDescriptionService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> ResolveAsync(
        string workflowProjectDir,
        ProjectInfo projectInfo,
        WeixinVideoPublishOptions options,
        WeixinMaterialPublishPage.PublishVideoItem publishItem,
        string baseDescription,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.AiDescriptionEnabled)
        {
            return baseDescription;
        }

        if (string.IsNullOrWhiteSpace(options.AiTextEndpoint) ||
            string.IsNullOrWhiteSpace(options.AiTextApiKey) ||
            string.IsNullOrWhiteSpace(options.AiTextModel))
        {
            progress?.Report("AI 视频描述：文本模型未配置，沿用基础文案。");
            return baseDescription;
        }

        var context = await BuildContextAsync(
            workflowProjectDir,
            projectInfo,
            options,
            publishItem,
            baseDescription,
            progress,
            cancellationToken);
        var cachePath = BuildCachePath(workflowProjectDir, publishItem.VideoPath, context.InputHash);
        if (options.AiDescriptionCacheEnabled)
        {
            var cached = TryReadCachedDescription(cachePath, context);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                progress?.Report($"AI 视频描述：复用缓存 {Path.GetFileName(publishItem.VideoPath)}");
                return cached;
            }
        }

        var attempts = Math.Max(1, options.AiDescriptionRetryAttempts);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var generated = await GenerateAsync(options, context, cancellationToken);
                var normalized = NormalizeGeneratedDescription(generated, projectInfo, options, baseDescription);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return baseDescription;
                }

                if (options.AiDescriptionCacheEnabled)
                {
                    WriteCache(cachePath, context, normalized, options.AiTextModel);
                }

                progress?.Report($"AI 视频描述：已生成 {Path.GetFileName(publishItem.VideoPath)} -> {TrimForLog(normalized)}");
                return normalized;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < attempts)
                {
                    progress?.Report($"AI 视频描述：第 {attempt} 次生成失败，准备重试：{ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 8)), cancellationToken);
                }
            }
        }

        if (options.AiDescriptionFallbackToOriginal)
        {
            progress?.Report($"AI 视频描述：生成失败，沿用基础文案：{lastError?.Message}");
            return baseDescription;
        }

        throw new InvalidOperationException($"AI 视频描述生成失败：{lastError?.Message}", lastError);
    }

    private async Task<string> GenerateAsync(
        WeixinVideoPublishOptions options,
        PublishDescriptionContext context,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.AiDescriptionTimeoutSeconds)));

        var payload = new
        {
            model = options.AiTextModel,
            temperature = 0.7,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是短视频发布文案助手，擅长把短剧素材上下文改写成自然、吸引人的视频号发布描述。"
                },
                new
                {
                    role = "user",
                    content = BuildPrompt(context)
                }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            options.AiTextEndpoint.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AiTextApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, linkedCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(linkedCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {responseText}");
        }

        var content = ExtractChatContent(responseText);
        return ExtractDescription(content);
    }

    private static async Task<PublishDescriptionContext> BuildContextAsync(
        string workflowProjectDir,
        ProjectInfo projectInfo,
        WeixinVideoPublishOptions options,
        WeixinMaterialPublishPage.PublishVideoItem publishItem,
        string baseDescription,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var sourceType = WeixinMaterialPublishPage.NormalizeVideoSourceMode(options.VideoSourceMode);
        var sourceMetadata = ReadNearestProjectMetadata(publishItem.VideoPath);
        var references = ReadReferenceDescriptions(publishItem.VideoPath);
        var transcript = options.AiDescriptionUseAsr
            ? await ResolveTranscriptAsync(
                workflowProjectDir,
                sourceType,
                options,
                publishItem.VideoPath,
                progress,
                cancellationToken)
            : string.Empty;

        var metadata = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source_type"] = sourceType,
            ["episode_index"] = publishItem.EpisodeIndex,
            ["workflow_project_dir"] = workflowProjectDir,
            ["source_video_path"] = publishItem.VideoPath,
            ["description_title"] = projectInfo.Title,
            ["original_title"] = projectInfo.OriginalTitle,
            ["short_title"] = projectInfo.ShortTitle,
            ["tags"] = projectInfo.Tags,
            ["mount_title"] = options.NewDramaMountResolvedTitle,
            ["mount_book_id"] = options.NewDramaMountResolvedBookId
        };

        foreach (var item in sourceMetadata)
        {
            metadata.TryAdd(item.Key, item.Value);
        }

        var input = new JsonObject
        {
            ["prompt_version"] = PromptVersion,
            ["source_type"] = sourceType,
            ["episode_index"] = publishItem.EpisodeIndex,
            ["description_title"] = projectInfo.Title,
            ["new_title"] = projectInfo.Title,
            ["original_title"] = projectInfo.OriginalTitle,
            ["short_title"] = projectInfo.ShortTitle,
            ["tag_text"] = projectInfo.Tags,
            ["base_description"] = baseDescription,
            ["metadata"] = JsonSerializer.SerializeToNode(metadata, JsonOptions),
            ["reference_descriptions"] = ToJsonArray(references),
            ["transcript_text"] = transcript
        };

        return new PublishDescriptionContext(
            SourceType: sourceType,
            EpisodeIndex: publishItem.EpisodeIndex,
            VideoPath: publishItem.VideoPath,
            SourceFingerprint: BuildFileFingerprint(publishItem.VideoPath),
            InputHash: Sha256Hex(input.ToJsonString(JsonOptions)),
            BaseDescription: baseDescription,
            Metadata: metadata,
            ReferenceDescriptions: references,
            TranscriptText: transcript);
    }

    private static string BuildPrompt(PublishDescriptionContext context)
    {
        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source_type"] = context.SourceType,
            ["episode_index"] = context.EpisodeIndex,
            ["base_description"] = context.BaseDescription,
            ["metadata"] = context.Metadata,
            ["reference_descriptions"] = context.ReferenceDescriptions.Take(6).ToArray(),
            ["transcript_excerpt"] = TrimTo(context.TranscriptText, 2600)
        };

        return """
请根据下面的视频上下文，生成一个适合视频号发表视频的描述。
要求：
1. 只返回 JSON：{"description":"..."}。
2. 描述不超过 500 字，开头用 12-24 字的口语化钩子。
3. 保留短剧语境，不要编造不存在的演员、平台数据、承诺收益。
4. 结尾带 3-6 个话题标签，优先包含要发布/挂载的新剧名和“短剧推荐”。
5. 如果上下文不足，以基础文案为主做自然改写。

上下文：
""" + JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string NormalizeGeneratedDescription(
        string generated,
        ProjectInfo projectInfo,
        WeixinVideoPublishOptions options,
        string baseDescription)
    {
        var text = WhitespaceRegex.Replace(generated.Trim(), " ");
        if (string.IsNullOrWhiteSpace(text))
        {
            return baseDescription;
        }

        text = HashtagJoinRegex.Replace(text, " #");
        if (!text.Contains('#', StringComparison.Ordinal))
        {
            var title = FirstNonEmpty(projectInfo.Title, options.NewDramaMountResolvedTitle, projectInfo.OriginalTitle);
            var tags = new[] { title, "短剧推荐" }
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(SanitizeTag)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .Select(item => "#" + item);
            text = $"{text} {string.Join(" ", tags)}".Trim();
        }

        return TrimTo(text, MaxDescriptionLength);
    }

    private static string ExtractChatContent(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private static string ExtractDescription(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var first = text.IndexOf('{');
            var last = text.LastIndexOf('}');
            if (first >= 0 && last > first)
            {
                text = text[first..(last + 1)];
            }
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            foreach (var key in new[] { "description", "caption", "text" })
            {
                if (document.RootElement.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString()?.Trim() ?? string.Empty;
                }
            }
        }
        catch
        {
            return text;
        }

        return text;
    }

    private static string TryReadCachedDescription(string cachePath, PublishDescriptionContext context)
    {
        if (!File.Exists(cachePath))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(cachePath, Encoding.UTF8));
            var root = document.RootElement;
            if (!StringPropertyEquals(root, "prompt_version", PromptVersion) ||
                !StringPropertyEquals(root, "source_fingerprint", context.SourceFingerprint) ||
                !StringPropertyEquals(root, "input_hash", context.InputHash))
            {
                return string.Empty;
            }

            return root.TryGetProperty("generated_description", out var description) &&
                   description.ValueKind == JsonValueKind.String
                ? description.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void WriteCache(
        string cachePath,
        PublishDescriptionContext context,
        string generatedDescription,
        string model)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
            var payload = new JsonObject
            {
                ["prompt_version"] = PromptVersion,
                ["created_at"] = DateTimeOffset.Now.ToString("O"),
                ["source_type"] = context.SourceType,
                ["episode_index"] = context.EpisodeIndex,
                ["video_path"] = context.VideoPath,
                ["source_fingerprint"] = context.SourceFingerprint,
                ["input_hash"] = context.InputHash,
                ["model"] = model,
                ["base_description"] = context.BaseDescription,
                ["reference_descriptions"] = ToJsonArray(context.ReferenceDescriptions),
                ["generated_description"] = generatedDescription,
                ["metadata"] = JsonSerializer.SerializeToNode(context.Metadata, JsonOptions)
            };
            File.WriteAllText(cachePath, payload.ToJsonString(JsonOptions), Encoding.UTF8);
        }
        catch
        {
            // Cache write failure should never block publishing.
        }
    }

    private static string BuildCachePath(string workflowProjectDir, string videoPath, string inputHash)
    {
        var root = Path.Combine(workflowProjectDir, ".publish-ai-description", "descriptions");
        var fileName = Sha256Hex($"{Path.GetFullPath(videoPath)}|{inputHash}")[..24] + ".json";
        return Path.Combine(root, fileName);
    }

    private static SortedDictionary<string, object?> ReadNearestProjectMetadata(string videoPath)
    {
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var directory = Path.GetDirectoryName(Path.GetFullPath(videoPath));
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var metadataPath = Path.Combine(directory, "shortdrama-project.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(metadataPath, Encoding.UTF8));
                    foreach (var key in new[]
                             {
                                 "title", "displayName", "sourceName", "originalTitle", "intro",
                                 "category", "episodeCount", "bookId", "book_id", "author"
                             })
                    {
                        if (document.RootElement.TryGetProperty(key, out var value))
                        {
                            result[key] = value.ValueKind switch
                            {
                                JsonValueKind.String => value.GetString(),
                                JsonValueKind.Number => value.GetRawText(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => value.GetRawText()
                            };
                        }
                    }
                }
                catch
                {
                }

                return result;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadReferenceDescriptions(string videoPath)
    {
        var results = new List<string>();
        AddSidecarDescription(results, Path.Combine(
            Path.GetDirectoryName(videoPath) ?? ".",
            Path.GetFileNameWithoutExtension(videoPath) + ".publish.json"));

        AddInputDescriptions(results, Path.Combine(
            Path.GetDirectoryName(videoPath) ?? ".",
            Path.GetFileNameWithoutExtension(videoPath) + ".inputs.json"));

        return results
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }

    private static void AddInputDescriptions(ICollection<string> results, string inputsPath)
    {
        if (!File.Exists(inputsPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(inputsPath, Encoding.UTF8));
            if (!document.RootElement.TryGetProperty("inputs", out var inputs) ||
                inputs.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in inputs.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    AddSidecarDescription(results, Path.ChangeExtension(item.GetString(), ".publish.json"));
                }
                else if (item.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                {
                    AddSidecarDescription(results, Path.ChangeExtension(path.GetString(), ".publish.json"));
                }
            }
        }
        catch
        {
        }
    }

    private static void AddSidecarDescription(ICollection<string> results, string? sidecarPath)
    {
        if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath, Encoding.UTF8));
            foreach (var key in new[] { "description", "caption" })
            {
                if (document.RootElement.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(text);
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static string ReadTranscriptSidecar(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var ext in new[] { ".srt", ".vtt", ".txt" })
        {
            var path = Path.Combine(directory, stem + ext);
            if (!File.Exists(path))
            {
                continue;
            }

            var text = ReadTranscriptFile(path);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static async Task<string> ResolveTranscriptAsync(
        string workflowProjectDir,
        string sourceType,
        WeixinVideoPublishOptions options,
        string videoPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var transcript = ReadTranscriptSidecar(videoPath);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            return transcript;
        }

        if (!string.Equals(sourceType, "new_drama_mount", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var cachePath = BuildAsrCachePath(workflowProjectDir, videoPath);
        transcript = ReadTranscriptFile(cachePath);
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            progress?.Report($"AI 视频描述：复用 ASR 字幕缓存 {Path.GetFileName(videoPath)}");
            return transcript;
        }

        try
        {
            var segments = await TranscribeVideoAsync(options, videoPath, progress, cancellationToken);
            if (segments.Count == 0)
            {
                return string.Empty;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? ".");
            await File.WriteAllTextAsync(cachePath, Srt.Write(segments), Encoding.UTF8, cancellationToken);
            return TrimTo(string.Join(" ", segments.Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text))), 5000);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report($"AI 视频描述：ASR 字幕参考生成失败，继续使用其他上下文（{ex.GetType().Name}: {ex.Message}）");
            return string.Empty;
        }
    }

    private static async Task<IReadOnlyList<SubtitleSegment>> TranscribeVideoAsync(
        WeixinVideoPublishOptions options,
        string videoPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var clipOptions = BuildClipAsrOptions(options);
        if (!HasUsableAsrConfig(clipOptions))
        {
            progress?.Report("AI 视频描述：未配置可用 ASR 参数，跳过字幕参考。");
            return [];
        }

        var wavPath = Path.Combine(Path.GetTempPath(), $"weixin-ai-desc-asr-{Guid.NewGuid():N}.wav");
        try
        {
            progress?.Report($"AI 视频描述：正在提取字幕参考 {Path.GetFileName(videoPath)}");
            await Ffmpeg.ExtractAudioAsync(clipOptions.FfmpegPath, videoPath, wavPath, cancellationToken);
            return await RunClipAsrAsync(wavPath, clipOptions, progress, cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(wavPath);
            }
            catch
            {
            }
        }
    }

    private static async Task<IReadOnlyList<SubtitleSegment>> RunClipAsrAsync(
        string wavPath,
        ClipEngineOptions clipOptions,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var engine = NormalizeAsrEngine(clipOptions.AsrEngine);
        var volc = new VolcengineAsrClient();
        var local = new LocalAsrClient();
        void Log(string message) => progress?.Report($"AI 视频描述 ASR：{message}");

        if (engine == "local")
        {
            return await local.TranscribeAsync(wavPath, clipOptions, Log, cancellationToken);
        }

        if (engine == "hybrid")
        {
            if (!HasLocalAsrConfig(clipOptions))
            {
                return await volc.TranscribeAsync(wavPath, clipOptions, Log, cancellationToken);
            }

            var localSegments = await local.TranscribeAsync(wavPath, clipOptions, Log, cancellationToken);
            var speechSeconds = localSegments.Sum(segment => Math.Max(0, segment.EndMs - segment.StartMs)) / 1000.0;
            var chars = localSegments.Sum(segment => (segment.Text ?? string.Empty).Trim().Length);
            var density = speechSeconds > 0 ? chars / speechSeconds : 0;
            if (speechSeconds > 0 && (density >= clipOptions.HybridMinCharsPerSec || !HasVolcAsrConfig(clipOptions)))
            {
                return localSegments;
            }

            progress?.Report($"AI 视频描述 ASR：本地字密度 {density:F2} < {clipOptions.HybridMinCharsPerSec:F2}，改用火山复核。");
            try
            {
                return await volc.TranscribeAsync(wavPath, clipOptions, Log, cancellationToken);
            }
            catch (Exception ex)
            {
                progress?.Report($"AI 视频描述 ASR：火山复核失败，保留本地结果（{ex.Message}）");
                return localSegments;
            }
        }

        return await volc.TranscribeAsync(wavPath, clipOptions, Log, cancellationToken);
    }

    private static ClipEngineOptions BuildClipAsrOptions(WeixinVideoPublishOptions options)
    {
        return new ClipEngineOptions
        {
            AsrEngine = NormalizeAsrEngine(options.AiDescriptionAsrEngine),
            AsrLanguage = string.IsNullOrWhiteSpace(options.AiDescriptionAsrLanguage)
                ? "zh-CN"
                : options.AiDescriptionAsrLanguage,
            VolcAppId = options.AiDescriptionVolcengineAppId,
            VolcAccessToken = options.AiDescriptionVolcengineAccessToken,
            LocalModelDir = options.AiDescriptionLocalModelDir,
            LocalVadPath = options.AiDescriptionLocalVadPath,
            LocalUseItn = options.AiDescriptionLocalUseItn,
            HybridMinCharsPerSec = options.AiDescriptionHybridMinCharsPerSec <= 0
                ? 1.0
                : options.AiDescriptionHybridMinCharsPerSec,
            FfmpegPath = string.IsNullOrWhiteSpace(options.FfmpegPath) ? "ffmpeg" : options.FfmpegPath,
            FfprobePath = string.IsNullOrWhiteSpace(options.FfprobePath) ? "ffprobe" : options.FfprobePath
        };
    }

    private static bool HasUsableAsrConfig(ClipEngineOptions options)
    {
        var engine = NormalizeAsrEngine(options.AsrEngine);
        return engine switch
        {
            "local" => HasLocalAsrConfig(options),
            "hybrid" => HasLocalAsrConfig(options) || HasVolcAsrConfig(options),
            _ => HasVolcAsrConfig(options)
        };
    }

    private static bool HasVolcAsrConfig(ClipEngineOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.VolcAppId);
    }

    private static bool HasLocalAsrConfig(ClipEngineOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.LocalModelDir) && Directory.Exists(options.LocalModelDir);
    }

    private static string NormalizeAsrEngine(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "local" or "hybrid" ? normalized : "volcengine";
    }

    private static string ReadTranscriptFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Where(line => !SrtIndexRegex.IsMatch(line))
                .Where(line => !SrtClockRegex.IsMatch(line))
                .Where(line => !line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase));
            return TrimTo(string.Join(" ", lines), 5000);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildAsrCachePath(string workflowProjectDir, string videoPath)
    {
        var root = Path.Combine(workflowProjectDir, ".publish-ai-description", "asr");
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (stem.Length > 80)
        {
            stem = stem[..80];
        }

        stem = SanitizeFileName(string.IsNullOrWhiteSpace(stem) ? "video" : stem);
        var fileName = $"{stem}-{Sha256Hex(Path.GetFullPath(videoPath))[..12]}.srt";
        return Path.Combine(root, fileName);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string BuildFileFingerprint(string path)
    {
        var info = new FileInfo(path);
        return info.Exists
            ? $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"
            : $"{Path.GetFullPath(path)}|missing";
    }

    private static bool StringPropertyEquals(JsonElement element, string key, string expected)
    {
        return element.TryGetProperty(key, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static string Sha256Hex(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string SanitizeTag(string value)
    {
        return Regex.Replace(value.Trim().TrimStart('#'), @"[^\u4e00-\u9fff\p{L}\p{Nd}]+", string.Empty);
    }

    private static string SanitizeFileName(string value)
    {
        var text = value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(text) ? "video" : text;
    }

    private static string TrimForLog(string value)
    {
        return TrimTo(value, 48);
    }

    private static string TrimTo(string value, int maxLength)
    {
        var text = value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private sealed record PublishDescriptionContext(
        string SourceType,
        int EpisodeIndex,
        string VideoPath,
        string SourceFingerprint,
        string InputHash,
        string BaseDescription,
        SortedDictionary<string, object?> Metadata,
        IReadOnlyList<string> ReferenceDescriptions,
        string TranscriptText);
}
