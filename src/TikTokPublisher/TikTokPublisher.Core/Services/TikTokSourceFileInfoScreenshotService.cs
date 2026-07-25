using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TikTokPublisher.Core.Media;
using Color = SixLabors.ImageSharp.Color;
using FontFamily = SixLabors.Fonts.FontFamily;
using WordColor = DocumentFormat.OpenXml.Wordprocessing.Color;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 从项目真实成片、海报和元数据生成「原始文件或素材文件信息」材料。
/// 不模拟不存在的剪辑工程、RAW、PSD 或剧本；所有展示项均可回溯到落盘文件。
/// </summary>
public static class TikTokSourceFileInfoScreenshotService
{
    public const string OutputDirectoryName = "原始文件信息截图";
    public const string EvidenceDirectoryName = "项目原始资料";
    public const int RequiredImageCount = 4;
    public const string ScreenshotVersion = "v3-real-media-evidence";

    private const string LegacyOutputDirectoryName = "原始文件或素材文件信息";
    private const int ContactSheetFrameCount = 8;
    private static readonly string[] FileNames =
    [
        "01_角色参考素材.png",
        "02_场景参考素材.png",
        "03_真实项目文件目录.png",
        "04_真实剧本与文件清单.png",
    ];

    public static string GetOutputDirectory(string workflowProjectDirectory) =>
        Path.Combine(Path.GetFullPath(workflowProjectDirectory), OutputDirectoryName);

    public static string GetEvidenceDirectory(string workflowProjectDirectory) =>
        Path.Combine(Path.GetFullPath(workflowProjectDirectory), EvidenceDirectoryName);

    public static IReadOnlyList<string> GetExpectedOutputPaths(string workflowProjectDirectory)
    {
        var dir = GetOutputDirectory(workflowProjectDirectory);
        return FileNames.Select(name => Path.Combine(dir, name)).ToArray();
    }

    public static IReadOnlyList<string> ListGeneratedImages(string workflowProjectDirectory)
    {
        var dir = GetOutputDirectory(workflowProjectDirectory);
        return Directory.Exists(dir)
            ? FileNames.Select(name => Path.Combine(dir, name)).Where(File.Exists).ToArray()
            : [];
    }

    public static bool HasCurrentOutput(string workflowProjectDirectory) =>
        ListGeneratedImages(workflowProjectDirectory).Count >= RequiredImageCount;

    public static IReadOnlyList<string> Generate(
        string workflowProjectDirectory,
        string dramaTitle,
        string? companyName = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowProjectDirectory);
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var title = string.IsNullOrWhiteSpace(dramaTitle) ? "未命名短剧" : dramaTitle.Trim();
        var company = string.IsNullOrWhiteSpace(companyName) ? "制作方未填写" : companyName.Trim();
        cancellationToken.ThrowIfCancellationRequested();

        TryDeleteOutput(workflow);
        var outputDir = GetOutputDirectory(workflow);
        var evidenceDir = GetEvidenceDirectory(workflow);
        TryDeleteDirectory(evidenceDir);
        var characterDir = Path.Combine(evidenceDir, "03_角色参考");
        var sceneDir = Path.Combine(evidenceDir, "04_场景参考");
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(characterDir);
        Directory.CreateDirectory(sceneDir);
        log?.Invoke($"原始文件信息/初始化：已清理旧产物；截图目录={outputDir}；证据目录={evidenceDir}。");

        var videos = FindVideos(workflow);
        var poster = FindPoster(workflow);
        log?.Invoke(
            $"原始文件信息/扫描：发现真实成片 {videos.Count} 个；" +
            $"海报={(string.IsNullOrWhiteSpace(poster) ? "未找到" : Path.GetFileName(poster))}。");

        var records = ProbeVideos(videos, cancellationToken, log);
        WriteProjectDescription(evidenceDir, workflow, title, company, videos.Count);
        log?.Invoke("原始文件信息/元数据：已生成 项目说明.txt。");
        var manifestPath = WriteManifest(evidenceDir, records, cancellationToken);
        log?.Invoke($"原始文件信息/清单：已生成 {DescribeOutput(manifestPath)}，共 {records.Count} 条视频记录。");
        var docxPath = WriteDerivedScriptDocument(evidenceDir, title, company, records);
        log?.Invoke($"原始文件信息/文档：已生成 {DescribeOutput(docxPath)}，口径=基于成片整理。");

        var characterFrames = ExtractEvidenceFrames(
            records, poster, characterDir, "角色", sceneMode: false, cancellationToken, log);
        log?.Invoke($"原始文件信息/角色抽帧：完成 {characterFrames.Count} 张 → {characterDir}。");
        var sceneFrames = ExtractEvidenceFrames(
            records, poster, sceneDir, "场景", sceneMode: true, cancellationToken, log);
        log?.Invoke($"原始文件信息/场景抽帧：完成 {sceneFrames.Count} 张 → {sceneDir}。");

        var family = ResolveFontFamily()
            ?? throw new InvalidOperationException("未找到可用中文字体，无法生成原始文件信息截图。");
        var outputs = new List<string>(RequiredImageCount);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var shot = RenderContactSheet(
                       title, "角色参考素材（从真实成片抽帧）", characterFrames, family,
                       "画面均来自项目成片；标签为原视频文件名、集数和时间码。"))
            {
                var path = Save(shot, outputDir, FileNames[0]);
                outputs.Add(path);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var shot = RenderContactSheet(
                       title, "场景参考素材（从真实成片抽帧）", sceneFrames, family,
                       "按不同集数和时间点抽取，用于证明项目实际场景素材来源。"))
            {
                var path = Save(shot, outputDir, FileNames[1]);
                outputs.Add(path);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var shot = RenderFileEvidence(title, workflow, evidenceDir, records, manifestPath, docxPath, family))
            {
                var path = Save(shot, outputDir, FileNames[2]);
                outputs.Add(path);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var shot = RenderDocumentEvidence(title, company, records, docxPath, manifestPath, family))
            {
                var path = Save(shot, outputDir, FileNames[3]);
                outputs.Add(path);
            }
        }
        finally
        {
            foreach (var frame in characterFrames.Concat(sceneFrames))
            {
                frame.Image.Dispose();
            }
        }

        log?.Invoke($"原始文件信息截图已生成：{outputs.Count} 张；真实资料目录：{evidenceDir}");
        return outputs;
    }

    public static void TryDeleteOutput(string workflowProjectDirectory)
    {
        TryDeleteDirectory(GetOutputDirectory(workflowProjectDirectory));
        TryDeleteDirectory(Path.Combine(Path.GetFullPath(workflowProjectDirectory), LegacyOutputDirectoryName));
    }

    private static List<string> FindVideos(string workflow)
    {
        string[] preferredDirectories =
        [
            Path.Combine(workflow, "tiktok_upload_videos"),
            Path.Combine(workflow, "videos"),
            workflow,
        ];
        foreach (var dir in preferredDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            var found = Directory.EnumerateFiles(dir, "*.mp4", SearchOption.TopDirectoryOnly)
                .OrderBy(ParseEpisodeNumber)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (found.Count > 0) return found;
        }

        return [];
    }

    private static string? FindPoster(string workflow)
    {
        string[] names = ["海报图片.png", "海报.png", "封面.png", "poster.png"];
        return names.Select(name => Path.Combine(workflow, name)).FirstOrDefault(File.Exists)
               ?? Directory.EnumerateFiles(workflow, "*.png", SearchOption.TopDirectoryOnly)
                   .FirstOrDefault(path => Path.GetFileName(path).Contains("海报", StringComparison.Ordinal));
    }

    private static List<VideoRecord> ProbeVideos(
        IReadOnlyList<string> videos,
        CancellationToken cancellationToken,
        Action<string>? log)
    {
        var result = new List<VideoRecord>(videos.Count);
        string? ffprobe = null;
        try
        {
            if (videos.Count > 0) ffprobe = MediaBinaryResolver.ResolveFfprobe();
        }
        catch (Exception ex)
        {
            log?.Invoke($"未找到 ffprobe，将仅记录文件系统信息：{ex.Message}");
        }

        for (var index = 0; index < videos.Count; index++)
        {
            var path = videos[index];
            cancellationToken.ThrowIfCancellationRequested();
            MediaProbe? probe = null;
            if (!string.IsNullOrWhiteSpace(ffprobe))
            {
                try
                {
                    probe = MediaProbe.ProbeAsync(ffprobe, path, cancellationToken)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    log?.Invoke($"媒体参数读取失败，已保留文件信息：{Path.GetFileName(path)}，{ex.Message}");
                }
            }

            var info = new FileInfo(path);
            result.Add(new VideoRecord(
                path,
                Path.GetFileName(path),
                ParseEpisodeNumber(path),
                info.Length,
                info.LastWriteTime,
                probe?.DurationSeconds ?? 0,
                probe?.Width ?? 0,
                probe?.Height ?? 0,
                probe?.FrameRateFps ?? 0,
                probe?.AudioCodec ?? "",
                ComputeSha256(path, cancellationToken)));
        }
        if (result.Count > 0)
        {
            log?.Invoke(
                $"原始文件信息/媒体分析：完成 {result.Count} 个视频；" +
                $"总大小={FormatBytes(result.Sum(record => record.Length))}；" +
                $"总时长={FormatDuration(result.Sum(record => record.DurationSeconds))}。");
        }

        return result;
    }

    private static string WriteManifest(
        string evidenceDir,
        IReadOnlyList<VideoRecord> records,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(evidenceDir, "视频文件清单.csv");
        var csv = new StringBuilder("\uFEFF");
        csv.AppendLine("序号,集数,文件名,字节数,时长,分辨率,帧率,音频编码,修改时间,SHA-256");
        for (var i = 0; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = records[i];
            csv.AppendLine(string.Join(",",
                i + 1,
                r.Episode,
                Csv(r.FileName),
                r.Length,
                FormatDuration(r.DurationSeconds),
                Csv(r.Width > 0 ? $"{r.Width}×{r.Height}" : "未读取"),
                r.FrameRate > 0 ? r.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) : "",
                Csv(r.AudioCodec),
                Csv(r.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")),
                r.Sha256));
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static void WriteProjectDescription(
        string evidenceDir,
        string workflow,
        string title,
        string company,
        int videoCount)
    {
        Directory.CreateDirectory(evidenceDir);
        var metadata = ReadProjectMetadata(workflow);
        var text = $"""
                   项目名称：{title}
                   原始项目名称：{metadata.OriginalTitle}
                   项目编号：{metadata.BookId}
                   成片集数：{videoCount}
                   制作方：{company}
                   资料生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}
                   资料来源：本目录内容由项目内真实视频、海报及 shortdrama-project.json 整理生成。
                   真实性说明：角色图和场景图均从成片抽取；剧本文档为成片整理稿，不冒充拍摄前原始剧本。
                   """;
        File.WriteAllText(Path.Combine(evidenceDir, "项目说明.txt"), text, new UTF8Encoding(true));
    }

    private static string WriteDerivedScriptDocument(
        string evidenceDir,
        string title,
        string company,
        IReadOnlyList<VideoRecord> records)
    {
        var path = Path.Combine(evidenceDir, $"{SanitizePathSegment(title)}_成片整理稿.docx");
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document();
        var body = new Body();
        main.Document.Append(body);
        AddStyles(main);

        body.Append(ParagraphOf($"{title}｜成片整理稿", "Title"));
        body.Append(ParagraphOf("根据项目内已完成视频整理，不代表拍摄前原始剧本", "Subtitle"));
        body.Append(ParagraphOf($"制作方：{company}　　整理时间：{DateTime.Now:yyyy-MM-dd}", "Normal"));
        body.Append(ParagraphOf("资料说明", "Heading1"));
        body.Append(ParagraphOf(
            "本文件以真实成片为唯一内容依据，记录每集源文件、媒体参数和可回溯时间点。对白及字幕以对应成片画面为准，未识别或未验证的内容不作推断。",
            "Normal"));
        body.Append(ParagraphOf("分集索引", "Heading1"));

        var table = new Table(
            new TableProperties(
                new TableWidth { Width = "9360", Type = TableWidthUnitValues.Dxa },
                new TableIndentation { Width = 120, Type = TableWidthUnitValues.Dxa },
                new TableLayout { Type = TableLayoutValues.Fixed },
                CreateBorders()),
            new TableGrid(
                new GridColumn { Width = "720" },
                new GridColumn { Width = "3600" },
                new GridColumn { Width = "1200" },
                new GridColumn { Width = "1440" },
                new GridColumn { Width = "2400" }));
        table.Append(CreateRow(["集数", "真实源文件", "时长", "画面规格", "整理说明"], header: true));
        foreach (var r in records)
        {
            table.Append(CreateRow(
            [
                r.Episode > 0 ? r.Episode.ToString() : "-",
                r.FileName,
                FormatDuration(r.DurationSeconds),
                r.Width > 0 ? $"{r.Width}×{r.Height} / {r.FrameRate:0.##}fps" : "未读取",
                "角色、场景与对白以本集成片及字幕为准",
            ], header: false));
        }

        if (records.Count == 0)
        {
            table.Append(CreateRow(["-", "项目内未发现 MP4 成片", "-", "-", "仅保留已有海报及项目元数据"], false));
        }

        body.Append(table);
        body.Append(new SectionProperties(
            new PageSize { Width = 12240, Height = 15840 },
            new PageMargin
            {
                Top = 1440, Right = 1440, Bottom = 1440, Left = 1440,
                Header = 708, Footer = 708, Gutter = 0,
            }));
        main.Document.Save();
        return path;
    }

    private static List<EvidenceFrame> ExtractEvidenceFrames(
        IReadOnlyList<VideoRecord> records,
        string? poster,
        string destination,
        string kind,
        bool sceneMode,
        CancellationToken cancellationToken,
        Action<string>? log)
    {
        var frames = new List<EvidenceFrame>();
        string? ffmpeg = null;
        try
        {
            if (records.Count > 0) ffmpeg = MediaBinaryResolver.ResolveFfmpeg();
        }
        catch (Exception ex)
        {
            log?.Invoke($"未找到 ffmpeg，将使用项目海报作为兜底素材：{ex.Message}");
        }

        foreach (var record in SelectEvenly(records, ContactSheetFrameCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = record.DurationSeconds > 0 ? record.DurationSeconds : 60;
            var ratio = sceneMode ? 0.68 : 0.28;
            var seconds = Math.Clamp(duration * ratio, 2, Math.Max(2, duration - 1));
            var fileName = $"第{Math.Max(0, record.Episode):D2}集_{kind}_{FormatTimeCodeForFile(seconds)}.jpg";
            var output = Path.Combine(destination, fileName);
            if (!string.IsNullOrWhiteSpace(ffmpeg) &&
                TryExtractFrame(ffmpeg, record.FullPath, output, seconds, cancellationToken))
            {
                try
                {
                    frames.Add(new EvidenceFrame(
                        Image.Load<Rgba32>(output),
                        $"第{record.Episode}集  {FormatDuration(seconds)}",
                        record.FileName));
                }
                catch
                {
                    // Continue with the next verifiable frame.
                }
            }
        }

        if (frames.Count == 0 && !string.IsNullOrWhiteSpace(poster))
        {
            try
            {
                frames.Add(new EvidenceFrame(Image.Load<Rgba32>(poster), "项目海报", Path.GetFileName(poster)));
            }
            catch
            {
                // Render a clear empty state below.
            }
        }

        return frames;
    }

    private static bool TryExtractFrame(
        string ffmpeg,
        string video,
        string output,
        double seconds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y", "-ss",
                         seconds.ToString("0.###", CultureInfo.InvariantCulture),
                         "-i", video, "-frames:v", "1", "-q:v", "2", output,
                     })
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            process.Start();
            while (!process.WaitForExit(200))
            {
                if (!cancellationToken.IsCancellationRequested) continue;
                try { process.Kill(entireProcessTree: true); } catch { }
                cancellationToken.ThrowIfCancellationRequested();
            }

            return process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static Image<Rgba32> RenderContactSheet(
        string title,
        string heading,
        IReadOnlyList<EvidenceFrame> frames,
        FontFamily family,
        string note)
    {
        const int width = 1440;
        const int height = 900;
        var image = new Image<Rgba32>(width, height, Color.ParseHex("F4F1EA"));
        var titleFont = family.CreateFont(27, FontStyle.Bold);
        var headingFont = family.CreateFont(16, FontStyle.Regular);
        var labelFont = family.CreateFont(14, FontStyle.Bold);
        var smallFont = family.CreateFont(12);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("20302B"), new RectangleF(0, 0, width, 105));
            ctx.DrawText(title, titleFont, Color.White, new PointF(52, 22));
            ctx.DrawText(heading, headingFont, Color.ParseHex("DCE7DF"), new PointF(54, 64));
            ctx.DrawText(note, smallFont, Color.ParseHex("59635F"), new PointF(52, 860));
        });

        if (frames.Count == 0)
        {
            image.Mutate(ctx =>
            {
                ctx.Fill(Color.White, new RectangleF(52, 140, width - 104, 650));
                ctx.DrawText("项目内未发现可读取的视频帧或海报", headingFont, Color.ParseHex("7B3232"),
                    new PointF(430, 430));
            });
            return image;
        }

        for (var i = 0; i < Math.Min(ContactSheetFrameCount, frames.Count); i++)
        {
            var col = i % 4;
            var row = i / 4;
            var x = 52 + col * 342;
            var y = 135 + row * 350;
            var frameRect = new Rectangle(x, y, 300, 270);
            DrawFrameContain(image, frames[i].Image, frameRect);
            image.Mutate(ctx =>
            {
                ctx.Draw(Color.ParseHex("C8C4BA"), 1, frameRect);
                ctx.DrawText(frames[i].Label, labelFont, Color.ParseHex("20302B"), new PointF(x, y + 282));
                ctx.DrawText(TrimForUi(frames[i].SourceFile, 32), smallFont, Color.ParseHex("68716D"),
                    new PointF(x, y + 309));
            });
        }

        return image;
    }

    private static Image<Rgba32> RenderFileEvidence(
        string title,
        string workflow,
        string evidenceDir,
        IReadOnlyList<VideoRecord> records,
        string manifestPath,
        string docxPath,
        FontFamily family)
    {
        const int width = 1440;
        const int height = 900;
        var image = new Image<Rgba32>(width, height, Color.White);
        var titleFont = family.CreateFont(25, FontStyle.Bold);
        var heading = family.CreateFont(16, FontStyle.Bold);
        var normal = family.CreateFont(13);
        var mono = family.CreateFont(12);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("F5F7FA"));
            ctx.Fill(Color.ParseHex("1F4D78"), new RectangleF(0, 0, width, 92));
            ctx.DrawText("真实项目文件目录", titleFont, Color.White, new PointF(45, 20));
            ctx.DrawText(title, normal, Color.ParseHex("DDEAF4"), new PointF(48, 58));

            ctx.Fill(Color.White, new RectangleF(35, 116, 1370, 110));
            ctx.Draw(Color.ParseHex("D9E1E8"), 1, new RectangleF(35, 116, 1370, 110));
            ctx.DrawText("项目路径", heading, Color.ParseHex("1F4D78"), new PointF(58, 135));
            ctx.DrawText(workflow, normal, Color.ParseHex("28323A"), new PointF(190, 137));
            ctx.DrawText("证据目录", heading, Color.ParseHex("1F4D78"), new PointF(58, 177));
            ctx.DrawText(evidenceDir, normal, Color.ParseHex("28323A"), new PointF(190, 179));

            ctx.DrawText($"真实成片：{records.Count} 个", heading, Color.ParseHex("20302B"), new PointF(50, 255));
            ctx.DrawText("文件名", normal, Color.ParseHex("52606A"), new PointF(65, 300));
            ctx.DrawText("大小", normal, Color.ParseHex("52606A"), new PointF(755, 300));
            ctx.DrawText("媒体参数", normal, Color.ParseHex("52606A"), new PointF(900, 300));
            ctx.DrawText("SHA-256 摘要", normal, Color.ParseHex("52606A"), new PointF(1110, 300));
        });

        for (var i = 0; i < Math.Min(8, records.Count); i++)
        {
            var r = records[i];
            var y = 330 + i * 54;
            image.Mutate(ctx =>
            {
                ctx.Fill(i % 2 == 0 ? Color.ParseHex("F8FAFC") : Color.White,
                    new RectangleF(50, y, 1340, 50));
                ctx.DrawText(TrimForUi(r.FileName, 48), normal, Color.ParseHex("1E2B34"), new PointF(65, y + 14));
                ctx.DrawText(FormatBytes(r.Length), normal, Color.ParseHex("1E2B34"), new PointF(755, y + 14));
                ctx.DrawText(
                    r.Width > 0 ? $"{r.Width}×{r.Height}  {FormatDuration(r.DurationSeconds)}" : "参数未读取",
                    normal, Color.ParseHex("1E2B34"), new PointF(900, y + 14));
                ctx.DrawText(r.Sha256[..Math.Min(16, r.Sha256.Length)] + "…", mono,
                    Color.ParseHex("35556D"), new PointF(1110, y + 14));
            });
        }

        image.Mutate(ctx =>
        {
            var y = 790f;
            ctx.DrawText($"已落盘：{Path.GetFileName(manifestPath)}", normal, Color.ParseHex("1F7A4D"),
                new PointF(50, y));
            ctx.DrawText($"已落盘：{Path.GetFileName(docxPath)}", normal, Color.ParseHex("1F7A4D"),
                new PointF(520, y));
            ctx.DrawText("全部条目取自文件系统和 ffprobe，无虚构文件。", normal, Color.ParseHex("7A5A00"),
                new PointF(980, y));
        });
        return image;
    }

    private static Image<Rgba32> RenderDocumentEvidence(
        string title,
        string company,
        IReadOnlyList<VideoRecord> records,
        string docxPath,
        string manifestPath,
        FontFamily family)
    {
        const int width = 1440;
        const int height = 900;
        var image = new Image<Rgba32>(width, height, Color.ParseHex("D8DDE3"));
        var titleFont = family.CreateFont(24, FontStyle.Bold);
        var heading = family.CreateFont(16, FontStyle.Bold);
        var normal = family.CreateFont(13);
        var small = family.CreateFont(11);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.White, new RectangleF(35, 30, 820, 840));
            ctx.Fill(Color.ParseHex("F4F6F8"), new RectangleF(35, 30, 820, 70));
            ctx.Draw(Color.ParseHex("AEB8C2"), 1, new RectangleF(35, 30, 820, 840));
            ctx.DrawText(Path.GetFileName(docxPath), normal, Color.ParseHex("1F7A4D"), new PointF(55, 48));
            ctx.DrawText("基于成片整理｜可回溯来源", small, Color.ParseHex("60717D"), new PointF(55, 75));
            ctx.DrawText($"{title}｜成片整理稿", titleFont, Color.ParseHex("1F4D78"), new PointF(85, 128));
            ctx.DrawText("根据项目内已完成视频整理，不代表拍摄前原始剧本", normal,
                Color.ParseHex("8A4B2A"), new PointF(85, 172));
            ctx.DrawText($"制作方：{company}", normal, Color.ParseHex("34424C"), new PointF(85, 210));
            ctx.DrawText("分集索引", heading, Color.ParseHex("1F4D78"), new PointF(85, 255));

            ctx.Fill(Color.ParseHex("E8EEF5"), new RectangleF(80, 292, 730, 38));
            ctx.DrawText("集数", normal, Color.ParseHex("20302B"), new PointF(95, 302));
            ctx.DrawText("真实源文件", normal, Color.ParseHex("20302B"), new PointF(160, 302));
            ctx.DrawText("时长", normal, Color.ParseHex("20302B"), new PointF(555, 302));
            ctx.DrawText("画面规格", normal, Color.ParseHex("20302B"), new PointF(650, 302));
        });

        for (var i = 0; i < Math.Min(12, records.Count); i++)
        {
            var r = records[i];
            var y = 334 + i * 39;
            image.Mutate(ctx =>
            {
                ctx.DrawLine(Color.ParseHex("E1E6EA"), 1, new PointF(80, y + 35), new PointF(810, y + 35));
                ctx.DrawText(r.Episode.ToString(), small, Color.ParseHex("26343D"), new PointF(98, y + 9));
                ctx.DrawText(TrimForUi(r.FileName, 36), small, Color.ParseHex("26343D"), new PointF(160, y + 9));
                ctx.DrawText(FormatDuration(r.DurationSeconds), small, Color.ParseHex("26343D"), new PointF(555, y + 9));
                ctx.DrawText(r.Width > 0 ? $"{r.Width}×{r.Height}" : "未读取", small,
                    Color.ParseHex("26343D"), new PointF(650, y + 9));
            });
        }

        image.Mutate(ctx =>
        {
            ctx.Fill(Color.White, new RectangleF(880, 30, 525, 840));
            ctx.Draw(Color.ParseHex("AEB8C2"), 1, new RectangleF(880, 30, 525, 840));
            ctx.Fill(Color.ParseHex("F4F6F8"), new RectangleF(880, 30, 525, 70));
            ctx.DrawText(Path.GetFileName(manifestPath), normal, Color.ParseHex("1F7A4D"), new PointF(900, 48));
            ctx.DrawText("真实媒体清单", small, Color.ParseHex("60717D"), new PointF(900, 75));
            ctx.DrawText("核验字段", heading, Color.ParseHex("1F4D78"), new PointF(915, 128));
            string[] fields =
            [
                $"成片数量：{records.Count}",
                $"总大小：{FormatBytes(records.Sum(r => r.Length))}",
                $"总时长：{FormatDuration(records.Sum(r => r.DurationSeconds))}",
                "逐文件字段：",
                "• 文件名与集数",
                "• 原始字节数",
                "• 时长、分辨率、帧率",
                "• 音频编码",
                "• 修改时间",
                "• SHA-256 完整摘要",
            ];
            for (var i = 0; i < fields.Length; i++)
            {
                ctx.DrawText(fields[i], normal, Color.ParseHex("2E3D46"), new PointF(915, 180 + i * 42));
            }

            ctx.Fill(Color.ParseHex("FFF7E2"), new RectangleF(910, 650, 440, 140));
            ctx.DrawText("真实性声明", heading, Color.ParseHex("7A5A00"), new PointF(930, 670));
            ctx.DrawText("剧本文档为成片整理稿；角色与场景图来自真实视频抽帧；",
                small, Color.ParseHex("5B4A20"), new PointF(930, 710));
            ctx.DrawText("未生成或展示不存在的 RAW、PSD、AI、MOV 等源文件。",
                small, Color.ParseHex("5B4A20"), new PointF(930, 740));
        });
        return image;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = new Styles();
        styles.Append(CreateStyle("Normal", "正文", 22, "Calibri", "222222", 0, 120, 300));
        styles.Append(CreateStyle("Title", "标题", 40, "Microsoft YaHei", "1F4D78", 0, 120, 300, bold: true));
        styles.Append(CreateStyle("Subtitle", "副标题", 22, "Microsoft YaHei", "7A5A00", 0, 160, 300));
        styles.Append(CreateStyle("Heading1", "一级标题", 32, "Microsoft YaHei", "2E74B5", 360, 200, 300, bold: true));
        main.AddNewPart<StyleDefinitionsPart>().Styles = styles;
    }

    private static Style CreateStyle(
        string id, string name, int halfPoints, string font, string color,
        int before, int after, int line, bool bold = false)
    {
        var runProps = new StyleRunProperties(
            new RunFonts { Ascii = font, HighAnsi = font, EastAsia = "Microsoft YaHei" },
            new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) },
            new WordColor { Val = color });
        if (bold) runProps.Append(new Bold());
        return new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new StyleParagraphProperties(
                new SpacingBetweenLines
                {
                    Before = before.ToString(CultureInfo.InvariantCulture),
                    After = after.ToString(CultureInfo.InvariantCulture),
                    Line = line.ToString(CultureInfo.InvariantCulture),
                    LineRule = LineSpacingRuleValues.Auto,
                }),
            runProps)
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            CustomStyle = true,
        };
    }

    private static Paragraph ParagraphOf(string text, string style) =>
        new(
            new ParagraphProperties(new ParagraphStyleId { Val = style }),
            new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static TableRow CreateRow(IReadOnlyList<string> values, bool header)
    {
        int[] widths = [720, 3600, 1200, 1440, 2400];
        var row = new TableRow();
        for (var i = 0; i < values.Count; i++)
        {
            var props = new TableCellProperties(
                new TableCellWidth { Width = widths[i].ToString(), Type = TableWidthUnitValues.Dxa },
                new TableCellMargin(
                    new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new StartMargin { Width = "120", Type = TableWidthUnitValues.Dxa },
                    new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new EndMargin { Width = "120", Type = TableWidthUnitValues.Dxa }));
            if (header) props.Append(new Shading { Fill = "E8EEF5", Val = ShadingPatternValues.Clear });
            var runProps = new RunProperties(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Microsoft YaHei" },
                new FontSize { Val = "20" });
            if (header) runProps.Append(new Bold());
            row.Append(new TableCell(
                props,
                new Paragraph(
                    new ParagraphProperties(new SpacingBetweenLines { After = "0", Line = "260", LineRule = LineSpacingRuleValues.Auto }),
                    new Run(runProps, new Text(values[i])))));
        }

        return row;
    }

    private static TableBorders CreateBorders() =>
        new(
            new TopBorder { Val = BorderValues.Single, Color = "B8C5D0", Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Color = "B8C5D0", Size = 4 },
            new BottomBorder { Val = BorderValues.Single, Color = "B8C5D0", Size = 4 },
            new RightBorder { Val = BorderValues.Single, Color = "B8C5D0", Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Color = "D7DEE5", Size = 4 },
            new InsideVerticalBorder { Val = BorderValues.Single, Color = "D7DEE5", Size = 4 });

    private static void DrawFrameContain(Image<Rgba32> canvas, Image<Rgba32> source, Rectangle target)
    {
        canvas.Mutate(ctx => ctx.Fill(Color.ParseHex("1D2522"), target));
        using var clone = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(target.Width, target.Height),
            Mode = ResizeMode.Max,
        }));
        var point = new Point(
            target.X + (target.Width - clone.Width) / 2,
            target.Y + (target.Height - clone.Height) / 2);
        canvas.Mutate(ctx => ctx.DrawImage(clone, point, 1));
    }

    private static string Save(Image<Rgba32> image, string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        image.Save(path, new PngEncoder());
        return path;
    }

    private static IEnumerable<VideoRecord> SelectEvenly(
        IReadOnlyList<VideoRecord> records,
        int count)
    {
        if (records.Count <= count) return records;
        return Enumerable.Range(0, count)
            .Select(i => records[(int)Math.Round(i * (records.Count - 1d) / (count - 1d))])
            .DistinctBy(r => r.FullPath);
    }

    private static int ParseEpisodeNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var marker = name.LastIndexOf("第", StringComparison.Ordinal);
        var end = name.LastIndexOf("集", StringComparison.Ordinal);
        if (marker >= 0 && end > marker &&
            int.TryParse(name.AsSpan(marker + 1, end - marker - 1), out var episode))
        {
            return episode;
        }

        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out episode) ? episode : int.MaxValue;
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static ProjectMetadata ReadProjectMetadata(string workflow)
    {
        try
        {
            var path = Path.Combine(workflow, "shortdrama-project.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new ProjectMetadata(
                root.TryGetProperty("originalTitle", out var title) ? title.GetString() ?? "" : "",
                root.TryGetProperty("bookId", out var id) ? id.GetString() ?? "" : "");
        }
        catch
        {
            return new ProjectMetadata("", "");
        }
    }

    private static FontFamily? ResolveFontFamily()
    {
        string[] candidates =
        [
            "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun",
            "PingFang SC", "Noto Sans CJK SC", "Noto Sans SC", "Arial",
        ];
        foreach (var name in candidates)
        {
            if (SystemFonts.TryGet(name, out var family)) return family;
        }

        return SystemFonts.Families.FirstOrDefault();
    }

    private static void TryDeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static string Csv(string value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
    private static string DescribeOutput(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.Name}（{FormatBytes(info.Length)}）" : $"{info.Name}（未落盘）";
    }
    private static string FormatDuration(double seconds) =>
        seconds <= 0 ? "未读取" : TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
    private static string FormatTimeCodeForFile(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"mm\-ss");
    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):0.00} GB"
            : bytes >= 1024L * 1024
                ? $"{bytes / (1024d * 1024):0.00} MB"
                : $"{bytes / 1024d:0.0} KB";

    private static string TrimForUi(string text, int maxChars)
    {
        var value = (text ?? "").Trim();
        return value.Length <= maxChars ? value : value[..Math.Max(1, maxChars - 1)] + "…";
    }

    private static string SanitizePathSegment(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((text ?? "").Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "短剧项目" : cleaned;
    }

    private sealed record VideoRecord(
        string FullPath,
        string FileName,
        int Episode,
        long Length,
        DateTime LastWriteTime,
        double DurationSeconds,
        int Width,
        int Height,
        double FrameRate,
        string AudioCodec,
        string Sha256);

    private sealed record EvidenceFrame(Image<Rgba32> Image, string Label, string SourceFile);
    private sealed record ProjectMetadata(string OriginalTitle, string BookId);
}
