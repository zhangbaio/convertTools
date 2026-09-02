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

    public KuaishouPersonalUploadService(
        KuaishouPersonalSessionService sessionService,
        KuaishouPersonalProjectDataService projectDataService,
        KuaishouPersonalFirstPageService firstPageService,
        KuaishouPersonalEpisodeUploadService episodeUploadService,
        KuaishouPersonalUploadStateStore stateStore)
    {
        _sessionService = sessionService;
        _projectDataService = projectDataService;
        _firstPageService = firstPageService;
        _episodeUploadService = episodeUploadService;
        _stateStore = stateStore;
    }

    public async Task RunAsync(
        PublishJob job,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var config = KuaishouPersonalConfig.Load(job);
        var data = await _projectDataService.ResolveAsync(job.ProjectDirectory, config, cancellationToken);
        if (data.VideoPaths.Count == 0) throw new InvalidOperationException("快手个人版项目没有可上传的剧集视频。");
        var state = _stateStore.Load(data.WorkflowDirectory);
        if (config.ForceRerun)
        {
            state = new KuaishouPersonalUploadState();
            progress?.Report("快手分账个人版：已按配置忽略旧状态并从头执行。 ");
        }
        if (state.Status == "completed" &&
            (!string.Equals(config.FinalAction, "submit_review", StringComparison.OrdinalIgnoreCase) || state.ReviewSubmitted))
        {
            progress?.Report("快手分账个人版：项目已经完成，跳过重复执行。 ");
            return;
        }
        progress?.Report($"快手个人版项目解析完成：《{data.Title}》，视频 {data.VideoPaths.Count} 集，工程图 {data.ProjectImagePaths.Count} 张。 ");
        state.Status = "running";
        state.CurrentStage = "opening_browser";
        state.LastError = string.Empty;
        await _stateStore.SaveAsync(data.WorkflowDirectory, state, cancellationToken);
        try
        {
            await _sessionService.ExecuteAuthenticatedAsync(job, async (page, effectiveConfig, ct) =>
            {
                var resume = ShouldResume(effectiveConfig, state);
                if (resume)
                {
                    var editUrl = BuildEditUrl(state.MiniSeriesId);
                    progress?.Report($"快手分账个人版：从已保存的剧集 {state.MiniSeriesId} 继续视频步骤。 ");
                    await page.GotoAsync(editUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60_000,
                    });
                }
                else
                {
                    state.CurrentStage = "first_page";
                    await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct);
                    await _firstPageService.FillAndSaveDraftAsync(page, data, effectiveConfig, progress, ct);
                }

                if (!string.Equals(effectiveConfig.FirstPageAction, "next", StringComparison.OrdinalIgnoreCase))
                {
                    CaptureMiniSeriesId(page, state);
                    state.Status = "draft_saved";
                    state.CurrentStage = "draft_saved";
                    await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct);
                    return;
                }

                state.CurrentStage = state.VideosUploaded ? "final_action" : "episode_upload";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct);
                await _episodeUploadService.UploadAsync(
                    page,
                    data,
                    effectiveConfig,
                    progress,
                    resume,
                    state.VideosUploaded,
                    stage => RecordStageAsync(page, data.WorkflowDirectory, state, stage, ct),
                    ct);
                CaptureMiniSeriesId(page, state);
                state.Status = "completed";
                state.CurrentStage = state.ReviewSubmitted ? "review_submitted" : "videos_uploaded";
                await _stateStore.SaveAsync(data.WorkflowDirectory, state, ct);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Status = "failed";
            state.LastError = ex.Message;
            await _stateStore.SaveAsync(data.WorkflowDirectory, state, CancellationToken.None);
            throw;
        }
    }

    private async Task RecordStageAsync(
        IPage page,
        string workflowDirectory,
        KuaishouPersonalUploadState state,
        string stage,
        CancellationToken cancellationToken)
    {
        CaptureMiniSeriesId(page, state);
        state.CurrentStage = stage;
        state.EpisodeInfoCompleted |= stage is "episode_info_completed" or "first_page_completed" or "videos_uploaded" or "review_submitted";
        state.FirstPageCompleted |= stage is "first_page_completed" or "videos_uploaded" or "review_submitted";
        state.VideosUploaded |= stage is "videos_uploaded" or "review_submitted";
        state.ReviewSubmitted |= stage == "review_submitted";
        await _stateStore.SaveAsync(workflowDirectory, state, cancellationToken);
    }

    private static bool ShouldResume(KuaishouPersonalConfig config, KuaishouPersonalUploadState state)
    {
        if (string.Equals(config.RunMode, "create", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(config.RunMode, "edit", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(state.MiniSeriesId))
            throw new InvalidOperationException("runMode=edit，但状态文件中没有 miniSeriesId，无法打开编辑页。");
        if (string.Equals(config.RunMode, "edit", StringComparison.OrdinalIgnoreCase)) return true;
        return state.FirstPageCompleted && !string.IsNullOrWhiteSpace(state.MiniSeriesId);
    }

    private static string BuildEditUrl(string miniSeriesId) =>
        $"https://kdj.kuaishou.com/home/content/content-management/edit?miniSeriesId={Uri.EscapeDataString(miniSeriesId)}&step=1";

    private static void CaptureMiniSeriesId(IPage page, KuaishouPersonalUploadState state)
    {
        var match = Regex.Match(page.Url, @"[?&]miniSeriesId=([^&#]+)", RegexOptions.IgnoreCase);
        if (match.Success) state.MiniSeriesId = Uri.UnescapeDataString(match.Groups[1].Value);
    }
}
