using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShortDrama.Core.Models;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

/// <summary>队列步骤：下载 / 改写 / 海报 / 删源（对齐 Python drama + tiktok 服务）。</summary>
public static class QueueMaterialStepService
{
    private const int MissingEpisodeRepairRounds = 3;
    private const string AiRewriteStateDocumentType = "ai_rewrite_state";
    private const int AiRewriteStateVersion = 1;

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
        var maxParallelProjects = Math.Clamp(
            settings.DramaDownloadMaxParallelProjects <= 0 ? 1 : settings.DramaDownloadMaxParallelProjects,
            1,
            4);
        var timeoutSeconds = Math.Clamp(settings.HongguoDownloadTimeoutSeconds, 10, 600);
        var attempts = Math.Clamp(settings.HongguoEpisodeDownloadAttempts, 1, 20);
        var displayName = FirstNonEmpty(item.Title, item.OriginalTitle, metadata.Title, Path.GetFileName(context.SourceProjectDir));
        log($"分集下载并发: {concurrent}，同时下载剧数: {maxParallelProjects}，单集超时: {timeoutSeconds}s，重试次数: {attempts}");

        using var downloadSlot = await QueueDownloadSlotCoordinator.WaitAsync(
            maxParallelProjects,
            displayName,
            log,
            ct).ConfigureAwait(false);

        var request = BuildDownloadRequest(context, metadata, settings, displayName, FirstNonEmpty(metadata.Episodes, "all"), concurrent);

        var progress = new Progress<string>(message =>
        {
            if (ShouldLogDownloadProgress(message))
                log(message);
        });
        var result = await ShortDramaDramaServices.Downloader.DownloadAsync(request, progress, ct);
        if (!result.Ok)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            if (await TryRepairMissingEpisodesAsync(context, item, metadata, settings, displayName, log, ct).ConfigureAwait(false))
            {
                ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
                ProjectWorkspaceService.RefreshQueueItemMetadata(item);
                return;
            }

            throw new InvalidOperationException(result.Message ?? "下载失败");
        }

        log(result.Message ?? $"下载完成，共 {result.VideoCount} 集");
        await RepairMissingEpisodesIfNeededAsync(context, item, metadata, settings, displayName, log, ct).ConfigureAwait(false);
        EnsureDownloadedEpisodesComplete(context.SourceProjectDir, item, log);
        ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        ProjectWorkspaceService.RefreshQueueItemMetadata(item);
    }

    private static DramaDownloadRequest BuildDownloadRequest(
        ProjectWorkspaceContext context,
        DownloadMetadata metadata,
        ClientSettings settings,
        string displayName,
        string episodes,
        int concurrent)
    {
        return new DramaDownloadRequest(
            ProjectDir: context.SourceProjectDir,
            OutputDir: context.SourceProjectDir,
            DisplayName: displayName,
            BookId: FirstNonEmpty(metadata.BookId),
            Episodes: FirstNonEmpty(episodes, "all"),
            Quality: FirstNonEmpty(metadata.Quality, settings.DramaDownloadDefaultQuality, "1080P"),
            Concurrent: Math.Clamp(concurrent, 1, 10),
            EpisodeNumberMode: FirstNonEmpty(metadata.EpisodeNumberMode, "source"));
    }

    private static bool ShouldLogDownloadProgress(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return !(message.Contains("下载中", StringComparison.Ordinal) && message.Contains('%'));
    }

    private static readonly System.Text.RegularExpressions.Regex EpisodeNumberInFileName =
        new(@"第\s*(\d+)\s*集", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] EpisodeVideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".ts"];

    private static async Task RepairMissingEpisodesIfNeededAsync(
        ProjectWorkspaceContext context,
        QueueProjectItem item,
        DownloadMetadata metadata,
        ClientSettings settings,
        string displayName,
        Action<string> log,
        CancellationToken ct)
    {
        if (await TryRepairMissingEpisodesAsync(context, item, metadata, settings, displayName, log, ct).ConfigureAwait(false))
            return;
    }

    private static async Task<bool> TryRepairMissingEpisodesAsync(
        ProjectWorkspaceContext context,
        QueueProjectItem item,
        DownloadMetadata metadata,
        ClientSettings settings,
        string displayName,
        Action<string> log,
        CancellationToken ct)
    {
        var inspection = InspectDownloadedEpisodes(context.SourceProjectDir, item);
        if (inspection.IsUnknown || inspection.IsComplete)
            return inspection.IsComplete;
        if (inspection.FoundCount <= 0)
            return false;

        for (var round = 1; round <= MissingEpisodeRepairRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var selection = FormatEpisodeSelection(inspection.Missing);
            log($"检测到缺集：应 {inspection.Expected} 集，实际 {inspection.FoundCount} 集，缺第 {FormatEpisodePreview(inspection.Missing)} 集。开始补下载（{round}/{MissingEpisodeRepairRounds}），补下载并发 1。");

            var repairRequest = BuildDownloadRequest(context, metadata, settings, displayName, selection, concurrent: 1);
            var repairResult = await ShortDramaDramaServices.Downloader.DownloadAsync(
                repairRequest,
                new Progress<string>(message =>
                {
                    if (ShouldLogDownloadProgress(message))
                        log(message);
                }),
                ct).ConfigureAwait(false);

            if (!repairResult.Ok)
                log($"缺集补下载未完成：{repairResult.Message ?? "下载失败"}");

            inspection = InspectDownloadedEpisodes(context.SourceProjectDir, item);
            if (inspection.IsComplete)
            {
                log($"缺集补下载完成，集数完整性校验通过：{inspection.Expected}/{inspection.Expected} 集齐全。");
                return true;
            }
        }

        return false;
    }

    /// <summary>下载后按「短剧信息」集数逐集核对文件，缺集视为下载失败，阻止进入后续步骤。</summary>
    private static void EnsureDownloadedEpisodesComplete(
        string sourceProjectDir,
        QueueProjectItem item,
        Action<string> log)
    {
        var inspection = InspectDownloadedEpisodes(sourceProjectDir, item);
        if (inspection.IsUnknown)
        {
            log(string.IsNullOrWhiteSpace(inspection.SkipReason)
                ? "未能确定短剧总集数，跳过集数完整性校验。"
                : inspection.SkipReason);
            return;
        }

        if (inspection.IsComplete)
        {
            log($"集数完整性校验通过：{inspection.Expected}/{inspection.Expected} 集齐全。");
            return;
        }

        throw new InvalidOperationException(
            $"下载完成但集数不完整：应 {inspection.Expected} 集，实际 {inspection.FoundCount} 集，" +
            $"缺第 {FormatEpisodePreview(inspection.Missing)} 集。" +
            "请重新执行下载步骤或检查片源。");
    }

    private static DownloadCompleteness InspectDownloadedEpisodes(
        string sourceProjectDir,
        QueueProjectItem item)
    {
        var expected = 0;
        try { expected = ProjectWorkspaceService.ResolveSourceEpisodeCount(item.ProjectDir); }
        catch { /* 信息文件缺失时回退 item.EpisodeCount */ }
        if (expected <= 0) expected = item.EpisodeCount;
        if (expected <= 1)
            return DownloadCompleteness.Unknown;

        var found = new HashSet<int>();
        try
        {
            foreach (var file in ProjectVideoResolver.ResolveSourceVideos(sourceProjectDir))
            {
                if (!EpisodeVideoExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                var match = EpisodeNumberInFileName.Match(Path.GetFileName(file));
                if (match.Success && int.TryParse(match.Groups[1].Value, out var episode) && episode > 0)
                    found.Add(episode);
            }
        }
        catch (Exception ex)
        {
            return new DownloadCompleteness(0, found.Count, [], $"集数完整性校验读取目录失败，跳过：{ex.Message}");
        }

        var missing = Enumerable.Range(1, expected).Where(i => !found.Contains(i)).ToList();
        return new DownloadCompleteness(expected, found.Count, missing, "");
    }

    private static string FormatEpisodeSelection(IReadOnlyList<int> episodes)
    {
        if (episodes.Count == 0) return "";

        var parts = new List<string>();
        var start = episodes[0];
        var previous = episodes[0];
        foreach (var episode in episodes.Skip(1))
        {
            if (episode == previous + 1)
            {
                previous = episode;
                continue;
            }

            parts.Add(start == previous ? start.ToString() : $"{start}-{previous}");
            start = previous = episode;
        }

        parts.Add(start == previous ? start.ToString() : $"{start}-{previous}");
        return string.Join(",", parts);
    }

    private static string FormatEpisodePreview(IReadOnlyList<int> episodes) =>
        $"{string.Join("、", episodes.Take(20))}{(episodes.Count > 20 ? "…" : "")}";

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
        var sourceSynopsis = ResolveSynopsis(infoPath, item, context);
        var rewriteHistory = AiRewriteHistoryService.LoadForOriginalTitle(originalTitle);
        var otherHistory = rewriteHistory
            .Where(record => !IsCurrentProjectHistory(record, context))
            .ToList();
        if (otherHistory.Count > 0)
        {
            log($"同原剧名历史记录：{otherHistory.Count} 条，AI 将避开已用新剧名/简介。");
        }

        var rewriteSynopsis = account?.TiktokAiRewriteSynopsis == true;
        var configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings, account);
        try
        {
            var outputPath = infoPath;
            var outputExists = File.Exists(outputPath);
            var rewriteVariantKey = BuildRewriteVariantKey(context, account);
            var duplicatesHistory = outputExists && ExistingInfoDuplicatesHistory(outputPath, item, otherHistory, rewriteSynopsis);
            var needsRewrite = outputExists &&
                               (NeedsAiRewrite(item, context, outputPath, account, rewriteSynopsis) || duplicatesHistory);
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
                var forbiddenSynopses = rewriteSynopsis
                    ? BuildForbiddenSynopses(sourceSynopsis, otherHistory)
                    : [];
                log("开始 AI 改写短剧信息…");
                try
                {
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
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (TryApplyRewriteHistoryFallback(
                    rewriteHistory,
                    context,
                    outputPath,
                    otherHistory,
                    item,
                    log,
                    ex,
                    rewriteSynopsis,
                    rewriteVariantKey))
                {
                    AppendCurrentRewriteHistory(
                        item, settings, account, context, outputPath, originalTitle, sourceSynopsis, rewriteVariantKey);
                }
            }
        }
        finally
        {
            TryDelete(configPath);
        }

        PersistRewriteCompletionState(context, account, infoPath, rewriteSynopsis);

        var newTitle = ResolveNewTitle(infoPath, item, context);
        if (!string.IsNullOrWhiteSpace(newTitle))
        {
            workflowDir = ProjectWorkspaceService.SyncWorkflowProjectDirName(context.SourceProjectDir, newTitle, log);
            infoPath = Path.Combine(workflowDir, "短剧信息.txt");
        }

        await WriteTikTokPublishFieldsAsync(item, settings, account, episodeCount, workflowDir, log, ct);
        ProjectWorkspaceService.RefreshQueueItemMetadata(item);
    }

    public static bool NeedsAiRewrite(QueueProjectItem item, TikTokAccountProfile? account = null)
    {
        try
        {
            var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
            var infoPath = Path.Combine(context.WorkflowProjectDir, "短剧信息.txt");
            if (!File.Exists(infoPath)) return true;
            return NeedsAiRewrite(
                item,
                context,
                infoPath,
                account,
                account?.TiktokAiRewriteSynopsis == true);
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
            throw new InvalidOperationException("未找到可用于生成海报的封面图片。请先提供下载海报或本地图片素材。");

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

    public static async Task RunDeleteSourceVideosAsync(
        QueueProjectItem item,
        ClientSettings settings,
        Action<string> log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        await EnsureSourceVideosCompleteBeforeCleanupAsync(context, item, settings, log, ct)
            .ConfigureAwait(false);
        TikTokSourceVideoCleanupService.DeleteSourceVideos(
            context.SourceProjectDir,
            context.WorkflowProjectDir,
            item.Title,
            item.OriginalTitle,
            log,
            ct);
    }

    private static async Task EnsureSourceVideosCompleteBeforeCleanupAsync(
        ProjectWorkspaceContext context,
        QueueProjectItem item,
        ClientSettings settings,
        Action<string> log,
        CancellationToken ct)
    {
        var inspection = InspectDownloadedEpisodes(context.SourceProjectDir, item);
        if (inspection.IsUnknown)
        {
            log(string.IsNullOrWhiteSpace(inspection.SkipReason)
                ? "删除源视频前未能确定短剧总集数，跳过补下载校验。"
                : $"删除源视频前{inspection.SkipReason}");
            return;
        }

        if (inspection.IsComplete)
        {
            log($"删除源视频前集数校验通过：{inspection.Expected}/{inspection.Expected} 集齐全。");
            return;
        }

        var metadata = ReadDownloadMetadata(context.SourceProjectDir);
        var displayName = FirstNonEmpty(item.Title, item.OriginalTitle, metadata.Title, Path.GetFileName(context.SourceProjectDir));
        log(
            $"删除源视频前发现源视频不完整：短剧总集数 {inspection.Expected}，源视频 {inspection.FoundCount} 个，" +
            $"缺第 {FormatEpisodePreview(inspection.Missing)} 集。先自动补下载，补齐后再删除源视频。");

        var repaired = await TryRepairMissingEpisodesAsync(context, item, metadata, settings, displayName, log, ct)
            .ConfigureAwait(false);

        ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        ProjectWorkspaceService.RefreshQueueItemMetadata(item);

        if (repaired)
            log("删源前缺集已补齐，继续执行删除源视频。");

        EnsureDownloadedEpisodesComplete(context.SourceProjectDir, item, log);
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

    private static string ResolveSynopsis(string infoPath, QueueProjectItem item, ProjectWorkspaceContext context)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var metadata = ReadDownloadMetadata(context.SourceProjectDir);
        return FirstNonEmpty(
            info.GetValueOrDefault("简介"),
            info.GetValueOrDefault("描述"),
            info.GetValueOrDefault("剧情简介"),
            item.Description,
            metadata.Intro);
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
        IReadOnlyList<AiRewriteHistoryRecord> history,
        bool checkSynopsis)
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

        if (!string.IsNullOrWhiteSpace(title) &&
            AiRewriteHistoryService.IsTitleDuplicate(title, history.Select(record => record.NewTitle)))
        {
            return true;
        }

        return checkSynopsis &&
               !string.IsNullOrWhiteSpace(synopsis) &&
               AiRewriteHistoryService.IsSynopsisDuplicate(synopsis, history.Select(record => record.NewSynopsis));
    }

    /// <summary>AI 改写多次失败时的兜底：复用本项目历史生成过的新剧名/简介（避开其它项目已用标题）。</summary>
    private static bool TryApplyRewriteHistoryFallback(
        IReadOnlyList<AiRewriteHistoryRecord> history,
        ProjectWorkspaceContext context,
        string infoPath,
        IReadOnlyList<AiRewriteHistoryRecord> otherHistory,
        QueueProjectItem item,
        Action<string> log,
        Exception failure,
        bool rewriteSynopsis,
        string rewriteVariantKey)
    {
        if (!File.Exists(infoPath))
            return false;

        var forbiddenTitles = otherHistory
            .Select(record => (record.NewTitle ?? "").Trim())
            .Where(title => title.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var candidate = history
            .Where(record => IsCurrentProjectHistory(record, context))
            .Where(record => !rewriteSynopsis || RewriteVariantKeysEqual(record.VariantKey, rewriteVariantKey))
            .Where(record => !string.IsNullOrWhiteSpace(record.NewTitle))
            .Where(record => !rewriteSynopsis || !string.IsNullOrWhiteSpace(record.NewSynopsis))
            .Where(record => !forbiddenTitles.Contains(record.NewTitle.Trim()))
            .OrderByDescending(record => record.CreatedAt, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is null)
            return false;

        var title = candidate.NewTitle.Trim();
        var synopsis = NormalizeSingleLine(candidate.NewSynopsis);
        ProjectWorkspaceService.UpdateProjectInfoField(infoPath, "新剧名", title);
        if (rewriteSynopsis && !string.IsNullOrWhiteSpace(synopsis))
            ProjectWorkspaceService.UpdateProjectInfoField(infoPath, "简介", synopsis);

        item.NewTitle = title;
        if (rewriteSynopsis && !string.IsNullOrWhiteSpace(synopsis))
            item.Description = synopsis;

        log($"AI 改写多次失败（{failure.Message}），已兜底复用本项目历史新剧名：「{title}」");
        return true;
    }

    private static string NormalizeSingleLine(string? text) =>
        string.Join(' ', (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

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
        var synopsisMode = account?.TiktokAiRewriteSynopsis == true ? "1" : "0";
        return $"{Path.GetFullPath(context.SourceProjectDir)}#{accountKey}#synopsis={synopsisMode}";
    }

    private static bool RewriteVariantKeysEqual(string? left, string? right) =>
        string.Equals(
            (left ?? string.Empty).Trim(),
            (right ?? string.Empty).Trim(),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
        var metadata = ReadDownloadMetadata(context.SourceProjectDir);
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
        var synopsis = FirstNonEmpty(
            existing.GetValueOrDefault("简介"),
            existing.GetValueOrDefault("描述"),
            existing.GetValueOrDefault("剧情简介"),
            item.Description,
            metadata.Intro);
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
        AddIfMissing(existing, updates, "简介", synopsis);

        ProjectInfoTextHelper.UpdateFields(infoPath, updates);
        if (!string.IsNullOrWhiteSpace(synopsis))
            item.Description = synopsis;
    }

    private static bool NeedsAiRewrite(
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        string infoPath,
        TikTokAccountProfile? account,
        bool rewriteSynopsis)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        if (!IsProjectInfoRewritten(info, item, context, rewriteSynopsis))
            return true;

        return rewriteSynopsis && !HasMatchingRewriteCompletionState(context, account, info);
    }

    internal static void PersistRewriteCompletionState(
        ProjectWorkspaceContext context,
        TikTokAccountProfile? account,
        string infoPath,
        bool rewriteSynopsis)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var synopsis = CurrentSynopsis(info);
        if (rewriteSynopsis && string.IsNullOrWhiteSpace(synopsis))
            throw new InvalidDataException("AI 简介改写已启用，但改写结果中没有简介，无法记录完成状态。");

        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            AiRewriteStateDocumentType,
            new Dictionary<string, object?>
            {
                ["version"] = AiRewriteStateVersion,
                ["variant_key"] = BuildRewriteVariantKey(context, account),
                ["rewrite_synopsis"] = rewriteSynopsis,
                ["synopsis_fingerprint"] = rewriteSynopsis ? SynopsisFingerprint(synopsis) : string.Empty,
            },
            context.WorkflowProjectDir);
    }

    private static bool HasMatchingRewriteCompletionState(
        ProjectWorkspaceContext context,
        TikTokAccountProfile? account,
        IReadOnlyDictionary<string, string> info)
    {
        var state = ProjectStateDocumentStore.LoadDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            AiRewriteStateDocumentType);

        if (!TryReadBoolean(state.GetValueOrDefault("rewrite_synopsis"), out var rewriteSynopsis) || !rewriteSynopsis)
            return false;

        var variantKey = ReadJsonString(state.GetValueOrDefault("variant_key"));
        if (!RewriteVariantKeysEqual(variantKey, BuildRewriteVariantKey(context, account)))
            return false;

        var expectedFingerprint = ReadJsonString(state.GetValueOrDefault("synopsis_fingerprint"));
        return !string.IsNullOrWhiteSpace(expectedFingerprint) &&
               string.Equals(
                   expectedFingerprint,
                   SynopsisFingerprint(CurrentSynopsis(info)),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string CurrentSynopsis(IReadOnlyDictionary<string, string> info) =>
        FirstNonEmpty(
            info.GetValueOrDefault("简介"),
            info.GetValueOrDefault("描述"),
            info.GetValueOrDefault("剧情简介"));

    private static string SynopsisFingerprint(string? synopsis)
    {
        var normalized = string.Concat((synopsis ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static bool TryReadBoolean(JsonElement element, out bool value)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        return bool.TryParse(ReadJsonString(element), out value);
    }

    private static string ReadJsonString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();

    private static bool IsProjectInfoRewritten(
        IReadOnlyDictionary<string, string> info,
        QueueProjectItem item,
        ProjectWorkspaceContext context,
        bool rewriteSynopsis)
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
        var tagline = FirstNonEmpty(info.GetValueOrDefault("推荐语"));
        var synopsis = FirstNonEmpty(
            info.GetValueOrDefault("简介"),
            info.GetValueOrDefault("描述"),
            info.GetValueOrDefault("剧情简介"));

        if (string.IsNullOrWhiteSpace(title)) return false;
        if (!string.IsNullOrWhiteSpace(originalTitle) && string.Equals(title, originalTitle, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(tagline)) return false;
        if (rewriteSynopsis &&
            IsDefaultSynopsis(synopsis, rawTitle, rawOriginalTitle, Path.GetFileName(context.SourceProjectDir)))
            return false;

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
        if (!File.Exists(path)) return new DownloadMetadata("", "all", "", "", "", "");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new DownloadMetadata("", "all", "", "", "", "");

            var root = doc.RootElement;
            return new DownloadMetadata(
                GetString(root, "bookId") ?? GetString(root, "book_id") ?? "",
                GetString(root, "episodes") ?? "all",
                GetString(root, "quality") ?? "",
                GetString(root, "title") ?? GetString(root, "displayName") ?? "",
                GetString(root, "episodeNumberMode") ?? GetString(root, "episode_number_mode") ?? "",
                GetString(root, "intro") ?? GetString(root, "description") ?? "");
        }
        catch
        {
            return new DownloadMetadata("", "all", "", "", "", "");
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

    private sealed record DownloadCompleteness(
        int Expected,
        int FoundCount,
        IReadOnlyList<int> Missing,
        string SkipReason)
    {
        public static DownloadCompleteness Unknown { get; } = new(0, 0, [], "");

        public bool IsUnknown => Expected <= 1 || !string.IsNullOrWhiteSpace(SkipReason);
        public bool IsComplete => !IsUnknown && Missing.Count == 0;
    }

    private sealed record DownloadMetadata(
        string BookId,
        string Episodes,
        string Quality,
        string Title,
        string EpisodeNumberMode,
        string Intro);
}
