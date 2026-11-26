using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShortDrama.Infrastructure.AI;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

/// <summary>
/// 源语言选「AI识别语言」时，上传前识别成具体语种。本土剧依据原剧资料，海外剧依据发布剧名。
/// </summary>
public static class TikTokNewTitleLanguageService
{
    public const string Auto = "auto";
    private const string CacheFileName = ".tiktok-source-language.json";
    private static readonly string[] KnownCodes = ["zh", "en", "id", "pt", "ja", "th", "es", "ko", "tr", "hi"];
    private static readonly string[] LatinCodes = ["en", "id", "pt", "es", "tr"];

    public static bool IsAuto(string? value) =>
        string.Equals((value ?? "").Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    public static void ApplyKnown(
        TikTokPublishOptions options,
        TikTokAccountProfile account,
        string? projectDir)
    {
        if (!IsAuto(account.TiktokSourceLanguage))
            return;

        options.SourceLanguage = TikTokNewTitleLanguageService.Auto;
    }

    public static async Task ApplyAsync(
        TikTokPublishOptions options,
        TikTokAccountProfile account,
        string? projectDir,
        string? fallbackTitle,
        Action<string>? log,
        CancellationToken ct,
        string? originalTitle = null)
    {
        if (!IsAuto(account.TiktokSourceLanguage))
            return;

        var market = TikTokAccountProfile.NormalizeDramaMarket(account.TiktokDramaMarket);
        options.SourceLanguage = market == TikTokAccountProfile.DramaMarketLocal
            ? await ResolveOriginalLanguageAsync(projectDir, originalTitle, account, log, ct).ConfigureAwait(false)
            : await ResolveAsync(fallbackTitle, projectDir, settings: null, log, ct).ConfigureAwait(false);
        if (options.IsAiDrama &&
            string.Equals(options.SourceLanguage, "zh", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(options.ContentCreationType, "remake", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "TikTok 发布配置不合理：仅源语言为非中文的短剧可选择「成片重制」。" +
                "请将源语言改为非中文，或将内容创作类型改为「原创/小说改编」。");
        }
    }

    public static async Task<string> ResolveAsync(
        string? newTitle,
        string? projectDir,
        ClientSettings? settings,
        Action<string>? log,
        CancellationToken ct)
    {
        var title = (newTitle ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            log?.Invoke("源语言为 AI 识别，但项目还没有新剧名，按中文处理。");
            return "zh";
        }

        if (TryReadRewriteLanguage(projectDir, title, out var rewriteLanguage))
        {
            SaveCache(projectDir, title, rewriteLanguage);
            log?.Invoke($"源语言复用标题和简介已识别的{DisplayName(rewriteLanguage)}，新剧名「{title}」。");
            return rewriteLanguage;
        }

        if (TryReadCache(projectDir, title, out var cached))
        {
            log?.Invoke($"源语言按新剧名「{title}」使用已识别结果：{DisplayName(cached)}。");
            return cached;
        }

        var script = ClassifyScript(title);
        if (script is not null)
        {
            SaveCache(projectDir, title, script);
            log?.Invoke($"源语言按新剧名「{title}」识别为{DisplayName(script)}。");
            return script;
        }

        try
        {
            var detected = await AskLatinLanguageAsync(title, settings ?? ClientSettingsStore.Load(), ct);
            if (LatinCodes.Contains(detected, StringComparer.OrdinalIgnoreCase))
            {
                SaveCache(projectDir, title, detected);
                log?.Invoke($"源语言按新剧名「{title}」识别为{DisplayName(detected)}。");
                return detected;
            }

            log?.Invoke($"源语言识别结果「{detected}」不在已有语种中，新剧名「{title}」按英语处理。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"源语言 AI 识别失败，新剧名「{title}」按英语处理：{ex.Message}");
        }

        return "en";
    }

    private static async Task<string> ResolveOriginalLanguageAsync(
        string? projectDir, string? originalTitle, TikTokAccountProfile account, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var title = (originalTitle ?? "").Trim();
        var synopsis = "";
        if (!string.IsNullOrWhiteSpace(projectDir))
        {
            var context = ProjectWorkspaceService.LoadContext(projectDir);
            var source = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(context.SourceProjectDir, "短剧信息.txt"));
            var workflow = ProjectInfoTextHelper.ParseInfoFile(Path.Combine(context.WorkflowProjectDir, "短剧信息.txt"));
            title = FirstNonEmpty(source.GetValueOrDefault("原剧名"), workflow.GetValueOrDefault("原剧名"),
                RewriteSourceLanguage.ReadImported(context.SourceProjectDir).Title,
                RewriteSourceLanguage.ReadImported(context.WorkflowProjectDir).Title, title);
            synopsis = ProjectOriginalSynopsisService.Resolve(context, title, workflow);
        }
        if (title.Length == 0 && synopsis.Length == 0)
        {
            log?.Invoke("未找到原剧资料，源语言按本土剧默认值中文处理。");
            return RewriteSourceLanguage.Chinese;
        }

        var sourceText = $"{title}\n{synopsis}";
        var language = ClassifyScript(sourceText);

        if (language is not null)
        {
            log?.Invoke($"源语言按原剧资料识别为{DisplayName(language)}。");
            return language;
        }

        // Distinguish original material from the publish-title cache and invalidate when either source field changes.
        var cacheKey = "original:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText)));
        if (TryReadCache(projectDir, cacheKey, out var cached))
        {
            log?.Invoke($"源语言复用原剧资料的识别结果：{DisplayName(cached)}。");
            return cached;
        }

        try
        {
            var settings = TikTokAccountAiSettings.Apply(ClientSettingsStore.Load(), account);
            var prompt = RewriteSourceLanguage.BuildDetectionPrompt(title, synopsis) +
                         "\n按 JSON 对象返回，例如 {\"language\":\"zh\"}。";
            var detected = await AskLanguageAsync(prompt, settings, ct).ConfigureAwait(false);
            if (KnownCodes.Contains(detected, StringComparer.OrdinalIgnoreCase))
            {
                SaveCache(projectDir, cacheKey, detected);
                log?.Invoke($"源语言按原剧资料识别为{DisplayName(detected)}。");
                return detected;
            }
            log?.Invoke("原剧资料的 AI 源语言识别未返回支持的语种，使用文字特征判断。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log?.Invoke($"原剧资料的 AI 源语言识别失败，使用文字特征判断：{ex.Message}");
        }

        return RewriteSourceLanguage.Detect(title, synopsis);
    }

    private static bool TryReadRewriteLanguage(string? projectDir, string title, out string language)
    {
        language = "";
        foreach (var dir in CandidateProjectDirs(projectDir))
        {
            var cached = RewriteSourceLanguage.ReadCached(dir);
            if (cached is null || !TitleAgreesWithRewriteLanguage(title, cached))
                continue;

            language = cached;
            return true;
        }

        return false;
    }

    private static bool TitleAgreesWithRewriteLanguage(string title, string language)
    {
        var script = ClassifyScript(title);
        if (script is null)
            return LatinCodes.Contains(language, StringComparer.OrdinalIgnoreCase);

        return string.Equals(script, language, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CandidateProjectDirs(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            yield break;

        string workflow = "";
        try
        {
            workflow = TikTokUploadStateStore.ResolveWorkflowProjectDir(projectDir);
        }
        catch (Exception)
        {
            workflow = "";
        }

        if (!string.IsNullOrWhiteSpace(workflow))
            yield return workflow;
        if (!string.Equals(workflow, projectDir, StringComparison.OrdinalIgnoreCase))
            yield return projectDir;
    }

    public static string? ClassifyScript(string? title)
    {
        var cjk = 0;
        var latin = 0;
        var hangul = 0;
        var kana = 0;
        var thai = 0;
        var devanagari = 0;
        foreach (var ch in title ?? "")
        {
            if (ch is >= '\u4E00' and <= '\u9FFF')
                cjk++;
            else if (ch is >= '\uAC00' and <= '\uD7AF')
                hangul++;
            else if (ch is >= '\u3040' and <= '\u30FF')
                kana++;
            else if (ch is >= '\u0E00' and <= '\u0E7F')
                thai++;
            else if (ch is >= '\u0900' and <= '\u097F')
                devanagari++;
            else if (char.IsLetter(ch))
                latin++;
        }

        if (devanagari > 0 && devanagari >= cjk && devanagari >= latin)
            return "hi";
        if (kana > 0 && kana + cjk >= latin)
            return "ja";
        if (hangul > 0 && hangul >= cjk && hangul >= latin)
            return "ko";
        if (thai > 0 && thai >= cjk && thai >= latin)
            return "th";
        if (cjk > 0 && cjk >= latin)
            return "zh";
        if (latin > 0)
            return null;
        return "zh";
    }

    public static string NormalizeModelCode(string? raw)
    {
        var text = (raw ?? "").Trim().Trim('"', '\'', '`', ' ', '\r', '\n');
        var code = text.ToLowerInvariant();
        if (KnownCodes.Contains(code, StringComparer.Ordinal))
            return code;

        foreach (var known in KnownCodes)
        {
            if (TikTokPublishConstants.SourceLanguageAliases.TryGetValue(known, out var aliases) &&
                aliases.Any(alias => text.Contains(alias, StringComparison.OrdinalIgnoreCase)))
            {
                return known;
            }
        }

        return "";
    }

    public static string ReadNewTitle(string? projectDir, string? fallbackTitle)
    {
        if (!string.IsNullOrWhiteSpace(projectDir))
        {
            string workflow;
            try
            {
                workflow = TikTokUploadStateStore.ResolveWorkflowProjectDir(projectDir);
            }
            catch
            {
                workflow = projectDir;
            }

            var info = ProjectInfoTextHelper.MergeProjectInfo(
                Path.Combine(projectDir, "短剧信息.txt"),
                Path.Combine(workflow, "短剧信息.txt"));
            var fromFile = FirstNonEmpty(info.GetValueOrDefault("新剧名"), info.GetValueOrDefault("剧名"));
            if (!string.IsNullOrWhiteSpace(fromFile))
                return fromFile.Trim();
        }

        return (fallbackTitle ?? "").Trim();
    }

    private static Task<string> AskLatinLanguageAsync(
        string title, ClientSettings settings, CancellationToken ct)
    {
        var prompt = """
            判断新剧名的语言。只能从 en、id、pt、es、tr 里选一个。
            en=英语，id=印尼语，pt=葡萄牙语，es=西班牙语，tr=土耳其语。
            只输出 JSON：{"language":"en"}
            新剧名：
            """ + title;
        return AskLanguageAsync(prompt, settings, ct);
    }

    private static async Task<string> AskLanguageAsync(
        string prompt, ClientSettings settings, CancellationToken ct)
    {
        var endpoint = (settings.AiTextEndpoint ?? "").Trim().TrimEnd('/');
        var apiKey = (settings.AiTextApiKey ?? "").Trim();
        var model = (settings.AiTextModel ?? "").Trim();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("AI 文本接口未配置");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                temperature = 0,
                thinking = new { type = "disabled" },
                messages = new object[]
                {
                    new { role = "system", content = "你只输出一个 JSON 对象，language 必须是给定代码之一。" },
                    new { role = "user", content = prompt },
                },
            }),
            Encoding.UTF8,
            "application/json");

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.AiTextTimeoutSeconds, 10, 120)),
        };
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");

        using var document = JsonDocument.Parse(body);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
        return ParseLanguageCode(content);
    }

    internal static string ParseLanguageCode(string content)
    {
        var text = (content ?? "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                var node = JsonNode.Parse(text[start..(end + 1)]);
                var fromJson = NormalizeModelCode(node?["language"]?.ToString());
                if (!string.IsNullOrWhiteSpace(fromJson))
                    return fromJson;
            }
            catch (JsonException)
            {
                // 继续按纯文本代码解析。
            }
        }

        return NormalizeModelCode(text);
    }

    private static bool TryReadCache(string? projectDir, string title, out string language)
    {
        language = "";
        var path = CachePath(projectDir);
        if (path is null || !File.Exists(path))
            return false;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var cachedTitle = node?["title"]?.ToString()?.Trim() ?? "";
            var cachedLanguage = NormalizeModelCode(node?["language"]?.ToString());
            if (!string.Equals(cachedTitle, title, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(cachedLanguage))
            {
                return false;
            }

            language = cachedLanguage;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SaveCache(string? projectDir, string title, string language)
    {
        var path = CachePath(projectDir);
        if (path is null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { title, language }));
        }
        catch (Exception)
        {
            // 缓存写失败不影响本次上传。
        }
    }

    private static string? CachePath(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
            return null;

        try
        {
            var workflow = TikTokUploadStateStore.ResolveWorkflowProjectDir(projectDir);
            if (string.IsNullOrWhiteSpace(workflow))
                workflow = projectDir;
            return Path.Combine(workflow, CacheFileName);
        }
        catch
        {
            return Path.Combine(projectDir, CacheFileName);
        }
    }

    private static string DisplayName(string code) =>
        TikTokPublishConstants.SourceLanguageAliases.TryGetValue(code, out var aliases) && aliases.Count > 0
            ? aliases[0]
            : code;

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => (value ?? "").Trim()).FirstOrDefault(value => value.Length > 0) ?? "";
}
