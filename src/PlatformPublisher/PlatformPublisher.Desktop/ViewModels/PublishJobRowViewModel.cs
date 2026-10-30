using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class PublishJobRowViewModel : ObservableObject
{
    public PublishJobRowViewModel(PublishJob model, bool loadArtifacts = true)
    {
        Model = model;
        _isChecked = model.IsChecked;
        _tableInfo = ResolveTableInfo(model);
        if (loadArtifacts)
            ReloadKuaishouArtifacts();
        OpenSourceDirectoryCommand = new RelayCommand(() => OpenSourceDirectory());
        OpenWorkflowDirectoryCommand = new RelayCommand(() => OpenWorkflowDirectory());
    }

    public PublishJob Model { get; }
    private ProjectTableInfo _tableInfo;
    private KuaishouArtifactColumnStatus? _kuaishou;
    public string Id => Model.Id;
    public PublishPlatform Platform => Model.Platform;
    public string PlatformName => Model.Platform.DisplayName();
    public string KindName => Model.Kind.DisplayName();
    public string ProjectName => Model.ProjectName;
    public string ProjectDirectory => Model.ProjectDirectory;
    public string OriginalTitle => FirstNonEmpty(_kuaishou?.OriginalTitle, _tableInfo.OriginalTitle);
    public string NewTitle => FirstNonEmpty(_kuaishou?.NewTitle, _tableInfo.NewTitle);
    public string SourceDirectory => _tableInfo.SourceDirectory;
    public string WorkflowDirectory => _tableInfo.WorkflowDirectory;
    public IRelayCommand OpenSourceDirectoryCommand { get; }
    public IRelayCommand OpenWorkflowDirectoryCommand { get; }
    public string SourceText => _tableInfo.Source;
    public string EpisodeCountText
    {
        get
        {
            var count = _kuaishou?.EpisodeCount ?? _tableInfo.EpisodeCount;
            return count > 0 ? count.ToString() : "-";
        }
    }
    public string CreatedAtText => Model.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    [ObservableProperty] private int _queueIndex;
    public string AccountName => string.IsNullOrWhiteSpace(Model.AccountName) ? "默认账号" : Model.AccountName;
    public string StatusText
    {
        get
        {
            if (_kuaishou is null) return ModelStatusText();
            if (Model.Status == PublishJobStatus.Running) return "执行中";
            if (Model.Status == PublishJobStatus.Failed) return "失败";
            if (!string.IsNullOrWhiteSpace(_kuaishou.OverallStatus) && _kuaishou.OverallStatus != "待执行")
                return _kuaishou.OverallStatus;
            if (Model.Status == PublishJobStatus.Succeeded) return "已完成";
            return string.IsNullOrWhiteSpace(_kuaishou.OverallStatus) ? "待执行" : _kuaishou.OverallStatus;
        }
    }
    public string StatusMessage => Model.StatusMessage;
    public string ScheduleText => Model.ScheduledAt is { } value
        ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "立即/手动";
    public string AttemptSummary => Model.AttemptCount == 0
        ? "未执行"
        : $"{Model.AttemptCount} 次";
    public string DownloadStepStatus => KuaishouStep(_kuaishou?.DownloadStatus, "download");
    public string SmartRecutStepStatus => StepStatus("smart-recut");
    public string RewriteStepStatus => KuaishouStep(_kuaishou?.RewriteStatus, "rewrite");
    public string PosterStepStatus => KuaishouStep(_kuaishou?.PosterStatus, "poster-rename");
    public string TranscodeStepStatus => StepStatus("transcode");
    public string RepairStepStatus => StepStatus("material-auto-repair");
    public string AutoFillStepStatus => KuaishouStep(_kuaishou?.AutoFillStatus, "auto-fill-info");
    public string CostReportStepStatus => StepStatus("cost-report");
    public string ProjectImageStepStatus => KuaishouStep(_kuaishou?.ProjectImageStatus, "project-image");
    public string ValidateStepStatus => StepStatus("material-validate");
    public string AiProofStepStatus => StepStatus("ai-proof");
    public string TimestampStepStatus => StepStatus("timestamp-certificate");
    public string UploadStepStatus => KuaishouStep(_kuaishou?.UploadStatus, "weixin-upload");
    public IBrush DownloadStepBrush => ForegroundBrush(DownloadStepStatus);
    public IBrush RewriteStepBrush => ForegroundBrush(RewriteStepStatus);
    public IBrush PosterStepBrush => ForegroundBrush(PosterStepStatus);
    public IBrush ProjectImageStepBrush => ForegroundBrush(ProjectImageStepStatus);
    public IBrush AutoFillStepBrush => ForegroundBrush(AutoFillStepStatus);
    public IBrush UploadStepBrush => ForegroundBrush(UploadStepStatus);
    public IBrush StatusBrushValue => ForegroundBrush(StatusText);
    public IBrush DownloadStepBackgroundBrush => BackgroundBrush(DownloadStepStatus);
    public IBrush RewriteStepBackgroundBrush => BackgroundBrush(RewriteStepStatus);
    public IBrush PosterStepBackgroundBrush => BackgroundBrush(PosterStepStatus);
    public IBrush ProjectImageStepBackgroundBrush => BackgroundBrush(ProjectImageStepStatus);
    public IBrush AutoFillStepBackgroundBrush => BackgroundBrush(AutoFillStepStatus);
    public IBrush UploadStepBackgroundBrush => BackgroundBrush(UploadStepStatus);
    public IBrush StatusBackgroundBrush => BackgroundBrush(StatusText);
    public IBrush DownloadStepBorderBrush => BorderBrush(DownloadStepStatus);
    public IBrush RewriteStepBorderBrush => BorderBrush(RewriteStepStatus);
    public IBrush PosterStepBorderBrush => BorderBrush(PosterStepStatus);
    public IBrush ProjectImageStepBorderBrush => BorderBrush(ProjectImageStepStatus);
    public IBrush AutoFillStepBorderBrush => BorderBrush(AutoFillStepStatus);
    public IBrush UploadStepBorderBrush => BorderBrush(UploadStepStatus);
    public IBrush StatusBorderBrush => BorderBrush(StatusText);
    public IBrush DramaTitleBrush
    {
        get
        {
            if (HasStepTone(StepTone.Failed)) return FailedBrush;
            if (ToneOf(StatusText) == StepTone.Running || ToneOf(UploadStepStatus) == StepTone.Running) return RunningBrush;
            if (ToneOf(UploadStepStatus) == StepTone.Completed) return CompletedBrush;
            return LinkBrush;
        }
    }
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

    private string ModelStatusText() => Model.Status switch
    {
        PublishJobStatus.Pending => "等待执行",
        PublishJobStatus.Running => "执行中",
        PublishJobStatus.Succeeded => "已完成",
        PublishJobStatus.Failed => "失败",
        PublishJobStatus.Blocked => "待接入",
        _ => Model.Status.ToString(),
    };

    private void ReloadKuaishouArtifacts()
    {
        if (Model.Platform is not (PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue)
            || string.IsNullOrWhiteSpace(Model.ProjectDirectory)
            || !Directory.Exists(Model.ProjectDirectory))
        {
            _kuaishou = null;
            return;
        }

        try
        {
            _kuaishou = KuaishouArtifactColumnStatusReader.Read(
                Model.ProjectDirectory,
                Model.Platform == PublishPlatform.KuaishouEnterpriseRevenue);
        }
        catch
        {
            _kuaishou = null;
        }
    }

    private string KuaishouStep(string? artifactStatus, string key)
    {
        if (_kuaishou is null) return StepStatus(key);
        if (Model.StepStates.TryGetValue(key, out var running) && running.Status == PublishJobStepStatus.Running)
            return string.Equals(running.Message, "等待槽位", StringComparison.Ordinal) ? "等待槽位" : "进行中";
        if (!string.IsNullOrWhiteSpace(artifactStatus) && artifactStatus != "未执行")
            return artifactStatus;
        if (!Model.StepStates.TryGetValue(key, out var state)) return artifactStatus ?? "未执行";
        return state.Status switch
        {
            PublishJobStepStatus.Succeeded => "已完成",
            PublishJobStepStatus.Failed => "失败",
            PublishJobStepStatus.Skipped => "已跳过",
            PublishJobStepStatus.Running => "进行中",
            _ => artifactStatus ?? "未执行",
        };
    }

    private bool HasStepTone(StepTone tone) =>
        ToneOf(StatusText) == tone ||
        ToneOf(DownloadStepStatus) == tone ||
        ToneOf(RewriteStepStatus) == tone ||
        ToneOf(PosterStepStatus) == tone ||
        ToneOf(ProjectImageStepStatus) == tone ||
        ToneOf(AutoFillStepStatus) == tone ||
        ToneOf(UploadStepStatus) == tone;

    private static IBrush ForegroundBrush(string status) => ToneOf(status) switch
    {
        StepTone.Completed => CompletedBrush,
        StepTone.Pending => PendingBrush,
        StepTone.Running => RunningBrush,
        StepTone.Failed => FailedBrush,
        StepTone.Stopped => StoppedBrush,
        StepTone.Held => HeldBrush,
        _ => DefaultBrush,
    };

    private static IBrush BackgroundBrush(string status) => ToneOf(status) switch
    {
        StepTone.Completed => CompletedBackgroundBrush,
        StepTone.Pending => PendingBackgroundBrush,
        StepTone.Running => RunningBackgroundBrush,
        StepTone.Failed => FailedBackgroundBrush,
        StepTone.Stopped => StoppedBackgroundBrush,
        StepTone.Held => HeldBackgroundBrush,
        _ => DefaultBackgroundBrush,
    };

    private static IBrush BorderBrush(string status) => ToneOf(status) switch
    {
        StepTone.Completed => CompletedBorderBrush,
        StepTone.Pending => PendingBorderBrush,
        StepTone.Running => RunningBorderBrush,
        StepTone.Failed => FailedBorderBrush,
        StepTone.Stopped => StoppedBorderBrush,
        StepTone.Held => HeldBorderBrush,
        _ => DefaultBorderBrush,
    };

    private static StepTone ToneOf(string status)
    {
        var text = status.Trim();
        if (text.StartsWith("已完成", StringComparison.Ordinal) || text is "完成" or "成功") return StepTone.Completed;
        if (text.StartsWith("进行中", StringComparison.Ordinal) || text is "执行中" or "上传中" or "发布中" or "等待槽位") return StepTone.Running;
        if (text is "失败" or "异常" or "错误" || text.Contains("失败", StringComparison.Ordinal)) return StepTone.Failed;
        if (text is "已停止" or "停止中" or "已跳过") return StepTone.Stopped;
        if (text is "手工介入" || text.StartsWith("部分完成", StringComparison.Ordinal) || text is "已保持" or "已保存草稿") return StepTone.Held;
        if (text is "未执行" or "待执行" or "等待执行") return StepTone.Pending;
        return StepTone.Default;
    }

    private enum StepTone
    {
        Completed,
        Pending,
        Running,
        Failed,
        Stopped,
        Held,
        Default,
    }

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

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }

    public string OpenSourceDirectory() => OpenResolvedDirectory(workflow: false);

    public string OpenWorkflowDirectory() => OpenResolvedDirectory(workflow: true);

    private string OpenResolvedDirectory(bool workflow)
    {
        _tableInfo = ResolveTableInfo(Model);
        OnPropertyChanged(nameof(SourceDirectory));
        OnPropertyChanged(nameof(WorkflowDirectory));
        if (workflow &&
            (string.IsNullOrWhiteSpace(WorkflowDirectory) ||
             (!string.IsNullOrWhiteSpace(SourceDirectory) &&
              string.Equals(
                  Path.GetFullPath(WorkflowDirectory),
                  Path.GetFullPath(SourceDirectory),
                  StringComparison.OrdinalIgnoreCase))))
            return $"未找到新剧名目录：{NewTitle}";
        return OpenDirectory(workflow ? WorkflowDirectory : SourceDirectory, workflow ? "新剧名" : "原剧名");
    }

    private static string OpenDirectory(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return string.IsNullOrWhiteSpace(path) ? $"未找到{label}目录" : $"未找到{label}目录：{path}";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return $"已打开{label}目录：{path}";
        }
        catch (Exception ex)
        {
            return $"打开{label}目录失败：{ex.Message}";
        }
    }

    private static ProjectTableInfo ResolveTableInfo(PublishJob model)
    {
        var original = model.ProjectName;
        var renamed = model.ProjectName;
        var source = model.Kind == PublishJobKind.Series ? "本地" : model.Kind.DisplayName();
        var episodeCount = 0;
        var sourceDirectory = model.ProjectDirectory;
        var workflowDirectory = model.ProjectDirectory;
        try
        {
            if (model.Platform is PublishPlatform.KuaishouPersonalRevenue or PublishPlatform.KuaishouEnterpriseRevenue
                && Directory.Exists(model.ProjectDirectory))
            {
                var directories = KuaishouArtifactColumnStatusReader.ResolveDirectories(model.ProjectDirectory);
                sourceDirectory = directories.SourceDirectory;
                workflowDirectory = directories.WorkflowDirectory;
            }

            var selected = sourceDirectory;
            var metadataPath = Path.Combine(selected, "shortdrama-project.json");
            if (File.Exists(metadataPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                var root = document.RootElement;
                original = JsonText(root, "originalTitle", "title", "sourceName") ?? original;
                source = JsonText(root, "sourceType", "queueEntrySource") ?? source;
                if (root.TryGetProperty("episodeCount", out var count) && count.TryGetInt32(out var parsed)) episodeCount = parsed;
                var workflow = JsonText(root, "workflowProjectDir");
                if (!string.IsNullOrWhiteSpace(workflow) && Directory.Exists(workflow))
                {
                    workflowDirectory = Path.GetFullPath(workflow);
                    selected = workflowDirectory;
                }
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
        return new ProjectTableInfo(original, renamed, source, episodeCount, sourceDirectory, workflowDirectory);
    }

    private static string? JsonText(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        return null;
    }

    public void ApplyKuaishouArtifacts(KuaishouArtifactColumnStatus? artifacts)
    {
        _kuaishou = artifacts;
        Refresh(reloadArtifacts: false);
    }

    public void Refresh(bool reloadArtifacts = true)
    {
        if (reloadArtifacts)
        {
            _tableInfo = ResolveTableInfo(Model);
            ReloadKuaishouArtifacts();
        }
        OnPropertyChanged(nameof(OriginalTitle));
        OnPropertyChanged(nameof(NewTitle));
        OnPropertyChanged(nameof(SourceDirectory));
        OnPropertyChanged(nameof(WorkflowDirectory));
        OnPropertyChanged(nameof(EpisodeCountText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ScheduleText));
        OnPropertyChanged(nameof(AttemptSummary));
        OnPropertyChanged(nameof(DownloadStepStatus));
        OnPropertyChanged(nameof(DownloadStepBrush));
        OnPropertyChanged(nameof(RewriteStepBrush));
        OnPropertyChanged(nameof(PosterStepBrush));
        OnPropertyChanged(nameof(ProjectImageStepBrush));
        OnPropertyChanged(nameof(AutoFillStepBrush));
        OnPropertyChanged(nameof(UploadStepBrush));
        OnPropertyChanged(nameof(StatusBrushValue));
        OnPropertyChanged(nameof(DownloadStepBackgroundBrush));
        OnPropertyChanged(nameof(RewriteStepBackgroundBrush));
        OnPropertyChanged(nameof(PosterStepBackgroundBrush));
        OnPropertyChanged(nameof(ProjectImageStepBackgroundBrush));
        OnPropertyChanged(nameof(AutoFillStepBackgroundBrush));
        OnPropertyChanged(nameof(UploadStepBackgroundBrush));
        OnPropertyChanged(nameof(StatusBackgroundBrush));
        OnPropertyChanged(nameof(DownloadStepBorderBrush));
        OnPropertyChanged(nameof(RewriteStepBorderBrush));
        OnPropertyChanged(nameof(PosterStepBorderBrush));
        OnPropertyChanged(nameof(ProjectImageStepBorderBrush));
        OnPropertyChanged(nameof(AutoFillStepBorderBrush));
        OnPropertyChanged(nameof(UploadStepBorderBrush));
        OnPropertyChanged(nameof(StatusBorderBrush));
        OnPropertyChanged(nameof(DramaTitleBrush));
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

    private sealed record ProjectTableInfo(
        string OriginalTitle,
        string NewTitle,
        string Source,
        int EpisodeCount,
        string SourceDirectory,
        string WorkflowDirectory);

    private static readonly IBrush CompletedBrush = new SolidColorBrush(Color.Parse("#047857"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#B45309"));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#075BC7"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#B42318"));
    private static readonly IBrush StoppedBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush HeldBrush = new SolidColorBrush(Color.Parse("#C2410C"));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#334155"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#0F63C7"));
    private static readonly IBrush CompletedBackgroundBrush = new SolidColorBrush(Color.Parse("#DDFBEA"));
    private static readonly IBrush PendingBackgroundBrush = new SolidColorBrush(Color.Parse("#FFF2CC"));
    private static readonly IBrush RunningBackgroundBrush = new SolidColorBrush(Color.Parse("#DDEBFF"));
    private static readonly IBrush FailedBackgroundBrush = new SolidColorBrush(Color.Parse("#FFE3E3"));
    private static readonly IBrush StoppedBackgroundBrush = new SolidColorBrush(Color.Parse("#EEF2F6"));
    private static readonly IBrush HeldBackgroundBrush = new SolidColorBrush(Color.Parse("#FFEDD5"));
    private static readonly IBrush DefaultBackgroundBrush = new SolidColorBrush(Color.Parse("#F1F5F9"));
    private static readonly IBrush CompletedBorderBrush = new SolidColorBrush(Color.Parse("#67E8A5"));
    private static readonly IBrush PendingBorderBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush RunningBorderBrush = new SolidColorBrush(Color.Parse("#86B7FF"));
    private static readonly IBrush FailedBorderBrush = new SolidColorBrush(Color.Parse("#F97066"));
    private static readonly IBrush StoppedBorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1"));
    private static readonly IBrush HeldBorderBrush = new SolidColorBrush(Color.Parse("#FB923C"));
    private static readonly IBrush DefaultBorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1"));
}
