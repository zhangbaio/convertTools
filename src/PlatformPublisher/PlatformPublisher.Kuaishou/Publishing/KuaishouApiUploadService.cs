using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouApiUploadService
{
    private readonly KuaishouPersonalProjectDataService _projectDataService;
    private readonly KuaishouPersonalUploadStateStore _stateStore;
    private readonly KuaishouCommitmentService _commitmentService;
    private readonly KuaishouDistributionService _distributionService;
    private readonly KuaishouOnlineQueueStore _onlineQueueStore;
    private readonly KuaishouApiVideoUploader _videos = new();

    public KuaishouApiUploadService(
        KuaishouPersonalProjectDataService projectDataService,
        KuaishouPersonalUploadStateStore stateStore,
        KuaishouCommitmentService commitmentService,
        KuaishouDistributionService distributionService,
        KuaishouOnlineQueueStore onlineQueueStore)
    {
        _projectDataService = projectDataService;
        _stateStore = stateStore;
        _commitmentService = commitmentService;
        _distributionService = distributionService;
        _onlineQueueStore = onlineQueueStore;
    }

    public async Task RunAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var config = KuaishouPersonalConfig.Load(job);
        var configurationIssues = KuaishouConfigurationValidator.Validate(config);
        if (configurationIssues.Count > 0)
            throw new InvalidOperationException("快手配置校验失败：" + string.Join("；", configurationIssues));
        var label = job.Platform.DisplayName();
        var data = await _projectDataService.ResolveAsync(job.ProjectDirectory, config, cancellationToken);
        var commitmentPdf = await _commitmentService.ResolveAsync(data, config, cancellationToken);
        if (!string.Equals(commitmentPdf, data.CommitmentPdfPath, StringComparison.OrdinalIgnoreCase))
            data = data with { CommitmentPdfPath = commitmentPdf };
        if (data.VideoPaths.Count == 0)
            throw new InvalidOperationException($"{label}项目没有可上传的剧集视频。");
        var state = _stateStore.Load(data.WorkflowDirectory, job.Platform);
        if (config.ForceRerun)
        {
            state = new KuaishouPersonalUploadState();
            progress?.Report($"{label}：已按配置忽略旧状态并从头执行。 ");
        }
        if (state.Status == "completed" &&
            (!string.Equals(config.FinalAction, "submit_review", StringComparison.OrdinalIgnoreCase) || state.ReviewSubmitted))
        {
            progress?.Report($"{label}：项目已经完成，跳过重复执行。 ");
            return;
        }
        if (!state.ReviewSubmitted && state.VideosUploaded)
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
            var resumeDraft = seriesId.Length > 0 && state.FirstPageCompleted;
            if (resumeDraft)
            {
                progress?.Report($"{label}：从已有草稿 {seriesId} 继续上传。已有视频的集会跳过，未完成的分片从断点续传。");
            }
            else
            {
                seriesId = await new KuaishouSeriesDraftClient().SaveAsync(
                    session, data, config, form, state.MiniSeriesId, progress, cancellationToken);
                state.MiniSeriesId = seriesId;
                state.EpisodeInfoCompleted = true;
                state.FirstPageCompleted = true;
                state.CurrentStage = "first_page";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
            }

            var firstPageAction = (config.FirstPageAction ?? "next").Trim().ToLowerInvariant();
            if (firstPageAction is "draft" or "keep")
            {
                state.Status = firstPageAction == "keep" ? "manual_hold" : "draft_saved";
                state.CurrentStage = state.Status;
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
                progress?.Report(firstPageAction == "keep"
                    ? $"{label}：第一页已按配置保持。"
                    : $"{label}：第一页草稿已通过接口保存。");
                return;
            }

            state.CurrentStage = "episode_upload";
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
            await _videos.UploadAsync(session, seriesId, data.VideoPaths, progress, cancellationToken, async (_, token) =>
            {
                state.MiniSeriesId = seriesId;
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, token, job.Platform);
            });
            state.VideosUploaded = true;
            state.CurrentStage = "videos_uploaded";
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);

            if (string.Equals(config.FinalAction, "submit_review", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"{label}：正在提交快手审核。");
                await _videos.SubmitAsync(session, seriesId, cancellationToken);
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
            if (config.OnlineAutoDistributionEnabled &&
                (config.AutoOnlineEnabled || config.StepOnlineSeries))
                progress?.Report($"{label}：分销已设置为上架成功后执行。 ");
            else
                await _distributionService.ApplyAsync(state.MiniSeriesId, config, progress, cancellationToken);
        }
        catch (KuaishouExistingSeriesNotEditableException ex)
        {
            state.MarkAlreadyOnPlatform(ex.SeriesId);
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, CancellationToken.None, job.Platform);
            progress?.Report($"{label}：{ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Status = "failed";
            state.LastError = ex.Message;
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, CancellationToken.None, job.Platform);
            throw;
        }
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
}
