using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

/// <summary>
/// 源语言选「AI识别语言」时，只根据生成的新剧名判断，结果必须是发布配置里已有的语种。
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

        var title = ReadNewTitle(projectDir, fallbackTitle: null);
        if (TryReadCache(projectDir, title, out var cached))
        {
            options.SourceLanguage = cached;
            return;
        }

        var script = ClassifyScript(title);
        if (script is not null)
            options.SourceLanguage = script;
    }

    public static async Task ApplyAsync(
        TikTokPublishOptions options,
        TikTokAccountProfile account,
        string? projectDir,
        string? fallbackTitle,
        Action<string>? log,
        CancellationToken ct)
    {
        if (!IsAuto(account.TiktokSourceLanguage))
            return;

        var title = ReadNewTitle(projectDir, fallbackTitle);
        var language = await ResolveAsync(title, projectDir, ClientSettingsStore.Load(), log, ct);
        options.SourceLanguage = language;
        if (account.TiktokIsAiDrama &&
            string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                TikTokPublishConstants.NormalizeContentCreationType(account.TiktokContentCreationType),
                "remake",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "TikTok 发布配置不合理：新剧名识别为中文，不能选择「成片重制」。" +
                "请将内容创作类型改为「原创/小说改编」，或把源语言改为非中文。");
        }
    }

    public static async Task<string> ResolveAsync(
        string? newTitle,
        string? projectDir,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        var title = (newTitle ?? "").Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            log?.Invoke("源语言为 AI 识别，但项目还没有新剧名，按中文处理。");
            return "zh";
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
            var detected = await AskLatinLanguageAsync(title, settings, ct);
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

    private static async Task<string> AskLatinLanguageAsync(
        string title,
        ClientSettings settings,
        CancellationToken ct)
    {
        var endpoint = (settings.AiTextEndpoint ?? "").Trim().TrimEnd('/');
        var apiKey = (settings.AiTextApiKey ?? "").Trim();
        var model = (settings.AiTextModel ?? "").Trim();
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("AI 文本接口未配置");

        var prompt = """
            判断新剧名的语言。只能从 en、id、pt、es、tr 里选一个。
            en=英语，id=印尼语，pt=葡萄牙语，es=西班牙语，tr=土耳其语。
            只输出 JSON：{"language":"en"}
            新剧名：
            """ + title;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                temperature = 0,
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
