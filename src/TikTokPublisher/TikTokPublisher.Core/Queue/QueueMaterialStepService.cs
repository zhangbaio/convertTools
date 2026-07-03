using ShortDrama.Core.Models;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>队列步骤：下载 / 改写 / 海报 / 删源（对齐 Python drama + tiktok 服务）。</summary>
public static class QueueMaterialStepService
{
    public static async Task RunDownloadAsync(
        QueueProjectItem item,
        ClientSettings settings,
        Action<string> log,
        CancellationToken ct)
    {
        ShortDramaDramaServices.RefreshSettings(settings);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var metadata = ReadDownloadMetadata(context.SourceProjectDir);
        var bookId = FirstNonEmpty(metadata.BookId);
        if (string.IsNullOrWhiteSpace(bookId))
            throw new InvalidOperationException("项目缺少 bookId，无法执行下载步骤。");

        var concurrent = Math.Clamp(settings.DramaDownloadConcurrent, 1, 10);
        log($"下载并发: {concurrent}");

        var request = new DramaDownloadRequest(
            ProjectDir: context.SourceProjectDir,
            OutputDir: context.SourceProjectDir,
            DisplayName: FirstNonEmpty(item.Title, item.OriginalTitle, Path.GetFileName(context.SourceProjectDir)),
            BookId: bookId,
            Episodes: FirstNonEmpty(metadata.Episodes, "all"),
            Quality: FirstNonEmpty(metadata.Quality, settings.DramaDownloadDefaultQuality, "1080P"),
            Concurrent: concurrent);

        var progress = new Progress<string>(log);
        var result = await ShortDramaDramaServices.Downloader.DownloadAsync(request, progress, ct);
        if (!result.Ok)
            throw new InvalidOperationException(result.Message ?? "下载失败");

        log(result.Message ?? $"下载完成，共 {result.VideoCount} 集");
        ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        ProjectWorkspaceService.RefreshQueueItemMetadata(item);
    }

    public static async Task RunRewriteAsync(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        bool overwriteExisting,
        Action<string> log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.AiTextEndpoint) || string.IsNullOrWhiteSpace(settings.AiTextApiKey))
            throw new InvalidOperationException("请先在系统设置中配置 AI 文本接口。");

        var episodeCount = ProjectWorkspaceService.ResolveSourceEpisodeCount(item.ProjectDir);
        var workflowDir = ProjectWorkspaceService.EnsureWorkflowInfo(item.ProjectDir, episodeCount, log);
        var infoPath = Path.Combine(workflowDir, "短剧信息.txt");
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        EnsureRewriteInputDefaults(infoPath, item, context, episodeCount);

        var configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
        try
        {
            var outputPath = infoPath;
            var outputExists = File.Exists(outputPath);
            var needsRewrite = outputExists && NeedsAiRewrite(item, context, outputPath);
            if (outputExists && !overwriteExisting && !needsRewrite)
            {
                log("短剧信息已存在且新剧名有效，跳过 AI 改写。");
            }
            else
            {
                if (outputExists && !overwriteExisting && needsRewrite)
                    log("短剧信息已存在但新剧名未改写，重新执行 AI 改写。");
                log("开始 AI 改写短剧信息…");
                var result = await QueueInfrastructureServices.InfoRewriter.RewriteAsync(
                    new ProjectInfoRewriteRequest(
                        workflowDir,
                        configPath,
                        outputPath,
                        overwriteExisting || outputExists),
                    ct);
                log($"改写完成：{result.Title}");
            }
        }
        finally
        {
            TryDelete(configPath);
        }

        await WriteTikTokPublishFieldsAsync(item, settings, account, episodeCount, workflowDir, log, ct);
        ProjectWorkspaceService.RefreshQueueItemMetadata(item);
    }

    public static bool NeedsAiRewrite(QueueProjectItem item)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            var infoPath = Path.Combine(context.WorkflowProjectDir, "短剧信息.txt");
            if (!File.Exists(infoPath)) return true;
            return NeedsAiRewrite(item, context, infoPath);
        }
        catch
        {
            return false;
        }
    }

    public static async Task RunGeneratePosterAsync(
        QueueProjectItem item,
        ClientSettings settings,
        Action<string> log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var workflowDir = ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        var outputPath = Path.Combine(workflowDir, "海报图片.jpg");

        var inputPath = await QueueMaterialPrepareService.PrepareMaterialInputsAsync(item.ProjectDir, log, ct)
            ?? ProjectWorkspaceService.FindPosterInputFile(context.SourceProjectDir, workflowDir);
        if (inputPath is null)
            throw new InvalidOperationException("未找到可用于生成海报的封面图片，且无法从视频抽帧。");

        var posterMode = (settings.PosterMode ?? "original").Trim();
        if (string.Equals(posterMode, "ai", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.ImageModelEndpoint) || string.IsNullOrWhiteSpace(settings.ImageModelApiKey))
                throw new InvalidOperationException("海报 AI 模式需要配置 ImageModel 接口。");

            var configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
            try
            {
                log("开始 AI 海报生成…");
                await QueueInfrastructureServices.Poster.RenameAsync(
                    new PosterRenameRequest(
                        ProjectDir: workflowDir,
                        InputFilePath: inputPath,
                        OutputFilePath: outputPath,
                        ConfigFile: configPath,
                        UseAi: true,
                        Overwrite: true),
                    ct);
                log($"海报已生成：{Path.GetFileName(outputPath)}");
            }
            finally
            {
                TryDelete(configPath);
            }
        }
        else
        {
            Directory.CreateDirectory(workflowDir);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Copy(inputPath, outputPath, overwrite: true);
            log($"已复制原图海报：{Path.GetFileName(outputPath)}");
        }

        item.CoverPath = outputPath;
    }

    public static Task RunDeleteSourceVideosAsync(
        QueueProjectItem item,
        Action<string> log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        TikTokSourceVideoCleanupService.DeleteSourceVideos(
            context.SourceProjectDir,
            context.WorkflowProjectDir,
            item.Title,
            item.OriginalTitle,
            log,
            ct);
        return Task.CompletedTask;
    }

    private static async Task WriteTikTokPublishFieldsAsync(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        int episodeCount,
        string workflowDir,
        Action<string> log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var infoPath = Path.Combine(workflowDir, "短剧信息.txt");
        var merged = ProjectInfoTextHelper.MergeProjectInfo(
            Path.Combine(context.SourceProjectDir, "短剧信息.txt"),
            infoPath);

        var title = FirstNonEmpty(
            merged.GetValueOrDefault("新剧名"),
            merged.GetValueOrDefault("剧名"),
            item.NewTitle,
            item.Title,
            Path.GetFileName(workflowDir).TrimStart('_'));
        var originalTitle = FirstNonEmpty(
            merged.GetValueOrDefault("原剧名"),
            item.OriginalTitle,
            Path.GetFileName(context.SourceProjectDir));
        var description = FirstNonEmpty(
            merged.GetValueOrDefault("简介"),
            merged.GetValueOrDefault("描述"),
            merged.GetValueOrDefault("剧情简介"),
            item.Description);

        var payload = new TikTokProjectPayload
        {
            SourceProjectDir = context.SourceProjectDir,
            WorkflowProjectDir = workflowDir,
            Title = title,
            OriginalTitle = originalTitle,
            Description = description,
            EpisodeCount = Math.Max(1, episodeCount),
        };

        var options = TikTokPublishOptionsBuilder.FromAccount(account);
        var recommendation = await TikTokPublishRecommendationService.BuildRecommendationAsync(
            payload,
            settings,
            options,
            log,
            ct);

        ProjectInfoTextHelper.UpdateFields(infoPath, new Dictionary<string, string>
        {
            ["目标观众"] = TikTokPublishRecommendationService.TargetAudienceDisplayText(recommendation.TargetAudience),
            ["题材类型"] = string.Join("、", recommendation.Genres),
        });

        item.GenreCategory = string.Join("、", recommendation.Genres);
        log($"TikTok 发布字段已生成：目标观众={TikTokPublishRecommendationService.TargetAudienceDisplayText(recommendation.TargetAudience)}，题材类型={string.Join("、", recommendation.Genres)}");
    }

    private static void EnsureRewriteInputDefaults(
        string infoPath,
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        int episodeCount)
    {
        var existing = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var originalTitle = FirstNonEmpty(
            existing.GetValueOrDefault("原剧名"),
            item.OriginalTitle,
            item.DisplayName,
            Path.GetFileName(context.SourceProjectDir));
        var currentTitle = FirstNonEmpty(
            existing.GetValueOrDefault("新剧名"),
            existing.GetValueOrDefault("剧名"),
            item.NewTitle,
            item.Title,
            originalTitle);
        var totalMinutes = Math.Max(1, episodeCount);
        var costWan = Math.Max(1, (int)Math.Round(totalMinutes * 1500d / 10000d, MidpointRounding.AwayFromZero));

        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfMissing(existing, updates, "原剧名", originalTitle);
        if (!existing.ContainsKey("新剧名") && !existing.ContainsKey("剧名"))
            updates["新剧名"] = currentTitle;
        AddIfMissing(existing, updates, "集数", totalMinutes.ToString());
        AddIfMissing(existing, updates, "时长", $"{totalMinutes} 分钟");
        AddIfMissing(existing, updates, "成本", $"{costWan} 万元");
        AddIfMissing(existing, updates, "制作公司", "未填写公司");

        ProjectInfoTextHelper.UpdateFields(infoPath, updates);
    }

    private static bool NeedsAiRewrite(
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        string infoPath)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        return !IsProjectInfoRewritten(info, item, context);
    }

    private static bool IsProjectInfoRewritten(
        IReadOnlyDictionary<string, string> info,
        QueueProjectItem item,
        ProjectWorkspaceContext context)
    {
        var originalTitle = NormalizeComparableTitle(FirstNonEmpty(
            info.GetValueOrDefault("原剧名"),
            item.OriginalTitle,
            Path.GetFileName(context.SourceProjectDir)));
        var title = NormalizeComparableTitle(FirstNonEmpty(
            info.GetValueOrDefault("新剧名"),
            info.GetValueOrDefault("剧名"),
            item.NewTitle,
            item.Title));
        var shortTitle = NormalizeComparableTitle(info.GetValueOrDefault("短标题"));

        if (string.IsNullOrWhiteSpace(title)) return false;
        if (!string.IsNullOrWhiteSpace(originalTitle) && string.Equals(title, originalTitle, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(shortTitle) &&
            (string.Equals(shortTitle, title, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(shortTitle, originalTitle, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static void AddIfMissing(
        IReadOnlyDictionary<string, string> existing,
        Dictionary<string, string> updates,
        string key,
        string value)
    {
        if (existing.ContainsKey(key) || string.IsNullOrWhiteSpace(value)) return;
        updates[key] = value;
    }

    private static string NormalizeComparableTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim().Replace('：', ':');
        return string.Concat(text.Where(ch => !char.IsWhiteSpace(ch)));
    }

    private static DownloadMetadata ReadDownloadMetadata(string sourceProjectDir)
    {
        var path = Path.Combine(sourceProjectDir, "shortdrama-project.json");
        if (!File.Exists(path)) return new DownloadMetadata("", "all", "", "");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new DownloadMetadata("", "all", "", "");

            var root = doc.RootElement;
            return new DownloadMetadata(
                GetString(root, "bookId") ?? GetString(root, "book_id") ?? "",
                GetString(root, "episodes") ?? "all",
                GetString(root, "quality") ?? "",
                GetString(root, "title") ?? GetString(root, "displayName") ?? "");
        }
        catch
        {
            return new DownloadMetadata("", "all", "", "");
        }
    }

    private static string? GetString(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    private sealed record DownloadMetadata(string BookId, string Episodes, string Quality, string Title);
}
