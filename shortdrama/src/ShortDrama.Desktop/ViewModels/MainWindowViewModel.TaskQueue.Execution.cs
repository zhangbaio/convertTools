using CommunityToolkit.Mvvm.ComponentModel;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private const string QueueStepMaterialTranscodeKey = "transcode";
    private const string QueueStepMaterialAutoRepairKey = "__material-auto-repair__";
    private const string QueueStepAutoFillInfoKey = "__auto-fill-info__";
    private const string QueueStepMaterialValidateKey = "__material-validate__";
    private const string QueueStepUploadRemuxKey = "__upload-remux__";

    [ObservableProperty]
    private bool queueStepDownloadEnabled = true;

    [ObservableProperty]
    private bool queueStepRewriteEnabled = true;

    [ObservableProperty]
    private bool queueStepPosterRenameEnabled = true;

    [ObservableProperty]
    private bool queueStepMaterialTranscodeEnabled = true;

    [ObservableProperty]
    private bool queueStepMaterialAutoRepairEnabled = true;

    [ObservableProperty]
    private bool queueStepAutoFillInfoEnabled = true;

    [ObservableProperty]
    private bool queueStepCostReportEnabled = true;

    [ObservableProperty]
    private bool queueStepProjectImageEnabled = true;

    [ObservableProperty]
    private bool queueStepMaterialValidateEnabled = true;

    [ObservableProperty]
    private bool queueStepUploadRemuxEnabled;

    [ObservableProperty]
    private bool queueStepEpisodeUploadEnabled;

    [ObservableProperty]
    private bool queueStepMaterialUploadEnabled;

    [ObservableProperty]
    private bool queueSyncManagementOnUploadSuccessEnabled;

    [ObservableProperty]
    private bool queueAutoArchiveAfterUploadEnabled;

    [ObservableProperty]
    private bool queueForceRerunCompletedStepsEnabled;

    [ObservableProperty]
    private bool queuePreferUploadWhenReadyEnabled = true;

    partial void OnQueueStepDownloadEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepRewriteEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepPosterRenameEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepMaterialTranscodeEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepMaterialAutoRepairEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepAutoFillInfoEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepCostReportEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepProjectImageEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepMaterialValidateEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepUploadRemuxEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepEpisodeUploadEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepMaterialUploadEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueSyncManagementOnUploadSuccessEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueAutoArchiveAfterUploadEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueForceRerunCompletedStepsEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueuePreferUploadWhenReadyEnabledChanged(bool value) => RefreshQueueStepSelectionState();

    private void RefreshQueueStepSelectionState()
    {
        OnPropertyChanged(nameof(TaskQueueSummary));
        RefreshCommandStates();
    }

    private (string Key, string Label)[] GetTaskQueueSelectedSteps()
    {
        var steps = new List<(string Key, string Label)>();

        if (QueueStepDownloadEnabled)
        {
            steps.Add(("download", "下载剧集"));
        }

        if (QueueStepRewriteEnabled)
        {
            steps.Add(("rewrite", "改写信息"));
        }

        if (QueueStepPosterRenameEnabled)
        {
            steps.Add(("poster-rename", "生成海报"));
        }

        if (QueueStepMaterialTranscodeEnabled)
        {
            steps.Add((QueueStepMaterialTranscodeKey, "素材转码"));
        }

        if (QueueStepMaterialAutoRepairEnabled)
        {
            steps.Add((QueueStepMaterialAutoRepairKey, "一键修复"));
        }

        if (QueueStepAutoFillInfoEnabled)
        {
            steps.Add((QueueStepAutoFillInfoKey, "补齐字段"));
        }

        if (QueueStepCostReportEnabled)
        {
            steps.Add(("cost-report", "生成成本报表"));
        }

        if (QueueStepProjectImageEnabled)
        {
            steps.Add(("project-image", "生成工程图"));
        }

        if (QueueStepMaterialValidateEnabled)
        {
            steps.Add((QueueStepMaterialValidateKey, "素材校验"));
        }

        if (QueueStepUploadRemuxEnabled)
        {
            steps.Add((QueueStepUploadRemuxKey, "无损重封装"));
        }

        if (QueueStepEpisodeUploadEnabled)
        {
            steps.Add(("weixin-upload", "上传剧集"));
        }

        if (QueueStepMaterialUploadEnabled)
        {
            steps.Add(("weixin-material-upload", "素材上传"));
        }

        return steps.ToArray();
    }

    private bool HasAnyTaskQueueStepSelected()
    {
        return QueueStepDownloadEnabled ||
               QueueStepRewriteEnabled ||
               QueueStepPosterRenameEnabled ||
               QueueStepMaterialTranscodeEnabled ||
               QueueStepMaterialAutoRepairEnabled ||
               QueueStepAutoFillInfoEnabled ||
               QueueStepCostReportEnabled ||
               QueueStepProjectImageEnabled ||
               QueueStepMaterialValidateEnabled ||
               QueueStepUploadRemuxEnabled ||
               QueueStepEpisodeUploadEnabled ||
               QueueStepMaterialUploadEnabled;
    }

    public void SetAllQueueStepsEnabled(bool isEnabled)
    {
        QueueStepDownloadEnabled = isEnabled;
        QueueStepRewriteEnabled = isEnabled;
        QueueStepPosterRenameEnabled = isEnabled;
        QueueStepMaterialTranscodeEnabled = isEnabled;
        QueueStepMaterialAutoRepairEnabled = isEnabled;
        QueueStepAutoFillInfoEnabled = isEnabled;
        QueueStepCostReportEnabled = isEnabled;
        QueueStepProjectImageEnabled = isEnabled;
        QueueStepMaterialValidateEnabled = isEnabled;
        QueueStepUploadRemuxEnabled = isEnabled;
        QueueStepEpisodeUploadEnabled = isEnabled;
        QueueStepMaterialUploadEnabled = isEnabled;
    }
}
