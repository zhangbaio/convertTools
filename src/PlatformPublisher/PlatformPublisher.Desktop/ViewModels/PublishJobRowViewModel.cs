using CommunityToolkit.Mvvm.ComponentModel;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class PublishJobRowViewModel : ObservableObject
{
    public PublishJobRowViewModel(PublishJob model)
    {
        Model = model;
        _isChecked = model.IsChecked;
    }

    public PublishJob Model { get; }
    public string Id => Model.Id;
    public PublishPlatform Platform => Model.Platform;
    public string PlatformName => Model.Platform.DisplayName();
    public string KindName => Model.Kind.DisplayName();
    public string ProjectName => Model.ProjectName;
    public string ProjectDirectory => Model.ProjectDirectory;
    public string AccountName => string.IsNullOrWhiteSpace(Model.AccountName) ? "默认账号" : Model.AccountName;
    public string StatusText => Model.Status switch
    {
        PublishJobStatus.Pending => "等待执行",
        PublishJobStatus.Running => "执行中",
        PublishJobStatus.Succeeded => "已完成",
        PublishJobStatus.Failed => "失败",
        PublishJobStatus.Blocked => "待接入",
        _ => Model.Status.ToString(),
    };
    public string StatusMessage => Model.StatusMessage;
    public string ScheduleText => Model.ScheduledAt is { } value
        ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "立即/手动";
    public string AttemptSummary => Model.AttemptCount == 0
        ? "未执行"
        : $"{Model.AttemptCount} 次";
    public string DownloadStepStatus => StepStatus("download");
    public string SmartRecutStepStatus => StepStatus("smart-recut");
    public string RewriteStepStatus => StepStatus("rewrite");
    public string PosterStepStatus => StepStatus("poster-rename");
    public string TranscodeStepStatus => StepStatus("transcode");
    public string RepairStepStatus => StepStatus("material-auto-repair");
    public string AutoFillStepStatus => StepStatus("auto-fill-info");
    public string CostReportStepStatus => StepStatus("cost-report");
    public string ProjectImageStepStatus => StepStatus("project-image");
    public string ValidateStepStatus => StepStatus("material-validate");
    public string AiProofStepStatus => StepStatus("ai-proof");
    public string TimestampStepStatus => StepStatus("timestamp-certificate");
    public string UploadStepStatus => StepStatus("weixin-upload");
    public string ShelfStepStatus => StepStatus("shelf");
    public string ManagementSyncStepStatus => StepStatus("management-sync");
    public string StepProgressSummary
    {
        get
        {
            var states = Model.StepStates.Values.ToArray();
            if (states.Length == 0) return "未开始";
            var failed = states.FirstOrDefault(item => item.Status == PublishJobStepStatus.Failed);
            if (failed is not null) return $"失败：{failed.Label}";
            var done = states.Count(item => item.Status is PublishJobStepStatus.Succeeded or PublishJobStepStatus.Skipped);
            return $"{done}/{states.Length}";
        }
    }

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => Model.IsChecked = value;

    private string StepStatus(string key)
    {
        if (!Model.StepStates.TryGetValue(key, out var state)) return "待执行";
        return state.Status switch
        {
            PublishJobStepStatus.Running => "执行中",
            PublishJobStepStatus.Succeeded => "已完成",
            PublishJobStepStatus.Failed => "失败",
            PublishJobStepStatus.Skipped => "已跳过",
            _ => "待执行",
        };
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ScheduleText));
        OnPropertyChanged(nameof(AttemptSummary));
        OnPropertyChanged(nameof(DownloadStepStatus));
        OnPropertyChanged(nameof(SmartRecutStepStatus));
        OnPropertyChanged(nameof(RewriteStepStatus));
        OnPropertyChanged(nameof(PosterStepStatus));
        OnPropertyChanged(nameof(TranscodeStepStatus));
        OnPropertyChanged(nameof(RepairStepStatus));
        OnPropertyChanged(nameof(AutoFillStepStatus));
        OnPropertyChanged(nameof(CostReportStepStatus));
        OnPropertyChanged(nameof(ProjectImageStepStatus));
        OnPropertyChanged(nameof(ValidateStepStatus));
        OnPropertyChanged(nameof(AiProofStepStatus));
        OnPropertyChanged(nameof(TimestampStepStatus));
        OnPropertyChanged(nameof(UploadStepStatus));
        OnPropertyChanged(nameof(ShelfStepStatus));
        OnPropertyChanged(nameof(ManagementSyncStepStatus));
        OnPropertyChanged(nameof(StepProgressSummary));
    }
}
