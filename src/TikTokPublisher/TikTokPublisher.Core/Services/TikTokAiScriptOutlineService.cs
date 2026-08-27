using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokAiScriptOutlineService
{
    public const string OutputFileName = "AI剧本大纲.pdf";
    private const string TemplateResourceName = "TikTokPublisher.Core.Resources.AiScriptOutlineTemplate.docx";
    private static readonly string[] ForbiddenDisclosurePhrases =
    [
        "部分内容可能由AI生成",
        "部分内容由AI生成",
        "本内容由AI生成",
        "内容由AI生成",
        "AI生成内容",
        "AI辅助生成",
        "由人工智能生成",
        "人工智能辅助生成",
    ];

    internal static string GetOutputPath(QueueProjectItem item) =>
        Path.Combine(
            ProjectWorkspaceService.LoadContext(item.ProjectDir).WorkflowProjectDir,
            OutputFileName);

    internal static bool HasCurrentOutput(QueueProjectItem item)
    {
        try
        {
            return IsReusablePdf(GetOutputPath(item));
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
        CancellationToken ct,
        RoleReferenceEpisodeFallback? episodeFallback = null)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var title = FirstNonEmpty(item.NewTitle, item.Title, item.DisplayName);
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("生成 AI 剧本大纲失败：项目没有可用的新剧名。");

        var synopsis = ResolveOriginalSynopsis(item, context);
        if (string.IsNullOrWhiteSpace(synopsis))
        {
            synopsis = await RecoverSynopsisFromMaterialVideosAsync(
                    item,
                    context,
                    settings,
                    episodeFallback,
                    log,
                    ct)
                .ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(synopsis))
            throw new InvalidOperationException(
                "生成 AI 剧本大纲失败：没有找到旧简介，也无法从恢复视频提取剧情摘要。");

        var outputPdf = Path.Combine(context.WorkflowProjectDir, OutputFileName);
        var outputDocx = Path.ChangeExtension(outputPdf, ".docx");
        if (!forceRerun && IsReusablePdf(outputPdf))
        {
            log?.Invoke($"已跳过生成 AI 剧本大纲：本地已存在 {Path.GetFileName(outputPdf)}。");
            return outputPdf;
        }

        TikTokEpisodeScriptService.EnsureAiConfigured(settings);
        var configuredEpisodeCount = account?.TiktokAiScriptOutlineEpisodeCount ?? 0;
        var episodeCount = Math.Clamp(
            configuredEpisodeCount > 0
                ? configuredEpisodeCount
                : TikTokAccountProfile.DefaultAiScriptOutlineEpisodeCount,
            1,
            120);
        log?.Invoke($"AI 剧本大纲：正在根据新剧名和旧简介生成 {episodeCount} 集大纲…");
        var prompt = BuildPrompt(title, synopsis, episodeCount);
        var response = await QueueWorkloadResourceScheduler.RunAsync(
            QueueWorkloadResource.AiText,
            () => TikTokEpisodeScriptService.RequestTextAsync(
                prompt, settings, ct, maxOutputTokens: 16384),
            log,
            ct).ConfigureAwait(false);
        AiScriptOutline outline;
        try
        {
            outline = ParseOutline(response, episodeCount);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
        {
            log?.Invoke($"AI 剧本大纲首次返回不完整，正在自动重试：{ex.Message}");
            response = await QueueWorkloadResourceScheduler.RunAsync(
                QueueWorkloadResource.AiText,
                () => TikTokEpisodeScriptService.RequestTextAsync(
                    prompt + "\n上一次输出被截断或不是完整 JSON。本次必须从头重新输出完整 JSON，并确保最后一个字符为 }。",
                    settings,
                    ct,
                    maxOutputTokens: 24576),
                log,
                ct).ConfigureAwait(false);
            outline = ParseOutline(response, episodeCount);
        }

        var videoVertical = await QueueWorkloadResourceScheduler.RunAsync(
            QueueWorkloadResource.Ffmpeg,
            () => ResolveVideoVerticalAsync(item, context, log, ct),
            log,
            ct).ConfigureAwait(false);
        CreateDocument(outputDocx, title, episodeCount, outline, videoVertical);
        await QueueWorkloadResourceScheduler.RunAsync(
            QueueWorkloadResource.Document,
            () => TikTokQueueDocumentWriter.RenderPdfAsync(outputDocx, outputPdf, settings, ct),
            log,
            ct).ConfigureAwait(false);
        if (!settings.TiktokProofKeepDocx) TikTokProofMaterialPdfRenderService.TryDelete(outputDocx);
        log?.Invoke($"AI 剧本大纲已生成：{outputPdf}");
        return outputPdf;
    }

    internal static string BuildPrompt(string title, string originalSynopsis, int episodeCount) => $$"""
        你是专业短剧总编剧。请根据“新剧名”和“改写前的旧简介”，扩写一份完整、连贯、可用于项目审核的阅读型剧本大纲。
        大纲内容必须采用六部分结构：剧本核心定位、世界观核心设定、核心人物设定、分集剧情大纲、核心爽点与名场面设计、剧本主题内核。
        不得沿用旧剧名，不得改变旧简介中的核心人物关系和主线冲突。仅生成前 {{episodeCount}} 集的分集内容；这不代表项目总集数，不得输出或声明项目总集数。
        分集剧情应按剧情阶段组织，每个阶段包含核心主线、连续集段剧情和阶段结尾钩子。所有连续集段必须无遗漏、无重复地覆盖第 1 集至第 {{episodeCount}} 集。
        禁止输出任何 AI 内容声明、AI 生成标注、免责声明、水印、页脚说明或类似文字。
        仅输出合法 JSON，不要 Markdown，不要解释。JSON 结构：
        {
          "genre":"题材类型",
          "coreSellingPoint":"核心卖点",
          "logline":"一句话梗概",
          "worldOverview":"世界背景或故事环境与核心规则概述",
          "worldRules":[{"title":"规则名称","description":"规则说明"}],
          "characters":[{"name":"姓名","role":"人物角色","identity":"身份","personality":"性格","ability":"核心能力或资源","motivation":"动机","arc":"人物弧光"}],
          "storyArcs":[{
            "title":"阶段名称",
            "startEpisode":1,
            "endEpisode":5,
            "mainline":"阶段核心主线",
            "episodeGroups":[{"startEpisode":1,"endEpisode":2,"plot":"连续集段核心剧情"}],
            "endingHook":"阶段结尾钩子"
          }],
          "highlights":[{"title":"爽点或名场面名称","description":"具体设计"}],
          "theme":"剧本主题内核"
        }
        worldRules 至少 3 项；characters 至少包含主要正反派和关键配角；storyArcs 应根据集数合理拆分为多个剧情阶段；highlights 至少 4 项。
        现实、都市等题材的 worldOverview/worldRules 应写故事环境、社会关系和冲突规则，不得强行套用玄幻修仙术语。

        新剧名：{{title}}
        改写前的旧简介：
        {{originalSynopsis}}
        """;

    private static string ResolveOriginalSynopsis(QueueProjectItem item, ProjectWorkspaceContext context)
    {
        var history = AiRewriteHistoryService.LoadForOriginalTitle(item.OriginalTitle);
        var matched = history.LastOrDefault(record =>
            string.Equals(record.NewTitle.Trim(), item.NewTitle.Trim(), StringComparison.OrdinalIgnoreCase));
        var fromHistory = matched?.OriginalSynopsis ?? history.LastOrDefault()?.OriginalSynopsis ?? "";
        if (!string.IsNullOrWhiteSpace(fromHistory)) return fromHistory.Trim();

        try
        {
            var source = WorkspaceProjectScanner.BuildProject(context.SourceProjectDir);
            if (!string.IsNullOrWhiteSpace(source.Description)) return source.Description.Trim();
        }
        catch
        {
            // Fall through to the queue snapshot for legacy projects.
        }

        return item.Description.Trim();
    }

    private static async Task<string> RecoverSynopsisFromMaterialVideosAsync(
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        ClientSettings settings,
        RoleReferenceEpisodeFallback? episodeFallback,
        Action<string>? log,
        CancellationToken ct)
    {
        var videos = ProjectVideoResolver.ResolveNarrativeVideos(
            context.SourceProjectDir,
            allowStagedFallback: true);
        if (videos.Count == 0)
        {
            try
            {
                _ = await QueueMaterialStepService.EnsureRoleReferenceEpisodeVideosAsync(
                        item,
                        settings,
                        [1],
                        log ?? (_ => { }),
                        ct,
                        episodeFallback)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"WARN AI 剧本大纲：真实分集视频补源失败：{ex.Message}");
            }
            videos = ProjectVideoResolver.ResolveNarrativeVideos(
                context.SourceProjectDir,
                allowStagedFallback: true);
        }
        if (videos.Count == 0) return string.Empty;

        TikTokEpisodeScriptService.EnsureAiConfigured(settings);
        var transcripts = new List<string>();
        foreach (var (video, index) in videos.Take(3).Select((path, index) => (path, index)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                log?.Invoke($"AI 剧本大纲：项目缺少旧简介，正在从恢复视频 {index + 1} 提取剧情线索…");
                var transcript = await TikTokEpisodeScriptService.ResolveTranscriptAsync(
                        video,
                        settings,
                        log,
                        ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(transcript))
                    transcripts.Add($"第{index + 1}集字幕：\n{transcript}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"WARN AI 剧本大纲：第 {index + 1} 集字幕提取失败，继续尝试其他集：{ex.Message}");
            }
        }
        if (transcripts.Count == 0) return string.Empty;

        var source = string.Join("\n\n", transcripts);
        if (source.Length > 36000) source = source[..36000];
        var title = FirstNonEmpty(item.NewTitle, item.Title, item.DisplayName);
        var prompt = $"""
            请根据以下已发布短剧前几集字幕，整理一段300至600字的中文剧情简介，供后续生成完整剧本大纲使用。
            只输出剧情简介正文，不要Markdown、标题、免责声明或分析过程。
            必须忠于字幕中已经出现的人物、关系和冲突；结局尚未出现时不要编造确定结局。

            新剧名：{title}
            {source}
            """;
        log?.Invoke("AI 剧本大纲：正在根据恢复视频字幕补建旧简介…");
        var synopsis = await TikTokEpisodeScriptService.RequestTextAsync(
                prompt,
                settings,
                ct,
                maxOutputTokens: 4096)
            .ConfigureAwait(false);
        PersistRecoveredSynopsis(item, context, synopsis);
        log?.Invoke("AI 剧本大纲：已从恢复视频补建剧情简介并写回项目元数据。");
        return synopsis;
    }

    internal static void PersistRecoveredSynopsis(
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        string synopsis)
    {
        var text = (synopsis ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        item.Description = text;
        foreach (var directory in new[] { context.SourceProjectDir, context.WorkflowProjectDir }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var metadataPath = Path.Combine(directory, "shortdrama-project.json");
            JsonObject metadata;
            try
            {
                metadata = File.Exists(metadataPath)
                    ? JsonNode.Parse(File.ReadAllText(metadataPath, Encoding.UTF8)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
            }
            catch
            {
                metadata = new JsonObject();
            }
            metadata["intro"] = text;
            metadata["description"] = text;
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                metadataPath,
                metadata.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }),
                Encoding.UTF8);

            var infoPath = Path.Combine(directory, "短剧信息.txt");
            if (File.Exists(infoPath))
                ProjectWorkspaceService.UpdateProjectInfoFieldIfBlank(infoPath, "简介", text);
        }
    }

    internal static AiScriptOutline ParseOutline(string raw, int episodeCount)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) text = text[(firstLine + 1)..lastFence].Trim();
        }
        if (ContainsAiContentDisclosure(text))
            throw new InvalidOperationException("生成 AI 剧本大纲失败：模型输出包含禁止的 AI 内容标注。");

        AiScriptOutline? outline;
        try
        {
            outline = JsonSerializer.Deserialize<AiScriptOutline>(text, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"生成 AI 剧本大纲失败：模型返回的 JSON 无法解析。{ex.Message}");
        }

        if (outline is null ||
            string.IsNullOrWhiteSpace(outline.Genre) ||
            string.IsNullOrWhiteSpace(outline.CoreSellingPoint) ||
            string.IsNullOrWhiteSpace(outline.Logline) ||
            string.IsNullOrWhiteSpace(outline.WorldOverview) ||
            string.IsNullOrWhiteSpace(outline.Theme))
            throw new InvalidOperationException("生成 AI 剧本大纲失败：模型返回的六部分大纲内容不完整。");
        if (outline.WorldRules.Count == 0 || outline.Characters.Count == 0 ||
            outline.StoryArcs.Count == 0 || outline.Highlights.Count == 0)
            throw new InvalidOperationException("生成 AI 剧本大纲失败：规则、人物、剧情阶段或爽点内容缺失。");

        ValidateEpisodeCoverage(outline, episodeCount);
        if (ContainsAiContentDisclosure(JsonSerializer.Serialize(outline, new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            })))
            throw new InvalidOperationException("生成 AI 剧本大纲失败：模型输出包含禁止的 AI 内容标注。");
        return outline;
    }

    private static void ValidateEpisodeCoverage(AiScriptOutline outline, int episodeCount)
    {
        var covered = new List<int>();
        foreach (var arc in outline.StoryArcs)
        {
            if (arc.StartEpisode < 1 || arc.EndEpisode < arc.StartEpisode || arc.EndEpisode > episodeCount)
                throw new InvalidOperationException($"生成 AI 剧本大纲失败：剧情阶段“{arc.Title}”的集数范围无效。");
            if (arc.EpisodeGroups.Count == 0)
                throw new InvalidOperationException($"生成 AI 剧本大纲失败：剧情阶段“{arc.Title}”没有分集剧情。");

            foreach (var group in arc.EpisodeGroups)
            {
                if (group.StartEpisode < arc.StartEpisode || group.EndEpisode < group.StartEpisode ||
                    group.EndEpisode > arc.EndEpisode || string.IsNullOrWhiteSpace(group.Plot))
                    throw new InvalidOperationException($"生成 AI 剧本大纲失败：剧情阶段“{arc.Title}”包含无效的连续集段。");
                covered.AddRange(Enumerable.Range(group.StartEpisode, group.EndEpisode - group.StartEpisode + 1));
            }
        }

        var expected = Enumerable.Range(1, episodeCount).ToArray();
        if (!covered.Order().SequenceEqual(expected))
            throw new InvalidOperationException($"生成 AI 剧本大纲失败：分集剧情未无重复地完整覆盖第 1 集至第 {episodeCount} 集。");
    }

    internal static bool ContainsAiContentDisclosure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = string.Concat(value.Where(char.IsLetterOrDigit));
        return ForbiddenDisclosurePhrases.Any(phrase =>
            normalized.Contains(string.Concat(phrase.Where(char.IsLetterOrDigit)), StringComparison.OrdinalIgnoreCase));
    }

    private static void ExtractTemplate(string outputDocx)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputDocx))!);
        TikTokProofMaterialPdfRenderService.TryDelete(outputDocx);
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(TemplateResourceName)
                          ?? throw new InvalidOperationException("未找到内置的项目 AI 剧本大纲模板。");
        using var output = File.Create(outputDocx);
        input.CopyTo(output);
    }

    internal static void CreateDocument(
        string outputDocx,
        string title,
        int episodeCount,
        AiScriptOutline outline,
        int videoVertical = -1)
    {
        ExtractTemplate(outputDocx);
        WriteDocument(outputDocx, title, episodeCount, outline, videoVertical);
    }

    private static void WriteDocument(
        string path,
        string title,
        int episodeCount,
        AiScriptOutline outline,
        int videoVertical)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("AI 剧本大纲模板缺少正文。");
        var body = main.Document.Body ?? main.Document.AppendChild(new Body());
        var section = body.GetFirstChild<SectionProperties>()?.CloneNode(true);
        body.RemoveAllChildren();
        var numbering = EnsureNumbering(main);

        body.Append(Title($"《{title}》AI剧本大纲"));

        body.Append(Heading("一、剧本核心定位", 32));
        body.Append(LabeledLine("题材类型：", outline.Genre));
        body.Append(LabeledLine("核心卖点：", outline.CoreSellingPoint));
        body.Append(LabeledLine("一句话梗概：", outline.Logline));
        var aspectRatio = FormatAspectRatio(videoVertical);
        if (!string.IsNullOrWhiteSpace(aspectRatio)) body.Append(Line(aspectRatio));

        body.Append(Heading("二、世界观核心设定（剧本核心规则）", 32));
        foreach (var paragraph in SplitParagraphs(outline.WorldOverview)) body.Append(Line(paragraph));
        var worldRuleList = CreateNumberingInstance(numbering.Part, numbering.DecimalAbstractId);
        foreach (var rule in outline.WorldRules)
            body.Append(NumberedLabeledLine(rule.Title, rule.Description, worldRuleList));

        body.Append(Heading("三、核心人物设定", 32));
        var characterList = CreateNumberingInstance(numbering.Part, numbering.DecimalAbstractId);
        foreach (var character in outline.Characters)
        {
            var role = string.IsNullOrWhiteSpace(character.Role) ? "" : $"（{character.Role}）";
            body.Append(NumberedHeading($"{character.Name}{role}", characterList));
            body.Append(LabeledLine("身份：", character.Identity));
            body.Append(LabeledLine("性格：", character.Personality));
            body.Append(LabeledLine("核心能力 / 资源：", character.Ability));
            body.Append(LabeledLine("人物动机：", character.Motivation));
            body.Append(LabeledLine("人物弧光：", character.Arc));
        }

        body.Append(Heading("四、分集剧情大纲", 32));
        foreach (var arc in outline.StoryArcs.OrderBy(arc => arc.StartEpisode))
        {
            body.Append(Heading(
                $"{arc.Title}（{FormatEpisodeRange(arc.StartEpisode, arc.EndEpisode)}）",
                28));
            body.Append(LabeledLine("核心主线：", arc.Mainline));
            body.Append(Heading("分集核心剧情", 24));
            var episodeList = CreateNumberingInstance(numbering.Part, numbering.BulletAbstractId);
            foreach (var group in arc.EpisodeGroups.OrderBy(group => group.StartEpisode))
                body.Append(BulletLabeledLine(
                    $"{FormatEpisodeRange(group.StartEpisode, group.EndEpisode)}：",
                    group.Plot,
                    episodeList));
            body.Append(LabeledLine("阶段结尾钩子：", arc.EndingHook));
        }

        body.Append(Heading("五、核心爽点与名场面设计", 32));
        var highlightList = CreateNumberingInstance(numbering.Part, numbering.DecimalAbstractId);
        foreach (var highlight in outline.Highlights)
            body.Append(NumberedLabeledLine(highlight.Title, highlight.Description, highlightList));

        body.Append(Heading("六、剧本主题内核", 32));
        foreach (var paragraph in SplitParagraphs(outline.Theme)) body.Append(Line(paragraph, firstLineIndent: true));

        body.Append(ConfigureSection(section as SectionProperties));
        main.Document.Save();
    }

    internal static string FormatAspectRatio(int videoVertical) => videoVertical switch
    {
        1 => "画面比例：9:16（竖屏短剧）",
        0 => "画面比例：16:9（横屏短剧）",
        _ => "",
    };

    private static async Task<int> ResolveVideoVerticalAsync(
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        Action<string>? log,
        CancellationToken ct)
    {
        if (item.VideoVertical is 0 or 1)
        {
            log?.Invoke($"AI 剧本大纲：使用项目元数据中的{(item.VideoVertical == 1 ? "竖屏" : "横屏")}信息。");
            return item.VideoVertical;
        }

        var candidates = new[]
        {
            item.PrimaryVideoPath,
            WorkspaceProjectScanner.BuildProject(context.SourceProjectDir).PrimaryVideoPath,
            WorkspaceProjectScanner.BuildProject(context.WorkflowProjectDir).PrimaryVideoPath,
        };
        var video = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (string.IsNullOrWhiteSpace(video))
        {
            log?.Invoke("AI 剧本大纲：项目无横竖屏元数据且未找到可检测的视频，画面比例标记为未知。");
            return -1;
        }

        try
        {
            var probe = await MediaProbe.ProbeAsync(MediaBinaryResolver.ResolveFfprobe(), video, ct)
                .ConfigureAwait(false);
            if (probe.Width <= 0 || probe.Height <= 0) return -1;
            if (probe.Width == probe.Height)
            {
                log?.Invoke($"AI 剧本大纲：首集视频为等宽高 {probe.Width}x{probe.Height}，不写入画面比例。");
                return -1;
            }
            item.VideoVertical = probe.Height > probe.Width ? 1 : 0;
            log?.Invoke($"AI 剧本大纲：根据首集视频 {probe.Width}x{probe.Height} 判定为{(item.VideoVertical == 1 ? "竖屏" : "横屏")}。");
            return item.VideoVertical;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke($"AI 剧本大纲：视频方向检测失败，画面比例标记为未知：{ex.Message}");
            return -1;
        }
    }

    private static bool IsReusablePdf(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 100) return false;
        Span<byte> header = stackalloc byte[5];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == header.Length && header.SequenceEqual("%PDF-"u8);
    }

    private static Paragraph Title(string text)
    {
        var properties = new ParagraphProperties(
            new KeepNext(),
            new SpacingBetweenLines { Before = "0", After = "300" });
        return new Paragraph(properties, Run(text, 44, true));
    }

    private static Paragraph Heading(string text, int halfPoints)
    {
        var properties = new ParagraphProperties(
            new KeepNext(),
            new KeepLines(),
            new SpacingBetweenLines
            {
                Before = halfPoints >= 32 ? "360" : halfPoints >= 28 ? "280" : "200",
                After = halfPoints >= 32 ? "160" : "120",
            });
        return new Paragraph(properties, Run(text, halfPoints, true));
    }

    private static Paragraph Line(string text, bool firstLineIndent = false)
    {
        var properties = new ParagraphProperties(
            new WidowControl(),
            new SpacingBetweenLines { After = "100", Line = "320", LineRule = LineSpacingRuleValues.Auto });
        if (firstLineIndent) properties.Append(new Indentation { FirstLine = "440" });
        return new Paragraph(properties, Run(text, 22, false));
    }

    private static Paragraph LabeledLine(string label, string value)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new WidowControl(),
            new SpacingBetweenLines { After = "100", Line = "320", LineRule = LineSpacingRuleValues.Auto }));
        paragraph.Append(Run(label, 22, true));
        paragraph.Append(Run(value, 22, false));
        return paragraph;
    }

    private static Paragraph NumberedHeading(string text, int numberingId) =>
        new(ListProperties(numberingId, before: "220", after: "100", keepNext: true), Run(text, 26, true));

    private static Paragraph NumberedLabeledLine(string label, string value, int numberingId)
    {
        var paragraph = new Paragraph(ListProperties(numberingId, before: "80", after: "100"));
        paragraph.Append(Run($"{label}：", 22, true));
        paragraph.Append(Run(value, 22, false));
        return paragraph;
    }

    private static Paragraph BulletLabeledLine(string label, string value, int numberingId)
    {
        var paragraph = new Paragraph(ListProperties(numberingId, before: "0", after: "100"));
        paragraph.Append(Run(label, 22, true));
        paragraph.Append(Run(value, 22, false));
        return paragraph;
    }

    private static ParagraphProperties ListProperties(int numberingId, string before, string after, bool keepNext = false)
    {
        var properties = new ParagraphProperties(
            new WidowControl(),
            new NumberingProperties(
                new NumberingLevelReference { Val = 0 },
                new NumberingId { Val = numberingId }),
            new SpacingBetweenLines { Before = before, After = after, Line = "320", LineRule = LineSpacingRuleValues.Auto });
        if (keepNext) properties.Append(new KeepNext());
        return properties;
    }

    private static Run Run(string text, int halfPoints, bool bold)
    {
        var properties = new RunProperties(
            new RunFonts { EastAsia = "微软雅黑", Ascii = "Arial", HighAnsi = "Arial" },
            new FontSize { Val = halfPoints.ToString() });
        if (bold) properties.Append(new Bold());
        return new Run(properties, new Text(text ?? "") { Space = SpaceProcessingModeValues.Preserve });
    }

    private static (NumberingDefinitionsPart Part, int BulletAbstractId, int DecimalAbstractId) EnsureNumbering(MainDocumentPart main)
    {
        var part = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering ??= new Numbering();
        var nextAbstractId = part.Numbering.Elements<AbstractNum>()
            .Select(value => value.AbstractNumberId?.Value ?? -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var bulletAbstractId = nextAbstractId;
        var decimalAbstractId = nextAbstractId + 1;

        part.Numbering.Append(CreateAbstractNumbering(bulletAbstractId, true));
        part.Numbering.Append(CreateAbstractNumbering(decimalAbstractId, false));
        part.Numbering.Save();
        return (part, bulletAbstractId, decimalAbstractId);
    }

    private static AbstractNum CreateAbstractNumbering(int abstractId, bool bullet)
    {
        var level = new Level(
            new StartNumberingValue { Val = 1 },
            new NumberingFormat { Val = bullet ? NumberFormatValues.Bullet : NumberFormatValues.Decimal },
            new LevelText { Val = bullet ? "•" : "%1、" },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = "540", Hanging = "270" }),
            new NumberingSymbolRunProperties(
                new RunFonts { EastAsia = "微软雅黑", Ascii = "Arial", HighAnsi = "Arial" },
                new Color { Val = bullet ? "2563EB" : "1F2937" }))
        {
            LevelIndex = 0,
        };
        return new AbstractNum(level) { AbstractNumberId = abstractId };
    }

    private static int CreateNumberingInstance(NumberingDefinitionsPart part, int abstractId)
    {
        var nextNumberId = part.Numbering!.Elements<NumberingInstance>()
            .Select(value => value.NumberID?.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        part.Numbering.Append(new NumberingInstance(
            new AbstractNumId { Val = abstractId },
            new LevelOverride(new StartOverrideNumberingValue { Val = 1 }) { LevelIndex = 0 })
        {
            NumberID = nextNumberId,
        });
        part.Numbering.Save();
        return nextNumberId;
    }

    private static SectionProperties ConfigureSection(SectionProperties? section)
    {
        section ??= new SectionProperties();
        section.RemoveAllChildren<PageSize>();
        section.RemoveAllChildren<PageMargin>();
        section.PrependChild(new PageMargin
        {
            Top = 1134,
            Right = 1134,
            Bottom = 1134,
            Left = 1134,
            Header = 708,
            Footer = 708,
            Gutter = 0,
        });
        section.PrependChild(new PageSize { Width = 11906, Height = 16838, Orient = PageOrientationValues.Portrait });
        return section;
    }

    private static string FormatEpisodeRange(int startEpisode, int endEpisode) =>
        startEpisode == endEpisode ? $"第 {startEpisode} 集" : $"第 {startEpisode}-{endEpisode} 集";

    private static IEnumerable<string> SplitParagraphs(string value) =>
        value.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

internal sealed class AiScriptOutline
{
    public string Genre { get; set; } = "";
    public string CoreSellingPoint { get; set; } = "";
    public string Logline { get; set; } = "";
    public string WorldOverview { get; set; } = "";
    public List<AiOutlineWorldRule> WorldRules { get; set; } = [];
    public List<AiOutlineCharacter> Characters { get; set; } = [];
    public List<AiOutlineStoryArc> StoryArcs { get; set; } = [];
    public List<AiOutlineHighlight> Highlights { get; set; } = [];
    public string Theme { get; set; } = "";
}

internal sealed class AiOutlineWorldRule
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

internal sealed class AiOutlineCharacter
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Ability { get; set; } = "";
    public string Motivation { get; set; } = "";
    public string Arc { get; set; } = "";
}

internal sealed class AiOutlineStoryArc
{
    public string Title { get; set; } = "";
    public int StartEpisode { get; set; }
    public int EndEpisode { get; set; }
    public string Mainline { get; set; } = "";
    public List<AiOutlineEpisodeGroup> EpisodeGroups { get; set; } = [];
    public string EndingHook { get; set; } = "";
}

internal sealed class AiOutlineEpisodeGroup
{
    public int StartEpisode { get; set; }
    public int EndEpisode { get; set; }
    public string Plot { get; set; } = "";
}

internal sealed class AiOutlineHighlight
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}
