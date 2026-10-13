using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalUploadService
{
    private readonly KuaishouPersonalSessionService _sessionService;
    private readonly KuaishouPersonalProjectDataService _projectDataService;
    private readonly KuaishouPersonalFirstPageService _firstPageService;
    private readonly KuaishouPersonalEpisodeUploadService _episodeUploadService;
    private readonly KuaishouPersonalUploadStateStore _stateStore;
    private readonly KuaishouCommitmentService _commitmentService;
    private readonly KuaishouContentComplianceService _contentComplianceService;
    private readonly KuaishouDistributionService _distributionService;
    private readonly KuaishouOnlineQueueStore _onlineQueueStore;

    public KuaishouPersonalUploadService(
        KuaishouPersonalSessionService sessionService,
        KuaishouPersonalProjectDataService projectDataService,
        KuaishouPersonalFirstPageService firstPageService,
        KuaishouPersonalEpisodeUploadService episodeUploadService,
        KuaishouPersonalUploadStateStore stateStore,
        KuaishouCommitmentService commitmentService,
        KuaishouContentComplianceService contentComplianceService,
        KuaishouDistributionService distributionService,
        KuaishouOnlineQueueStore onlineQueueStore)
    {
        _sessionService = sessionService;
        _projectDataService = projectDataService;
        _firstPageService = firstPageService;
        _episodeUploadService = episodeUploadService;
        _stateStore = stateStore;
        _commitmentService = commitmentService;
        _contentComplianceService = contentComplianceService;
        _distributionService = distributionService;
        _onlineQueueStore = onlineQueueStore;
    }

    public async Task RunAsync(
        PublishJob job,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
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
        _contentComplianceService.Validate(data, config);
        if (data.VideoPaths.Count == 0) throw new InvalidOperationException($"{label}项目没有可上传的剧集视频。");
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
        progress?.Report($"{label}项目解析完成：《{data.Title}》，视频 {data.VideoPaths.Count} 集，工程图 {data.ProjectImagePaths.Count} 张。 ");
        state.Status = "running";
        state.CurrentStage = "opening_browser";
        state.LastError = string.Empty;
        await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
        try
        {
            await _sessionService.ExecuteAuthenticatedAsync(job, async (page, effectiveConfig, ct) =>
            {
                var resume = ShouldResume(effectiveConfig, state);
                if (resume)
                {
                    await NavigateToExistingSeriesAsync(page, data.Title, state, progress, ct);
                }
                else
                {
                    state.CurrentStage = "first_page";
                    await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct, job.Platform);
                    await _firstPageService.FillAndSaveDraftAsync(page, data, effectiveConfig, progress, ct);
                }

                if (!string.Equals(effectiveConfig.FirstPageAction, "next", StringComparison.OrdinalIgnoreCase))
                {
                    CaptureMiniSeriesId(page, state);
                    state.Status = "draft_saved";
                    state.CurrentStage = "draft_saved";
                    await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct, job.Platform);
                    return;
                }

                state.CurrentStage = state.VideosUploaded ? "final_action" : "episode_upload";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct, job.Platform);
                await _episodeUploadService.UploadAsync(
                    page,
                    data,
                    effectiveConfig,
                    progress,
                    resume,
                    state.VideosUploaded,
                    stage => RecordStageAsync(page, data.WorkflowDirectory, state, stage, ct, job.Platform),
                    ct);
                CaptureMiniSeriesId(page, state);
                state.Status = "completed";
                state.CurrentStage = state.ReviewSubmitted ? "review_submitted" : "videos_uploaded";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct, job.Platform);
                if (state.ReviewSubmitted)
                {
                    var onlineItem = _onlineQueueStore.Register(job, data, state, effectiveConfig);
                    if (onlineItem is not null)
                        progress?.Report($"{label}：已加入当前账号的自动上架队列，等待审核通过。 ");
                    else if (effectiveConfig.AutoOnlineEnabled || effectiveConfig.StepOnlineSeries)
                        progress?.Report($"{label}：未取得短剧 ID，无法加入自动上架队列，请在视频管理中补录。 ");
                }
                if (effectiveConfig.OnlineAutoDistributionEnabled &&
                    (effectiveConfig.AutoOnlineEnabled || effectiveConfig.StepOnlineSeries))
                    progress?.Report($"{label}：分销已设置为上架成功后执行。 ");
                else
                    await _distributionService.ApplyAsync(state.MiniSeriesId, effectiveConfig, progress, ct);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Status = "failed";
            state.LastError = ex.Message;
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, CancellationToken.None, job.Platform);
            throw;
        }
    }

    private async Task RecordStageAsync(
        IPage page,
        string workflowDirectory,
        KuaishouPersonalUploadState state,
        string stage,
        CancellationToken cancellationToken,
        PublishPlatform platform)
    {
        CaptureMiniSeriesId(page, state);
        state.CurrentStage = stage;
        state.EpisodeInfoCompleted |= stage is "episode_info_completed" or "first_page_completed" or "videos_uploaded" or "review_submitted";
        state.FirstPageCompleted |= stage is "first_page_completed" or "videos_uploaded" or "review_submitted";
        state.VideosUploaded |= stage is "videos_uploaded" or "review_submitted";
        state.ReviewSubmitted |= stage == "review_submitted";
        await _stateStore.SaveAsync(workflowDirectory, state, cancellationToken, platform);
    }

    private static bool ShouldResume(KuaishouPersonalConfig config, KuaishouPersonalUploadState state)
    {
        if (string.Equals(config.RunMode, "create", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(config.RunMode, "edit", StringComparison.OrdinalIgnoreCase)) return true;
        return state.FirstPageCompleted;
    }

    private static async Task NavigateToExistingSeriesAsync(
        IPage page,
        string title,
        KuaishouPersonalUploadState state,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(state.MiniSeriesId))
        {
            progress?.Report($"快手分账个人版：从已保存的剧集 {state.MiniSeriesId} 继续视频步骤。 ");
            await page.GotoAsync(BuildEditUrl(state.MiniSeriesId), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            });
            return;
        }

        progress?.Report($"快手分账个人版：状态中没有短剧 ID，正在按剧名《{title}》查找已有剧集。 ");
        var opened = await SearchAndOpenEditAsync(page, title, cancellationToken);
        if (!opened)
            throw new InvalidOperationException($"runMode=edit，但内容管理列表中未找到《{title}》，为避免重复创建已停止任务。");
        CaptureMiniSeriesId(page, state);
        progress?.Report(string.IsNullOrWhiteSpace(state.MiniSeriesId)
            ? $"快手分账个人版：已按剧名打开《{title}》编辑页。 "
            : $"快手分账个人版：已找到《{title}》，短剧 ID：{state.MiniSeriesId}。 ");
    }

    private static async Task<bool> SearchAndOpenEditAsync(
        IPage page,
        string title,
        CancellationToken cancellationToken)
    {
        if (await SearchAndOpenEditInPageAsync(page, title, cancellationToken)) return true;
        foreach (var frame in page.Frames.Where(frame => frame != page.MainFrame))
            if (await SearchAndOpenEditInFrameAsync(frame, title, cancellationToken)) return true;
        return false;
    }

    private static async Task<bool> SearchAndOpenEditInPageAsync(
        IPage page,
        string title,
        CancellationToken cancellationToken)
    {
        var input = page.Locator("input[placeholder*='短剧名称'], input[placeholder*='剧名'], input[placeholder*='请输入']").First;
        if (!await FillSearchInputAsync(input, title)) return false;
        if (!await ClickSearchAsync(page.GetByText("查询", new PageGetByTextOptions { Exact = true }),
                                    page.GetByText("搜索", new PageGetByTextOptions { Exact = true }))) return false;
        await page.WaitForTimeoutAsync(1000);
        cancellationToken.ThrowIfCancellationRequested();
        var row = page.Locator("tr, [class*=table-row], [class*=list-item]")
            .Filter(new LocatorFilterOptions { HasTextString = title }).First;
        return await ClickEditInRowAsync(row, page);
    }

    private static async Task<bool> SearchAndOpenEditInFrameAsync(
        IFrame frame,
        string title,
        CancellationToken cancellationToken)
    {
        var input = frame.Locator("input[placeholder*='短剧名称'], input[placeholder*='剧名'], input[placeholder*='请输入']").First;
        if (!await FillSearchInputAsync(input, title)) return false;
        if (!await ClickSearchAsync(frame.GetByText("查询", new FrameGetByTextOptions { Exact = true }),
                                    frame.GetByText("搜索", new FrameGetByTextOptions { Exact = true }))) return false;
        await frame.Page.WaitForTimeoutAsync(1000);
        cancellationToken.ThrowIfCancellationRequested();
        var row = frame.Locator("tr, [class*=table-row], [class*=list-item]")
            .Filter(new LocatorFilterOptions { HasTextString = title }).First;
        return await ClickEditInRowAsync(row, frame.Page);
    }

    private static async Task<bool> FillSearchInputAsync(ILocator input, string title)
    {
        if (await input.CountAsync() == 0 || !await input.IsVisibleAsync()) return false;
        await input.FillAsync(title);
        return string.Equals(await input.InputValueAsync(), title, StringComparison.Ordinal);
    }

    private static async Task<bool> ClickSearchAsync(params ILocator[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (await candidate.CountAsync() == 0 || !await candidate.First.IsVisibleAsync()) continue;
            await candidate.First.ClickAsync();
            return true;
        }
        return false;
    }

    private static async Task<bool> ClickEditInRowAsync(ILocator row, IPage page)
    {
        if (await row.CountAsync() == 0 || !await row.IsVisibleAsync()) return false;
        var edit = row.GetByText("编辑", new LocatorGetByTextOptions { Exact = true }).Last;
        if (await edit.CountAsync() == 0 || !await edit.IsVisibleAsync()) return false;
        await edit.ClickAsync();
        try
        {
            await page.WaitForURLAsync(url => url.Contains("content-management/edit", StringComparison.OrdinalIgnoreCase),
                new PageWaitForURLOptions { Timeout = 20_000 });
        }
        catch (TimeoutException)
        {
            return false;
        }
        return true;
    }

    private static string BuildEditUrl(string miniSeriesId) =>
        $"https://kdj.kuaishou.com/home/content/content-management/edit?miniSeriesId={Uri.EscapeDataString(miniSeriesId)}&step=1";

    private static void CaptureMiniSeriesId(IPage page, KuaishouPersonalUploadState state)
    {
        var match = Regex.Match(page.Url, @"[?&]miniSeriesId=([^&#]+)", RegexOptions.IgnoreCase);
        if (match.Success) state.MiniSeriesId = Uri.UnescapeDataString(match.Groups[1].Value);
    }
}
