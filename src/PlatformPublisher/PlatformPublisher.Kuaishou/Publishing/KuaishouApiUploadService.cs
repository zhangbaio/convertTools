using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouApiUploadService
{
    private readonly KuaishouPersonalProjectDataService _projectDataService;
    private readonly KuaishouPersonalUploadStateStore _stateStore;
    private readonly KuaishouCommitmentService _commitmentService;
    private readonly KuaishouOnlineQueueStore _onlineQueueStore;
    private readonly KuaishouApiVideoUploader _videos = new();

    public KuaishouApiUploadService(
        KuaishouPersonalProjectDataService projectDataService,
        KuaishouPersonalUploadStateStore stateStore,
        KuaishouCommitmentService commitmentService,
        KuaishouOnlineQueueStore onlineQueueStore)
    {
        _projectDataService = projectDataService;
        _stateStore = stateStore;
        _commitmentService = commitmentService;
        _onlineQueueStore = onlineQueueStore;
    }

    public async Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var account = KuaishouDailyUploadLimit.UseAccount(job.AccountId);
        var config = KuaishouPersonalConfig.Load(job);
        var configurationIssues = KuaishouConfigurationValidator.Validate(config);
        if (configurationIssues.Count > 0)
            throw new InvalidOperationException("快手配置校验失败：" + string.Join("；", configurationIssues));
        var label = job.Platform.DisplayName();
        var (data, lease) = await _projectDataService.OpenForUploadAsync(job, config, cancellationToken);
        await using var videoLease = lease;
        var videoBatch = string.IsNullOrEmpty(data.VideoBatchIdentity)
            ? null : new PublishVideoBatch(data.VideoBatchIdentity, data.VideoPaths);
        var commitmentPdf = await _commitmentService.ResolveAsync(data, config, cancellationToken);
        if (!string.Equals(commitmentPdf, data.CommitmentPdfPath, StringComparison.OrdinalIgnoreCase))
            data = data with { CommitmentPdfPath = commitmentPdf };
        if (data.VideoPaths.Count == 0)
            throw new InvalidOperationException($"{label}项目没有可上传的剧集视频。");
        var state = _stateStore.Load(data.WorkflowDirectory, job.Platform);
        var replacing = state.ReplaceExistingVideos && !string.IsNullOrWhiteSpace(state.MiniSeriesId);
        if (config.ForceRerun && !replacing)
        {
            state.BindVideoBatch(videoBatch);
            state = new KuaishouPersonalUploadState();
            replacing = false;
            progress?.Report($"{label}：已按配置忽略旧状态并从头执行。 ");
        }
        else if (config.ForceRerun && replacing)
        {
            progress?.Report($"{label}：这部已绑定短剧 {state.MiniSeriesId.Trim()}，继续替换，不新建。");
        }
        if (state.Status == "completed" &&
            (!string.Equals(config.FinalAction, "submit_review", StringComparison.OrdinalIgnoreCase) || state.ReviewSubmitted))
        {
            progress?.Report($"{label}：项目已经完成，跳过重复执行。 ");
            return;
        }
        state.BindVideoBatch(videoBatch);
        if (!replacing && !state.ReviewSubmitted && state.VideosUploaded)
        {
            state.VideosUploaded = false;
            progress?.Report($"{label}：上次未完成提交审核，不能把视频视为已全部上传，将继续上传。 ");
        }

        progress?.Report($"{label}：接口上传，不打开浏览器。");
        state.Status = "running";
        state.CurrentStage = "api_upload";
        state.LastError = string.Empty;
        await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
        try
        {
            using var session = KuaishouApiSession.Open(config.AuthStatePath);
            var size = await KuaishouPersonalFirstPageService.ProbeVideoSizeAsync(data.VideoPaths.FirstOrDefault());
            var resolved = KuaishouPersonalFormResolver.Resolve(config, size.Width, size.Height);
            var form = KuaishouPersonalFormResolver.ClampToEpisodeCount(resolved, data.VideoPaths.Count);
            if (form.FreeEpisodeCount != resolved.FreeEpisodeCount || form.UnlockCount != resolved.UnlockCount)
                progress?.Report($"{label}：当前共 {data.VideoPaths.Count} 集，免费集数和解锁集数已限制为不超过总集数。 ");
            var enterprise = config.StoragePlatform == PublishPlatform.KuaishouEnterpriseRevenue;
            if (enterprise)
            {
                if (string.IsNullOrWhiteSpace(config.KuaishouNickname) ||
                    string.IsNullOrWhiteSpace(First(config.AdvertiserId, config.KuaishouId)))
                    throw new InvalidOperationException("请先在快手分账配置的“登录授权”中填写快手昵称和用户快手号 ID。");
            }
            else if (string.IsNullOrWhiteSpace(form.CreditName))
            {
                throw new InvalidOperationException("请先在快手分账配置的“分账配置”中填写用户名。");
            }
            else if (string.IsNullOrWhiteSpace(config.KuaishouNickname) || !form.ProductionOrg.Contains('+'))
            {
                throw new InvalidOperationException("请先在快手分账配置的“登录授权”中填写快手昵称和用户快手号 ID。");
            }

            var seriesId = state.MiniSeriesId?.Trim() ?? string.Empty;
            replacing = state.ReplaceExistingVideos && seriesId.Length > 0;
            var resumeDraft = !replacing && seriesId.Length > 0 && state.FirstPageCompleted;
            if (replacing)
            {
                progress?.Report(state.FirstPageCompleted
                    ? $"{label}：继续替换短剧 {seriesId}，不新建。"
                    : $"{label}：正在把《{data.Title}》写入短剧 {seriesId}，不新建。");
                if (!state.FirstPageCompleted)
                {
                    await KuaishouDraftReplace.EnsureEpisodeCountAsync(session, seriesId, data.VideoPaths.Count, cancellationToken);
                    seriesId = await new KuaishouSeriesDraftClient().SaveAsync(
                        session, data, config, form, seriesId, progress, cancellationToken);
                    state.MiniSeriesId = seriesId;
                    state.EpisodeInfoCompleted = true;
                    state.FirstPageCompleted = true;
                    state.CurrentStage = "replace_draft";
                    await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
                }
            }
            else if (resumeDraft)
            {
                progress?.Report($"{label}：从已有草稿 {seriesId} 继续上传。已有视频的集会跳过，未完成的分片从断点续传。");
            }
            else
            {
                KuaishouDailyUploadLimit.ThrowIfNoted(job.AccountId);
                seriesId = await new KuaishouSeriesDraftClient().SaveAsync(
                    session, data, config, form, state.MiniSeriesId, progress, cancellationToken);
                state.MiniSeriesId = seriesId;
                state.EpisodeInfoCompleted = true;
                state.FirstPageCompleted = true;
                state.CurrentStage = "first_page";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
            }

            var firstPageAction = (config.FirstPageAction ?? "next").Trim().ToLowerInvariant();
            if (!replacing && firstPageAction is "draft" or "keep")
            {
                state.Status = firstPageAction == "keep" ? "manual_hold" : "draft_saved";
                state.CurrentStage = state.Status;
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
                progress?.Report(firstPageAction == "keep"
                    ? $"{label}：第一页已按配置保持。"
                    : $"{label}：第一页草稿已通过接口保存。");
                return;
            }

            if (replacing && state.VideosUploaded)
            {
                progress?.Report($"{label}：短剧 {seriesId} 的视频已经替换，继续后续步骤。");
            }
            else
            {
                state.CurrentStage = "episode_upload";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
                await UploadVideosDespiteDailyCapAsync(session, seriesId, data.VideoPaths, label, progress, cancellationToken, async (_, token) =>
                {
                    state.MiniSeriesId = seriesId;
                    await _stateStore.SaveAsync(data.WorkflowDirectory, state, token, job.Platform);
                }, replacing);
                state.VideosUploaded = true;
                state.CurrentStage = "videos_uploaded";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
            }

            if (string.Equals(config.FinalAction, "submit_review", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"{label}：正在提交快手审核。");
                await SubmitDespiteDailyCapAsync(session, seriesId, label, progress, cancellationToken);
                state.ReviewSubmitted = true;
                state.CurrentStage = "review_submitted";
                progress?.Report($"{label}：已提交审核。 ");
            }
            else
            {
                progress?.Report($"{label}：视频上传完成，按配置保持在提交审核前。 ");
            }

            state.Status = "completed";
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
            if (state.ReviewSubmitted)
            {
                var onlineItem = _onlineQueueStore.Register(job, data, state, config);
                if (onlineItem is not null)
                    progress?.Report($"{label}：已加入当前账号的自动上架队列，等待审核通过。 ");
                else if (config.AutoOnlineEnabled || config.StepOnlineSeries)
                    progress?.Report($"{label}：未取得短剧 ID，无法加入自动上架队列，请在视频管理中补录。 ");
            }
        }
        catch (KuaishouExistingSeriesNotEditableException ex)
        {
            state.MarkAlreadyOnPlatform(ex.SeriesId);
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, CancellationToken.None, job.Platform);
            progress?.Report($"{label}：{ex.Message}");
        }
        catch (Exception ex) when (KuaishouDailyUploadLimit.Is(ex))
        {
            KuaishouDailyUploadLimit.TryNote(job.AccountId);
            if (!KuaishouDailyUploadLimit.HasStartedUpload(state) && !state.ReplaceExistingVideos)
            {
                state.Status = "failed";
                state.LastError = ex.Message;
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, CancellationToken.None, job.Platform);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Status = "failed";
            state.LastError = ex.Message;
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, CancellationToken.None, job.Platform);
            throw;
        }
    }

    public async Task ReplaceDraftAsync(
        PublishJob job,
        string seriesId,
        string platformTitle,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!KuaishouDraftReplace.TryParseSeriesId(seriesId, out var id))
            throw new InvalidOperationException("短剧 ID 只能是数字。");
        var config = KuaishouPersonalConfig.Load(job);
        var label = job.Platform.DisplayName();
        var data = await _projectDataService.ResolveForUploadAsync(job, config, cancellationToken);
        if (data.VideoPaths.Count == 0)
            throw new InvalidOperationException("这部本地剧没有可上传的视频。");
        var state = _stateStore.Load(data.WorkflowDirectory, job.Platform);
        var binding = KuaishouDraftReplace.BindingMessage(state.MiniSeriesId, id);
        if (binding.Length > 0)
            throw new InvalidOperationException(binding);
        state.ReplaceExistingVideos = true;
        state.MiniSeriesId = id;
        state.VideoBatchIdentity = data.VideoBatchIdentity;
        state.VideoBatchEpisodeCount = string.IsNullOrEmpty(data.VideoBatchIdentity) ? 0 : data.VideoPaths.Count;
        state.SkipTodayUploadCount = true;
        state.Status = "running";
        state.CurrentStage = "replace_draft";
        state.FirstPageCompleted = false;
        state.EpisodeInfoCompleted = false;
        state.VideosUploaded = false;
        state.ReviewSubmitted = false;
        state.LastError = string.Empty;
        await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
        var oldName = string.IsNullOrWhiteSpace(platformTitle) ? id : platformTitle.Trim();
        progress?.Report($"{label}：用《{data.Title}》替换平台上的《{oldName}》（{id}）。原标题、封面、简介和视频会被覆盖。不新建短剧。");
        await RunAsync(job, progress, cancellationToken);
    }

    private static string First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private async Task UploadVideosDespiteDailyCapAsync(
        KuaishouApiSession session,
        string seriesId,
        IReadOnlyList<string> videoPaths,
        string label,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, Task>? afterEpisode,
        bool replaceExisting = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _videos.UploadAsync(session, seriesId, videoPaths, progress, cancellationToken, afterEpisode, replaceExisting);
                return;
            }
            catch (Exception ex) when (KuaishouDailyUploadLimit.Is(ex) && attempt < 3)
            {
                KuaishouDailyUploadLimit.TryNote(KuaishouDailyUploadLimit.CurrentAccountId);
                progress?.Report($"{label}：今日新建已达限额，这部已在上传，继续传完。");
            }
        }
    }

    private async Task SubmitDespiteDailyCapAsync(
        KuaishouApiSession session,
        string seriesId,
        string label,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _videos.SubmitAsync(session, seriesId, cancellationToken);
                return;
            }
            catch (Exception ex) when (KuaishouDailyUploadLimit.Is(ex) && attempt < 3)
            {
                KuaishouDailyUploadLimit.TryNote(KuaishouDailyUploadLimit.CurrentAccountId);
                progress?.Report($"{label}：今日新建已达限额，这部已在上传，继续传完。");
            }
        }
    }
}
