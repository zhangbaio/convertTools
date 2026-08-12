using System.Reflection;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokAiScriptOutlineService
{
    public const string OutputFileName = "AI剧本大纲.pdf";
    private const string TemplateResourceName = "TikTokPublisher.Core.Resources.AiScriptOutlineTemplate.docx";

    public static async Task<string> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var title = FirstNonEmpty(item.NewTitle, item.Title, item.DisplayName);
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("生成 AI 剧本大纲失败：项目没有可用的新剧名。");

        var synopsis = ResolveOriginalSynopsis(item, context);
        if (string.IsNullOrWhiteSpace(synopsis))
            throw new InvalidOperationException("生成 AI 剧本大纲失败：没有找到改写前的旧简介。");

        var outputPdf = Path.Combine(context.WorkflowProjectDir, OutputFileName);
        var outputDocx = Path.ChangeExtension(outputPdf, ".docx");
        if (!forceRerun && File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 100)
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
        var response = await TikTokEpisodeScriptService.RequestTextAsync(
            prompt, settings, ct, maxOutputTokens: 16384).ConfigureAwait(false);
        AiScriptOutline outline;
        try
        {
            outline = ParseOutline(response, episodeCount);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
        {
            log?.Invoke($"AI 剧本大纲首次返回不完整，正在自动重试：{ex.Message}");
            response = await TikTokEpisodeScriptService.RequestTextAsync(
                prompt + "\n上一次输出被截断或不是完整 JSON。本次必须从头重新输出完整 JSON，并确保最后一个字符为 }。",
                settings,
                ct,
                maxOutputTokens: 24576).ConfigureAwait(false);
            outline = ParseOutline(response, episodeCount);
        }

        CreateDocument(outputDocx, title, episodeCount, outline);
        await TikTokQueueDocumentWriter.RenderPdfAsync(outputDocx, outputPdf, settings, ct).ConfigureAwait(false);
        if (!settings.TiktokProofKeepDocx) TikTokProofMaterialPdfRenderService.TryDelete(outputDocx);
        log?.Invoke($"AI 剧本大纲已生成：{outputPdf}");
        return outputPdf;
    }

    internal static string BuildPrompt(string title, string originalSynopsis, int episodeCount) => $$"""
        你是专业短剧总编剧。请根据“新剧名”和“改写前的旧简介”扩写一份完整、连贯、可用于项目审核的 AI 剧本大纲。
        不得沿用旧剧名，不得改变旧简介中的核心人物关系和主线冲突。总集数必须严格为 {{episodeCount}} 集。
        仅输出合法 JSON，不要 Markdown，不要解释。JSON 结构：
        {
          "genre":"类型",
          "style":"影像与叙事风格",
          "tone":"剧作基调",
          "synopsis":"完整剧情梗概",
          "characters":[{"name":"姓名","positioning":"一句话定位","personality":"性格","motivation":"动机","arc":"成长弧线","visual":"视觉方向","props":"关键道具或记忆点"}],
          "scenes":[{"number":"S01","name":"场景名","function":"场景类型/功能","space":"空间概念方向","mood":"情绪氛围基调","props":"叙事性关键陈设","time":"常见时间/内外景"}],
          "episodes":[{"number":1,"title":"单集标题","event":"核心事件","hook":"钩子/爽点","foreshadow":"伏笔"}]
        }
        characters 至少包含主要正反派和关键配角；scenes 至少 5 个；episodes 必须从 1 连续到 {{episodeCount}}，不得缺集。

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

    private static AiScriptOutline ParseOutline(string raw, int episodeCount)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) text = text[(firstLine + 1)..lastFence].Trim();
        }

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

        if (outline is null || string.IsNullOrWhiteSpace(outline.Synopsis))
            throw new InvalidOperationException("生成 AI 剧本大纲失败：模型未返回剧情梗概。");
        if (outline.Episodes.Count != episodeCount ||
            outline.Episodes.Select(e => e.Number).Order().SequenceEqual(Enumerable.Range(1, episodeCount)) == false)
            throw new InvalidOperationException($"生成 AI 剧本大纲失败：分集大纲应为 {episodeCount} 集，实际返回 {outline.Episodes.Count} 集。");
        return outline;
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

    internal static void CreateDocument(string outputDocx, string title, int episodeCount, AiScriptOutline outline)
    {
        ExtractTemplate(outputDocx);
        WriteDocument(outputDocx, title, episodeCount, outline);
    }

    private static void WriteDocument(string path, string title, int episodeCount, AiScriptOutline outline)
    {
        using var document = WordprocessingDocument.Open(path, true);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("AI 剧本大纲模板缺少正文。");
        var body = main.Document.Body ?? main.Document.AppendChild(new Body());
        var section = body.GetFirstChild<SectionProperties>()?.CloneNode(true);
        body.RemoveAllChildren();

        body.Append(Heading("项目信息", 32, true));
        body.Append(Line($"项目名称：{title}"));
        body.Append(Line($"类型：{outline.Genre}"));
        body.Append(Line($"风格：{outline.Style}"));
        body.Append(Line("默认比例：9:16（竖屏短剧）"));
        body.Append(Line($"总集数：{episodeCount} 集"));
        body.Append(Line($"剧作基调：{outline.Tone}"));

        body.Append(Heading("产物一：剧情梗概", 30, true));
        foreach (var paragraph in SplitParagraphs(outline.Synopsis)) body.Append(Line(paragraph, firstLineIndent: true));

        body.Append(Heading("产物二：人物小传", 30, true));
        foreach (var character in outline.Characters)
        {
            body.Append(Heading(character.Name, 26, false));
            body.Append(Line($"一句话定位：{character.Positioning}"));
            body.Append(Line($"性格：{character.Personality}"));
            body.Append(Line($"动机：{character.Motivation}"));
            body.Append(Line($"成长弧线：{character.Arc}"));
            body.Append(Line($"视觉方向：{character.Visual}"));
            body.Append(Line($"关键道具 / 记忆点：{character.Props}"));
        }

        body.Append(Heading("产物三：主要故事场景", 30, true));
        body.Append(CreateTable(
            ["场景编号", "场景名称", "场景类型 / 功能", "空间概念方向", "情绪氛围基调", "叙事性关键陈设", "常见时间 / 内外景"],
            outline.Scenes.Select(s => new[] { s.Number, s.Name, s.Function, s.Space, s.Mood, s.Props, s.Time })));

        body.Append(Heading($"产物四：{episodeCount} 集分集大纲", 30, true));
        body.Append(CreateTable(
            ["集数", "单集标题", "核心事件", "钩子 / 爽点", "伏笔"],
            outline.Episodes.OrderBy(e => e.Number)
                .Select(e => new[] { $"第 {e.Number} 集", e.Title, e.Event, e.Hook, e.Foreshadow })));

        if (section is not null) body.Append(section);
        main.Document.Save();
    }

    private static Paragraph Heading(string text, int halfPoints, bool pageBreakBefore)
    {
        var properties = new ParagraphProperties(
            new KeepNext(),
            new SpacingBetweenLines { Before = "240", After = "120" });
        if (pageBreakBefore) properties.Append(new PageBreakBefore());
        return new Paragraph(properties, Run(text, halfPoints, true));
    }

    private static Paragraph Line(string text, bool firstLineIndent = false)
    {
        var properties = new ParagraphProperties(
            new SpacingBetweenLines { After = "100", Line = "360", LineRule = LineSpacingRuleValues.Auto });
        if (firstLineIndent) properties.Append(new Indentation { FirstLine = "440" });
        return new Paragraph(properties, Run(text, 22, false));
    }

    private static Run Run(string text, int halfPoints, bool bold)
    {
        var properties = new RunProperties(
            new RunFonts { EastAsia = "宋体", Ascii = "Calibri", HighAnsi = "Calibri" },
            new FontSize { Val = halfPoints.ToString() });
        if (bold) properties.Append(new Bold());
        return new Run(properties, new Text(text ?? "") { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Table CreateTable(IEnumerable<string> headers, IEnumerable<string[]> rows)
    {
        var table = new Table(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
        table.Append(CreateRow(headers, true));
        foreach (var row in rows) table.Append(CreateRow(row, false));
        return table;
    }

    private static TableRow CreateRow(IEnumerable<string> values, bool header)
    {
        var row = new TableRow();
        foreach (var value in values)
        {
            var cellProperties = new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto });
            if (header) cellProperties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "D9EAF7" });
            row.Append(new TableCell(cellProperties, new Paragraph(Run(value, header ? 19 : 18, header))));
        }
        return row;
    }

    private static IEnumerable<string> SplitParagraphs(string value) =>
        value.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

internal sealed class AiScriptOutline
{
    public string Genre { get; set; } = "";
    public string Style { get; set; } = "";
    public string Tone { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public List<AiOutlineCharacter> Characters { get; set; } = [];
    public List<AiOutlineScene> Scenes { get; set; } = [];
    public List<AiOutlineEpisode> Episodes { get; set; } = [];
}

internal sealed class AiOutlineCharacter
{
    public string Name { get; set; } = "";
    public string Positioning { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Motivation { get; set; } = "";
    public string Arc { get; set; } = "";
    public string Visual { get; set; } = "";
    public string Props { get; set; } = "";
}

internal sealed class AiOutlineScene
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string Function { get; set; } = "";
    public string Space { get; set; } = "";
    public string Mood { get; set; } = "";
    public string Props { get; set; } = "";
    public string Time { get; set; } = "";
}

internal sealed class AiOutlineEpisode
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Event { get; set; } = "";
    public string Hook { get; set; } = "";
    public string Foreshadow { get; set; } = "";
}
