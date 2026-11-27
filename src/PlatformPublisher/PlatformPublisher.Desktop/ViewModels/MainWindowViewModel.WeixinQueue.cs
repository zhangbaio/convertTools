using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    [ObservableProperty] private bool _showOnlyWeixinPendingUpload;
    private string? _savedWeixinWorkDirectory;

    internal bool HasWeixinJobs(string accountId) => _jobs.Any(job =>
        job.Platform == PublishPlatform.WeixinChannel && string.Equals(job.AccountId, accountId, StringComparison.OrdinalIgnoreCase));

    public string WeixinStepsSummary => $"执行步骤 · 已选 {new[]
    {
        PipelineDownloadEnabled, PipelineSmartRecutEnabled, PipelineRewriteEnabled, PipelinePosterEnabled,
        PipelineTranscodeEnabled, PipelineAutoRepairEnabled, PipelineAutoFillEnabled, PipelineCostReportEnabled,
        PipelineProjectImageEnabled, PipelineGenerateAiProofEnabled, PipelineStoryboardRecordingEnabled,
        PipelineGenerateTimestampCertificateEnabled, PipelineMaterialValidateEnabled,
    }.Count(value => value)} 项";

    public string WeixinQueueSummary =>
        $"显示 {VisibleJobs.Count} 个项目 · 勾选 {VisibleJobs.Count(row => row.IsChecked)} 个 · 待上传 {VisibleJobs.Count(row => row.IsReadyForWeixinUpload)} 个";
    public bool HasWeixinQueueResults => VisibleJobs.Count > 0;

    partial void OnShowOnlyWeixinPendingUploadChanged(bool value) => RefreshVisibleJobs(SelectedJob?.Id);

    private void RefreshWeixinQueueIndicators()
    {
        if (!IsWeixinPlatform) return;
        OnPropertyChanged(nameof(WeixinQueueSummary));
        OnPropertyChanged(nameof(HasWeixinQueueResults));
        RunSharedPipelineCommand.NotifyCanExecuteChanged();
        RunCheckedCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void EnableAllWeixinSteps() => SetWeixinSteps(true);
    [RelayCommand] private void DisableAllWeixinSteps() => SetWeixinSteps(false);

    private void SetWeixinSteps(bool enabled)
    {
        if (IsBusy) return;
        _applyingWorkflowSettings = true;
        try
        {
            PipelineDownloadEnabled = enabled;
            PipelineSmartRecutEnabled = enabled;
            PipelineRewriteEnabled = enabled;
            PipelinePosterEnabled = enabled;
            PipelineTranscodeEnabled = enabled;
            PipelineAutoRepairEnabled = enabled;
            PipelineAutoFillEnabled = enabled;
            PipelineCostReportEnabled = enabled;
            PipelineProjectImageEnabled = enabled;
            PipelineGenerateAiProofEnabled = enabled;
            PipelineStoryboardRecordingEnabled = enabled;
            PipelineGenerateTimestampCertificateEnabled = enabled;
            PipelineMaterialValidateEnabled = enabled;
        }
        finally { _applyingWorkflowSettings = false; }
        RememberWorkflowSteps();
    }
}
