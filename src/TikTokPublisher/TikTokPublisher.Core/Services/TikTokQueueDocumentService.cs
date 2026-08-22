using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokEpisodeScriptService
{
    public const string OutputSuffix = "前5集剧本.pdf";
    private const int MaximumTranscriptCharacters = 24000;
    private const int MaximumCharacterTableSourceCharacters = 48000;
    private static readonly TimeSpan CharacterTableRequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(8) };

    internal static string GetOutputPath(QueueProjectItem item, TikTokAccountProfile? account)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var configuredEpisodeCount = ResolveConfiguredEpisodeCount(account);
        var availableVideoCount = ProjectVideoResolver
            .ResolveUploadVideos(context.SourceProjectDir, allowStagedFallback: true)
            .Take(configuredEpisodeCount)
            .Count();
        var targetEpisodeCount = ResolveTargetEpisodeCount(
            account,
            availableVideoCount,
            item.EpisodeCount);
        var title = string.IsNullOrWhiteSpace(item.NewTitle) ? item.Title : item.NewTitle.Trim();
        return BuildOutputPath(context.WorkflowProjectDir, title, targetEpisodeCount);
    }

    internal static bool HasCurrentOutput(QueueProjectItem item, TikTokAccountProfile? account)
    {
        try
        {
            return IsReusablePdf(GetOutputPath(item, account));
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var configuredEpisodeCount = ResolveConfiguredEpisodeCount(account);
        var videos = ProjectVideoResolver.ResolveUploadVideos(context.SourceProjectDir, allowStagedFallback: true)
            .Take(configuredEpisodeCount).ToArray();

        var title = string.IsNullOrWhiteSpace(item.NewTitle) ? item.Title : item.NewTitle.Trim();
        var synopsis = ResolveSynopsis(item, context);
        var targetEpisodeCount = ResolveTargetEpisodeCount(
            account,
            videos.Length,
            item.EpisodeCount);
        if (videos.Length == 0 && string.IsNullOrWhiteSpace(synopsis))
            throw new InvalidOperationException("生成剧本失败：项目既没有可用视频，也没有旧简介。");

        var outputPdf = BuildOutputPath(context.WorkflowProjectDir, title, targetEpisodeCount);
        var outputDocx = Path.ChangeExtension(outputPdf, ".docx");
        if (!forceRerun && IsReusablePdf(outputPdf))
        {
            log?.Invoke($"已跳过生成剧本：本地已存在 {Path.GetFileName(outputPdf)}。");
            return outputPdf;
        }

        EnsureAiConfigured(settings);
        var episodeResults = new EpisodeScriptSection?[targetEpisodeCount];
        using var episodeSlots = new SemaphoreSlim(2, 2);
        var episodeTasks = Enumerable.Range(0, targetEpisodeCount).Select(GenerateEpisodeAsync).ToArray();
        await Task.WhenAll(episodeTasks).ConfigureAwait(false);
        var episodes = episodeResults.Select(result => result!).ToList();

        async Task GenerateEpisodeAsync(int i)
        {
            ct.ThrowIfCancellationRequested();
            await episodeSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (videos.Length > 0)
                {
                    var video = videos[i];
                    log?.Invoke($"剧本 {i + 1}/{targetEpisodeCount}：提取字幕与台词…");
                    var transcript = await QueueWorkloadResourceScheduler.RunAsync(
                        QueueWorkloadResource.Asr,
                        () => ResolveTranscriptAsync(video, settings, log, ct),
                        log,
                        ct).ConfigureAwait(false);
                    log?.Invoke($"剧本 {i + 1}/{targetEpisodeCount}：AI 整理参考版式分场剧本…");
                    var content = await QueueWorkloadResourceScheduler.RunAsync(
                        QueueWorkloadResource.AiText,
                        () => RequestScriptAsync(
                            title, i + 1, Path.GetFileName(video), transcript, settings, ct),
                        log,
                        ct).ConfigureAwait(false);
                    episodeResults[i] = new EpisodeScriptSection(i + 1, Path.GetFileName(video), content);
                }
                else
                {
                    log?.Invoke($"剧本 {i + 1}/{targetEpisodeCount}：无本地视频，正在根据新剧名和旧简介生成分场剧本…");
                    var content = await QueueWorkloadResourceScheduler.RunAsync(
                        QueueWorkloadResource.AiText,
                        () => RequestSynopsisScriptAsync(
                            title, i + 1, targetEpisodeCount, synopsis, settings, ct),
                        log,
                        ct).ConfigureAwait(false);
                    episodeResults[i] = new EpisodeScriptSection(i + 1, "旧简介", content);
                }
            }
            finally
            {
                episodeSlots.Release();
            }
        }

        string characterTable;
        try
        {
            log?.Invoke("剧本：汇总前几集角色表…");
            using var characterTableTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            characterTableTimeout.CancelAfter(CharacterTableRequestTimeout);
            characterTable = await QueueWorkloadResourceScheduler.RunAsync(
                QueueWorkloadResource.AiText,
                () => RequestCharacterTableAsync(
                    title,
                    episodes,
                    settings,
                    characterTableTimeout.Token),
                log,
                characterTableTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log?.Invoke($"角色表 AI 汇总失败，已按分集人物名单生成基础角色表：{ex.Message}");
            characterTable = TikTokQueueDocumentWriter.BuildFallbackCharacterTable(episodes);
        }

        TikTokQueueDocumentWriter.WriteScriptDocument(outputDocx, title, episodes, characterTable);
        await QueueWorkloadResourceScheduler.RunAsync(
            QueueWorkloadResource.Document,
            () => TikTokQueueDocumentWriter.RenderPdfAsync(outputDocx, outputPdf, settings, ct),
            log,
            ct).ConfigureAwait(false);
        if (!settings.TiktokProofKeepDocx) TikTokProofMaterialPdfRenderService.TryDelete(outputDocx);
        log?.Invoke($"前{targetEpisodeCount}集剧本已生成：{outputPdf}");
        return outputPdf;
    }

    private static string BuildOutputPath(
        string workflowProjectDirectory,
        string title,
        int targetEpisodeCount)
    {
        var safeTitle = string.Concat((title ?? string.Empty).Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var outputSuffix = targetEpisodeCount == 5 ? OutputSuffix : $"前{targetEpisodeCount}集剧本.pdf";
        return Path.Combine(workflowProjectDirectory, safeTitle + outputSuffix);
    }

    private static bool IsReusablePdf(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 100) return false;
        Span<byte> header = stackalloc byte[5];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == header.Length && header.SequenceEqual("%PDF-"u8);
    }

    internal static int ResolveConfiguredEpisodeCount(TikTokAccountProfile? account)
    {
        var configured = account?.TiktokEpisodeScriptEpisodeCount ?? 0;
        return Math.Clamp(
            configured > 0 ? configured : TikTokAccountProfile.DefaultEpisodeScriptEpisodeCount,
            1,
            120);
    }

    internal static int ResolveTargetEpisodeCount(
        TikTokAccountProfile? account,
        int availableVideoCount,
        int declaredEpisodeCount)
    {
        var configured = ResolveConfiguredEpisodeCount(account);
        return availableVideoCount > 0
            ? Math.Min(availableVideoCount, configured)
            : Math.Clamp(declaredEpisodeCount > 0 ? declaredEpisodeCount : configured, 1, configured);
    }

    private static string ResolveSynopsis(QueueProjectItem item, ProjectWorkspaceContext context)
    {
        if (!string.IsNullOrWhiteSpace(item.Description)) return item.Description.Trim();
        try
        {
            var metadataPath = new[]
                {
                    Path.Combine(context.WorkflowProjectDir, "shortdrama-project.json"),
                    Path.Combine(context.SourceProjectDir, "shortdrama-project.json"),
                }
                .FirstOrDefault(File.Exists);
            if (metadataPath is null) return "";
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            foreach (var name in new[] { "intro", "description", "desc" })
            {
                if (root.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString()!.Trim();
            }
        }
        catch
        {
            // Queue data remains the primary source; invalid legacy metadata is ignored.
        }
        return "";
    }

    private static async Task<string> RequestSynopsisScriptAsync(
        string title,
        int episode,
        int episodeCount,
        string synopsis,
        ClientSettings settings,
        CancellationToken ct)
    {
        var prompt = BuildSynopsisEpisodePrompt(title, episode, episodeCount, synopsis);
        return await RequestTextAsync(prompt, settings, ct).ConfigureAwait(false);
    }

    internal static string BuildSynopsisEpisodePrompt(
        string title,
        int episode,
        int episodeCount,
        string synopsis) => $"""
        请根据下面的短剧旧简介，为新剧名《{title}》创作前 {episodeCount} 集中的第 {episode} 集分场剧本。
        只输出本集分场正文，不输出剧名、总集数、解释、Markdown 或代码围栏。
        必须保持旧简介中的核心人物关系、主线冲突和结局方向，不得沿用旧剧名，不得增加改变剧情性质的设定。

        格式要求：
        1. 场次依次写成“{episode}-1 场所 时段/室内外”“{episode}-2 场所 时段/室内外”。
        2. 每场第二行写“人物：角色甲，角色乙”。
        3. 画面、动作和镜头描述单独成段并以“△”开头。
        4. 对白写成“角色名：台词”；旁白、内心、音效和音乐分别使用“旁白：”“OS：”“音效：”“BGM：”。
        5. 本集要与前后集衔接，内容量适合一集竖屏短剧；不能声称内容来自视频或字幕。

        旧简介：
        {synopsis}
        """;

    private static async Task<string> ResolveTranscriptAsync(
        string video,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        var srt = Path.ChangeExtension(video, ".srt");
        if (File.Exists(srt))
            return await File.ReadAllTextAsync(srt, ct).ConfigureAwait(false);

        var segments = await TikTokSilenceAsrService
            .RecognizeLocalTranscriptAsync(video, settings, log, ct)
            .ConfigureAwait(false);
        if (segments.Count == 0)
            throw new InvalidOperationException($"{Path.GetFileName(video)} 未识别到有效台词。");
        return string.Join('\n', segments.Select(segment =>
            $"[{FormatTime(segment.StartSeconds)}-{FormatTime(segment.EndSeconds)}] {segment.Text}"));
    }

    private static async Task<string> RequestScriptAsync(
        string title,
        int episode,
        string videoName,
        string transcript,
        ClientSettings settings,
        CancellationToken ct)
    {
        var prompt = BuildEpisodePrompt(title, episode, videoName, transcript);
        return await RequestTextAsync(prompt, settings, ct).ConfigureAwait(false);
    }

    internal static string BuildEpisodePrompt(string title, int episode, string videoName, string transcript)
    {
        var transcriptExcerpt = transcript[..Math.Min(transcript.Length, MaximumTranscriptCharacters)];
        return $"""
                请根据下面的短剧字幕时间轴，把第 {episode} 集整理成可直接排版的中文分场剧本。
                只输出本集的分场正文，不要使用 Markdown、代码围栏、项目符号或序号说明。

                严格使用下面的行格式：
                {episode}-1 场景名称 深夜/室外
                人物：角色甲，角色乙
                △画面、动作、表情或镜头描述。
                角色甲：台词。
                音效：声音说明。
                BGM：音乐说明。

                格式要求：
                1. 第一行必须从“{episode}-1”场次开始，后续场次依次写成“{episode}-2”“{episode}-3”。
                2. 场次标题必须写明场所、时段，以及“室内”或“室外”，格式为“集号-场号 场所 时段/室内外”。
                3. 每个场次标题下一行必须写“人物：”，多个人物使用中文逗号分隔。
                4. 每段画面、动作、表情或镜头描述必须单独成段并以“△”开头。
                5. 对白必须写成“角色名：台词”，不加引号，不在对白前加“△”。
                6. 旁白、内心独白、音效和音乐分别使用“旁白：”“OS：”“音效：”“BGM：”。
                7. 不要输出剧名、视频名、“第 {episode} 集”、本集梗概、分场剧本、角色表或任何解释文字。
                8. 不得编造字幕中不存在的台词、人物关系、年龄、外貌或关键剧情；画面无法确认时使用中性动作描述。

                剧名：{title}
                视频：{videoName}
                字幕时间轴：
                {transcriptExcerpt}
                """;
    }

    private static async Task<string> RequestCharacterTableAsync(
        string title,
        IReadOnlyList<EpisodeScriptSection> episodes,
        ClientSettings settings,
        CancellationToken ct)
    {
        var prompt = BuildCharacterTablePrompt(title, episodes);
        return await RequestTextAsync(prompt, settings, ct).ConfigureAwait(false);
    }

    internal static string BuildCharacterTablePrompt(
        string title,
        IReadOnlyList<EpisodeScriptSection> episodes)
    {
        var perEpisodeBudget = Math.Max(
            512,
            MaximumCharacterTableSourceCharacters / Math.Max(1, episodes.Count) - 64);
        var source = string.Join("\n\n", episodes.Select(episode =>
            $"第 {episode.EpisodeNumber} 集：\n{BalancedExcerpt(episode.Content, perEpisodeBudget)}"));
        return $"""
                请根据以下《{title}》前 {episodes.Count} 集剧本汇总角色表。
                只输出角色条目，不要输出“角色表”等标题，不要使用 Markdown、代码围栏、表格或项目符号。
                每个角色单独一段，格式为“角色名：性别、年龄、外貌、服装、身份、性格或人物关系等剧本已经明确的信息。”
                合并同一角色的重复信息；没有明确出现的信息就省略，严禁推测或编造。

                {source}
                """;
    }

    private static string BalancedExcerpt(string content, int maximumCharacters)
    {
        if (content.Length <= maximumCharacters) return content;
        const string omission = "\n…（中间内容已省略）…\n";
        var retainedCharacters = maximumCharacters - omission.Length;
        var headLength = retainedCharacters / 2;
        var tailLength = retainedCharacters - headLength;
        return content[..headLength] + omission + content[^tailLength..];
    }

    internal static async Task<string> RequestTextAsync(
        string prompt,
        ClientSettings settings,
        CancellationToken ct,
        int maxOutputTokens = 8192)
    {
        var endpoint = settings.AiTextEndpoint.Trim().TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiTextApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = settings.AiTextModel.Trim(),
            temperature = 0.2,
            max_tokens = Math.Clamp(maxOutputTokens, 1024, 32768),
            messages = new object[]
            {
                new { role = "system", content = "你是影视编剧和审核材料整理专家，只依据输入事实整理剧本，并严格遵守指定的纯文本格式。" },
                new { role = "user", content = prompt },
            },
        }), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"剧本生成接口失败：HTTP {(int)response.StatusCode} {body[..Math.Min(body.Length, 500)]}");
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("剧本生成模型返回空内容。");
        return content.Trim();
    }

    internal static void EnsureAiConfigured(ClientSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AiTextEndpoint) ||
            string.IsNullOrWhiteSpace(settings.AiTextApiKey) ||
            string.IsNullOrWhiteSpace(settings.AiTextModel))
            throw new InvalidOperationException("生成剧本失败：请先配置 AI 文本接口、API Key 和模型。");
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff");
}

internal sealed record EpisodeScriptSection(int EpisodeNumber, string VideoFileName, string Content);

internal static class TikTokQueueDocumentWriter
{
    private const string AccentBlue = "4F81BD";
    private const string TitleBlue = "17365D";
    private const string BodyFont = "SimSun";
    private const string BodyLatinFont = "Cambria";
    private const string HeadingFont = "MS Gothic";
    private const string HeadingLatinFont = "Calibri";
    private const int BodyFontSize = 22;
    private const int EpisodeHeadingFontSize = 26;
    private static readonly Regex SceneHeadingPattern = new(
        @"^(?<episode>\d+)\s*[-－—–.]\s*(?<scene>\d+)\s+(?<description>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EpisodeHeadingPattern = new(
        @"^第\s*\d+\s*集(?:\s*[·.．]\s*.+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DialoguePattern = new(
        @"^[^：:]{1,20}[：:]\s*.+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void WriteScriptDocument(
        string path,
        string title,
        IReadOnlyList<EpisodeScriptSection> episodes,
        string characterTable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        TikTokProofMaterialPdfRenderService.TryDelete(path);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;

        body.Append(CreateTitleParagraph($"{title} 前 {episodes.Count} 集剧本"));
        for (var episodeIndex = 0; episodeIndex < episodes.Count; episodeIndex++)
        {
            var episode = episodes[episodeIndex];
            body.Append(CreateEpisodeHeadingParagraph(
                $"第 {episode.EpisodeNumber} 集 · {episode.VideoFileName}",
                before: episodeIndex == 0 ? 0 : 700,
                after: 570));
            body.Append(CreateEpisodeHeadingParagraph(
                $"第 {episode.EpisodeNumber} 集",
                before: 0,
                after: 520));

            var firstScene = true;
            foreach (var line in NormalizeEpisodeLines(episode.Content, episode.EpisodeNumber))
            {
                body.Append(CreateScriptParagraph(line, firstScene));
                if (line.Kind == ScriptParagraphKind.SceneHeading) firstScene = false;
            }
        }

        var characterLines = NormalizeCharacterLines(characterTable);
        if (characterLines.Count > 0)
        {
            body.Append(CreateAggregateCharacterHeadingParagraph("全集角色表"));
            body.Append(CreateCharacterHeadingParagraph("角色表"));
            foreach (var line in characterLines)
                body.Append(CreateBodyParagraph(line));
        }

        body.Append(new SectionProperties(
            new PageSize { Width = 12240, Height = 15840 },
            new PageMargin
            {
                Top = 1440,
                Right = 1800,
                Bottom = 1440,
                Left = 1800,
                Header = 720,
                Footer = 720,
                Gutter = 0,
            }));
        main.Document.Save();
    }

    public static async Task RenderPdfAsync(
        string docx,
        string pdf,
        ClientSettings settings,
        CancellationToken ct)
    {
        var renderer = new TikTokProofMaterialPdfRenderService();
        await renderer.RenderAsync(docx, pdf, new TikTokProofMaterialPdfRenderOptions
        {
            PreferredRenderer = TikTokProofMaterialPdfRendererPreferenceExtensions.Parse(settings.TiktokProofPdfRenderer),
            WpsExecutablePath = settings.TiktokProofWpsPath,
        }, cancellationToken: ct).ConfigureAwait(false);
    }

    internal static string BuildFallbackCharacterTable(IReadOnlyList<EpisodeScriptSection> episodes)
    {
        var appearances = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var episode in episodes)
        {
            foreach (var line in NormalizeEpisodeLines(episode.Content, episode.EpisodeNumber)
                         .Where(line => line.Kind == ScriptParagraphKind.Characters))
            {
                var names = line.Text[(line.Text.IndexOf('：') + 1)..]
                    .Split(['，', '、', ',', '；', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var name in names.Where(name => name is not ("无" or "无人")))
                {
                    if (!appearances.TryGetValue(name, out var episodeNumbers))
                        appearances[name] = episodeNumbers = [];
                    episodeNumbers.Add(episode.EpisodeNumber);
                }
            }
        }

        return string.Join('\n', appearances.Select(pair =>
            $"{pair.Key}：出现在第 {string.Join("、", pair.Value)} 集，其他人物信息以剧本正文为准。"));
    }

    private static IReadOnlyList<ScriptLine> NormalizeEpisodeLines(string content, int episodeNumber)
    {
        var cleaned = Regex.Split(content ?? string.Empty, "\\r?\\n")
            .Select(CleanModelLine)
            .Where(line => line.Length > 0)
            .ToList();
        var firstSceneIndex = cleaned.FindIndex(line => SceneHeadingPattern.IsMatch(line));
        if (firstSceneIndex > 0) cleaned.RemoveRange(0, firstSceneIndex);

        var result = new List<ScriptLine>();
        var nextSceneNumber = 1;
        foreach (var line in cleaned)
        {
            if (IsCharacterTableHeading(line)) break;
            if (EpisodeHeadingPattern.IsMatch(line) || IsDiscardedEpisodeHeading(line)) continue;

            var scene = SceneHeadingPattern.Match(line);
            if (scene.Success)
            {
                result.Add(new ScriptLine(
                    $"{episodeNumber}-{nextSceneNumber++} {scene.Groups["description"].Value.Trim()}",
                    ScriptParagraphKind.SceneHeading));
                continue;
            }

            if (line.StartsWith("人物：", StringComparison.Ordinal) ||
                line.StartsWith("人物:", StringComparison.Ordinal))
            {
                var names = Regex.Replace(
                    line[(line.IndexOfAny(['：', ':']) + 1)..].Trim(),
                    @"\s*[,、;；]\s*",
                    "，");
                result.Add(new ScriptLine("人物：" + names,
                    ScriptParagraphKind.Characters));
                continue;
            }

            if (line[0] is '△' or '▲' or 'Δ')
            {
                result.Add(new ScriptLine("△" + line[1..].TrimStart(), ScriptParagraphKind.Body));
                continue;
            }

            var actionLabel = Regex.Match(line, @"^(?:动作|画面|镜头)[：:]\s*(?<text>.+)$");
            if (actionLabel.Success)
            {
                result.Add(new ScriptLine("△" + actionLabel.Groups["text"].Value.Trim(), ScriptParagraphKind.Body));
                continue;
            }

            result.Add(new ScriptLine(
                DialoguePattern.IsMatch(line) ? NormalizeColon(line) : "△" + line,
                ScriptParagraphKind.Body));
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeCharacterLines(string characterTable) =>
        Regex.Split(characterTable ?? string.Empty, "\\r?\\n")
            .Select(CleanModelLine)
            .Where(line => line.Length > 0 && !IsCharacterTableHeading(line))
            .Select(NormalizeColon)
            .ToArray();

    private static Paragraph CreateTitleParagraph(string text)
    {
        var properties = new ParagraphProperties(
            new KeepNext(),
            new KeepLines(),
            new WidowControl(),
            new ParagraphBorders(new BottomBorder
            {
                Val = BorderValues.Single,
                Color = AccentBlue,
                Size = 8,
                Space = 1,
            }),
            new SpacingBetweenLines { After = "420", Line = "624", LineRule = LineSpacingRuleValues.Exact },
            new Justification { Val = JustificationValues.Left });
        return CreateParagraphWithLatinSpaces(
            properties, text, HeadingFont, HeadingLatinFont, 52, bold: false, TitleBlue);
    }

    private static Paragraph CreateEpisodeHeadingParagraph(string text, int before, int after)
    {
        var properties = new ParagraphProperties(
            new KeepLines(),
            new WidowControl(),
            new SpacingBetweenLines
            {
                Before = before.ToString(),
                After = after.ToString(),
                Line = "312",
                LineRule = LineSpacingRuleValues.Exact,
            },
            new Justification { Val = JustificationValues.Left });
        return CreateParagraphWithLatinSpaces(
            properties, text, HeadingFont, HeadingLatinFont, EpisodeHeadingFontSize, bold: true, AccentBlue);
    }

    private static Paragraph CreateCharacterHeadingParagraph(string text)
    {
        var properties = new ParagraphProperties(
            new KeepNext(),
            new KeepLines(),
            new WidowControl(),
            new SpacingBetweenLines { Before = "480", After = "200", Line = "330", LineRule = LineSpacingRuleValues.Exact },
            new Justification { Val = JustificationValues.Left });
        return new Paragraph(properties, CreateRun(
            text, HeadingFont, HeadingLatinFont, BodyFontSize, bold: true, AccentBlue));
    }

    private static Paragraph CreateAggregateCharacterHeadingParagraph(string text)
    {
        var properties = new ParagraphProperties(
            new KeepLines(),
            new WidowControl(),
            new SpacingBetweenLines { Before = "700", After = "200", Line = "330", LineRule = LineSpacingRuleValues.Exact },
            new Justification { Val = JustificationValues.Left });
        return new Paragraph(properties, CreateRun(
            text, BodyFont, BodyLatinFont, BodyFontSize, bold: false));
    }

    private static Paragraph CreateScriptParagraph(ScriptLine line, bool firstScene)
    {
        if (line.Kind != ScriptParagraphKind.SceneHeading) return CreateBodyParagraph(line.Text);

        var properties = new ParagraphProperties(
            new KeepLines(),
            new WidowControl(),
            new SpacingBetweenLines
            {
                Before = firstScene ? "0" : "700",
                After = "200",
                Line = "330",
                LineRule = LineSpacingRuleValues.Exact,
            },
            new Justification { Val = JustificationValues.Left });
        return new Paragraph(properties, CreateRun(
            line.Text, BodyFont, BodyLatinFont, BodyFontSize, bold: true));
    }

    private static Paragraph CreateBodyParagraph(string text)
    {
        var properties = new ParagraphProperties(
            new KeepLines(),
            new WidowControl(),
            new SpacingBetweenLines { After = "200", Line = "330", LineRule = LineSpacingRuleValues.Exact },
            new Justification { Val = JustificationValues.Left });
        return new Paragraph(properties, CreateRun(
            text, BodyFont, BodyLatinFont, BodyFontSize, bold: false));
    }

    private static Run CreateRun(
        string text,
        string eastAsiaFont,
        string latinFont,
        int size,
        bool bold,
        string? color = null,
        int? scale = null)
    {
        var properties = new RunProperties(new RunFonts
        {
            Ascii = latinFont,
            HighAnsi = latinFont,
            EastAsia = eastAsiaFont,
            ComplexScript = latinFont,
        });
        if (bold) properties.Append(new Bold());
        if (!string.IsNullOrWhiteSpace(color)) properties.Append(new Color { Val = color });
        if (scale.HasValue) properties.Append(new CharacterScale { Val = scale.Value });
        properties.Append(
            new FontSize { Val = size.ToString() },
            new FontSizeComplexScript { Val = size.ToString() },
            new Languages { EastAsia = "zh-CN" });
        return new Run(properties, new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Paragraph CreateParagraphWithLatinSpaces(
        ParagraphProperties properties,
        string text,
        string eastAsiaFont,
        string latinFont,
        int size,
        bool bold,
        string? color)
    {
        var paragraph = new Paragraph(properties);
        foreach (var part in Regex.Split(text, "( +)").Where(part => part.Length > 0))
        {
            var isSpace = part.All(character => character == ' ');
            paragraph.Append(CreateRun(
                part,
                isSpace ? latinFont : eastAsiaFont,
                latinFont,
                size,
                bold,
                color,
                scale: isSpace ? 50 : null));
        }
        return paragraph;
    }

    private static string CleanModelLine(string value)
    {
        var line = value.Trim();
        if (line.StartsWith("```", StringComparison.Ordinal) || line is "---" or "***") return string.Empty;
        line = Regex.Replace(line, @"^#{1,6}\s*", string.Empty);
        line = Regex.Replace(line, @"^(?:[-*•]\s+)", string.Empty);
        return line.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static bool IsDiscardedEpisodeHeading(string line) =>
        Regex.IsMatch(line, @"^(?:本集梗概|梗概|分场剧本|剧本正文|正文)(?:[：:].*)?$", RegexOptions.CultureInvariant);

    private static bool IsCharacterTableHeading(string line) =>
        Regex.IsMatch(line, @"^(?:全集)?角色表(?:[：:]?)$", RegexOptions.CultureInvariant);

    private static string NormalizeColon(string line)
    {
        var index = line.IndexOf(':');
        return index < 0 ? line : line[..index] + '：' + line[(index + 1)..].TrimStart();
    }

    private enum ScriptParagraphKind
    {
        SceneHeading,
        Characters,
        Body,
    }

    private sealed record ScriptLine(string Text, ScriptParagraphKind Kind);
}
