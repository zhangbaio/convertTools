using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Weixin.Publishing;
using TikTokPublisher.Core.Drama;
using TikTokPublisher.Core.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool ShowWeixinTitleUpload => _jobScope == PublishJobScope.Weixin;

    internal sealed record WeixinTitleUploadTarget(
        string AccountId, string AccountName, string Workspace, string SessionDirectory,
        string SourceLabel, WeixinPreparationOptions Preparation);

    internal sealed record WeixinPreparationOptions(
        ClientSettings RuntimeSettings, IReadOnlyList<(string Key, string Label)> Steps,
        bool AutoRepair, bool AutoFill, bool Validate, bool Force,
        int SmartRecutEpisodeCount, int SmartRecutMinSeconds, int SmartRecutMaxSeconds,
        StoryboardRecordingOptions Storyboard)
    {
        public string DescribeSteps()
        {
            var labels = Steps.Select(step => step.Label).ToList();
            if (AutoRepair) labels.Add("修复");
            if (AutoFill) labels.Add("补字段");
            if (Validate) labels.Add("校验");
            return string.Join("、", labels);
        }
    }

    internal WeixinTitleUploadTarget CaptureWeixinTitleUploadTarget()
    {
        if (!ShowWeixinTitleUpload || _confirmEmbeddedWeixinQueue is null)
            throw new InvalidOperationException("请在视频号任务队列使用上传短剧。");
        if (IsBusy || _canStartEmbeddedWeixinWork?.Invoke() == false)
            throw new InvalidOperationException("有其他视频号任务正在运行，请等任务结束后再操作。");
        if (_activeGlobalAccount is not { } account || !HasActiveWeixinAccount)
            throw new InvalidOperationException("请先在左侧选择视频号账号。");
        if (!Directory.Exists(DraftProjectDirectory))
            throw new InvalidOperationException("请在当前视频号的账号配置中设置有效的工作目录。");

        var workspace = NormalizeTitleUploadDirectory(DraftProjectDirectory);
        var otherAccounts = _globalAccounts.Where(other => !string.Equals(other.Id, account.Id, StringComparison.OrdinalIgnoreCase));
        if (otherAccounts.Any(other =>
        {
            var directory = ChannelsPublisher.Core.Services.WeixinWorkspaceAccounts.WorkDirectory(other, _globalAccounts);
            return !string.IsNullOrWhiteSpace(directory) &&
                string.Equals(NormalizeTitleUploadDirectory(directory), workspace, StringComparison.OrdinalIgnoreCase);
        }))
            throw new InvalidOperationException("工作目录已被其他视频号账号使用。请为当前账号设置独立的工作目录。");

        var settings = _dramaSettings.LoadClientSettings().Clone();
        var source = DramaSourceConfigurationValidator.Check(settings);
        if (!source.IsConfigured)
            throw new InvalidOperationException(source.Message);

        return new WeixinTitleUploadTarget(account.Id, ActiveWeixinAccountName, workspace, account.ProfileDir,
            VisibleDramaSource.Label(source.Source), CaptureWeixinPreparationOptions(settings, titleUpload: true));
    }

    internal async Task<string> UploadWeixinTitlesAsync(WeixinTitleUploadTarget target, string titles, string matchMode)
    {
        if (!IsCurrentTitleUploadTarget(target))
            return StatusMessage = "账号或工作目录已切换，请重新打开上传短剧。";
        if (IsBusy || _canStartEmbeddedWeixinWork?.Invoke() == false)
            return StatusMessage = "有其他视频号任务正在运行，请等任务结束后再操作。";

        await RunBusyAsync(async ct =>
        {
            var imported = new List<PublishJob>();
            DramaTitleImportOutcome? outcome = null;
            string? stopReason = null;
            try
            {
                var seriesJobs = _jobs.Where(job => job.Platform == PublishPlatform.WeixinChannel &&
                    job.Kind == PublishJobKind.Series && !string.IsNullOrWhiteSpace(job.ProjectDirectory)).ToArray();
                var own = seriesJobs.Where(job => string.Equals(job.AccountId, target.AccountId, StringComparison.OrdinalIgnoreCase))
                    .Select(job => NormalizeTitleUploadDirectory(job.ProjectDirectory)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var other = seriesJobs.Where(job => !string.Equals(job.AccountId, target.AccountId, StringComparison.OrdinalIgnoreCase))
                    .Select(job => NormalizeTitleUploadDirectory(job.ProjectDirectory)).ToHashSet(StringComparer.OrdinalIgnoreCase);

                StatusMessage = "正在按剧名搜索视频号短剧…";
                outcome = await _dramaTitleImportService.ImportForWeixinAsync(target.Workspace, titles, matchMode,
                    target.Preparation.RuntimeSettings, own, other, AppendActivityLog, ct);
                await AddImportedProjectJobsAsync(outcome.ProjectDirectories, target.AccountId, target.AccountName,
                    target.SessionDirectory, PublishPlatform.WeixinChannel, batchJobs: imported);

                if (outcome.Cancelled)
                    ct.ThrowIfCancellationRequested();

                if (imported.Count > 0)
                {
                    var batchJobs = imported.ToDictionary(job => job.ProjectDirectory, StringComparer.OrdinalIgnoreCase);
                    var prepared = await RunWeixinPreparationAsync(batchJobs.Keys.ToArray(), target.Preparation, target.AccountName, ct, batchJobs);
                    ct.ThrowIfCancellationRequested();
                    stopReason = await ConfirmAndUploadWeixinTitlesAsync(target, imported, prepared, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stopReason = "已停止。未完成的任务保留在队列中。";
            }
            catch (WeixinSeriesApiQuotaExceededException ex)
            {
                stopReason = $"视频号提审额度已用完，本批上传已停止。{ex.Message}";
            }
            catch (Exception ex)
            {
                stopReason = $"本批操作未完成：{ex.Message}";
            }

            var ready = imported.Count(job => job.StepStates.GetValueOrDefault("material-validate")?.Status == PublishJobStepStatus.Succeeded);
            var preparationFailures = imported.Count(job => job.Status == PublishJobStatus.Failed &&
                job.StepStates.GetValueOrDefault("material-validate")?.Status != PublishJobStepStatus.Succeeded);
            var uploaded = imported.Count(job => job.Status == PublishJobStatus.Succeeded);
            var uploadFailures = imported.Count(job => job.Status == PublishJobStatus.Failed) - preparationFailures;
            var pending = imported.Count - uploaded - preparationFailures - uploadFailures;
            var summary = $"请求 {outcome?.RequestedCount ?? 0} 部 · 新增 {imported.Count} 部 · 重复 {outcome?.Duplicates.Count ?? 0} 部 · 未加入 {outcome?.Failures.Count ?? 0} 部\n"
                + $"校验通过 {ready} 部 · 准备失败 {preparationFailures} 部 · 上传完成 {uploaded} 部 · 上传失败 {uploadFailures} 部 · 待继续 {pending} 部";
            if (!string.IsNullOrWhiteSpace(stopReason))
                summary += $"\n{stopReason}";
            var issues = outcome?.Failures.Take(6).Select(item => $"{item.Title} · {item.Reason}").ToArray();
            if (issues is { Length: > 0 })
                summary += "\n" + string.Join("\n", issues);
            StatusMessage = summary;
            AppendActivityLog(summary);
        });
        return StatusMessage;
    }

    private async Task<string?> ConfirmAndUploadWeixinTitlesAsync(WeixinTitleUploadTarget target,
        IReadOnlyList<PublishJob> imported, IReadOnlySet<string> prepared, CancellationToken ct)
    {
        var readyRows = imported.Where(job => prepared.Contains(job.ProjectDirectory))
            .Select(RowForJob).Where(row => row.IsReadyForWeixinUpload).ToArray();
        if (readyRows.Length == 0)
            return null;
        if (!IsCurrentTitleUploadTarget(target))
            return "账号或工作目录已切换。已校验的剧集保留在原账号队列中。";
        if (_confirmEmbeddedWeixinQueue is null || readyRows.Any(row => !BindEmbeddedWeixinJobAccount(row.Model)))
            return "当前账号的上传会话不可用，已校验的剧集保留在队列中。";
        var confirmed = await ExecuteQueueRowsAsync(readyRows, clearSchedule: true, cancellationToken: ct, storyboardPrepared: true);
        ct.ThrowIfCancellationRequested();
        return confirmed
            ? null
            : "已取消上传。通过校验的剧集保留在队列中。";
    }

    private bool IsCurrentTitleUploadTarget(WeixinTitleUploadTarget target) =>
        ShowWeixinTitleUpload && string.Equals(_activeWeixinAccountId, target.AccountId, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(DraftProjectDirectory) &&
        string.Equals(NormalizeTitleUploadDirectory(DraftProjectDirectory), target.Workspace, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTitleUploadDirectory(string directory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private WeixinPreparationOptions CaptureWeixinPreparationOptions(ClientSettings runtime, bool titleUpload = false)
    {
        var steps = new List<(string Key, string Label)>();
        if (titleUpload || PipelineDownloadEnabled) steps.Add(("download", "下载"));
        if (PipelineSmartRecutEnabled) steps.Add(("smart-recut", "重剪"));
        if (PipelineRewriteEnabled) steps.Add(("rewrite", "改写"));
        if (PipelinePosterEnabled) steps.Add(("poster-rename", "海报"));
        if (PipelineTranscodeEnabled && !PipelineSmartRecutEnabled) steps.Add(("transcode", "转码"));
        if (PipelineCostReportEnabled) steps.Add(("cost-report", "成本表"));
        if (PipelineProjectImageEnabled) steps.Add(("project-image", "工程图"));
        if (PipelineGenerateAiProofEnabled) steps.Add(("ai-proof", "AI 证明"));
        if (PipelineStoryboardRecordingEnabled) steps.Add((WeixinStoryboardRecordingService.StepKey, WeixinStoryboardRecordingService.StepLabel));
        if (PipelineGenerateTimestampCertificateEnabled) steps.Add(("timestamp-certificate", "时间戳"));

        var force = !titleUpload && PipelineForceRerun;
        return new WeixinPreparationOptions(runtime, steps, PipelineAutoRepairEnabled, PipelineAutoFillEnabled,
            titleUpload || PipelineMaterialValidateEnabled, force, SmartRecutEpisodeCount, SmartRecutMinSeconds,
            SmartRecutMaxSeconds, GetStoryboardOptions() with { Force = force });
    }
}
