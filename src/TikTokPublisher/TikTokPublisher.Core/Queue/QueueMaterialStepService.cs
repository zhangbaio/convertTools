using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Core.Posters;
using ShortDrama.Infrastructure.Automation;
using ShortDrama.Infrastructure.Files;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Remote;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public delegate Task<IReadOnlyDictionary<int, string>> RoleReferenceEpisodeFallback(
    QueueProjectItem item,
    IReadOnlyList<int> episodeNumbers,
    Action<string>? log,
    CancellationToken ct);

/// <summary>队列步骤：下载 / 改写 / 海报 / 删源（对齐 Python drama + tiktok 服务）。</summary>
public static partial class QueueMaterialStepService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublishedFallbackLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MissingEpisodeRepairRounds = 3;
    private const string ProofMaterialFrameFallbackVideoFileName = "证明材料抽帧兜底.mp4";
    private const string AiRewriteStateDocumentType = "ai_rewrite_state";
    private const int AiRewriteStateVersion = 2;
    private const string EnglishRewriteVariantSuffix = "#output=en";
    private const string PublishTextLanguageField = "发布文案语言";

    public static Task RunDownloadAsync(
        QueueProjectItem item,
        ClientSettings globalSettings,
        TikTokAccountProfile? account,
        bool forceRerun,
        Action<string> log,
        CancellationToken ct) =>
        RunDownloadAsync(item, globalSettings, account, forceRerun, log, ct, ShortDramaDramaServices.Downloader);

    internal static async Task RunDownloadAsync(
        QueueProjectItem item,
        ClientSettings globalSettings,
        TikTokAccountProfile? account,
        bool forceRerun,
        Action<string> log,
        CancellationToken ct,
        IDramaDownloader downloader)
    {
        ct.ThrowIfCancellationRequested();
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var metadata = ReadDownloadMetadata(context.SourceProjectDir);
        var originalBookId = metadata.BookId;
        var settings = VisibleDramaSource.SettingsFor(globalSettings, account?.TiktokDramaMarket);
        XingeAutoPublishMarkerStore.AdoptLocalDramaBoxChain(context.SourceProjectDir);
        var clientAssignment = XingeAutoPublishMarkerStore.TryRead(context.SourceProjectDir);
        var hasClientChain = !string.IsNullOrWhiteSpace(clientAssignment?.ClientDownloadChain);
        var strictDownload = clientAssignment is { ExpectedEpisodeTotal: > 0 } && hasClientChain;
        TikTokUploadTitleRoute? importedRoute = null;
        if (hasClientChain)
        {
            settings = settings.Clone();
            settings.DramaSourceChain = clientAssignment!.ClientDownloadChain;
            if (strictDownload && !clientAssignment.RequiresClientDownloadLookup &&
                !IsBookIdForSource(metadata.BookId, NormalizeDramaSource(settings.DramaSourceChain)))
                throw new InvalidOperationException("项目剧集标识与派单时配置的下载链路不一致");
        }
        else
        {
            importedRoute = TikTokUploadTitleRoute.Read(context.SourceProjectDir);
            if (importedRoute is not null)
            {
                var activeRoute = ResolveImportedAccountRoute(globalSettings, account, importedRoute);
                settings = activeRoute.ApplyTo(globalSettings);
                log($"上传短剧下载链路：{activeRoute.Describe()}。");
            }
            else if (string.IsNullOrWhiteSpace(metadata.BookId))
            {
                throw new InvalidOperationException("项目缺少 bookId，无法执行下载步骤。");
            }
        }
        using var chainHold = ShortDramaDramaServices.HoldSettings(settings);

        var concurrent = Math.Clamp(settings.DramaDownloadConcurrent, 1, 10);
        var maxParallelProjects = Math.Clamp(
            settings.DramaDownloadMaxParallelProjects <= 0 ? 1 : settings.DramaDownloadMaxParallelProjects,
            1,
            4);
        var timeoutSeconds = Math.Clamp(settings.HongguoDownloadTimeoutSeconds, 10, 600);
        var attempts = Math.Clamp(settings.HongguoEpisodeDownloadAttempts, 1, 20);
        var fileSegments = Math.Clamp(settings.DownloadFileSegments <= 0 ? 4 : settings.DownloadFileSegments, 1, 8);
        var displayName = FirstNonEmpty(item.Title, item.OriginalTitle, metadata.Title, Path.GetFileName(context.SourceProjectDir));
        var replacingOtherSource = false;
        if (strictDownload && clientAssignment!.RequiresClientDownloadLookup)
        {
            var matchedBookId = await ResolveMigratedAssignmentBookIdAsync(clientAssignment, settings, log, ct).ConfigureAwait(false);
            replacingOtherSource = !string.Equals(matchedBookId, metadata.BookId, StringComparison.OrdinalIgnoreCase);
            metadata = metadata with { BookId = matchedBookId };
        }
        var effectiveBookId = strictDownload ? metadata.BookId : await ResolveDownloadBookIdAsync(
                context.SourceProjectDir,
                item,
                metadata,
                settings,
                importedRoute,
                log,
                ct)
            .ConfigureAwait(false);
        metadata = metadata with { BookId = effectiveBookId };

        var episodes = strictDownload ? "all" : LimitEpisodesToKeptPrefix(metadata.Episodes, item, settings, context.SourceProjectDir);
        if (!strictDownload && OverLimitEpisodePolicy.TryReadOriginalEpisodeCount(context.SourceProjectDir, out var originalEpisodeCount))
        {
            var keep = OverLimitEpisodePolicy.ResolveKeepCount(settings);
            var hadOldKeep = OverLimitEpisodePolicy.TryReadKeepCount(context.SourceProjectDir, out var storedKeep);
            OverLimitEpisodePolicy.MarkTruncated(
                context.SourceProjectDir,
                originalEpisodeCount,
                settings,
                log);
            if (hadOldKeep && storedKeep != keep)
            {
                var shown = originalEpisodeCount <= keep ? originalEpisodeCount : keep;
                item.EpisodeCount = shown;
                if (item.SourceEpisodeCount < originalEpisodeCount)
                    item.SourceEpisodeCount = originalEpisodeCount;
                if (originalEpisodeCount <= keep)
                    log($"当前最大集数是 {keep}，不再沿用之前的前 {storedKeep} 集，改为下载全部 {originalEpisodeCount} 集。");
            }
        }

        if (!forceRerun && !replacingOtherSource &&
            string.Equals(originalBookId, effectiveBookId, StringComparison.OrdinalIgnoreCase))
        {
            var expected = ResolveRequiredDownloadEpisodeNumbers(context, item, episodes);
            var local = InspectDownloadedEpisodes(context.SourceProjectDir, item, expected, allowStagedFallback: true);
            if (local.IsComplete)
            {
                log($"本地分集已齐全（{local.Expected}/{local.Expected} 集），只补缺少的原简介或原海报，跳过分集下载。");
                await EnsureRequiredDownloadMetadataAsync(context, item, metadata, displayName, log, ct, downloader)
                    .ConfigureAwait(false);
                if (item.StepStates.GetValueOrDefault(QueueStepRegistry.VideoDedup) != QueueStepStatus.Completed &&
                    !ProjectWorkspaceService.TryResolveDownloadedEpisodeCount(context.SourceProjectDir, out _))
                {
                    await EnsureDownloadedVideosPlayableAsync(context.SourceProjectDir, log, ct, item).ConfigureAwait(false);
                }
                FinishDownload(context, item, log, ct);
                return;
            }
        }

        log($"分集下载并发: {concurrent}，单集分块连接数: {fileSegments}，同时下载剧数: {maxParallelProjects}，单集超时: {timeoutSeconds}s，重试次数: {attempts}");
        using var downloadSlot = await QueueDownloadSlotCoordinator.WaitAsync(
            maxParallelProjects,
            displayName,
            log,
            ct).ConfigureAwait(false);

        var request = BuildDownloadRequest(
            context,
            metadata,
            settings,
            displayName,
            episodes,
            concurrent,
            forceRerun || replacingOtherSource ? ExistingVideoPolicy.ReplaceAll : ExistingVideoPolicy.ReuseValid);

        var progress = CreateDownloadProgress(log);
        var result = await downloader.DownloadAsync(request, progress, ct).ConfigureAwait(false);
        var expectedEpisodeNumbers = ResolveRequiredDownloadEpisodeNumbers(context, item, episodes);
        if (!result.Ok)
        {
            ct.ThrowIfCancellationRequested();

            var recoveredVideos = !strictDownload && ShouldUseExistingVideosToRepairFailedDownload(forceRerun) &&
                await TryRepairMissingEpisodesAsync(
                    context,
                    item,
                    metadata,
                    settings,
                    displayName,
                    expectedEpisodeNumbers,
                    log,
                    ct,
                    downloader).ConfigureAwait(false);
            if (!recoveredVideos)
            {
                if (forceRerun)
                    log("强制重跑下载未成功；已保留原视频作为安全备份，但不会把旧文件计为本轮下载成功。");
                throw new InvalidOperationException(result.Message ?? "下载失败");
            }
        }
        else
        {
            log("分集传输结束，正在核验应下载的分集和原始资料。");
            await TryRepairMissingEpisodesAsync(context, item, metadata, settings, displayName,
                expectedEpisodeNumbers, log, ct, downloader).ConfigureAwait(false);
        }

        EnsureDownloadedEpisodesComplete(context.SourceProjectDir, item, expectedEpisodeNumbers, log);
        await EnsureRequiredDownloadMetadataAsync(context, item, metadata, displayName, log, ct, downloader).ConfigureAwait(false);
        await EnsureDownloadedVideosPlayableAsync(context.SourceProjectDir, log, ct, item).ConfigureAwait(false);
        FinishDownload(context, item, log, ct);
    }

    private static void FinishDownload(ProjectWorkspaceContext context, QueueProjectItem item, Action<string> log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        ProjectWorkspaceService.RefreshQueueItemMetadata(item);
        log("下载验收通过，原简介、原海报和分集均已齐全。");
    }

    internal const string EmptyDownloadVideoMessage = "没有视频文件，下载未完成。";

    /// <summary>
    /// 源视频已删、上传副本仍齐全时，下载失败只是复查误判，可以跳过重下。
    /// </summary>
    internal static bool CanSkipFailedDownloadUsingUploadCopies(QueueProjectItem item)
    {
        if (!HasEmptyVideoDownloadFailure(item))
            return false;
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var selection = ReadDownloadMetadata(context.SourceProjectDir).Episodes;
        var expected = ResolveRequiredDownloadEpisodeNumbers(context, item, selection);
        return expected.Count > 0 &&
               UploadCopiesCoverEpisodeNumbers(ProjectVideoResolver.ResolveStagedUploadVideos(context.SourceProjectDir), expected) &&
               HasRequiredDownloadMetadata(context, item);
    }

    internal static void ClearEmptyVideoDownloadFailure(string sourceProjectDir)
    {
        var path = Path.Combine(sourceProjectDir, ".weixin-channel-download-state.json");
        if (!File.Exists(path))
            return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null)
                return;
            var message = root["message"]?.GetValue<string>() ?? "";
            if (!message.Contains(EmptyDownloadVideoMessage, StringComparison.Ordinal))
                return;
            root["ok"] = true;
            root["stopped"] = false;
            root["message"] = "上传副本齐全，下载已完成。";
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    internal static async Task EnsureDownloadedVideosPlayableAsync(
        string sourceProjectDir,
        Action<string> log,
        CancellationToken ct,
        QueueProjectItem? item = null)
    {
        if (ShortDramaDramaServices.Downloader is not DramaSourceRouter router)
            throw new InvalidOperationException("当前下载器不能检查视频能否播放。");
        var videos = ProjectVideoResolver.ResolveSourceVideos(sourceProjectDir);
        if (videos.Count == 0)
        {
            videos = ProjectVideoResolver.ResolveStagedUploadVideos(sourceProjectDir);
            if (videos.Count > 0 &&
                item is not null &&
                TryReadExpectedEpisodeNumbers(sourceProjectDir, item) is { Count: > 1 } expected &&
                !UploadCopiesCoverEpisodeNumbers(videos, expected))
            {
                throw new InvalidOperationException(
                    $"上传副本不完整：应 {expected.Count} 集，实际 {CountCoveredEpisodes(videos, expected)} 集。");
            }
        }

        await router.EnsureVideosPlayableAsync(
            sourceProjectDir,
            videos,
            new Progress<string>(log),
            ct).ConfigureAwait(false);
        if (videos.Count > 0)
            ClearEmptyVideoDownloadFailure(sourceProjectDir);
    }

    private static bool HasEmptyVideoDownloadFailure(QueueProjectItem item)
    {
        if ((item.LastError ?? "").Contains(EmptyDownloadVideoMessage, StringComparison.Ordinal))
            return true;
        var path = Path.Combine(item.ProjectDir, ".weixin-channel-download-state.json");
        if (!File.Exists(path))
            return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("message", out var message) &&
                   message.ValueKind == JsonValueKind.String &&
                   (message.GetString() ?? "").Contains(EmptyDownloadVideoMessage, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool UploadCopiesCoverEpisodeNumbers(
        IReadOnlyList<string> videos,
        IReadOnlyList<int> expected) =>
        CountCoveredEpisodes(videos, expected) == expected.Count;

    private static int CountCoveredEpisodes(IReadOnlyList<string> videos, IReadOnlyList<int> expected)
    {
        var found = new HashSet<int>();
        foreach (var video in videos)
        {
            var file = new FileInfo(video);
            if (file.Exists && file.Length > 0 && TryReadEpisodeNumberFromFileName(video, out var episode))
                found.Add(episode);
        }

        return expected.Count(found.Contains);
    }

    private static IReadOnlyList<int>? TryReadExpectedEpisodeNumbers(string sourceProjectDir, QueueProjectItem item)
    {
        var numbers = ResolveExpectedDownloadedEpisodeNumbers(sourceProjectDir, item);
        return numbers.Count > 1 ? numbers : null;
    }

    internal static bool ShouldUseExistingVideosToRepairFailedDownload(bool forceRerun) => !forceRerun;

    internal static async Task<ProofMaterialVideoHydrationResult> EnsureProofMaterialVideosAsync(
        QueueProjectItem item,
        ClientSettings settings,
        int requiredEpisodeCount,
        Action<string> log,
        CancellationToken ct,
        RoleReferenceEpisodeFallback? episodeFallback = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        var required = Math.Clamp(requiredEpisodeCount, 1, 200);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var existingCandidates = ProjectVideoResolver.ResolveMaterialVideos(
            context.SourceProjectDir,
            allowStagedFallback: true);
        var existingVideos = await FilterUsableProofMaterialVideosAsync(
                existingCandidates,
                log,
                ct)
            .ConfigureAwait(false);
        var existingEpisodeNumbers = existingVideos
            .Select(path => EpisodeNumberInFileName.Match(Path.GetFileName(path)))
            .Where(match => match.Success)
            .Select(match => int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : 0)
            .Where(number => number > 0)
            .Distinct()
            .ToArray();
        var missingEpisodes = ResolveMissingProofMaterialEpisodes(
            existingEpisodeNumbers,
            existingVideos.Count,
            required);
        if (missingEpisodes.Count == 0)
        {
            log($"证明材料补源：已有可用视频 {existingVideos.Count} 个，已满足 {required} 集最低需求，无需下载。");
            return ProofMaterialVideoHydrationResult.Empty;
        }

        var metadata = ReadDownloadMetadata(context.SourceProjectDir);
        var isPublishedRecovery =
            PublishedRecoveryOriginalTitleCompletionService.CanComplete(item);
        if (isPublishedRecovery && episodeFallback is not null)
        {
            log(
                "检测到 TikTok 已发布视频恢复项目，优先从原平台剧集补充证明材料视频，" +
                "避免使用跨电脑迁移后可能失效的红果 bookId。");
            var platformCreated = await TryHydrateMaterialVideosFromEpisodeFallbackAsync(
                    item,
                    missingEpisodes,
                    beforePaths: existingVideos,
                    episodeFallback,
                    log,
                    ct)
                .ConfigureAwait(false);
            if (platformCreated.Count > 0)
                return new ProofMaterialVideoHydrationResult(platformCreated);

            log(
                string.IsNullOrWhiteSpace(metadata.BookId)
                    ? "WARN TikTok 已发布视频补源未取得文件，且项目没有可用 bookId。"
                    : "WARN TikTok 已发布视频补源未取得文件，尝试使用已确认原剧名对应的红果源补下载。");
        }

        if (string.IsNullOrWhiteSpace(metadata.BookId))
        {
            if (!isPublishedRecovery)
            {
                var platformCreated = await TryHydrateMaterialVideosFromEpisodeFallbackAsync(
                        item,
                        missingEpisodes,
                        beforePaths: existingVideos,
                        episodeFallback,
                        log,
                        ct)
                    .ConfigureAwait(false);
                if (platformCreated.Count > 0)
                    return new ProofMaterialVideoHydrationResult(platformCreated);
            }

            throw new InvalidOperationException(
                isPublishedRecovery
                    ? $"TikTok 已发布视频恢复项目未能恢复前 {required} 集，且尚未补全真实原剧名/bookId。" +
                      "请确认该电脑已登录对应 TikTok 账号，或先在项目菜单中补全原剧名。"
                    : $"证明材料缺少视频，且项目缺少 bookId，无法补下载前 {required} 集。");
        }

        ShortDramaDramaServices.RefreshSettings(settings);
        var before = ProjectVideoResolver.ResolveMaterialVideos(context.SourceProjectDir)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selection = FormatEpisodeSelection(missingEpisodes);
        var displayName = FirstNonEmpty(
            item.Title,
            item.OriginalTitle,
            metadata.Title,
            Path.GetFileName(context.SourceProjectDir));
        var concurrent = Math.Clamp(settings.DramaDownloadConcurrent, 1, 10);
        var maxParallelProjects = Math.Clamp(
            settings.DramaDownloadMaxParallelProjects <= 0 ? 1 : settings.DramaDownloadMaxParallelProjects,
            1,
            4);

        log(
            $"证明材料缺少视频源：仅补下载第 {selection} 集，" +
            $"不执行整剧下载，不改变正式下载步骤状态；下载的视频将保留到项目归档。");
        try
        {
            DramaDownloadResult downloadResult;
            using (await QueueDownloadSlotCoordinator.WaitAsync(
                       maxParallelProjects,
                       $"{displayName}（证明材料补源）",
                       log,
                       ct).ConfigureAwait(false))
            {
                var request = BuildDownloadRequest(
                    context,
                    metadata,
                    settings,
                    displayName,
                    selection,
                    concurrent,
                    ExistingVideoPolicy.ReplaceInvalid);
                downloadResult = await ShortDramaDramaServices.Downloader
                    .DownloadAsync(request, CreateDownloadProgress(log), ct)
                    .ConfigureAwait(false);
            }

            var after = ProjectVideoResolver.ResolveMaterialVideos(context.SourceProjectDir)
                .Select(Path.GetFullPath)
                .ToArray();
            var created = after.Where(path => !before.Contains(path)).ToArray();
            if (!downloadResult.Ok && ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            var afterEpisodeNumbers = after
                .Select(path => EpisodeNumberInFileName.Match(Path.GetFileName(path)))
                .Where(match => match.Success)
                .Select(match => int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : 0)
                .Where(number => number > 0)
                .Distinct()
                .ToArray();
            var remainingEpisodes = ResolveMissingProofMaterialEpisodes(
                afterEpisodeNumbers,
                after.Length,
                required);
            if (remainingEpisodes.Count > 0)
            {
                _ = await TryHydrateMaterialVideosFromEpisodeFallbackAsync(
                        item,
                        remainingEpisodes,
                        after,
                        episodeFallback,
                        log,
                        ct)
                    .ConfigureAwait(false);
                after = ProjectVideoResolver.ResolveMaterialVideos(context.SourceProjectDir)
                    .Select(Path.GetFullPath)
                    .ToArray();
                created = after.Where(path => !before.Contains(path)).ToArray();
            }

            var disposition = ResolveProofMaterialHydrationDisposition(
                downloadResult.Ok,
                after.Length);
            if (disposition == ProofMaterialVideoHydrationDisposition.Failed)
            {
                throw new InvalidOperationException(
                    downloadResult.Ok
                        ? "证明材料补源完成后仍未找到可用视频。"
                        : downloadResult.Message ?? "证明材料补源失败");
            }

            if (disposition == ProofMaterialVideoHydrationDisposition.Partial)
            {
                log(
                    $"证明材料补源存在未完成分集：{downloadResult.Message}；" +
                    $"已获得 {after.Length} 个有效视频，将继续生成证明材料。" +
                    "后续再次执行时会直接复用这些视频。");
            }

            log(
                $"证明材料补源完成：新增 {created.Length} 个视频，可用 {after.Length} 个；" +
                "下载的视频已保留，将在项目归档时按账号配置清理。");
            return new ProofMaterialVideoHydrationResult(created);
        }
        catch
        {
            if (ct.IsCancellationRequested)
                throw;

            var currentlyAvailable = ProjectVideoResolver.ResolveMaterialVideos(context.SourceProjectDir)
                .Select(Path.GetFullPath)
                .ToArray();
            var currentEpisodeNumbers = currentlyAvailable
                .Select(path => EpisodeNumberInFileName.Match(Path.GetFileName(path)))
                .Where(match => match.Success)
                .Select(match => int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : 0)
                .Where(number => number > 0)
                .Distinct()
                .ToArray();
            var stillMissing = ResolveMissingProofMaterialEpisodes(
                currentEpisodeNumbers,
                currentlyAvailable.Length,
                required);
            var platformCreated = await TryHydrateMaterialVideosFromEpisodeFallbackAsync(
                    item,
                    stillMissing,
                    currentlyAvailable,
                    episodeFallback,
                    log,
                    ct)
                .ConfigureAwait(false);
            if (platformCreated.Count > 0)
                return new ProofMaterialVideoHydrationResult(platformCreated);

            var partial = ProjectVideoResolver.ResolveMaterialVideos(context.SourceProjectDir)
                .Select(Path.GetFullPath)
                .Where(path => !before.Contains(path))
                .ToArray();
            if (partial.Length > 0)
            {
                log(
                    $"证明材料补源中断：已保留下载完成的 {partial.Length} 个视频，" +
                    "将直接使用这些视频继续生成证明材料。");
                return new ProofMaterialVideoHydrationResult(partial);
            }

            var fallbackVideo = await TryCreateProofMaterialFrameFallbackVideoAsync(
                    context,
                    log,
                    ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fallbackVideo))
            {
                return new ProofMaterialVideoHydrationResult([fallbackVideo]);
            }
            throw;
        }
    }

    private static async Task<IReadOnlyList<string>> FilterUsableProofMaterialVideosAsync(
        IReadOnlyList<string> candidates,
        Action<string> log,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return candidates;

        var ffprobe = MediaBinaryResolver.ResolveFfprobe();
        var usable = new List<string>(candidates.Count);
        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var probe = await MediaProbe.ProbeAsync(ffprobe, path, ct).ConfigureAwait(false);
                if (IsUsableProofMaterialVideo(probe))
                {
                    usable.Add(path);
                    continue;
                }

                log(
                    $"WARN 证明材料视频不可解码，将按缺失分集重新补源：{Path.GetFileName(path)}；" +
                    $"codec={DisplayProbeValue(probe.VideoCodec)}，tag={DisplayProbeValue(probe.VideoCodecTag)}。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log(
                    $"WARN 证明材料视频体检失败，将按缺失分集重新补源：{Path.GetFileName(path)}；{ex.Message}");
            }
        }

        return usable;
    }

    internal static bool IsUsableProofMaterialVideo(MediaProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var codec = probe.VideoCodec.Trim();
        var tag = probe.VideoCodecTag.Trim();
        return codec.Length > 0 &&
               !string.Equals(codec, "none", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(codec, "unknown", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(tag, "encv", StringComparison.OrdinalIgnoreCase) &&
               probe.Width > 0 &&
               probe.Height > 0 &&
               probe.DurationSeconds > 0;
    }

    private static string DisplayProbeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();

    private static async Task<IReadOnlyList<string>> TryHydrateMaterialVideosFromEpisodeFallbackAsync(
        QueueProjectItem item,
        IReadOnlyList<int> missingEpisodes,
        IEnumerable<string> beforePaths,
        RoleReferenceEpisodeFallback? episodeFallback,
        Action<string> log,
        CancellationToken ct)
    {
        if (episodeFallback is null || missingEpisodes.Count == 0)
            return [];

        var before = beforePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            log(
                $"证明材料原片补源不可用，正在从 TikTok 已上传项目恢复第 " +
                $"{FormatEpisodeSelection(missingEpisodes)} 集素材视频。");
            var fallbackVideos = await episodeFallback(item, missingEpisodes, log, ct)
                .ConfigureAwait(false);
            var created = fallbackVideos
                .Where(pair => missingEpisodes.Contains(pair.Key))
                .Select(pair => pair.Value)
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(Path.GetFullPath)
                .Where(path => !before.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (created.Length > 0)
            {
                log(
                    $"TikTok 已上传视频补源完成：获得 {created.Length} 集；" +
                    "仅供 AI 截图、工程图和原始文件信息使用，不会加入上传视频列表。");
            }
            return created;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"WARN TikTok 已上传视频补源失败：{ex.Message}");
            return [];
        }
    }

    internal static IReadOnlyList<int> ResolveMissingProofMaterialEpisodes(
        IReadOnlyCollection<int> existingEpisodeNumbers,
        int existingVideoCount,
        int requiredEpisodeCount)
    {
        ArgumentNullException.ThrowIfNull(existingEpisodeNumbers);
        var required = Math.Clamp(requiredEpisodeCount, 1, 200);
        var existingCount = Math.Max(0, existingVideoCount);
        if (existingCount >= required)
            return [];

        var existing = existingEpisodeNumbers
            .Where(number => number > 0)
            .ToHashSet();
        return Enumerable.Range(1, required)
            .Where(number => !existing.Contains(number))
            .Take(required - existingCount)
            .ToArray();
    }

    private static async Task<string?> TryCreateProofMaterialFrameFallbackVideoAsync(
        ProjectWorkspaceContext context,
        Action<string> log,
        CancellationToken ct)
    {
        var framePath = FindProofMaterialFallbackFrame(context.WorkflowProjectDir);
        if (string.IsNullOrWhiteSpace(framePath))
            return null;

        var outputPath = Path.Combine(
            context.SourceProjectDir,
            ProofMaterialFrameFallbackVideoFileName);
        try
        {
            log(
                $"WARN 证明材料视频下载失败，检测到已保留的抽帧图片，" +
                $"正在生成本地兜底视频：{Path.GetFileName(framePath)}。");
            await CreateProofMaterialFrameFallbackVideoAsync(framePath, outputPath, ct)
                .ConfigureAwait(false);
            log(
                $"WARN 已使用抽帧图片生成证明材料兜底视频，将继续生成工程图和 AI 过程截图：" +
                $"{outputPath}");
            return outputPath;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log($"WARN 抽帧图片兜底视频生成失败，将保留原下载错误：{ex.Message}");
            return null;
        }
    }

    internal static async Task CreateProofMaterialFrameFallbackVideoAsync(
        string framePath,
        string outputPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(framePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!File.Exists(framePath) || new FileInfo(framePath).Length <= 0)
            throw new FileNotFoundException("证明材料抽帧兜底图片不存在或为空。", framePath);

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var temporaryPath = fullOutputPath + $".{Guid.NewGuid():N}.tmp.mp4";
        try
        {
            var ffmpeg = FfmpegLocator.ResolveFfmpeg();
            await FfmpegRunner.RunAsync(
                    ffmpeg,
                    [
                        "-hide_banner", "-loglevel", "error", "-y",
                        "-loop", "1", "-i", Path.GetFullPath(framePath),
                        "-t", "4", "-r", "25",
                        "-vf", "scale=1080:1920:force_original_aspect_ratio=decrease," +
                               "pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black,format=yuv420p",
                        "-an", "-c:v", "libx264", "-preset", "veryfast",
                        "-movflags", "+faststart", temporaryPath,
                    ],
                    ct)
                .ConfigureAwait(false);
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length <= 0)
                throw new InvalidOperationException("FFmpeg 未生成有效的证明材料抽帧兜底视频。");

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    internal static string? FindProofMaterialFallbackFrame(string workflowProjectDirectory)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var retained = TikTokAiGenerationScreenshotService.ListRetainedFrameImages(workflow)
            .Where(IsUsableProofMaterialFallbackImage)
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(retained))
            return retained;

        if (!Directory.Exists(workflow))
            return null;

        return Directory.EnumerateDirectories(workflow, "抽帧原图", SearchOption.AllDirectories)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly))
            .Where(IsUsableProofMaterialFallbackImage)
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
    }

    private static bool IsUsableProofMaterialFallbackImage(string path) =>
        File.Exists(path) &&
        new FileInfo(path).Length > 0 &&
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp";

    internal sealed record ProofMaterialVideoHydrationResult(
        IReadOnlyList<string> CreatedVideoPaths)
    {
        internal static ProofMaterialVideoHydrationResult Empty { get; } = new([]);
    }

    internal static ProofMaterialVideoHydrationDisposition ResolveProofMaterialHydrationDisposition(
        bool downloadOk,
        int availableVideoCount)
    {
        if (availableVideoCount <= 0)
            return ProofMaterialVideoHydrationDisposition.Failed;

        return downloadOk
            ? ProofMaterialVideoHydrationDisposition.Completed
            : ProofMaterialVideoHydrationDisposition.Partial;
    }

    internal enum ProofMaterialVideoHydrationDisposition
    {
        Failed,
        Partial,
        Completed,
    }

    internal static async Task<string> EnsureRoleReferenceEpisodeVideoAsync(
        QueueProjectItem item,
        ClientSettings settings,
        int episodeNumber,
        Action<string> log,
        CancellationToken ct)
    {
        if (episodeNumber <= 0) throw new ArgumentOutOfRangeException(nameof(episodeNumber));

        var videos = await EnsureRoleReferenceEpisodeVideosAsync(
            item,
            settings,
            [episodeNumber],
            log,
            ct).ConfigureAwait(false);
        if (videos.TryGetValue(episodeNumber, out var video)) return video;
        throw new InvalidOperationException($"第 {episodeNumber} 集补下载完成后未找到视频文件。");
    }

    internal static async Task<IReadOnlyDictionary<int, string>> EnsureRoleReferenceEpisodeVideosAsync(
        QueueProjectItem item,
        ClientSettings settings,
        IReadOnlyList<int> episodeNumbers,
        Action<string> log,
        CancellationToken ct,
        RoleReferenceEpisodeFallback? episodeFallback = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(episodeNumbers);
        ArgumentNullException.ThrowIfNull(log);
        var requested = episodeNumbers
            .Where(episode => episode > 0)
            .Distinct()
            .OrderBy(episode => episode)
            .ToArray();
        if (requested.Length == 0) return new Dictionary<int, string>();

        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var resolved = ResolveExistingRoleReferenceEpisodeVideos(
                context.SourceProjectDir,
                requested,
                item.EpisodeCount)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var missing = requested.Where(episode => !resolved.ContainsKey(episode)).ToArray();
        if (missing.Length == 0)
        {
            log($"角色补源：复用已下载的第 {FormatEpisodeSelection(requested)} 集视频。");
            return resolved;
        }

        var metadata = ReadDownloadMetadata(context.SourceProjectDir);
        if (string.IsNullOrWhiteSpace(metadata.BookId) && episodeFallback is null)
            throw new InvalidOperationException(
                $"角色素材缺少第 {FormatEpisodeSelection(missing)} 集视频，且项目缺少 bookId，无法自动补下载。");

        var displayName = FirstNonEmpty(
            item.Title,
            item.OriginalTitle,
            metadata.Title,
            Path.GetFileName(context.SourceProjectDir));
        var selection = FormatEpisodeSelection(missing);
        DramaDownloadResult result;
        if (string.IsNullOrWhiteSpace(metadata.BookId))
        {
            result = new DramaDownloadResult(
                false,
                context.SourceProjectDir,
                resolved.Count,
                "项目缺少 bookId，已跳过原下载器。");
            log($"角色补源：项目缺少 bookId，无法使用原下载器补第 {selection} 集。");
        }
        else
        {
            ShortDramaDramaServices.RefreshSettings(settings);
            var maxParallelProjects = Math.Clamp(
                settings.DramaDownloadMaxParallelProjects <= 0 ? 1 : settings.DramaDownloadMaxParallelProjects,
                1,
                4);
            var concurrent = Math.Clamp(
                Math.Min(missing.Length, settings.DramaDownloadConcurrent <= 0 ? 1 : settings.DramaDownloadConcurrent),
                1,
                10);
            log($"角色素材人物不足：批量补下载第 {selection} 集，并发 {concurrent}，不执行整剧下载。");
            try
            {
                using (await QueueDownloadSlotCoordinator.WaitAsync(
                           maxParallelProjects,
                           $"{displayName}（角色素材补源）",
                           log,
                           ct).ConfigureAwait(false))
                {
                    var request = BuildDownloadRequest(
                        context,
                        metadata,
                        settings,
                        displayName,
                        selection,
                        concurrent);
                    result = await ShortDramaDramaServices.Downloader
                        .DownloadAsync(request, CreateDownloadProgress(log), ct)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new DramaDownloadResult(
                    false,
                    context.SourceProjectDir,
                    resolved.Count,
                    $"原下载器异常：{ex.Message}");
                log($"角色补源：原下载器异常，将尝试 TikTok 已上传视频兜底：{ex.Message}");
            }
        }

        var refreshed = ResolveExistingRoleReferenceEpisodeVideos(
            context.SourceProjectDir,
            requested,
            item.EpisodeCount);
        foreach (var episode in requested)
        {
            if (refreshed.TryGetValue(episode, out var downloaded))
                resolved[episode] = downloaded;
        }
        var unresolved = requested.Where(episode => !resolved.ContainsKey(episode)).ToArray();
        if (unresolved.Length > 0 && episodeFallback is not null)
        {
            var fallbackLock = PublishedFallbackLocks.GetOrAdd(
                Path.GetFullPath(context.SourceProjectDir),
                static _ => new SemaphoreSlim(1, 1));
            await fallbackLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Other parallel steps may have downloaded the same episodes while this
                // request waited. Re-scan before opening another browser/download batch.
                var concurrentlyDownloaded = ResolveExistingRoleReferenceEpisodeVideos(
                    context.SourceProjectDir,
                    requested,
                    item.EpisodeCount);
                foreach (var episode in requested)
                {
                    if (concurrentlyDownloaded.TryGetValue(episode, out var path))
                        resolved[episode] = path;
                }
                unresolved = requested.Where(episode => !resolved.ContainsKey(episode)).ToArray();
                if (unresolved.Length > 0)
                {
                    log(
                        $"角色补源：原下载器未取得第 {FormatEpisodeSelection(unresolved)} 集，" +
                        "切换 TikTok 已上传视频兜底。");
                    var fallbackVideos = await episodeFallback(item, unresolved, log, ct).ConfigureAwait(false);
                    MergeRoleReferenceFallbackVideos(resolved, unresolved, fallbackVideos);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"角色补源：TikTok 已上传视频兜底失败，将继续后续批次：{ex.Message}");
            }
            finally
            {
                fallbackLock.Release();
            }
            unresolved = requested.Where(episode => !resolved.ContainsKey(episode)).ToArray();
        }
        if (unresolved.Length > 0)
            log($"角色补源：第 {FormatEpisodeSelection(unresolved)} 集未取得视频，将跳过并继续后续批次。" +
                (result.Ok ? string.Empty : $" 下载结果：{result.Message}"));
        if (!result.Ok && resolved.Count > 0)
            log($"角色补源批量下载未完整结束，但已有 {resolved.Count}/{requested.Length} 集视频可用于抽帧：{result.Message}");
        else if (resolved.Count > 0)
            log($"角色补源：批次视频已准备 {resolved.Count}/{requested.Length} 集，将并行抽取人物候选帧。");
        return resolved;
    }

    internal static int MergeRoleReferenceFallbackVideos(
        IDictionary<int, string> resolved,
        IEnumerable<int> requestedEpisodes,
        IReadOnlyDictionary<int, string> fallbackVideos)
    {
        var added = 0;
        foreach (var episode in requestedEpisodes.Distinct())
        {
            if (resolved.ContainsKey(episode) ||
                !fallbackVideos.TryGetValue(episode, out var path) ||
                !File.Exists(path))
            {
                continue;
            }

            resolved[episode] = path;
            added++;
        }
        return added;
    }

    internal static IReadOnlyDictionary<int, string> ResolveExistingRoleReferenceEpisodeVideos(
        string sourceProjectDirectory,
        IReadOnlyList<int> requestedEpisodes,
        int expectedEpisodeCount)
    {
        var requested = requestedEpisodes
            .Where(episode => episode > 0)
            .Distinct()
            .ToHashSet();
        var resolved = new Dictionary<int, string>();
        if (requested.Count == 0)
            return resolved;

        MapExistingEpisodeVideos(
            ProjectVideoResolver.ResolveNarrativeVideos(sourceProjectDirectory),
            requested,
            expectedEpisodeCount,
            resolved);
        if (resolved.Count < requested.Count)
        {
            MapExistingEpisodeVideos(
                ProjectVideoResolver.ResolveStagedUploadVideos(sourceProjectDirectory),
                requested,
                expectedEpisodeCount,
                resolved);
        }

        return resolved;
    }

    private static void MapExistingEpisodeVideos(
        IReadOnlyList<string> videos,
        IReadOnlySet<int> requested,
        int expectedEpisodeCount,
        IDictionary<int, string> resolved)
    {
        foreach (var path in videos)
        {
            if (!TryReadEpisodeNumberFromFileName(path, out var episodeNumber) ||
                !requested.Contains(episodeNumber) ||
                resolved.ContainsKey(episodeNumber))
            {
                continue;
            }

            resolved[episodeNumber] = path;
        }

        // Some imported complete series use names such as 001.mp4 or arbitrary clip names.
        // Natural-order fallback is safe only when the directory contains the declared full set;
        // never guess episode numbers from a partial folder.
        if (expectedEpisodeCount <= 0 || videos.Count != expectedEpisodeCount)
            return;

        foreach (var episodeNumber in requested)
        {
            if (resolved.ContainsKey(episodeNumber) || episodeNumber > videos.Count)
                continue;
            resolved[episodeNumber] = videos[episodeNumber - 1];
        }
    }

    internal static bool TryReadEpisodeNumberFromFileName(string path, out int episodeNumber)
    {
        var stem = Path.GetFileNameWithoutExtension(path).Trim();
        foreach (var pattern in EpisodeNumberFileNamePatterns)
        {
            var match = pattern.Match(stem);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out episodeNumber) &&
                episodeNumber > 0)
            {
                return true;
            }
        }

        episodeNumber = 0;
        return false;
    }

    private static DramaDownloadRequest BuildDownloadRequest(
        ProjectWorkspaceContext context,
        DownloadMetadata metadata,
        ClientSettings settings,
        string displayName,
        string episodes,
        int concurrent,
        ExistingVideoPolicy existingVideoPolicy = ExistingVideoPolicy.ReuseValid)
    {
        var assignment = XingeAutoPublishMarkerStore.TryRead(context.SourceProjectDir);
        var expectedEpisodeTotal = !string.IsNullOrWhiteSpace(assignment?.ClientDownloadChain)
            ? assignment.ExpectedEpisodeTotal : 0;
        return new DramaDownloadRequest(
            ProjectDir: context.SourceProjectDir,
            OutputDir: context.SourceProjectDir,
            DisplayName: displayName,
            BookId: FirstNonEmpty(metadata.BookId),
            Episodes: FirstNonEmpty(episodes, "all"),
            Quality: FirstNonEmpty(metadata.Quality, settings.DramaDownloadDefaultQuality, "1080P"),
            Concurrent: Math.Clamp(concurrent, 1, 10),
            EpisodeNumberMode: expectedEpisodeTotal > 0 ? "source" : FirstNonEmpty(metadata.EpisodeNumberMode, "source"),
            ExistingVideoPolicy: existingVideoPolicy,
            ExpectedEpisodeTotal: expectedEpisodeTotal);
    }

    private static async Task<string> ResolveMigratedAssignmentBookIdAsync(
        XingeAutoPublishMarker assignment, ClientSettings settings, Action<string> log, CancellationToken ct)
    {
        var configuration = DramaSourceConfigurationValidator.Check(settings);
        if (!configuration.IsConfigured)
            throw new InvalidOperationException(configuration.Message);
        log($"旧派单改用客户端链路：{VisibleDramaSource.Label(settings.DramaSourceChain)}，校验原剧名与 {assignment.ExpectedEpisodeTotal} 集。");
        var results = await ShortDramaDramaServices.SearchAsync(assignment.Title, 1, ct).ConfigureAwait(false);
        var match = UploadTitleImportService.PickStrictDispatchMatch(assignment.Title, assignment.ExpectedEpisodeTotal, results);
        if (match.Item is null)
            throw new InvalidOperationException(match.Reason);
        if (!IsBookIdForSource(match.Item.BookId, settings.DramaSourceChain))
            throw new InvalidOperationException("搜索返回的剧集标识与客户端配置的链路不一致");
        return match.Item.BookId;
    }

    private static async Task<string> ResolveDownloadBookIdAsync(
        string sourceProjectDir,
        QueueProjectItem item,
        DownloadMetadata metadata,
        ClientSettings settings,
        TikTokUploadTitleRoute? importedRoute,
        Action<string> log,
        CancellationToken ct)
    {
        var selectedSource = NormalizeDramaSource(settings.DramaSourceChain);
        if (importedRoute is not null)
        {
            return await ResolveImportedDownloadBookIdAsync(
                sourceProjectDir, item, metadata, importedRoute, selectedSource, log, ct).ConfigureAwait(false);
        }

        var selectedLabel = DramaSourceLabel(selectedSource);
        var metadataBookId = FirstNonEmpty(metadata.BookId);
        var metadataSource = InferBookIdSource(metadataBookId);
        log($"当前下载数据链路：{selectedLabel}。");

        if (IsBookIdForSource(metadataBookId, selectedSource))
            return metadataBookId;

        var retargetedBookId = DramaDownloadSourceSelector.TryRetarget(metadataBookId, selectedSource);
        if (!string.IsNullOrWhiteSpace(retargetedBookId) &&
            !string.Equals(retargetedBookId, metadataBookId, StringComparison.OrdinalIgnoreCase))
        {
            log($"当前链路是{selectedLabel}，沿用同一书号：{retargetedBookId}");
            PersistBookReference(sourceProjectDir, metadataSource, metadataBookId, metadata.Title);
            PersistBookReference(sourceProjectDir, selectedSource, retargetedBookId, metadata.Title);
            return retargetedBookId;
        }

        if (ShouldKeepRetiredBookId(selectedSource, metadataBookId))
        {
            log($"该剧书号属于{DramaSourceLabel(metadataSource)}，按原书号继续下载。");
            return metadataBookId;
        }

        var cachedBookId = ReadCachedBookId(sourceProjectDir, selectedSource);
        if (IsBookIdForSource(cachedBookId, selectedSource))
        {
            log($"复用已缓存的 {selectedLabel} 剧集标识：{cachedBookId}");
            return cachedBookId;
        }

        var lookupTitle = FirstNonEmpty(
            metadata.Title,
            item.OriginalTitle,
            item.DisplayName,
            item.Title);
        if (string.IsNullOrWhiteSpace(lookupTitle))
        {
            throw new InvalidOperationException(
                $"项目原数据链路为{DramaSourceLabel(metadataSource)}，已切换到{selectedLabel}，" +
                "但项目缺少可用于重新匹配的原剧名。");
        }

        log(
            $"检测到数据链路切换：{DramaSourceLabel(metadataSource)} → {selectedLabel}；" +
            $"正在按原剧名《{lookupTitle}》重新匹配 book_id。");
        var expectedEpisodes = ResolveRematchEpisodeTotal(sourceProjectDir, item);
        var keep = settings.TiktokAllowOverLimitUploadImport
            ? OverLimitEpisodePolicy.ResolveKeepCount(settings)
            : 0;
        var truncatedKeep = expectedEpisodes == item.EpisodeCount && keep > 0 && item.EpisodeCount == keep
            ? keep
            : 0;
        if (expectedEpisodes > item.EpisodeCount)
            log($"队列显示 {item.EpisodeCount} 集，按真实集数 {expectedEpisodes} 匹配。");
        else if (truncatedKeep > 0)
            log($"队列集数为截取上限 {truncatedKeep}，接受同名且真实集数更多的唯一结果，下载仍只取前 {truncatedKeep} 集。");
        var lookup = await UploadTitleImportService.FindExactDramaAsync(
                lookupTitle,
                expectedEpisodes,
                ct,
                truncatedKeep)
            .ConfigureAwait(false);
        if (lookup.Item is null)
        {
            throw new InvalidOperationException(
                $"切换到{selectedLabel}后，未能按原剧名《{lookupTitle}》找到唯一剧集：{lookup.Reason}。" +
                "请在短剧下载页使用当前链路确认该剧可被搜索到。");
        }

        var resolvedBookId = FirstNonEmpty(lookup.Item.BookId);
        if (!IsBookIdForSource(resolvedBookId, selectedSource))
        {
            throw new InvalidOperationException(
                $"{selectedLabel} 返回了不属于当前链路的 book_id：{resolvedBookId}。" +
                "已停止下载，避免误用其他数据源。");
        }

        PersistBookReference(sourceProjectDir, metadataSource, metadataBookId, lookupTitle);
        PersistBookReference(sourceProjectDir, selectedSource, resolvedBookId, lookup.Item.Title);
        if (lookup.Item.EpisodeTotal > item.EpisodeCount && item.EpisodeCount > 0)
        {
            log($"已匹配原剧 {lookup.Item.EpisodeTotal} 集，下载仍只取前 {item.EpisodeCount} 集。");
            if (item.SourceEpisodeCount < lookup.Item.EpisodeTotal)
                item.SourceEpisodeCount = lookup.Item.EpisodeTotal;
        }

        log(
            $"已匹配并缓存 {selectedLabel} 剧集标识：" +
            $"{metadataBookId} → {resolvedBookId}");
        return resolvedBookId;
    }

    internal static TikTokUploadTitleRoute ResolveImportedAccountRoute(
        ClientSettings settings,
        TikTokAccountProfile? account,
        TikTokUploadTitleRoute importedRoute)
    {
        if (account is null || !string.Equals(account.Id, importedRoute.AccountProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("找不到导入该剧时绑定的 TikTok 账号，请恢复账号或重新导入该剧。");
        if (TikTokAccountProfile.NormalizeDramaMarket(account.TiktokDramaMarket) != importedRoute.DramaMarket)
            throw new InvalidOperationException("绑定账号的剧集类型已改变，请使用当前剧集类型重新导入该剧。");

        return TikTokUploadTitleRoute.Capture(settings, account);
    }

    private static async Task<string> ResolveImportedDownloadBookIdAsync(
        string sourceProjectDir,
        QueueProjectItem item,
        DownloadMetadata metadata,
        TikTokUploadTitleRoute importedRoute,
        string selectedSource,
        Action<string> log,
        CancellationToken ct)
    {
        if (importedRoute.DramaSourceChain == selectedSource)
            return importedRoute.BookId;

        var title = FirstNonEmpty(item.OriginalTitle, metadata.Title);
        var expectedEpisodes = ResolveRematchEpisodeTotal(sourceProjectDir, item);
        if (string.IsNullOrWhiteSpace(title) || expectedEpisodes <= 0)
            throw new InvalidOperationException("数据链路已改变，项目缺少原剧名或真实总集数，无法重新匹配。");

        var selectedLabel = DramaSourceLabel(selectedSource);
        log($"数据链路由 {DramaSourceLabel(importedRoute.DramaSourceChain)} 切换到 {selectedLabel}，按原剧名《{title}》和真实总集数 {expectedEpisodes} 集重新匹配。");
        var results = await ShortDramaDramaServices.SearchAsync(title, 1, ct).ConfigureAwait(false);
        var lookup = UploadTitleImportService.PickStrictDispatchMatch(title, expectedEpisodes, results);
        if (lookup.Item is null)
            throw new InvalidOperationException($"切换到 {selectedLabel} 后无法唯一匹配《{title}》：{lookup.Reason}。");
        if (!IsBookIdForSource(lookup.Item.BookId, selectedSource))
            throw new InvalidOperationException($"{selectedLabel} 返回了不属于当前链路的剧集标识，无法下载。");

        var bookId = lookup.Item.BookId.Trim();
        PersistBookReference(sourceProjectDir, selectedSource, bookId, lookup.Item.Title);
        log($"已在 {selectedLabel} 匹配《{title}》，使用剧集标识 {bookId} 下载。");
        return bookId;
    }

    internal static int ResolveRematchEpisodeTotal(string projectDir, QueueProjectItem item)
    {
        if (item.SourceEpisodeCount > item.EpisodeCount)
            return item.SourceEpisodeCount;
        if (OverLimitEpisodePolicy.TryReadOriginalEpisodeCount(projectDir, out var original) &&
            original > item.EpisodeCount)
            return original;
        return Math.Max(0, item.EpisodeCount);
    }

    internal static string LimitEpisodesToKeptPrefix(
        string? episodes,
        QueueProjectItem item,
        ClientSettings settings,
        string projectDir)
    {
        var selection = string.IsNullOrWhiteSpace(episodes) ? "all" : episodes.Trim();
        if (!settings.TiktokAllowOverLimitUploadImport || item.EpisodeCount <= 0)
            return selection;

        var keep = OverLimitEpisodePolicy.ResolveKeepCount(settings);
        if (OverLimitEpisodePolicy.TryReadOriginalEpisodeCount(projectDir, out var storedOriginal) &&
            OverLimitEpisodePolicy.TryReadKeepCount(projectDir, out var storedKeep) &&
            storedKeep != keep)
            return OverLimitEpisodePolicy.ResolveDownloadSelection("all", storedOriginal, settings);

        var truncated = item.EpisodeCount == keep &&
                        (item.SourceEpisodeCount > item.EpisodeCount ||
                         OverLimitEpisodePolicy.TryReadKeepCount(projectDir, out _) ||
                         OverLimitEpisodePolicy.TryReadOriginalEpisodeCount(projectDir, out var original) &&
                         original > item.EpisodeCount);
        if (!truncated)
            return selection;

        return OverLimitEpisodePolicy.ResolveDownloadSelection(selection, keep + 1, settings);
    }

    internal static string? AdoptHongguoAppBookId(string? selectedSource, string? bookId)
    {
        if (!string.Equals(NormalizeDramaSource(selectedSource), "hongguoapp", StringComparison.Ordinal))
            return null;

        var id = (bookId ?? string.Empty).Trim();
        string raw;
        string prefix;
        if (id.StartsWith("hghigh_ep:", StringComparison.OrdinalIgnoreCase))
        {
            raw = id["hghigh_ep:".Length..];
            prefix = "hongguoapp_ep:";
        }
        else if (id.StartsWith("hghigh:", StringComparison.OrdinalIgnoreCase))
        {
            raw = id["hghigh:".Length..];
            prefix = "hongguoapp:";
        }
        else
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(raw) ? null : prefix + raw;
    }

    internal static bool ShouldKeepRetiredBookId(string? selectedSource, string? bookId)
    {
        var selected = (selectedSource ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.Equals(selected, VisibleDramaSource.DefaultChain, StringComparison.Ordinal))
            return false;
        var id = (bookId ?? string.Empty).Trim();
        return id.Length > 0 && !DramaDownloadSourceSelector.IsRemovedBookId(id) &&
               VisibleDramaSource.IsRetired(InferBookIdSource(id));
    }

    internal static string NormalizeDramaSource(string? source)
    {
        var value = (source ?? string.Empty).Trim().ToLowerInvariant();
        return value is "hgnew" or "pikachu" or "playlet" or "hongguoapp" or "hghigh" or "downloader" or "dramabox" or "foreign"
            ? value
            : "hgnew";
    }

    internal static string InferBookIdSource(string? bookId)
    {
        var id = (bookId ?? string.Empty).Trim();
        if (id.StartsWith("downloader:", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("downloader_ep:", StringComparison.OrdinalIgnoreCase)) return "downloader";
        if (id.StartsWith("hghigh:", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("hghigh_ep:", StringComparison.OrdinalIgnoreCase)) return "hghigh";
        if (id.StartsWith("pikachu:", StringComparison.OrdinalIgnoreCase)) return "pikachu";
        if (id.StartsWith("playlet:", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("playlet_ep:", StringComparison.OrdinalIgnoreCase)) return "playlet";
        if (id.StartsWith("hongguoapp:", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("hongguoapp_ep:", StringComparison.OrdinalIgnoreCase)) return "hongguoapp";
        if (id.StartsWith("dramabox:", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("dramabox_ep:", StringComparison.OrdinalIgnoreCase)) return "dramabox";
        if (id.StartsWith("foreign:", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("foreign_ep:", StringComparison.OrdinalIgnoreCase)) return "foreign";
        return "hgnew";
    }

    internal static bool IsBookIdForSource(string? bookId, string? source)
    {
        var id = (bookId ?? string.Empty).Trim();
        return id.Length > 0 && !DramaDownloadSourceSelector.IsRemovedBookId(id) &&
               string.Equals(
                   InferBookIdSource(id),
                   NormalizeDramaSource(source),
                   StringComparison.Ordinal);
    }

    private static string DramaSourceLabel(string? source) =>
        NormalizeDramaSource(source) switch
        {
            "hghigh" => "红果短剧助手",
            "pikachu" => "皮卡丘",
            "playlet" => "红果短剧下载器",
            "hongguoapp" => "红果APP",
            "dramabox" => "DramaBox",
            "foreign" => "海外短剧下载器",
            _ => "红果新接口",
        };

    internal static string ReadCachedBookId(string sourceProjectDir, string source)
    {
        var path = Path.Combine(sourceProjectDir, "shortdrama-project.json");
        if (!File.Exists(path)) return string.Empty;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var references = root?["sourceReferences"] as JsonObject;
            var reference = references?[NormalizeDramaSource(source)] as JsonObject;
            return reference?["bookId"]?.GetValue<string>()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static void PersistProjectBookId(string sourceProjectDir, string bookId)
    {
        var path = Path.Combine(sourceProjectDir, "shortdrama-project.json");
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(bookId))
            return;

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (root is null)
            return;
        root["bookId"] = bookId.Trim();
        root["book_id"] = bookId.Trim();
        var temporary = path + ".book-id.tmp";
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    internal static void PersistBookReference(
        string sourceProjectDir,
        string source,
        string bookId,
        string title)
    {
        if (DramaDownloadSourceSelector.IsRemovedBookId(bookId)) return;

        var path = Path.Combine(sourceProjectDir, "shortdrama-project.json");
        if (!File.Exists(path)) return;

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException("shortdrama-project.json 不是有效对象。");
        var references = root["sourceReferences"] as JsonObject;
        if (references is null)
        {
            references = new JsonObject();
            root["sourceReferences"] = references;
        }

        if (!string.IsNullOrWhiteSpace(bookId))
        {
            references[NormalizeDramaSource(source)] = new JsonObject
            {
                ["bookId"] = bookId.Trim(),
                ["title"] = (title ?? string.Empty).Trim(),
                ["resolvedAt"] = DateTimeOffset.Now.ToString("o"),
            };
        }

        var temporary = path + ".source-references.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool ShouldLogDownloadProgress(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return !(message.Contains("下载中", StringComparison.Ordinal) && message.Contains('%'));
    }

    private static IProgress<string> CreateDownloadProgress(Action<string> log) =>
        new DownloadProgress(log);

    /// <summary>
    /// <see cref="Progress{T}"/> 在没有同步上下文的队列线程中会异步投递，可能让逐集日志
    /// 延迟到下载步骤结束以后。这里同步、串行转发，确保每条生命周期日志在返回前已交给队列。
    /// </summary>
    private sealed class DownloadProgress(Action<string> log) : IProgress<string>
    {
        private readonly object _lock = new();

        public void Report(string message)
        {
            if (!ShouldLogDownloadProgress(message))
                return;

            lock (_lock)
                log(message);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex EpisodeNumberInFileName =
        new(@"第\s*(\d+)\s*集", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex[] EpisodeNumberFileNamePatterns =
    [
        EpisodeNumberInFileName,
        new(@"(?:^|[\s_.-])(?:episode|ep)[\s_.-]*0*(\d+)(?:$|[\s_.-])",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        new(@"^0*(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled),
        new(@"(?:^|[\s_.-])0*(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled),
    ];

    private static readonly string[] EpisodeVideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".ts"];

    private static async Task<bool> TryRepairMissingEpisodesAsync(
        ProjectWorkspaceContext context,
        QueueProjectItem item,
        DownloadMetadata metadata,
        ClientSettings settings,
        string displayName,
        IReadOnlyList<int> expectedEpisodeNumbers,
        Action<string> log,
        CancellationToken ct,
        IDramaDownloader? downloader = null)
    {
        XingeAutoPublishMarkerStore.AdoptLocalDramaBoxChain(context.SourceProjectDir);
        var clientAssignment = XingeAutoPublishMarkerStore.TryRead(context.SourceProjectDir);
        if (clientAssignment is { ClientDownloadChain: { Length: > 0 } })
        {
            settings = settings.Clone();
            settings.DramaSourceChain = clientAssignment.ClientDownloadChain;
            if (clientAssignment.ExpectedEpisodeTotal > 0)
            {
                if (clientAssignment.RequiresClientDownloadLookup)
                {
                    using var migrationScope = ShortDramaDramaServices.HoldSettings(settings);
                    var matched = await ResolveMigratedAssignmentBookIdAsync(clientAssignment, settings, log, ct).ConfigureAwait(false);
                    if (!string.Equals(metadata.BookId, matched, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                else if (!IsBookIdForSource(metadata.BookId, settings.DramaSourceChain))
                    throw new InvalidOperationException("项目剧集标识与派单时配置的下载链路不一致");
                expectedEpisodeNumbers = Enumerable.Range(1, clientAssignment.ExpectedEpisodeTotal).ToArray();
            }
        }
        using var chainHold = ShortDramaDramaServices.HoldSettings(settings);
        var inspection = InspectDownloadedEpisodes(context.SourceProjectDir, item, expectedEpisodeNumbers);
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
            var repairResult = await (downloader ?? ShortDramaDramaServices.Downloader).DownloadAsync(
                repairRequest,
                CreateDownloadProgress(log),
                ct).ConfigureAwait(false);

            if (!repairResult.Ok)
                log($"缺集补下载未完成：{repairResult.Message ?? "下载失败"}");

            inspection = InspectDownloadedEpisodes(context.SourceProjectDir, item, expectedEpisodeNumbers);
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
        IReadOnlyList<int> expectedEpisodeNumbers,
        Action<string> log)
    {
        var inspection = InspectDownloadedEpisodes(sourceProjectDir, item, expectedEpisodeNumbers);
        if (inspection.IsUnknown)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(inspection.SkipReason)
                ? "下载未完成：无法确定应下载的集数。请核对原剧集数后重试下载。"
                : inspection.SkipReason);
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

    internal static DownloadCompleteness InspectDownloadedEpisodes(
        string sourceProjectDir,
        QueueProjectItem item,
        IReadOnlyList<int>? expectedEpisodeNumbers = null,
        bool allowStagedFallback = false)
    {
        var expectedNumbers = (expectedEpisodeNumbers ?? ResolveExpectedDownloadedEpisodeNumbers(sourceProjectDir, item))
            .Where(number => number > 0)
            .Distinct()
            .Order()
            .ToArray();
        if (expectedNumbers.Length == 0)
            return DownloadCompleteness.Unknown;

        var found = new HashSet<int>();
        var videoPaths = new List<string>();
        try
        {
            foreach (var file in ProjectVideoResolver.ResolveSourceVideos(sourceProjectDir, allowStagedFallback))
            {
                var info = new FileInfo(file);
                if (!EpisodeVideoExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase) || !info.Exists || info.Length == 0)
                    continue;
                videoPaths.Add(file);
                // 下载文件是「第N集」。本地导入常见 01.mp4、episode-01.mp4，不能只认前一种。
                if (TryReadEpisodeNumberFromFileName(file, out var episode) && episode > 0)
                    found.Add(episode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DownloadCompleteness(0, found.Count, [], $"下载未完成：无法读取分集目录。{ex.Message}");
        }

        var missing = expectedNumbers.Where(number => !found.Contains(number)).ToList();
        var foundExpectedCount = expectedNumbers.Count(found.Contains);
        // 本地整包有时没有任何集数文件名。文件数与声明集数一致时按自然顺序视为齐全，
        // 避免把已导入的完整剧集判成缺集后再走补下载。
        if (missing.Count > 0 &&
            found.Count == 0 &&
            videoPaths.Count == expectedNumbers.Length)
        {
            missing = [];
            foundExpectedCount = expectedNumbers.Length;
        }

        return new DownloadCompleteness(expectedNumbers.Length, foundExpectedCount, missing, "");
    }

    internal static IReadOnlyList<int> ResolveExpectedDownloadedEpisodeNumbers(
        string sourceProjectDir,
        QueueProjectItem item)
    {
        var expectedCount = 0;
        try { expectedCount = ProjectWorkspaceService.ResolveSourceEpisodeCount(item.ProjectDir); }
        catch { /* 信息文件缺失时回退 item.EpisodeCount */ }
        if (expectedCount <= 0) expectedCount = item.EpisodeCount;

        return TryReadExpectedEpisodeNumbersFromDownloadState(sourceProjectDir, expectedCount) ??
               (expectedCount > 0 ? Enumerable.Range(1, expectedCount).ToArray() : []);
    }

    private static IReadOnlyList<int>? TryReadExpectedEpisodeNumbersFromDownloadState(
        string sourceProjectDir,
        int expectedCount,
        string? matchingSelection = null)
    {
        var statePath = Path.Combine(sourceProjectDir, ".weixin-channel-download-state.json");
        if (!File.Exists(statePath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = document.RootElement;
            if (!root.TryGetProperty("episode_mappings", out var mappings) ||
                mappings.ValueKind != JsonValueKind.Array)
                return null;

            if (matchingSelection is not null &&
                !string.Equals(GetString(root, "episodes")?.Trim(), matchingSelection, StringComparison.OrdinalIgnoreCase))
                return null;

            var mode = root.TryGetProperty("episode_number_mode", out var modeElement)
                ? modeElement.GetString() ?? "source"
                : "source";
            var propertyName = string.Equals(mode, "continuous", StringComparison.OrdinalIgnoreCase)
                ? "sequence_episode_number"
                : "source_episode_number";
            var numbers = mappings.EnumerateArray()
                .Select(mapping => mapping.TryGetProperty(propertyName, out var numberElement) &&
                                   numberElement.TryGetInt32(out var number)
                    ? number
                    : 0)
                .Where(number => number > 0)
                .Distinct()
                .Order()
                .ToArray();
            var representsFullDownload = expectedCount <= 0 ||
                                          numbers.Length == expectedCount;
            return representsFullDownload && numbers.Length > 0 && numbers.Length == mappings.GetArrayLength()
                ? numbers : null;
        }
        catch
        {
            return null;
        }
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
        var originalTitle = ResolveOriginalTitle(infoPath, item, context);
        var rewriteHistory = AiRewriteHistoryService.LoadForOriginalTitle(originalTitle);
        var rewriteSynopsis = account?.TiktokAiRewriteSynopsis == true;
        var useEnglish = account?.UsesEnglishPublishText() == true;
        var workflowInfo = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var sourceSynopsis = ProjectOriginalSynopsisService.Resolve(context, originalTitle, workflowInfo, () => rewriteHistory);
        if (sourceSynopsis.Length != 0 &&
            !ProjectOriginalSynopsisService.IsUsable(workflowInfo.GetValueOrDefault(ProjectOriginalSynopsisService.FieldName), originalTitle))
        {
            ProjectInfoTextHelper.WriteOriginalSynopsisIfMissing(infoPath, sourceSynopsis, originalTitle);
        }
        EnsureRewriteInputDefaults(infoPath, item, context, episodeCount);
        var otherHistory = rewriteHistory
            .Where(record => !ProjectOriginalSynopsisService.IsCurrentProjectHistory(record, context))
            .ToList();
        if (otherHistory.Count > 0)
        {
            log($"同原剧名历史记录：{otherHistory.Count} 条，AI 将避开已用新剧名/简介。");
        }

        if (useEnglish)
            log("本土剧：生成英文新剧名和简介。");
        else if (TikTokAccountProfile.RewriteFollowsImportedLanguage(account?.TiktokDramaMarket))
            log("海外剧：按发布提示词中跟随原文的规则生成。");

        var rewriteVariantKey = BuildRewriteVariantKey(context, account);
        var configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings, account);
        ProjectInfoRewriteResult? generatedResult = null;
        try
        {
            var outputPath = infoPath;
            var outputExists = File.Exists(outputPath);
            var previousEnglish = false;
            var needsRewrite = outputExists &&
                               NeedsAiRewrite(item, context, outputPath, account, rewriteSynopsis, out previousEnglish);
            var recoveredPersistedResult = outputExists &&
                                           !overwriteExisting &&
                                           needsRewrite &&
                                           CurrentInfoMatchesRewriteHistory(
                                               context,
                                               outputPath,
                                               rewriteHistory,
                                               rewriteSynopsis || useEnglish,
                                               rewriteVariantKey);
            if (recoveredPersistedResult)
                needsRewrite = false;

            if (outputExists && !overwriteExisting && !needsRewrite)
            {
                log(recoveredPersistedResult
                    ? "检测到已落盘且经历史核验的 AI 改写结果，将补录完成状态并跳过重复调用。"
                    : "已有改写结果，跳过 AI 改写。");
                ApplyConfiguredPublishAudience(outputPath, account, log);
                if (!recoveredPersistedResult)
                {
                    AppendCurrentRewriteHistory(
                        item, settings, account, context, outputPath, originalTitle, sourceSynopsis, rewriteVariantKey);
                }
            }
            else
            {
                if (outputExists && !overwriteExisting && needsRewrite)
                {
                    log("当前短剧信息缺少匹配的改写结果，执行 AI 改写。");
                }

                var forbiddenTitles = BuildForbiddenTitles(item, context, originalTitle, outputPath, otherHistory);
                var forbiddenSynopses = rewriteSynopsis
                    ? UniqueTexts(otherHistory.Select(record => record.NewSynopsis))
                    : [];
                var restoreOriginalSynopsis = !useEnglish && previousEnglish && !rewriteSynopsis;
                var archiveOccupancy = NewTitleOccupancyService.LoadArchiveTitles(
                    context.WorkspaceRoot,
                    account?.ResolveArchiveRootPath(context.WorkspaceRoot),
                    context.SourceProjectDir,
                    context.WorkflowProjectDir);
                if (!archiveOccupancy.Checked)
                    log($"归档新剧名查重未完成，继续生成剧名：{archiveOccupancy.SkipReason}");
                else if (archiveOccupancy.Titles.Count > 0)
                    log($"将当前账号归档中 {archiveOccupancy.Titles.Count} 条新剧名传入提示词作为占用名单。");
                log("开始 AI 改写短剧信息，写入前核验剧名占用…");
                generatedResult = await RewriteWithTitleReservationAsync(
                    QueueInfrastructureServices.InfoRewriter,
                    new ProjectInfoRewriteRequest(
                        ProjectDir: workflowDir,
                        ConfigFile: configPath,
                        OutputFilePath: outputPath,
                        Overwrite: overwriteExisting || outputExists,
                        ForbiddenTitles: forbiddenTitles,
                        ForbiddenSynopses: forbiddenSynopses,
                        RewriteVariantKey: rewriteVariantKey,
                        OccupiedExactTitles: archiveOccupancy.Titles,
                        OriginalSynopsis: rewriteSynopsis || useEnglish || restoreOriginalSynopsis || sourceSynopsis.Length != 0
                            ? sourceSynopsis
                            : null,
                        UseUnifiedPublishPrompt: true,
                        UseEnglishPublishText: useEnglish,
                        RestoreOriginalSynopsis: restoreOriginalSynopsis),
                    archiveOccupancy,
                    account,
                    context.SourceProjectDir,
                    log,
                    ct);
            }
        }
        finally
        {
            TryDelete(configPath);
        }

        if (generatedResult is not null)
        {
            AppendRewriteHistory(
                generatedResult,
                item,
                settings,
                account,
                context,
                originalTitle,
                sourceSynopsis,
                rewriteVariantKey);
        }

        PersistRewriteCompletionState(context, account, infoPath, rewriteSynopsis, rewriteVariantKey);
        if (generatedResult is not null)
            log($"改写完成：{generatedResult.Title}");

        var newTitle = ResolveNewTitle(infoPath, item, context);
        if (!string.IsNullOrWhiteSpace(newTitle))
            ProjectWorkspaceService.SyncWorkflowProjectDirName(context.SourceProjectDir, newTitle, log);

        ProjectWorkspaceService.RefreshQueueItemMetadata(item);
    }

    internal static async Task EnsureDownloadedMaterialsAsync(
        QueueProjectItem item,
        ClientSettings globalSettings,
        TikTokAccountProfile? account,
        Action<string> log,
        CancellationToken ct)
    {
        if (ShortDramaDramaServices.Downloader is not DramaSourceRouter router)
            return;
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        XingeAutoPublishMarkerStore.AdoptLocalDramaBoxChain(context.SourceProjectDir);
        var settings = VisibleDramaSource.SettingsFor(globalSettings, account?.TiktokDramaMarket);
        if (XingeAutoPublishMarkerStore.TryRead(context.SourceProjectDir) is { } assignment &&
            !string.IsNullOrWhiteSpace(assignment.ClientDownloadChain))
        {
            settings = settings.Clone();
            settings.DramaSourceChain = assignment.ClientDownloadChain;
        }
        else if (TikTokUploadTitleRoute.Read(context.SourceProjectDir) is { } importedRoute)
        {
            settings = ResolveImportedAccountRoute(globalSettings, account, importedRoute).ApplyTo(globalSettings);
        }

        using var chainHold = ShortDramaDramaServices.HoldSettings(settings);
        var displayName = FirstNonEmpty(item.OriginalTitle, item.Title, Path.GetFileName(context.SourceProjectDir));
        await router.EnsurePosterAndIntroAsync(
            context.SourceProjectDir,
            displayName,
            new Progress<string>(log),
            ct).ConfigureAwait(false);
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
                account?.TiktokAiRewriteSynopsis == true,
                out _);
        }
        catch
        {
            return false;
        }
    }

    public static async Task RunGeneratePosterAsync(
        QueueProjectItem item,
        ClientSettings globalSettings,
        TikTokAccountProfile? account,
        Action<string> log,
        CancellationToken ct)
    {
        var settings = VisibleDramaSource.SettingsFor(globalSettings, account?.TiktokDramaMarket);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var workflowDir = ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        var outputPath = Path.Combine(workflowDir, TikTokPosterGenerationStateService.OutputFileName);

        var posterMode = (settings.PosterMode ?? ClientSettingsDefaults.PosterMode).Trim().ToLowerInvariant();
        var effectiveSettings = settings;
        Exception? videoFrameFailure = null;
        if (posterMode == "video_frame")
        {
            if (!HasImageModelConfig(settings))
                throw new InvalidOperationException("视频抽帧 AI 封面模式需要配置 ImageModel 接口。");

            try
            {
                var framePath = await VideoFramePosterSourceService.ExtractBestFrameAsync(
                    context.SourceProjectDir,
                    workflowDir,
                    settings,
                    log,
                    ct).ConfigureAwait(false);
                await GenerateAiPosterAsync(
                    workflowDir,
                    framePath,
                    outputPath,
                    settings,
                    posterMode,
                    log,
                    ct).ConfigureAwait(false);
                item.CoverPath = outputPath;
                TikTokPosterGenerationStateService.SaveGeneratedState(item, settings, framePath, outputPath);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                videoFrameFailure = ex;
                log($"视频抽帧封面生成失败，回退到原始海报模式：{ex.Message}");
                posterMode = "original";
                effectiveSettings = settings.Clone();
                effectiveSettings.PosterMode = posterMode;
                effectiveSettings.PosterAgentDirectPrompt = PosterGenerationTemplates.ResolvePlainText(
                    PosterGenerationTemplates.DirectMode, account?.AiOptions?.DirectPosterPrompt);
            }
        }

        var excludedPath = PosterGenerationTemplates.IsSupportedMode(posterMode) ? outputPath : null;
        string? preparedInput = null;
        if (excludedPath is null)
        {
            preparedInput = await QueueMaterialPrepareService.PrepareMaterialInputsAsync(item.ProjectDir, log, ct);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            var episodeCount = ProjectWorkspaceService.ResolveSourceEpisodeCount(item.ProjectDir);
            ProjectWorkspaceService.EnsureWorkflowInfo(item.ProjectDir, episodeCount, log);
        }

        var inputPath = preparedInput is not null && IsUsableDownloadPoster(preparedInput)
            ? preparedInput
            : ProjectWorkspaceService.FindPosterInputFile(context.SourceProjectDir, workflowDir, excludedPath, IsUsableDownloadPoster);
        if (inputPath is null)
        {
            await EnsureDownloadedMaterialsAsync(item, globalSettings, account, log, ct).ConfigureAwait(false);
            inputPath = ProjectWorkspaceService.FindPosterInputFile(context.SourceProjectDir, workflowDir, excludedPath, IsUsableDownloadPoster);
        }

        if (inputPath is null)
        {
            if (videoFrameFailure is not null)
            {
                throw new InvalidOperationException(
                    "视频抽帧封面生成失败，且未找到可用于回退的原始海报图片。",
                    videoFrameFailure);
            }

            throw new InvalidOperationException("未找到可用于生成海报的封面图片。请先提供下载海报或本地图片素材。");
        }

        if (IsAiPosterMode(posterMode))
        {
            if (!HasImageModelConfig(effectiveSettings))
                throw new InvalidOperationException("海报 AI 模式需要配置 ImageModel 接口。");

            await GenerateAiPosterAsync(
                workflowDir,
                inputPath,
                outputPath,
                effectiveSettings,
                posterMode,
                log,
                ct).ConfigureAwait(false);
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
        TikTokPosterGenerationStateService.SaveGeneratedState(
            item,
            settings,
            inputPath,
            outputPath,
            effectivePosterMode: posterMode);
    }

    private static async Task GenerateAiPosterAsync(
        string workflowDir,
        string inputPath,
        string outputPath,
        ClientSettings settings,
        string posterMode,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var configPath = ClientSettingsWorkflowConfigWriter.WriteTempConfig(settings);
        try
        {
            switch (posterMode)
            {
                case ClientSettingsDefaults.PosterModeReferenceRegenerate:
                    log("开始参考原图生成新封面…");
                    break;
                case "video_frame":
                    log("开始使用视频抽帧画面 AI 生成新封面…");
                    break;
                case "poster_ai_edit":
                    log("开始参考原海报由 AI 重做封面…");
                    break;
                case "poster_ai_erase_pil_title":
                    log("开始使用 AI 去字并程序绘制标题…");
                    break;
                case "erase_fixed_lower_title":
                    log("开始清除封面原文字，并在下半部分用固定字体写入新剧名…");
                    break;
                default:
                    log("开始使用当前图片提示词生成海报…");
                    break;
            }

            if (IsHeicLike(inputPath))
                log($"海报源图为 {Path.GetExtension(inputPath)}，将先转换为 PNG 再调用 AI。");

            try
            {
                await QueueInfrastructureServices.Poster.RenameAsync(
                    new PosterRenameRequest(
                        ProjectDir: workflowDir,
                        InputFilePath: inputPath,
                        OutputFilePath: outputPath,
                        ConfigFile: configPath,
                        UseAi: false,
                        Overwrite: true,
                        Log: log,
                        UseVisibleImagePrompt: PosterGenerationTemplates.IsSupportedMode(posterMode),
                        PlainTextImagePrompt: TikTokAccountAiSettings.GetPlainTextPosterPrompt(settings, posterMode)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (QueueWorkerRunner.IsPosterSensitiveContent(ex))
            {
                throw new InvalidOperationException(PosterRenamer.PosterSensitiveContentMessage);
            }
            catch (Exception ex)
            {
                var firstLine = ex.Message.Split(['\r', '\n'], 2, StringSplitOptions.None)[0];
                log(
                    $"海报生成失败：{ex.GetType().Name}，外部取消={cancellationToken.IsCancellationRequested}，{firstLine}");
                log(
                    $"海报生成失败详情：类型={ex.GetType().FullName}，HResult=0x{ex.HResult:X8}。" +
                    Environment.NewLine + ex);
                throw;
            }
            log($"海报已生成：{Path.GetFileName(outputPath)}（请使用此 PNG 文件，不会覆盖原素材）");
        }
        finally
        {
            TryDelete(configPath);
        }
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
        var expectedEpisodeNumbers = ResolveExpectedDownloadedEpisodeNumbers(context.SourceProjectDir, item);
        var inspection = InspectDownloadedEpisodes(context.SourceProjectDir, item, expectedEpisodeNumbers);
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

        if (LocalManualDramaImportService.IsLocalManualImportProject(context.SourceProjectDir))
        {
            throw new InvalidOperationException(
                $"删除源视频前发现本地导入视频不完整：短剧总集数 {inspection.Expected}，" +
                $"已识别源视频 {inspection.FoundCount} 个，缺第 {FormatEpisodePreview(inspection.Missing)} 集。" +
                "本地导入不会自动补下载，请补齐源视频后再执行。");
        }

        var metadata = ReadDownloadMetadata(context.SourceProjectDir);
        var displayName = FirstNonEmpty(item.Title, item.OriginalTitle, metadata.Title, Path.GetFileName(context.SourceProjectDir));
        log(
            $"删除源视频前发现源视频不完整：短剧总集数 {inspection.Expected}，源视频 {inspection.FoundCount} 个，" +
            $"缺第 {FormatEpisodePreview(inspection.Missing)} 集。先自动补下载，补齐后再删除源视频。");

        var repaired = await TryRepairMissingEpisodesAsync(
                context,
                item,
                metadata,
                settings,
                displayName,
                expectedEpisodeNumbers,
                log,
                ct)
            .ConfigureAwait(false);

        ProjectWorkspaceService.PrepareWorkflowProject(context.SourceProjectDir, log);
        ProjectWorkspaceService.RefreshQueueItemMetadata(item);

        if (repaired)
            log("删源前缺集已补齐，继续执行删除源视频。");

        EnsureDownloadedEpisodesComplete(context.SourceProjectDir, item, expectedEpisodeNumbers, log);
    }

    private static void ApplyConfiguredPublishAudience(string infoPath, TikTokAccountProfile? account, Action<string> log)
    {
        var configured = account?.TiktokTargetAudienceMode?.Trim() switch
        {
            "male" => "男",
            "female" => "女",
            _ => null,
        };
        if (configured is null ||
            string.Equals(ProjectInfoTextHelper.ParseInfoFile(infoPath).GetValueOrDefault("目标观众"), configured, StringComparison.Ordinal))
        {
            return;
        }

        ProjectInfoTextHelper.UpdateFields(infoPath, new Dictionary<string, string> { ["目标观众"] = configured });
        log($"目标观众采用账号固定设置：{configured}。");
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

    internal static bool CurrentInfoMatchesRewriteHistory(
        ProjectWorkspaceContext context,
        string infoPath,
        IReadOnlyList<AiRewriteHistoryRecord> history,
        bool rewriteSynopsis,
        string rewriteVariantKey)
    {
        if (!File.Exists(infoPath) || history.Count == 0)
            return false;

        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var title = FirstNonEmpty(
            info.GetValueOrDefault("新剧名"),
            info.GetValueOrDefault("剧名"));
        var normalizedTitle = NormalizeComparableTitle(title);
        var synopsis = CurrentSynopsis(info);
        if (string.IsNullOrWhiteSpace(normalizedTitle) ||
            (rewriteSynopsis && string.IsNullOrWhiteSpace(synopsis)))
        {
            return false;
        }

        var synopsisFingerprint = rewriteSynopsis ? SynopsisFingerprint(synopsis) : string.Empty;
        return history.Any(record =>
            ProjectOriginalSynopsisService.IsCurrentProjectHistory(record, context) &&
            RewriteVariantKeysEqual(record.VariantKey, rewriteVariantKey) &&
            string.Equals(
                NormalizeComparableTitle(record.NewTitle),
                normalizedTitle,
                StringComparison.OrdinalIgnoreCase) &&
            (!rewriteSynopsis || string.Equals(
                SynopsisFingerprint(record.NewSynopsis),
                synopsisFingerprint,
                StringComparison.OrdinalIgnoreCase)));
    }

    internal static async Task<ProjectInfoRewriteResult> RewriteWithTitleReservationAsync(
        IProjectInfoRewriter rewriter,
        ProjectInfoRewriteRequest request,
        NewTitleOccupancySnapshot archiveOccupancy,
        TikTokAccountProfile? account,
        string projectDir,
        Action<string> log,
        CancellationToken ct)
    {
        var outputPath = request.OutputFilePath;
        var temporaryPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $".rewrite-{Guid.NewGuid():N}.tmp");
        ct.ThrowIfCancellationRequested();
        try
        {
            if (File.Exists(outputPath))
            {
                if (!request.Overwrite)
                    throw new IOException($"输出文件已存在: {outputPath}");
                File.Copy(outputPath, temporaryPath);
            }

            var generated = await rewriter.RewriteAsync(request with { OutputFilePath = temporaryPath }, ct);
            ct.ThrowIfCancellationRequested();
            var title = generated.Title;
            if (archiveOccupancy.Checked && NewTitleOccupancyService.IsExactMatch(title, archiveOccupancy.Titles))
                throw new NewTitleOccupiedException($"写入前校验发现新剧名「{title}」已被归档占用，保留原短剧信息。");

            var managementChecked = false;
            IReadOnlyCollection<string> managementDuplicates = [];
            try
            {
                var management = await TikTokManagementUploadRecordSyncService.CheckDuplicateNewNamesAsync([title], account, ct);
                managementChecked = management.Ok;
                managementDuplicates = management.Duplicates;
                if (!management.Ok)
                    log($"管理系统新剧名占用校验未完成：{management.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"管理系统新剧名占用校验未完成：{ex.Message}");
            }

            var decision = NewTitleOccupancyService.Decide(title, archiveOccupancy, managementChecked, managementDuplicates);
            if (decision.Regenerate)
                throw new NewTitleOccupiedException($"写入前校验发现新剧名「{title}」已被{decision.Reason}占用，保留原短剧信息。");

            if (request.UseEnglishPublishText)
                ProjectInfoTextHelper.UpdateFields(temporaryPath, new Dictionary<string, string> { [PublishTextLanguageField] = "en" });
            ApplyConfiguredPublishAudience(temporaryPath, account, log);
            var currentInfo = ProjectInfoTextHelper.ParseInfoFile(outputPath);
            var currentTitle = FirstNonEmpty(currentInfo.GetValueOrDefault("新剧名"), currentInfo.GetValueOrDefault("剧名"));
            var sameAsCurrentTitle = NewTitleOccupancyService.IsExactMatch(title, [currentTitle]);
            var accountKey = NewTitleOccupancyService.ReservationAccountKey(account);
            if (!NewTitleOccupancyService.TryClaim(accountKey, title, projectDir))
                throw new NewTitleOccupiedException($"写入前校验发现新剧名「{title}」已被同一账号的其他项目占用，保留原短剧信息。");

            try
            {
                ct.ThrowIfCancellationRequested();
                File.Move(temporaryPath, outputPath, request.Overwrite);
            }
            catch
            {
                if (!sameAsCurrentTitle)
                    NewTitleOccupancyService.Release(accountKey, title, projectDir);
                throw;
            }

            if (!sameAsCurrentTitle && !string.IsNullOrWhiteSpace(currentTitle))
                NewTitleOccupancyService.Release(accountKey, currentTitle, projectDir);
            return generated with { OutputFilePath = outputPath };
        }
        finally
        {
            TryDelete(temporaryPath);
        }
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
        var followSource = TikTokAccountProfile.RewriteFollowsImportedLanguage(account?.TiktokDramaMarket)
            ? "#follow=source"
            : "";
        var output = account?.UsesEnglishPublishText() == true ? EnglishRewriteVariantSuffix : "";
        return $"{Path.GetFullPath(context.SourceProjectDir)}#{accountKey}{followSource}#synopsis={synopsisMode}{output}";
    }

    private static bool IsEnglishRewriteVariant(string? key) =>
        key?.EndsWith(EnglishRewriteVariantSuffix, StringComparison.OrdinalIgnoreCase) == true;

    private static bool RewriteVariantKeysEqual(string? left, string? right) =>
        string.Equals(
            (left ?? string.Empty).Trim(),
            (right ?? string.Empty).Trim(),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool RewriteVariantKeysEquivalentForReuse(string? left, string? right)
    {
        if (RewriteVariantKeysEqual(left, right))
            return true;

        return TrySplitRewriteVariantKey(left, out var leftProject, out var leftMode) &&
               TrySplitRewriteVariantKey(right, out var rightProject, out var rightMode) &&
               string.Equals(
                   leftProject,
                   rightProject,
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
               string.Equals(leftMode, rightMode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplitRewriteVariantKey(
        string? value,
        out string projectPath,
        out string synopsisMode)
    {
        projectPath = string.Empty;
        synopsisMode = string.Empty;
        var text = (value ?? string.Empty).Trim();
        var modeIndex = text.LastIndexOf("#synopsis=", StringComparison.OrdinalIgnoreCase);
        if (modeIndex <= 0)
            return false;

        var accountIndex = text.LastIndexOf('#', modeIndex - 1);
        if (accountIndex <= 0)
            return false;

        projectPath = text[..accountIndex];
        synopsisMode = text[modeIndex..];
        return !string.IsNullOrWhiteSpace(projectPath);
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
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfMissing(existing, updates, "原剧名", originalTitle);
        if (!existing.ContainsKey("新剧名") && !existing.ContainsKey("剧名"))
            updates["新剧名"] = currentTitle;
        AddIfMissing(existing, updates, "集数", Math.Max(1, episodeCount).ToString());
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
        bool rewriteSynopsis,
        out bool previousEnglish)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var title = FirstNonEmpty(info.GetValueOrDefault("新剧名"), info.GetValueOrDefault("剧名"));
        var matchingCompletion = HasMatchingRewriteCompletionState(context, account, info, rewriteSynopsis,
            out var recordedTitle, out previousEnglish);
        if (matchingCompletion && title.Length > 0 && string.Equals(title, recordedTitle, StringComparison.Ordinal))
            return false;

        if (account is not null && (account.UsesEnglishPublishText() || previousEnglish))
            return true;

        // Older projects did not record a completed title. Keep their existing resume behavior.
        if (!IsProjectInfoRewritten(info, item, context, rewriteSynopsis))
            return true;

        return rewriteSynopsis && !matchingCompletion;
    }

    internal static void PersistRewriteCompletionState(
        ProjectWorkspaceContext context,
        TikTokAccountProfile? account,
        string infoPath,
        bool rewriteSynopsis,
        string? rewriteVariantKey = null)
    {
        var info = ProjectInfoTextHelper.ParseInfoFile(infoPath);
        var synopsis = CurrentSynopsis(info);
        var variantKey = string.IsNullOrWhiteSpace(rewriteVariantKey)
            ? BuildRewriteVariantKey(context, account)
            : rewriteVariantKey;
        var saveSynopsisFingerprint = rewriteSynopsis || IsEnglishRewriteVariant(variantKey);
        if (saveSynopsisFingerprint && string.IsNullOrWhiteSpace(synopsis))
            throw new InvalidDataException("改写结果中没有简介，无法记录完成状态。");

        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            AiRewriteStateDocumentType,
            new Dictionary<string, object?>
            {
                ["version"] = AiRewriteStateVersion,
                ["variant_key"] = variantKey,
                ["rewrite_synopsis"] = rewriteSynopsis,
                ["synopsis_fingerprint"] = saveSynopsisFingerprint ? SynopsisFingerprint(synopsis) : string.Empty,
                ["title"] = FirstNonEmpty(info.GetValueOrDefault("新剧名"), info.GetValueOrDefault("剧名")),
            },
            context.WorkflowProjectDir);
    }

    private static bool HasMatchingRewriteCompletionState(
        ProjectWorkspaceContext context,
        TikTokAccountProfile? account,
        IReadOnlyDictionary<string, string> info,
        bool rewriteSynopsis,
        out string recordedTitle,
        out bool previousEnglish)
    {
        var state = ProjectStateDocumentStore.LoadDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            AiRewriteStateDocumentType);

        recordedTitle = ReadJsonString(state.GetValueOrDefault("title"));
        var variantKey = ReadJsonString(state.GetValueOrDefault("variant_key"));
        previousEnglish = IsEnglishRewriteVariant(variantKey) ||
                          string.Equals(info.GetValueOrDefault(PublishTextLanguageField), "en", StringComparison.OrdinalIgnoreCase);
        if (!TryReadBoolean(state.GetValueOrDefault("rewrite_synopsis"), out var recordedRewriteSynopsis))
            return false;

        if (account is not null)
        {
            if (recordedRewriteSynopsis != rewriteSynopsis ||
                previousEnglish != account.UsesEnglishPublishText() ||
                !RewriteVariantKeysEquivalentForReuse(variantKey, BuildRewriteVariantKey(context, account)))
                return false;
        }

        if (!recordedRewriteSynopsis && !previousEnglish)
            return true;

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
        var synopsis = FirstNonEmpty(
            info.GetValueOrDefault("简介"),
            info.GetValueOrDefault("描述"),
            info.GetValueOrDefault("剧情简介"));

        if (string.IsNullOrWhiteSpace(title)) return false;
        if (!string.IsNullOrWhiteSpace(originalTitle) && string.Equals(title, originalTitle, StringComparison.OrdinalIgnoreCase))
            return false;
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
        return mode is "original" or "ai" or "poster_ai_erase_pil_title" or "erase_fixed_lower_title"
            or "video_frame" or "poster_ai_edit" or ClientSettingsDefaults.PosterModeReferenceRegenerate;
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

    internal sealed record DownloadCompleteness(
        int Expected,
        int FoundCount,
        IReadOnlyList<int> Missing,
        string SkipReason)
    {
        public static DownloadCompleteness Unknown { get; } = new(0, 0, [], "");

        public bool IsUnknown => Expected <= 0 || !string.IsNullOrWhiteSpace(SkipReason);
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
