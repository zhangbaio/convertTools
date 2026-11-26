using System.Text.Json;
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
    private readonly KuaishouOnlineQueueStore _onlineQueueStore;

    public KuaishouPersonalUploadService(
        KuaishouPersonalSessionService sessionService,
        KuaishouPersonalProjectDataService projectDataService,
        KuaishouPersonalFirstPageService firstPageService,
        KuaishouPersonalEpisodeUploadService episodeUploadService,
        KuaishouPersonalUploadStateStore stateStore,
        KuaishouCommitmentService commitmentService,
        KuaishouOnlineQueueStore onlineQueueStore)
    {
        _sessionService = sessionService;
        _projectDataService = projectDataService;
        _firstPageService = firstPageService;
        _episodeUploadService = episodeUploadService;
        _stateStore = stateStore;
        _commitmentService = commitmentService;
        _onlineQueueStore = onlineQueueStore;
    }

    public async Task RunAsync(
        PublishJob job,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var account = KuaishouDailyUploadLimit.UseAccount(job.AccountId);
        var config = KuaishouPersonalConfig.Load(job);
        var apiTransport = KuaishouUploadTransport.IsApi(config.UploadTransport);
        var boundId = "";
        var continueReplace = !apiTransport && TryBoundReplaceSeries(job, out boundId);
        if (apiTransport || continueReplace)
        {
            if (continueReplace)
                progress?.Report($"{job.Platform.DisplayName()}：这部已绑定短剧 {boundId}，继续用接口替换，不打开浏览器。");
            await ApiUpload().RunAsync(job, progress, cancellationToken);
            return;
        }

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
        if (data.VideoPaths.Count == 0) throw new InvalidOperationException($"{label}项目没有可上传的剧集视频。");
        var state = _stateStore.Load(data.WorkflowDirectory, job.Platform);
        if (config.ForceRerun)
        {
            state.BindVideoBatch(videoBatch);
            state = new KuaishouPersonalUploadState();
            progress?.Report($"{label}：已按配置忽略旧状态并从头执行。 ");
        }
        if (state.Status == "completed" && state.ReviewSubmitted)
        {
            progress?.Report($"{label}：项目已经完成，跳过重复执行。 ");
            return;
        }
        state.BindVideoBatch(videoBatch);
        if (!state.ReviewSubmitted && state.VideosUploaded)
        {
            state.VideosUploaded = false;
            progress?.Report($"{label}：上次未完成提交审核，不能把视频视为已全部上传，将继续上传。 ");
        }
        progress?.Report($"{label}项目解析完成：《{data.Title}》，视频 {data.VideoPaths.Count} 集，工程图 {data.ProjectImagePaths.Count} 张。 ");
        if (data.VideoPaths.Count > 0)
            progress?.Report($"{label}：本次上传视频目录 {Path.GetDirectoryName(data.VideoPaths[0])}。 ");
        state.Status = "running";
        state.CurrentStage = "opening_browser";
        state.LastError = string.Empty;
        await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken, job.Platform);
        try
        {
            await _sessionService.ExecuteUploadAsync(job, async (page, effectiveConfig, ct) =>
            {
                var resume = ShouldResume(effectiveConfig, state) &&
                    (string.IsNullOrEmpty(data.VideoBatchIdentity) || !string.IsNullOrWhiteSpace(state.MiniSeriesId));
                if (resume)
                {
                    var opened = await NavigateToExistingSeriesAsync(page, data.Title, state, progress, ct);
                    if (!opened)
                    {
                        progress?.Report($"{label}：内容管理页未搜索到《{data.Title}》，回退到新建上传。 ");
                        state.MiniSeriesId = string.Empty;
                        state.FirstPageCompleted = false;
                        state.EpisodeInfoCompleted = false;
                        resume = false;
                    }
                }
                if (!resume)
                {
                    KuaishouDailyUploadLimit.ThrowIfNoted(job.AccountId);
                    state.CurrentStage = "first_page";
                    await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct, job.Platform);
                    var seriesId = await _firstPageService.FillAndSaveDraftAsync(
                        page, data, effectiveConfig, state.MiniSeriesId, progress, ct);
                    state.MiniSeriesId = seriesId;
                    state.EpisodeInfoCompleted = true;
                    await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct, job.Platform);
                }

                state.CurrentStage = state.VideosUploaded && state.ReviewSubmitted ? "final_action" : "episode_upload";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct, job.Platform);
                if (!resume && !string.IsNullOrWhiteSpace(state.MiniSeriesId))
                {
                    progress?.Report($"{label}：正在打开视频上传。");
                    await page.GotoAsync(BuildEditUrl(state.MiniSeriesId), new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60_000,
                    });
                }
                await _episodeUploadService.UploadAsync(
                    page,
                    data,
                    effectiveConfig,
                    progress,
                    resume || !string.IsNullOrWhiteSpace(state.MiniSeriesId),
                    state.VideosUploaded && state.ReviewSubmitted,
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
            }, cancellationToken, progress);
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
            if (!KuaishouDailyUploadLimit.HasStartedUpload(state))
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

    public async Task<KuaishouDraftReplacePreview> PreviewReplaceAsync(
        PublishJob job,
        string seriesId,
        CancellationToken cancellationToken)
    {
        if (!KuaishouDraftReplace.TryParseSeriesId(seriesId, out var id))
            throw new InvalidOperationException("短剧 ID 只能是数字。");
        var config = KuaishouPersonalConfig.Load(job);
        if (string.IsNullOrWhiteSpace(config.AuthStatePath) || !File.Exists(config.AuthStatePath))
            throw new InvalidOperationException("请先打开经营者管理平台登录。");
        var data = await _projectDataService.ResolveForUploadAsync(job, config, cancellationToken);
        if (data.VideoPaths.Count == 0)
            throw new InvalidOperationException("这部本地剧没有可上传的视频。");
        var state = _stateStore.Load(data.WorkflowDirectory, job.Platform);
        var binding = KuaishouDraftReplace.BindingMessage(state.MiniSeriesId, id);
        if (binding.Length > 0)
            throw new InvalidOperationException(binding);
        using var session = KuaishouApiSession.Open(config.AuthStatePath);
        var listed = await session.PostJsonAsync(KuaishouShelfApi.ListUrl, KuaishouShelfApi.StatusBody(id), cancellationToken);
        using var document = JsonDocument.Parse(listed);
        if (!KuaishouDraftReplace.TryRead(document.RootElement, id, out var target))
            throw new InvalidOperationException($"内容管理没有短剧 {id}。");
        var refusal = KuaishouDraftReplace.Refusal(target);
        if (refusal.Length > 0)
            throw new InvalidOperationException(refusal);
        await KuaishouDraftReplace.EnsureEpisodeCountAsync(session, id, data.VideoPaths.Count, cancellationToken);
        return new KuaishouDraftReplacePreview(id, target.Title, data.Title, data.VideoPaths.Count);
    }

    public Task ReplaceDraftAsync(
        PublishJob job,
        string seriesId,
        string platformTitle,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        ApiUpload().ReplaceDraftAsync(job, seriesId, platformTitle, progress, cancellationToken);

    private KuaishouApiUploadService ApiUpload() =>
        new(_projectDataService, _stateStore, _commitmentService, _onlineQueueStore);

    private bool TryBoundReplaceSeries(PublishJob job, out string seriesId)
    {
        seriesId = "";
        if (string.IsNullOrWhiteSpace(job.ProjectDirectory))
            return false;
        var workflow = KuaishouArtifactColumnStatusReader.ResolveDirectories(job.ProjectDirectory).WorkflowDirectory;
        if (string.IsNullOrWhiteSpace(workflow) || !Directory.Exists(workflow))
            workflow = job.ProjectDirectory;
        var state = _stateStore.Load(workflow, job.Platform);
        if (!state.ReplaceExistingVideos || string.IsNullOrWhiteSpace(state.MiniSeriesId))
            return false;
        seriesId = state.MiniSeriesId.Trim();
        return true;
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

    private static async Task<bool> NavigateToExistingSeriesAsync(
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
            return true;
        }

        progress?.Report($"快手分账个人版：状态中没有短剧 ID，正在按剧名《{title}》查找已有剧集。 ");
        var dismissed = await KuaishouPageNotice.DismissAsync(page, cancellationToken);
        if (dismissed > 0)
            progress?.Report("快手分账个人版：已关闭首页短剧下架通知，继续查找剧集。 ");
        var opened = await SearchAndOpenEditAsync(page, title, cancellationToken);
        if (!opened)
            return false;
        CaptureMiniSeriesId(page, state);
        progress?.Report(string.IsNullOrWhiteSpace(state.MiniSeriesId)
            ? $"快手分账个人版：已按剧名打开《{title}》编辑页。 "
            : $"快手分账个人版：已找到《{title}》，短剧 ID：{state.MiniSeriesId}。 ");
        return true;
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

    private static string BuildEditUrl(string miniSeriesId, int step = 1) =>
        $"https://kdj.kuaishou.com/home/content/content-management/edit?miniSeriesId={Uri.EscapeDataString(miniSeriesId)}&step={step}";

    private static void CaptureMiniSeriesId(IPage page, KuaishouPersonalUploadState state)
    {
        var match = Regex.Match(page.Url, @"[?&]miniSeriesId=([^&#]+)", RegexOptions.IgnoreCase);
        if (match.Success) state.MiniSeriesId = Uri.UnescapeDataString(match.Groups[1].Value);
    }

    public async Task<IReadOnlyList<KuaishouVideoRepairNote>> RepairFailedVideosAsync(
        PublishJob job,
        IReadOnlyList<string> titles,
        Func<string, IReadOnlyList<int>, CancellationToken, Task<KuaishouVideoRepairFiles>> resolveFiles,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<KuaishouVideoRepairNote>();
        await _sessionService.ExecuteAuthenticatedAsync(job, async (page, config, token) =>
        {
            foreach (var title in titles)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var failure = await _episodeUploadService.ReadSeriesFailureAsync(page, title, token);
                    if (failure is null)
                    {
                        notes.Add(Note(title, KuaishouVideoRepairOutcome.Skipped, $"《{title}》在平台上没有同名短剧，未新建。"));
                        continue;
                    }

                    if (failure.FailedEpisodes.Count == 0)
                    {
                        notes.Add(Note(title, KuaishouVideoRepairOutcome.Skipped, $"《{title}》没有需要补传的视频。"));
                        continue;
                    }

                    var files = await resolveFiles(title, failure.FailedEpisodes, token);
                    using var videoLease = files.Lease;
                    if (files.ShouldSkip)
                    {
                        notes.Add(Note(title, KuaishouVideoRepairOutcome.Skipped, $"《{title}》{files.SkipReason}"));
                        continue;
                    }

                    var uploadEpisodes = files.UploadEpisodes is { Count: > 0 }
                        ? files.UploadEpisodes
                        : failure.FailedEpisodes;
                    await _episodeUploadService.RepairFailedEpisodesAsync(
                        page, failure with { FailedEpisodes = uploadEpisodes }, files.VideoPaths, config, progress, token);
                    var skipped = failure.FailedEpisodes.Where(episode => !uploadEpisodes.Contains(episode)).ToArray();
                    var uploadedText = $"《{title}》已补传{KuaishouFailedEpisodeRepair.FormatEpisodes(uploadEpisodes)}，并提交审核。";
                    if (skipped.Length > 0)
                        uploadedText += $"{KuaishouFailedEpisodeRepair.FormatEpisodes(skipped)}没有 FFmpeg 支持的编码，已跳过。请更换剧集或更换下载链路。";
                    notes.Add(Note(title, KuaishouVideoRepairOutcome.Uploaded, uploadedText));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    notes.Add(Note(title, KuaishouVideoRepairOutcome.Failed, $"《{title}》补传失败：{ex.Message}"));
                }
            }
        }, cancellationToken);

        return notes;
    }

    private static KuaishouVideoRepairNote Note(string title, KuaishouVideoRepairOutcome outcome, string message) =>
        new(title, outcome, message);
}
