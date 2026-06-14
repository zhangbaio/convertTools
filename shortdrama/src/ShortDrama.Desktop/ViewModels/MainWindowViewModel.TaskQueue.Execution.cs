using CommunityToolkit.Mvvm.ComponentModel;

namespace ShortDrama.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private bool queueStepDownloadEnabled = true;

    [ObservableProperty]
    private bool queueStepTranscodeEnabled = true;

    [ObservableProperty]
    private bool queueStepRewriteEnabled = true;

    [ObservableProperty]
    private bool queueStepPosterRenameEnabled = true;

    [ObservableProperty]
    private bool queueStepProjectImageEnabled = true;

    [ObservableProperty]
    private bool queueStepCostReportEnabled = true;

    [ObservableProperty]
    private bool queueStepBatchFileRenameEnabled = true;

    [ObservableProperty]
    private bool queueStepMaterialConvertEnabled = true;

    [ObservableProperty]
    private bool queueStepEpisodeUploadEnabled;

    [ObservableProperty]
    private bool queueStepMaterialUploadEnabled;

    partial void OnQueueStepDownloadEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepTranscodeEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepRewriteEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepPosterRenameEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepProjectImageEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepCostReportEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepBatchFileRenameEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepMaterialConvertEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepEpisodeUploadEnabledChanged(bool value) => RefreshQueueStepSelectionState();
    partial void OnQueueStepMaterialUploadEnabledChanged(bool value) => RefreshQueueStepSelectionState();

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

        if (QueueStepTranscodeEnabled)
        {
            steps.Add(("transcode", "视频转码"));
        }

        if (QueueStepRewriteEnabled)
        {
            steps.Add(("rewrite", "改写信息"));
        }

        if (QueueStepPosterRenameEnabled)
        {
            steps.Add(("poster-rename", "生成海报"));
        }

        if (QueueStepProjectImageEnabled)
        {
            steps.Add(("project-image", "生成工程图"));
        }

        if (QueueStepCostReportEnabled)
        {
            steps.Add(("cost-report", "生成成本报表"));
        }

        if (QueueStepBatchFileRenameEnabled)
        {
            steps.Add(("batch-file-rename", "重命名视频"));
        }

        if (QueueStepMaterialConvertEnabled)
        {
            steps.Add(("material-convert", "素材转码"));
        }

        if (QueueStepEpisodeUploadEnabled)
        {
            steps.Add(("weixin-upload", "剧集上传"));
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
               QueueStepTranscodeEnabled ||
               QueueStepRewriteEnabled ||
               QueueStepPosterRenameEnabled ||
               QueueStepProjectImageEnabled ||
               QueueStepCostReportEnabled ||
               QueueStepBatchFileRenameEnabled ||
               QueueStepMaterialConvertEnabled ||
               QueueStepEpisodeUploadEnabled ||
               QueueStepMaterialUploadEnabled;
    }
}
