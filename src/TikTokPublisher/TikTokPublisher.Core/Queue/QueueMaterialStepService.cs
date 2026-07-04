using System.Diagnostics;
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
        var timeoutSeconds = Math.Clamp(settings.HongguoDownloadTimeoutSeconds, 10, 600);
        var attempts = Math.Clamp(settings.HongguoEpisodeDownloadAttempts, 1, 20);
        log($"下载并发: {concurrent}，单集超时: {timeoutSeconds}s，重试次数: {attempts}");

        var request = new DramaDownloadRequest(
            ProjectDir: context.SourceProjectDir,
            OutputDir: context.SourceProjectDir,
            DisplayName: FirstNonEmpty(item.Title, item.OriginalTitle, Path.GetFileName(context.SourceProjectDir)),
            BookId: bookId,
            Episodes: FirstNonEmpty(metadata.Episodes, "all"),
            Quality: FirstNonEmpty(metadata.Quality, settings.DramaDownloadDefaultQuality, "1080P"),
            Concurrent: concurrent,
            EpisodeNumberMode: FirstNonEmpty(metadata.EpisodeNumberMode, "source"));

        var progress = new Progress<string>(log);
        var result = await ShortDramaDramaServices.Downloader.DownloadAsync(request, progress, ct);
        if (!result.Ok)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            throw new InvalidOperationException(result.Message ?? "下载失败");
        }

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
        var originalTitle = ResolveOriginalTitle(infoPath, item, context);
        var sourceSynopsis = ResolveSynopsis(infoPath, item);
        var rewriteHistory = AiRewriteHistoryService.LoadForOriginalTitle(originalTitle);
        var otherHistory = rewriteHistory
            .Where(record => !IsCurrentProjectHistory(record, context))
            .ToList();
        if (otherHistory.Count > 0)
        {
            log($"同原剧名历史记录：{otherHistory.Count} 条，AI 将避开已用新剧名/简介。");
        }

        var configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
        try
        {
            var outputPath = infoPath;
            var outputExists = File.Exists(outputPath);
            var rewriteVariantKey = BuildRewriteVariantKey(context, account);
            var duplicatesHistory = outputExists && ExistingInfoDuplicatesHistory(outputPath, item, otherHistory);
            var needsRewrite = outputExists && (NeedsAiRewrite(item, context, outputPath) || duplicatesHistory);
            if (outputExists && !overwriteExisting && !needsRewrite)
            {
                log("短剧信息已存在且新剧名有效，跳过 AI 改写。");
                AppendCurrentRewriteHistory(item, settings, account, context, outputPath, originalTitle, sourceSynopsis, rewriteVariantKey);
            }
            else
            {
                if (outputExists && !overwriteExisting && needsRewrite)
                {
                    log(duplicatesHistory
                        ? "短剧信息已存在但与其它账号历史重复，重新执行 AI 改写。"
                        : "短剧信息已存在但新剧名未改写，重新执行 AI 改写。");
                }

                var forbiddenTitles = BuildForbiddenTitles(item, context, originalTitle, outputPath, otherHistory);
                var forbiddenSynopses = BuildForbiddenSynopses(sourceSynopsis, otherHistory);
                log("开始 AI 改写短剧信息…");
                var result = await QueueInfrastructureServices.InfoRewriter.RewriteAsync(
                    new ProjectInfoRewriteRequest(
                        ProjectDir: workflowDir,
                        ConfigFile: configPath,
                        OutputFilePath: outputPath,
                        Overwrite: overwriteExisting || outputExists,
                        ForbiddenTitles: forbiddenTitles,
                        ForbiddenSynopses: forbiddenSynopses,
                        TargetSynopsisLength: TargetSynopsisLength(sourceSynopsis),
                        RewriteVariantKey: rewriteVariantKey),
                    ct);
                AppendRewriteHistory(
                    result,
                    item,
                    settings,
                    account,
                    context,
                    originalTitle,
                    sourceSynopsis,
                    rewriteVariantKey);
                log($"改写完成：{result.Title}");
            }
        }
        finally
        {
            TryDelete(configPath);
        }

        var newTitle = ResolveNewTitle(infoPath, item, context);
        if (!string.IsNullOrWhiteSpace(newTitle))
        {
            workflowDir = ProjectWorkspaceService.SyncWorkflowProjectDirName(context.SourceProjectDir, newTitle, log);
            infoPath = Path.Combine(workflowDir, "短剧信息.txt");
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
        var outputPath = Path.Combine(workflowDir, "海报图片.png");

        var inputPath = await QueueMaterialPrepareService.PrepareMaterialInputsAsync(item.ProjectDir, log, ct)
            ?? ProjectWorkspaceService.FindPosterInputFile(context.SourceProjectDir, workflowDir);
        if (inputPath is null)
            throw new InvalidOperationException("未找到可用于生成海报的封面图片，且无法从视频抽帧。");

        var posterMode = (settings.PosterMode ?? "original").Trim();
        if (IsAiPosterMode(posterMode))
        {
            if (!HasImageModelConfig(settings))
                throw new InvalidOperationException("海报 AI 模式需要配置 ImageModel 接口。");

            var configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
            try
            {
                log("开始 AI 海报改字…");
                if (IsHeicLike(inputPath))
                    log($"海报源图为 {Path.GetExtension(inputPath)}，将先转换为 PNG 再调用 AI。");
                log("正在检测海报标题区域…");
                await QueueInfrastructureServices.Poster.RenameAsync(
                    new PosterRenameRequest(
                        ProjectDir: workflowDir,
                        InputFilePath: inputPath,
                        OutputFilePath: outputPath,
                        ConfigFile: configPath,
                        UseAi: false,
                        Overwrite: true,
                        Log: log),
                    ct);
                log($"海报已生成：{Path.GetFileName(outputPath)}（请使用此 PNG 文件，不会覆盖原 HEIC）");
            }
            finally
            {
                TryDelete(configPath);
            }
        }
        else
        {
            Directory.CreateDirectory(workflowDir);
            var tempOutputPath = Path.Combine(
                workflowDir,
                $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.tmp.png");
            try
            {
                await ConvertPosterToPngAsync(inputPath, tempOutputPath, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                File.Copy(tempOutputPath, outputPath, overwrite: true);
            }
            finally
            {
                TryDelete(tempOutputPath);
            }
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

        var infoPayload = TikTokProjectPayloadFactory.BuildFromPublishItem(new PublishItem
        {
            ProjectDir = item.ProjectDir,
            GenreCategory = item.GenreCategory,
            EpisodeCount = episodeCount,
        });

        var payload = new TikTokProjectPayload
        {
            SourceProjectDir = context.SourceProjectDir,
            WorkflowProjectDir = workflowDir,
            Title = title ?? "",
            OriginalTitle = originalTitle ?? "",
            Description = description ?? "",
            EpisodeCount = Math.Max(1, episodeCount),
            TargetAudience = infoPayload.TargetAudience,
            Genres = infoPayload.Genres,
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

    private static string ResolveOriginalTitle(
        string infoPath,
        QueueProjectItem item,
        ProjectWorkspaceContext context)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        return FirstNonEmpty(
            info.GetValueOrDefault("原剧名"),
            item.OriginalTitle,
            item.DisplayName,
            Path.GetFileName(context.SourceProjectDir));
    }

    private static string ResolveSynopsis(string infoPath, QueueProjectItem item)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        return FirstNonEmpty(
            info.GetValueOrDefault("简介"),
            info.GetValueOrDefault("描述"),
            info.GetValueOrDefault("剧情简介"),
            item.Description);
    }

    private static IReadOnlyList<string> BuildForbiddenTitles(
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        string originalTitle,
        string infoPath,
        IReadOnlyList<AiRewriteHistoryRecord> history)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        return UniqueTexts(
        [
            Path.GetFileName(context.SourceProjectDir),
            Path.GetFileName(context.WorkflowProjectDir).TrimStart('_'),
            originalTitle,
            info.GetValueOrDefault("新剧名"),
            info.GetValueOrDefault("剧名"),
            item.NewTitle,
            .. history.Select(record => record.NewTitle),
        ]);
    }

    private static IReadOnlyList<string> BuildForbiddenSynopses(
        string sourceSynopsis,
        IReadOnlyList<AiRewriteHistoryRecord> history)
    {
        return UniqueTexts(
        [
            sourceSynopsis,
            .. history.Select(record => record.NewSynopsis),
        ]);
    }

    private static bool ExistingInfoDuplicatesHistory(
        string infoPath,
        QueueProjectItem item,
        IReadOnlyList<AiRewriteHistoryRecord> history)
    {
        if (history.Count == 0) return false;

        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var title = FirstNonEmpty(
            info.GetValueOrDefault("新剧名"),
            info.GetValueOrDefault("剧名"),
            item.NewTitle,
            item.Title);
        var synopsis = FirstNonEmpty(
            info.GetValueOrDefault("简介"),
            info.GetValueOrDefault("描述"),
            info.GetValueOrDefault("剧情简介"),
            item.Description);

        return (!string.IsNullOrWhiteSpace(title) &&
                AiRewriteHistoryService.IsTitleDuplicate(title, history.Select(record => record.NewTitle))) ||
               (!string.IsNullOrWhiteSpace(synopsis) &&
                AiRewriteHistoryService.IsSynopsisDuplicate(synopsis, history.Select(record => record.NewSynopsis)));
    }

    private static bool IsCurrentProjectHistory(AiRewriteHistoryRecord record, ProjectWorkspaceContext context)
    {
        if (PathsEqual(record.ProjectDir, context.SourceProjectDir) ||
            PathsEqual(record.ProjectDir, context.WorkflowProjectDir))
        {
            return true;
        }

        return PathStartsWith(record.VariantKey, context.SourceProjectDir) ||
               PathStartsWith(record.VariantKey, context.WorkflowProjectDir);
    }

    private static void AppendRewriteHistory(
        ProjectInfoRewriteResult result,
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        ProjectWorkspaceContext context,
        string originalTitle,
        string sourceSynopsis,
        string variantKey)
    {
        AiRewriteHistoryService.Append(new AiRewriteHistoryRecord(
            OriginalTitle: originalTitle,
            OriginalSynopsis: sourceSynopsis,
            NewTitle: result.Title,
            NewSynopsis: result.Synopsis,
            ProjectName: Path.GetFileName(context.SourceProjectDir),
            ProjectDir: context.SourceProjectDir,
            WorkspacePath: context.WorkspaceRoot,
            AccountProfileId: FirstNonEmpty(account?.Id, item.AccountProfileId),
            AccountProfileName: FirstNonEmpty(account?.DisplayName, item.AccountProfileName),
            VariantKey: variantKey,
            ModelName: settings.AiTextModel,
            CreatedAt: DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")));
    }

    private static void AppendCurrentRewriteHistory(
        QueueProjectItem item,
        ClientSettings settings,
        TikTokAccountProfile? account,
        ProjectWorkspaceContext context,
        string infoPath,
        string originalTitle,
        string sourceSynopsis,
        string variantKey)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var newTitle = FirstNonEmpty(
            info.GetValueOrDefault("新剧名"),
            info.GetValueOrDefault("剧名"),
            item.NewTitle,
            item.Title);
        var synopsis = FirstNonEmpty(
            info.GetValueOrDefault("简介"),
            info.GetValueOrDefault("描述"),
            info.GetValueOrDefault("剧情简介"),
            item.Description);

        if (string.IsNullOrWhiteSpace(newTitle) || string.IsNullOrWhiteSpace(originalTitle))
        {
            return;
        }

        AiRewriteHistoryService.Append(new AiRewriteHistoryRecord(
            OriginalTitle: originalTitle,
            OriginalSynopsis: sourceSynopsis,
            NewTitle: newTitle,
            NewSynopsis: synopsis,
            ProjectName: Path.GetFileName(context.SourceProjectDir),
            ProjectDir: context.SourceProjectDir,
            WorkspacePath: context.WorkspaceRoot,
            AccountProfileId: FirstNonEmpty(account?.Id, item.AccountProfileId),
            AccountProfileName: FirstNonEmpty(account?.DisplayName, item.AccountProfileName),
            VariantKey: variantKey,
            ModelName: settings.AiTextModel,
            CreatedAt: DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")));
    }

    private static string BuildRewriteVariantKey(ProjectWorkspaceContext context, TikTokAccountProfile? account)
    {
        var accountKey = FirstNonEmpty(account?.Id, account?.DisplayName, "default");
        return $"{Path.GetFullPath(context.SourceProjectDir)}#{accountKey}";
    }

    private static int TargetSynopsisLength(string sourceSynopsis)
    {
        var length = (sourceSynopsis ?? "").Trim().Length;
        if (length <= 0) return 0;
        if (length < 40) return 60;
        return Math.Min(220, Math.Max(40, length));
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
        var rawOriginalTitle = FirstNonEmpty(
            info.GetValueOrDefault("原剧名"),
            item.OriginalTitle,
            Path.GetFileName(context.SourceProjectDir));
        var rawTitle = FirstNonEmpty(
            info.GetValueOrDefault("新剧名"),
            info.GetValueOrDefault("剧名"),
            item.NewTitle,
            item.Title);
        var originalTitle = NormalizeComparableTitle(rawOriginalTitle);
        var title = NormalizeComparableTitle(rawTitle);
        var shortTitle = NormalizeComparableTitle(info.GetValueOrDefault("短标题"));
        var tagline = FirstNonEmpty(info.GetValueOrDefault("推荐语"));
        var synopsis = FirstNonEmpty(
            info.GetValueOrDefault("简介"),
            info.GetValueOrDefault("描述"),
            info.GetValueOrDefault("剧情简介"));
        var tags = FirstNonEmpty(info.GetValueOrDefault("标签"));

        if (string.IsNullOrWhiteSpace(title)) return false;
        if (!string.IsNullOrWhiteSpace(originalTitle) && string.Equals(title, originalTitle, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(shortTitle) &&
            (string.Equals(shortTitle, title, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(shortTitle, originalTitle, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(tagline)) return false;
        if (IsDefaultSynopsis(synopsis, rawTitle, rawOriginalTitle, Path.GetFileName(context.SourceProjectDir))) return false;
        if (IsDefaultTags(tags)) return false;

        return true;
    }

    private static bool IsDefaultSynopsis(string? value, params string?[] titleCandidates)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Contains("待补充", StringComparison.Ordinal) ||
            text.Contains("暂无简介", StringComparison.Ordinal) ||
            text.Contains("未填写", StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = NormalizeComparableTitle(text);
        return titleCandidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(NormalizeComparableTitle)
            .Any(candidate => !string.IsNullOrWhiteSpace(candidate) &&
                              string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDefaultTags(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return true;

        var normalized = text.Replace("#", string.Empty, StringComparison.Ordinal)
            .Replace("，", ",", StringComparison.Ordinal)
            .Replace("、", ",", StringComparison.Ordinal)
            .Trim();
        return string.Equals(normalized, "短视频", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "短剧", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveNewTitle(
        string infoPath,
        QueueProjectItem item,
        ProjectWorkspaceContext context)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        return FirstNonEmpty(
            info.GetValueOrDefault("新剧名"),
            info.GetValueOrDefault("剧名"),
            item.NewTitle,
            item.Title,
            Path.GetFileName(context.SourceProjectDir));
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

    private static IReadOnlyList<string> UniqueTexts(IEnumerable<string?> values)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var text = value?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var key = NormalizeComparableTitle(text);
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!seen.Add(key)) continue;

            output.Add(text);
        }

        return output;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        var leftKey = NormalizePathText(left);
        var rightKey = NormalizePathText(right);
        return !string.IsNullOrWhiteSpace(leftKey) &&
               !string.IsNullOrWhiteSpace(rightKey) &&
               string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathStartsWith(string? value, string? path)
    {
        var valueKey = NormalizePathText(value);
        var pathKey = NormalizePathText(path);
        if (string.IsNullOrWhiteSpace(valueKey) || string.IsNullOrWhiteSpace(pathKey)) return false;

        return string.Equals(valueKey, pathKey, StringComparison.OrdinalIgnoreCase) ||
               valueKey.StartsWith(pathKey + "/", StringComparison.OrdinalIgnoreCase) ||
               valueKey.StartsWith(pathKey + "#", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePathText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        try
        {
            text = Path.GetFullPath(text);
        }
        catch
        {
            // Variant keys may append "#account"; keep textual normalization in that case.
        }

        return text.Replace('\\', '/').TrimEnd('/');
    }

    private static DownloadMetadata ReadDownloadMetadata(string sourceProjectDir)
    {
        var path = Path.Combine(sourceProjectDir, "shortdrama-project.json");
        if (!File.Exists(path)) return new DownloadMetadata("", "all", "", "", "");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new DownloadMetadata("", "all", "", "", "");

            var root = doc.RootElement;
            return new DownloadMetadata(
                GetString(root, "bookId") ?? GetString(root, "book_id") ?? "",
                GetString(root, "episodes") ?? "all",
                GetString(root, "quality") ?? "",
                GetString(root, "title") ?? GetString(root, "displayName") ?? "",
                GetString(root, "episodeNumberMode") ?? GetString(root, "episode_number_mode") ?? "");
        }
        catch
        {
            return new DownloadMetadata("", "all", "", "", "");
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

    private static async Task ConvertPosterToPngAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (Path.GetExtension(inputPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            return;
        }

        var ffmpeg = FfmpegLocator.ResolveFfmpeg();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                WorkingDirectory = Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath, outputPath })
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
            throw new InvalidOperationException($"海报图片转 PNG 失败：{Path.GetFileName(inputPath)}（{stderr.Trim()}）");
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

    private static bool IsAiPosterMode(string? posterMode)
    {
        var mode = (posterMode ?? "original").Trim().ToLowerInvariant();
        return mode is "original" or "ai" or "poster_ai_erase_pil_title" or "poster_ai_edit";
    }

    private static bool IsHeicLike(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".heic", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasImageModelConfig(ClientSettings settings)
    {
        var provider = (settings.ImageProvider ?? "doubao").Trim().ToLowerInvariant();
        if (provider == "ofox_image2")
        {
            return !string.IsNullOrWhiteSpace(settings.OfoxImage2Endpoint)
                   && !string.IsNullOrWhiteSpace(settings.OfoxImage2ApiKey);
        }

        return !string.IsNullOrWhiteSpace(settings.ImageModelEndpoint)
               && !string.IsNullOrWhiteSpace(settings.ImageModelApiKey);
    }

    private sealed record DownloadMetadata(string BookId, string Episodes, string Quality, string Title, string EpisodeNumberMode);
}
