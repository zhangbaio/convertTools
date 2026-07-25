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
using ImageFont = SixLabors.Fonts.Font;
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
    public const string ScreenshotVersion = "v4-ai-drama-source-assets";

    private const string LegacyOutputDirectoryName = "原始文件或素材文件信息";
    private const int ContactSheetFrameCount = 4;
    private const int MaxCatalogFiles = 12;
    private static readonly string[] FileNames =
    [
        "01_角色参考素材.png",
        "02_场景参考素材.png",
        "03_真实项目文件目录.png",
        "04_真实制作链路.png",
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
        var sourceMaterials = DiscoverSourceMaterials(workflow, cancellationToken);
        log?.Invoke(
            $"原始文件信息/扫描：发现真实成片 {videos.Count} 个；" +
            $"可用制作素材 {sourceMaterials.Count} 个；" +
            $"海报={(string.IsNullOrWhiteSpace(poster) ? "未找到" : Path.GetFileName(poster))}。");

        var records = ProbeVideos(videos, cancellationToken, log);
        WriteProjectDescription(evidenceDir, workflow, title, company, videos.Count);
        log?.Invoke("原始文件信息/元数据：已生成 项目说明.txt。");
        var manifestPath = WriteManifest(evidenceDir, records, cancellationToken);
        log?.Invoke($"原始文件信息/清单：已生成 {DescribeOutput(manifestPath)}，共 {records.Count} 条视频记录。");
        var docxPath = WriteDerivedScriptDocument(evidenceDir, title, company, records);
        log?.Invoke($"原始文件信息/文档：已生成 {DescribeOutput(docxPath)}，口径=基于成片整理。");

        var characterFrames = LoadDirectEvidenceFrames(
            sourceMaterials, SourceMaterialCategory.Character, ContactSheetFrameCount, workflow);
        if (characterFrames.Count < ContactSheetFrameCount)
        {
            var directCount = characterFrames.Count;
            var extractedFrames = ExtractEvidenceFrames(
                records, poster, characterDir, "角色", sceneMode: false, cancellationToken, log);
            AppendFrames(characterFrames, extractedFrames, ContactSheetFrameCount);
            log?.Invoke(
                $"原始文件信息/角色素材：复用真实主体/定妆文件 {directCount} 张，" +
                $"并用成片抽帧补足至 {characterFrames.Count} 张。");
        }
        else
        {
            log?.Invoke($"原始文件信息/角色素材：复用真实主体/定妆文件 {characterFrames.Count} 张，未执行角色抽帧。");
        }

        var sceneFrames = LoadDirectEvidenceFrames(
            sourceMaterials, SourceMaterialCategory.Scene, ContactSheetFrameCount, workflow);
        if (sceneFrames.Count < ContactSheetFrameCount)
        {
            var directCount = sceneFrames.Count;
            var extractedFrames = ExtractEvidenceFrames(
                records, poster, sceneDir, "场景", sceneMode: true, cancellationToken, log);
            AppendFrames(sceneFrames, extractedFrames, ContactSheetFrameCount);
            log?.Invoke(
                $"原始文件信息/场景素材：复用真实场景参考文件 {directCount} 张，" +
                $"并用成片抽帧补足至 {sceneFrames.Count} 张。");
        }
        else
        {
            log?.Invoke($"原始文件信息/场景素材：复用真实场景参考文件 {sceneFrames.Count} 张，未执行场景抽帧。");
        }

        var keyframeFrames = LoadDirectEvidenceFrames(
            sourceMaterials, SourceMaterialCategory.Keyframe, ContactSheetFrameCount, workflow);

        var family = ResolveFontFamily()
            ?? throw new InvalidOperationException("未找到可用中文字体，无法生成原始文件信息截图。");
        var outputs = new List<string>(RequiredImageCount);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var shot = RenderContactSheet(
                       title, "角色主体与定妆素材", characterFrames, family,
                       "优先复用项目内真实主体图、四宫格和定妆图；缺失时才使用成片抽帧。"))
            {
                var path = Save(shot, outputDir, FileNames[0]);
                outputs.Add(path);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var shot = RenderContactSheet(
                       title, "场景参考与镜头首帧素材", sceneFrames, family,
                       "优先复用真实场景板与首帧；所有标签均可回溯到实际文件。"))
            {
                var path = Save(shot, outputDir, FileNames[1]);
                outputs.Add(path);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var shot = RenderFileEvidence(
                       title, workflow, evidenceDir, records, sourceMaterials, manifestPath, docxPath, family))
            {
                var path = Save(shot, outputDir, FileNames[2]);
                outputs.Add(path);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using (var shot = RenderProductionChainEvidence(
                       title, company, records, sourceMaterials, keyframeFrames, docxPath, manifestPath, family))
            {
                var path = Save(shot, outputDir, FileNames[3]);
                outputs.Add(path);
            }
        }
        finally
        {
            foreach (var frame in characterFrames.Concat(sceneFrames).Concat(keyframeFrames))
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

    private static List<SourceMaterialRecord> DiscoverSourceMaterials(
        string workflow,
        CancellationToken cancellationToken)
    {
        var outputDir = GetOutputDirectory(workflow);
        var evidenceDir = GetEvidenceDirectory(workflow);
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp",
            ".md", ".txt", ".json", ".csv", ".docx", ".xlsx",
            ".wav", ".mp3", ".m4a", ".aac",
            ".mp4", ".mov", ".webm",
        };

        var materials = new List<SourceMaterialRecord>();
        foreach (var path in EnumerateFilesSafely(workflow, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (materials.Count >= 5000) break;
            var full = Path.GetFullPath(path);
            if (IsUnderDirectory(full, outputDir) || IsUnderDirectory(full, evidenceDir)) continue;
            var relative = Path.GetRelativePath(workflow, full);
            if (ContainsIgnoredPathSegment(relative)) continue;
            var extension = Path.GetExtension(full);
            if (!supported.Contains(extension)) continue;

            SourceMaterialCategory? category = ClassifySourceMaterial(relative, extension);
            if (category is null) continue;
            try
            {
                var info = new FileInfo(full);
                materials.Add(new SourceMaterialRecord(
                    full,
                    relative,
                    category.Value,
                    info.Length,
                    info.LastWriteTime));
            }
            catch
            {
                // A concurrently changed source file is not reliable evidence.
            }
        }

        return materials
            .OrderBy(record => GetMaterialPriority(record.RelativePath, record.Category))
            .ThenByDescending(record => record.LastWriteTime)
            .ThenBy(record => record.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateFilesSafely(
        string root,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            foreach (var child in directories)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    pending.Push(child);
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories that cannot be inspected.
                }
                catch (IOException)
                {
                    // Skip directories that disappeared during traversal.
                }
            }
        }
    }

    private static SourceMaterialCategory? ClassifySourceMaterial(string relativePath, string extension)
    {
        var text = relativePath.Replace('\\', '/').ToLowerInvariant();
        var isImage = extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                      || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                      || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                      || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        if (isImage && ContainsAny(text, "首帧", "关键帧", "keyframe", "storyboard", "分镜"))
            return SourceMaterialCategory.Keyframe;
        if (isImage && ContainsAny(text, "场景", "scene", "location", "店铺", "街道", "空镜"))
            return SourceMaterialCategory.Scene;
        if (isImage && ContainsAny(
                text, "角色", "主体", "定妆", "四宫格", "character", "cast", "principal", "人物"))
            return SourceMaterialCategory.Character;
        if (ContainsAny(text, "提示词", "prompt") &&
            ContainsAny(extension.ToLowerInvariant(), ".md", ".txt", ".json"))
            return SourceMaterialCategory.Prompt;
        if (ContainsAny(extension.ToLowerInvariant(), ".wav", ".mp3", ".m4a", ".aac"))
            return SourceMaterialCategory.Audio;
        if (ContainsAny(extension.ToLowerInvariant(), ".mp4", ".mov", ".webm"))
            return SourceMaterialCategory.Video;
        if (ContainsAny(extension.ToLowerInvariant(), ".docx", ".xlsx", ".csv", ".md", ".txt", ".json"))
            return SourceMaterialCategory.Document;
        return null;
    }

    private static List<EvidenceFrame> LoadDirectEvidenceFrames(
        IReadOnlyList<SourceMaterialRecord> materials,
        SourceMaterialCategory category,
        int count,
        string workflow)
    {
        var frames = new List<EvidenceFrame>(count);
        foreach (var material in materials.Where(item => item.Category == category))
        {
            if (frames.Count >= count) break;
            try
            {
                frames.Add(new EvidenceFrame(
                    Image.Load<Rgba32>(material.FullPath),
                    GetCategoryDisplayName(category),
                    Path.GetRelativePath(workflow, material.FullPath)));
            }
            catch
            {
                // Invalid or partially-written images are not included.
            }
        }

        return frames;
    }

    private static void AppendFrames(
        List<EvidenceFrame> destination,
        List<EvidenceFrame> candidates,
        int maximumCount)
    {
        var take = Math.Min(maximumCount - destination.Count, candidates.Count);
        destination.AddRange(candidates.Take(take));
        foreach (var unused in candidates.Skip(take))
            unused.Image.Dispose();
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
                                  + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIgnoredPathSegment(string relativePath)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            segment.StartsWith(".", StringComparison.Ordinal)
            || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("缓存", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("temp", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("tmp", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetMaterialPriority(string relativePath, SourceMaterialCategory category)
    {
        var text = relativePath.ToLowerInvariant();
        if (category == SourceMaterialCategory.Character)
        {
            if (ContainsAny(text, "主体主图", "正面全身")) return 0;
            if (ContainsAny(text, "四宫格", "定妆")) return 1;
            return 2;
        }
        if (category == SourceMaterialCategory.Scene)
        {
            if (ContainsAny(text, "场景参考板", "场景板")) return 0;
            return 1;
        }
        if (category == SourceMaterialCategory.Keyframe)
        {
            if (ContainsAny(text, "首帧", "keyframe")) return 0;
            return 1;
        }
        return 2;
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string GetCategoryDisplayName(SourceMaterialCategory category) => category switch
    {
        SourceMaterialCategory.Character => "主体/定妆",
        SourceMaterialCategory.Scene => "场景参考",
        SourceMaterialCategory.Keyframe => "镜头首帧",
        SourceMaterialCategory.Prompt => "提示词",
        SourceMaterialCategory.Audio => "声音素材",
        SourceMaterialCategory.Video => "视频文件",
        _ => "制作文档",
    };

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
                   资料来源：本目录内容由项目内真实角色、场景、首帧、提示词、视频、海报及 shortdrama-project.json 整理生成。
                   真实性说明：优先使用项目内真实制作素材，缺失时才从成片抽帧；剧本文档为成片整理稿，不冒充拍摄前原始剧本。
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
        IReadOnlyList<SourceMaterialRecord> materials,
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

            ctx.DrawText(
                $"真实制作素材：{materials.Count} 个　真实成片：{records.Count} 个",
                heading, Color.ParseHex("20302B"), new PointF(50, 255));
            ctx.DrawText("类型", normal, Color.ParseHex("52606A"), new PointF(65, 300));
            ctx.DrawText("项目内相对路径", normal, Color.ParseHex("52606A"), new PointF(205, 300));
            ctx.DrawText("大小", normal, Color.ParseHex("52606A"), new PointF(1000, 300));
            ctx.DrawText("修改时间", normal, Color.ParseHex("52606A"), new PointF(1140, 300));
        });

        var visibleMaterials = materials
            .Where(item => item.Category != SourceMaterialCategory.Video)
            .Take(MaxCatalogFiles)
            .ToArray();
        for (var i = 0; i < visibleMaterials.Length; i++)
        {
            var material = visibleMaterials[i];
            var y = 330 + i * 36;
            image.Mutate(ctx =>
            {
                ctx.Fill(i % 2 == 0 ? Color.ParseHex("F8FAFC") : Color.White,
                    new RectangleF(50, y, 1340, 34));
                ctx.DrawText(GetCategoryDisplayName(material.Category), mono, Color.ParseHex("35556D"),
                    new PointF(65, y + 8));
                ctx.DrawText(TrimForUi(material.RelativePath, 78), normal, Color.ParseHex("1E2B34"),
                    new PointF(205, y + 8));
                ctx.DrawText(FormatBytes(material.Length), normal, Color.ParseHex("1E2B34"),
                    new PointF(1000, y + 8));
                ctx.DrawText(material.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), normal,
                    Color.ParseHex("1E2B34"), new PointF(1140, y + 8));
            });
        }

        if (visibleMaterials.Length == 0)
        {
            image.Mutate(ctx => ctx.DrawText(
                "未发现角色、场景、首帧、提示词或制作文档；当前仅登记真实成片。",
                heading, Color.ParseHex("7B3232"), new PointF(310, 455)));
        }

        image.Mutate(ctx =>
        {
            var y = 790f;
            ctx.DrawText($"已落盘：{Path.GetFileName(manifestPath)}", normal, Color.ParseHex("1F7A4D"),
                new PointF(50, y));
            ctx.DrawText($"已落盘：{Path.GetFileName(docxPath)}", normal, Color.ParseHex("1F7A4D"),
                new PointF(520, y));
            ctx.DrawText("仅列真实落盘文件；不生成额外素材。", normal, Color.ParseHex("7A5A00"),
                new PointF(1030, y));
        });
        return image;
    }

    private static Image<Rgba32> RenderProductionChainEvidence(
        string title,
        string company,
        IReadOnlyList<VideoRecord> records,
        IReadOnlyList<SourceMaterialRecord> materials,
        IReadOnlyList<EvidenceFrame> keyframes,
        string docxPath,
        string manifestPath,
        FontFamily family)
    {
        const int width = 1440;
        const int height = 900;
        var image = new Image<Rgba32>(width, height, Color.ParseHex("EEF1F4"));
        var titleFont = family.CreateFont(24, FontStyle.Bold);
        var heading = family.CreateFont(16, FontStyle.Bold);
        var normal = family.CreateFont(13);
        var small = family.CreateFont(11);
        var promptFile = materials.FirstOrDefault(item => item.Category == SourceMaterialCategory.Prompt);
        var promptExcerpt = promptFile is null ? "项目内未发现可读取的提示词文件" : ReadPromptExcerpt(promptFile.FullPath);
        image.Mutate(ctx =>
        {
            ctx.Fill(Color.ParseHex("20302B"), new RectangleF(0, 0, width, 100));
            ctx.DrawText("AI短剧真实制作链路", titleFont, Color.White, new PointF(45, 18));
            ctx.DrawText($"{title}　制作方：{company}", normal, Color.ParseHex("DCE7DF"), new PointF(48, 58));

            string[] stages = ["脚本/提示词", "角色与场景", "镜头首帧", "视频片段/成片"];
            for (var i = 0; i < stages.Length; i++)
            {
                var x = 45 + i * 345;
                ctx.Fill(Color.White, new RectangleF(x, 126, 300, 76));
                ctx.Draw(Color.ParseHex("B9C7C0"), 1, new RectangleF(x, 126, 300, 76));
                ctx.DrawText($"{i + 1}", heading, Color.ParseHex("1F7A4D"), new PointF(x + 18, 148));
                ctx.DrawText(stages[i], heading, Color.ParseHex("20302B"), new PointF(x + 55, 148));
                if (i < stages.Length - 1)
                    ctx.DrawText("→", titleFont, Color.ParseHex("789087"), new PointF(x + 310, 145));
            }

            ctx.DrawText("真实首帧/分镜素材", heading, Color.ParseHex("1F4D78"), new PointF(50, 235));
        });

        for (var i = 0; i < Math.Min(4, keyframes.Count); i++)
        {
            var x = 50 + i * 335;
            var rect = new Rectangle(x, 270, 295, 245);
            DrawFrameContain(image, keyframes[i].Image, rect);
            image.Mutate(ctx =>
            {
                ctx.Draw(Color.ParseHex("C2CAD1"), 1, rect);
                ctx.DrawText(TrimForUi(keyframes[i].SourceFile, 34), small, Color.ParseHex("34424C"),
                    new PointF(x, 525));
            });
        }

        image.Mutate(ctx =>
        {
            if (keyframes.Count == 0)
            {
                ctx.Fill(Color.White, new RectangleF(50, 270, 1325, 245));
                ctx.DrawText("项目内未发现首帧或分镜图片；未为证明材料额外生成图片。",
                    heading, Color.ParseHex("7A5A00"), new PointF(400, 380));
            }

            ctx.Fill(Color.White, new RectangleF(50, 575, 850, 250));
            ctx.Draw(Color.ParseHex("CBD3D9"), 1, new RectangleF(50, 575, 850, 250));
            ctx.DrawText("真实提示词节选", heading, Color.ParseHex("1F4D78"), new PointF(75, 598));
            DrawWrappedText(ctx, promptExcerpt, normal, Color.ParseHex("2E3D46"),
                new RectangleF(75, 635, 800, 160), 54, 6);
            ctx.DrawText(promptFile is null ? "未发现提示词文件" : TrimForUi(promptFile.RelativePath, 70),
                small, Color.ParseHex("60717D"), new PointF(75, 795));

            ctx.Fill(Color.White, new RectangleF(930, 575, 445, 250));
            ctx.Draw(Color.ParseHex("CBD3D9"), 1, new RectangleF(930, 575, 445, 250));
            ctx.DrawText("项目核验", heading, Color.ParseHex("1F4D78"), new PointF(955, 598));
            string[] facts =
            [
                $"角色素材：{materials.Count(m => m.Category == SourceMaterialCategory.Character)}",
                $"场景素材：{materials.Count(m => m.Category == SourceMaterialCategory.Scene)}",
                $"首帧素材：{materials.Count(m => m.Category == SourceMaterialCategory.Keyframe)}",
                $"提示词文件：{materials.Count(m => m.Category == SourceMaterialCategory.Prompt)}",
                $"成片数量：{records.Count}",
                $"媒体总时长：{FormatDuration(records.Sum(r => r.DurationSeconds))}",
            ];
            for (var i = 0; i < facts.Length; i++)
                ctx.DrawText(facts[i], normal, Color.ParseHex("2E3D46"), new PointF(955, 638 + i * 27));

            ctx.DrawText($"清单：{Path.GetFileName(manifestPath)}　文档：{Path.GetFileName(docxPath)}",
                small, Color.ParseHex("1F7A4D"), new PointF(50, 855));
            ctx.DrawText("全部内容取自真实落盘文件；未调用AI生成额外证明图片。",
                small, Color.ParseHex("7A5A00"), new PointF(970, 855));
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

    private static void DrawWrappedText(
        IImageProcessingContext context,
        string text,
        ImageFont font,
        Color color,
        RectangleF bounds,
        int maxCharsPerLine,
        int maxLines)
    {
        var normalized = string.Join(" ", (text ?? "").Split(
            ['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var lines = new List<string>();
        while (normalized.Length > 0 && lines.Count < maxLines)
        {
            var take = Math.Min(maxCharsPerLine, normalized.Length);
            if (take < normalized.Length)
            {
                var breakAt = normalized.LastIndexOf(' ', take - 1, take);
                if (breakAt > maxCharsPerLine / 2) take = breakAt;
            }
            lines.Add(normalized[..take].Trim());
            normalized = normalized[take..].TrimStart();
        }
        if (normalized.Length > 0 && lines.Count > 0)
            lines[^1] = TrimForUi(lines[^1], Math.Max(2, maxCharsPerLine - 1));

        for (var i = 0; i < lines.Count; i++)
            context.DrawText(lines[i], font, color, new PointF(bounds.X, bounds.Y + i * 25));
    }

    private static string ReadPromptExcerpt(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var normalized = string.Join(" ", text.Split(
                ['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
            return TrimForUi(normalized, 320);
        }
        catch
        {
            return "提示词文件存在，但当前无法读取文本内容。";
        }
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

    private sealed record SourceMaterialRecord(
        string FullPath,
        string RelativePath,
        SourceMaterialCategory Category,
        long Length,
        DateTime LastWriteTime);

    private enum SourceMaterialCategory
    {
        Character,
        Scene,
        Keyframe,
        Prompt,
        Audio,
        Video,
        Document,
    }

    private sealed record EvidenceFrame(Image<Rgba32> Image, string Label, string SourceFile);
    private sealed record ProjectMetadata(string OriginalTitle, string BookId);
}
