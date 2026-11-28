using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task<string> CheckKuaishouDraftAsync(Func<KuaishouDraftInspection, Task<bool>>? confirm = null)
    {
        if (!IsKuaishouPlatform || _activeGlobalAccount is null)
            return "请先选择快手分账账号。";
        if (IsBusy || IsKuaishouAccountRunning(CurrentKuaishouRunKey()))
            return "当前有任务在执行，请稍后再检查草稿。";
        var checkedRows = VisibleJobs.Where(row => row.IsChecked).ToArray();
        var row = checkedRows.Length switch { 0 => SelectedJob, 1 => checkedRows[0], _ => null };
        if (row is null || row.Model.Platform != SelectedPlatform.Value ||
            !string.Equals(row.Model.AccountId, _activeGlobalAccount.Id, StringComparison.OrdinalIgnoreCase))
            return "请选中当前快手账号下的一部剧。";

        var job = row.Model;
        var summary = "已停止检查草稿。";
        try
        {
            await RunBusyAsync(async cancellationToken =>
            {
                var runLock = _kuaishouProjectRunLocks.GetOrAdd(Path.GetFullPath(job.ProjectDirectory), _ => new SemaphoreSlim(1, 1));
                if (!await runLock.WaitAsync(0, cancellationToken))
                    throw new InvalidOperationException("这部剧正在执行，请稍后再检查草稿。");
                try
                {
                    var check = await _kuaishouUploadService.CheckBoundDraftAsync(job, cancellationToken);
                    var inspection = check.Inspection;
                    summary = inspection.Describe();
                    if (confirm is null || !(inspection.AlreadySubmitted || inspection.CanSubmit))
                        return;
                    if (inspection.CanSubmit && !await confirm(inspection))
                    {
                        summary = "已取消提审。";
                        return;
                    }

                    var submitted = await _kuaishouUploadService.CompleteBoundDraftReviewAsync(
                        job, check, submit: inspection.CanSubmit, cancellationToken);
                    summary = submitted
                        ? $"《{inspection.Title}》（{inspection.SeriesId}）已提交审核。"
                        : $"《{inspection.Title}》（{inspection.SeriesId}）已同步平台审核状态。";
                    await MarkBoundDraftReviewedAsync(job, row, summary);
                }
                finally
                {
                    runLock.Release();
                }
            });
        }
        catch (OperationCanceledException)
        {
            summary = "已停止。再次操作前可检查线上审核状态。";
        }
        catch (Exception ex)
        {
            summary = ex.Message;
        }

        StatusMessage = summary;
        AppendActivityLog(summary, job.ProjectName);
        return summary;
    }

    private async Task CheckKuaishouDraftBeforeRetryAsync(
        PublishJob job, PublishJobRowViewModel? row, CancellationToken cancellationToken)
    {
        var state = LoadKuaishouUploadState(job.ProjectDirectory, job.Platform);
        if (state is not { FirstPageCompleted: true, EpisodeInfoCompleted: true, ReviewSubmitted: false, ReplaceExistingVideos: false } ||
            !state.HasBoundDedupBatch() || state.VideoBatchEpisodeCount <= 0)
            return;

        await RecordHistoricalDedupAsync(job, row);
        KuaishouBoundDraftCheck check;
        try
        {
            check = await _kuaishouUploadService.CheckBoundDraftAsync(job, cancellationToken);
            if (check.Inspection.AlreadySubmitted)
            {
                await _kuaishouUploadService.CompleteBoundDraftReviewAsync(job, check, submit: false, cancellationToken);
                await MarkBoundDraftReviewedAsync(job, row, check.Inspection.Describe());
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordDraftRetryIssueAsync(job, row, hold: false, ex.Message);
            throw;
        }

        var inspection = check.Inspection;
        if (inspection.CanSubmit || inspection.EpisodeLayoutMatches && inspection.ProcessingEpisodes.Count > 0)
        {
            var detail = inspection.Describe() + (inspection.CanSubmit
                ? " 请使用「仅提交审核」。" : " 请在视频处理完成后重新检查草稿。");
            await RecordDraftRetryIssueAsync(job, row, hold: true, detail);
            throw new KuaishouDraftOnHoldException(detail);
        }

        if (inspection.IsPending && inspection.EpisodeLayoutMatches && HasOriginalDraftBatch(job, check))
        {
            AppendActivityLog(inspection.Describe(), job.ProjectName);
            return;
        }

        var error = inspection.Describe();
        if (inspection.IsPending && inspection.EpisodeLayoutMatches)
            error += " 本地缺少原去重批次，请恢复原批次后补传缺集。";
        else if (inspection.IsPending)
            error += " 请核对原草稿和绑定批次。";
        await RecordDraftRetryIssueAsync(job, row, hold: false, error);
        throw new InvalidOperationException(error);
    }

    private static bool HasOriginalDraftBatch(PublishJob job, KuaishouBoundDraftCheck check)
    {
        var workflow = KuaishouArtifactColumnStatusReader.ResolveDirectories(job.ProjectDirectory).WorkflowDirectory;
        try
        {
            var batch = KuaishouSettlementEpisodeVideoService.ReadCurrentDedupBatch(workflow, job.AccountId);
            return batch is not null && batch.Identity == check.BatchIdentity &&
                batch.Paths.Count == check.Inspection.ExpectedEpisodes;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task RecordHistoricalDedupAsync(PublishJob job, PublishJobRowViewModel? row)
    {
        if (job.StepStates.GetValueOrDefault(KuaishouVideoDedup.Step)?.Status != PublishJobStepStatus.Succeeded)
            await UpdateStepStateAsync(job, row, KuaishouVideoDedup.Step, KuaishouVideoDedup.Label,
                PublishJobStepStatus.Succeeded, "历史去重已完成");
    }

    private async Task MarkBoundDraftReviewedAsync(PublishJob job, PublishJobRowViewModel? row, string message)
    {
        await RecordHistoricalDedupAsync(job, row);
        job.Status = PublishJobStatus.Succeeded;
        job.StatusMessage = message;
        job.LastCompletedAt = DateTimeOffset.Now;
        await UpdateStepStateAsync(job, row, "weixin-upload", "上传剧集", PublishJobStepStatus.Succeeded, message);
        SetKuaishouLogTone(job.ProjectDirectory, "ok");
    }

    private async Task RecordDraftRetryIssueAsync(PublishJob job, PublishJobRowViewModel? row, bool hold, string message)
    {
        job.Status = hold ? PublishJobStatus.Blocked : PublishJobStatus.Failed;
        job.StatusMessage = message;
        if (!hold)
            job.LastCompletedAt = DateTimeOffset.Now;
        await UpdateStepStateAsync(job, row, "weixin-upload", "上传剧集",
            hold ? PublishJobStepStatus.Pending : PublishJobStepStatus.Failed,
            hold ? PublishJobRowViewModel.DraftReviewPendingText : message);
    }

    private sealed class KuaishouDraftOnHoldException(string message) : Exception(message)
    {
    }
}
