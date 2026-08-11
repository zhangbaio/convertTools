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
    public const string OutputSuffix = "-前5集剧本.pdf";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(8) };

    public static async Task<string> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var videos = ProjectVideoResolver.ResolveUploadVideos(context.SourceProjectDir, allowStagedFallback: true)
            .Take(5).ToArray();
        if (videos.Length == 0)
            throw new InvalidOperationException("生成剧本失败：项目中没有可用视频。");

        var title = string.IsNullOrWhiteSpace(item.NewTitle) ? item.Title : item.NewTitle.Trim();
        var safeTitle = string.Concat(title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var outputPdf = Path.Combine(context.WorkflowProjectDir, safeTitle + OutputSuffix);
        var outputDocx = Path.ChangeExtension(outputPdf, ".docx");
        if (!forceRerun && File.Exists(outputPdf) && new FileInfo(outputPdf).Length > 100)
        {
            log?.Invoke($"已跳过生成剧本：本地已存在 {Path.GetFileName(outputPdf)}。");
            return outputPdf;
        }

        EnsureAiConfigured(settings);
        var episodes = new List<(string Heading, string Content)>();
        for (var i = 0; i < videos.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var video = videos[i];
            log?.Invoke($"剧本 {i + 1}/{videos.Length}：提取字幕与台词…");
            var transcript = await ResolveTranscriptAsync(video, settings, log, ct).ConfigureAwait(false);
            log?.Invoke($"剧本 {i + 1}/{videos.Length}：AI 整理分场剧本…");
            var content = await RequestScriptAsync(title, i + 1, Path.GetFileName(video), transcript, settings, ct)
                .ConfigureAwait(false);
            episodes.Add(($"第{i + 1}集", content));
        }

        TikTokQueueDocumentWriter.WriteDocument(
            outputDocx,
            $"{title} 前{videos.Length}集剧本",
            "根据成片字幕整理，用于内容审核材料归档。",
            episodes);
        await TikTokQueueDocumentWriter.RenderPdfAsync(outputDocx, outputPdf, settings, ct).ConfigureAwait(false);
        if (!settings.TiktokProofKeepDocx) TikTokProofMaterialPdfRenderService.TryDelete(outputDocx);
        log?.Invoke($"前{videos.Length}集剧本已生成：{outputPdf}");
        return outputPdf;
    }

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
        var prompt = $"""
                     请根据以下短剧字幕时间轴整理第{episode}集规范中文剧本。不得编造字幕和剧情中不存在的信息。
                     输出纯文本，依次包含：本集梗概、人物、分场剧本。每个场次写明时间、场景、人物、动作和台词。
                     剧名：{title}
                     视频：{videoName}
                     字幕时间轴：
                     {transcript[..Math.Min(transcript.Length, 24000)]}
                     """;
        var endpoint = settings.AiTextEndpoint.Trim().TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiTextApiKey.Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = settings.AiTextModel.Trim(),
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = "你是影视编剧和审核材料整理专家，只依据输入事实整理剧本。" },
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

    private static void EnsureAiConfigured(ClientSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AiTextEndpoint) ||
            string.IsNullOrWhiteSpace(settings.AiTextApiKey) ||
            string.IsNullOrWhiteSpace(settings.AiTextModel))
            throw new InvalidOperationException("生成剧本失败：请先配置 AI 文本接口、API Key 和模型。");
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff");
}

internal static class TikTokQueueDocumentWriter
{
    public static void WriteDocument(
        string path,
        string title,
        string subtitle,
        IReadOnlyList<(string Heading, string Content)> sections)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        TikTokProofMaterialPdfRenderService.TryDelete(path);
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        body.Append(Paragraph(title, 36, true, JustificationValues.Center));
        body.Append(Paragraph(subtitle, 20, false, JustificationValues.Center));
        foreach (var section in sections)
        {
            body.Append(Paragraph(section.Heading, 27, true, JustificationValues.Left));
            foreach (var line in Regex.Split(section.Content, "\\r?\\n"))
                body.Append(Paragraph(line, 22, false, JustificationValues.Left));
        }
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

    private static Paragraph Paragraph(string text, int size, bool bold, JustificationValues alignment)
    {
        var runProperties = new RunProperties(
            new RunFonts { EastAsia = "Microsoft YaHei", Ascii = "Microsoft YaHei" },
            new FontSize { Val = size.ToString() });
        if (bold) runProperties.Append(new Bold());
        return new Paragraph(
            new ParagraphProperties(new Justification { Val = alignment }, new SpacingBetweenLines { After = "120" }),
            new Run(runProperties, new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve }));
    }
}
