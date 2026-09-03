using CommunityToolkit.Mvvm.ComponentModel;
using PlatformPublisher.Common.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class PublishJobRowViewModel : ObservableObject
{
    public PublishJobRowViewModel(PublishJob model)
    {
        Model = model;
        _isChecked = model.IsChecked;
        _tableInfo = ResolveTableInfo(model);
    }

    public PublishJob Model { get; }
    private readonly ProjectTableInfo _tableInfo;
    public string Id => Model.Id;
    public PublishPlatform Platform => Model.Platform;
    public string PlatformName => Model.Platform.DisplayName();
    public string KindName => Model.Kind.DisplayName();
    public string ProjectName => Model.ProjectName;
    public string ProjectDirectory => Model.ProjectDirectory;
    public string OriginalTitle => _tableInfo.OriginalTitle;
    public string NewTitle => _tableInfo.NewTitle;
    public string SourceText => _tableInfo.Source;
    public string EpisodeCountText => _tableInfo.EpisodeCount > 0 ? _tableInfo.EpisodeCount.ToString() : "-";
    public string CreatedAtText => Model.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
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

    private static ProjectTableInfo ResolveTableInfo(PublishJob model)
    {
        var original = model.ProjectName;
        var renamed = model.ProjectName;
        var source = model.Kind == PublishJobKind.Series ? "本地" : model.Kind.DisplayName();
        var episodeCount = 0;
        try
        {
            var selected = model.ProjectDirectory;
            var metadataPath = Path.Combine(selected, "shortdrama-project.json");
            if (File.Exists(metadataPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                var root = document.RootElement;
                original = JsonText(root, "originalTitle", "title", "sourceName") ?? original;
                source = JsonText(root, "sourceType", "queueEntrySource") ?? source;
                if (root.TryGetProperty("episodeCount", out var count) && count.TryGetInt32(out var parsed)) episodeCount = parsed;
                var workflow = JsonText(root, "workflowProjectDir");
                if (!string.IsNullOrWhiteSpace(workflow) && Directory.Exists(workflow)) selected = workflow;
            }
            var infoPath = Path.Combine(selected, "短剧信息.txt");
            if (File.Exists(infoPath))
            {
                foreach (var line in File.ReadLines(infoPath))
                {
                    var index = line.IndexOfAny([':', '：']);
                    if (index <= 0) continue;
                    var key = line[..index].Trim();
                    var value = line[(index + 1)..].Trim();
                    if (key == "原剧名" && !string.IsNullOrWhiteSpace(value)) original = value;
                    if (key == "新剧名" && !string.IsNullOrWhiteSpace(value)) renamed = value;
                    if (key == "集数")
                    {
                        var match = Regex.Match(value, @"\d+");
                        if (match.Success) episodeCount = int.Parse(match.Value);
                    }
                }
            }
        }
        catch
        {
            // 表格摘要读取失败时使用任务自身字段，不影响队列执行。
        }
        return new ProjectTableInfo(original, renamed, source, episodeCount);
    }

    private static string? JsonText(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        return null;
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

    private sealed record ProjectTableInfo(string OriginalTitle, string NewTitle, string Source, int EpisodeCount);
}
