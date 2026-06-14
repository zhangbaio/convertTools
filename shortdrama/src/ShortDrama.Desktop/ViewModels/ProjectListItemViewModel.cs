using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using ShortDrama.Core.Models;
using ShortDrama.Desktop.Models;
using ShortDrama.Infrastructure.Automation;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;

namespace ShortDrama.Desktop.ViewModels;

public partial class ProjectListItemViewModel : ViewModelBase
{
    private const double DownloadWeight = 35d;
    private const double TranscodeWeight = 35d;
    private const double UploadWeight = 30d;
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];
    private static readonly Regex DownloadProgressRegex = new(@"\[(\d+)/(\d+)\].*?(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex DownloadSpeedRegex = new(@"(\d+(?:\.\d+)?)\s*(KB|MB|GB)/s", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FractionProgressRegex = new(@"\b(\d+)/(\d+)\b", RegexOptions.Compiled);
    private static readonly string[] ProjectMaterialStepKeys = ["transcode", "rewrite", "poster-rename", "project-image", "cost-report", "batch-file-rename", "material-convert"];
    private readonly Dictionary<int, double> _downloadEpisodeProgress = [];
    private readonly Dictionary<int, string> _downloadEpisodeSpeeds = [];
    private readonly HashSet<int> _transcodedEpisodes = [];
    private readonly HashSet<int> _skippedEpisodeUploadEpisodes = [];
    private readonly HashSet<int> _manuallyCompletedEpisodeUploadEpisodes = [];
    private int _transcodeTotalEpisodes;
    private int _downloadExpectedEpisodes;
    private int _episodeUploadTotalEpisodes;
    private int _episodeUploadCompletedEpisodes;
    private int _episodeUploadActiveEpisode;
    private string _episodeUploadStageText = "等待开始";
    private string _episodeUploadSubmitStatusText = "未开始";
    private double _episodeUploadPercent;
    private double _materialUploadPercent;
    private string _transcodeProgressText = "未开始";
    private string _episodeUploadProgressText = "未开始";
    private string _materialUploadProgressText = "未开始";
    private string _materialUploadStrategySummary = "未配置";
    private string _materialUploadSelectionSummary = "未配置";

    public ProjectListItemViewModel(ScannedProject project)
    {
        ProjectKey = project.ProjectKey;
        SourceProjectDir = project.SourceProjectDir;
        OriginalTitle = project.SourceName;
        Refresh(project);
    }

    public string ProjectKey { get; }
    public string SourceProjectDir { get; }
    public string OriginalTitle { get; }
    public string NewTitle => DisplayName;
    public event EventHandler? CheckedChanged;
    public ObservableCollection<DownloadEpisodeItemViewModel> DownloadEpisodes { get; } = [];
    public ObservableCollection<EpisodeUploadItemViewModel> EpisodeUploadEpisodes { get; } = [];
    public ObservableCollection<MaterialPublishVideoItemViewModel> MaterialPublishVideos { get; } = [];

    [ObservableProperty]
    private string displayName = string.Empty;

    [ObservableProperty]
    private bool isChecked;

    [ObservableProperty]
    private string status = string.Empty;

    [ObservableProperty]
    private int videoCount;

    [ObservableProperty]
    private int completedSteps;

    [ObservableProperty]
    private int totalSteps;

    [ObservableProperty]
    private string? resumeFrom;

    [ObservableProperty]
    private string? failedStep;

    [ObservableProperty]
    private bool hasFailure;

    [ObservableProperty]
    private string? workflowProjectDir;

    [ObservableProperty]
    private string lastEvent = "等待任务";

    [ObservableProperty]
    private double downloadPercent;

    [ObservableProperty]
    private string downloadProgressText = "0%";

    [ObservableProperty]
    private double overallPercent;

    [ObservableProperty]
    private string overallProgressText = "0%";

    [ObservableProperty]
    private string currentStepLabel = "等待任务";

    [ObservableProperty]
    private string currentStepProgressText = "待开始";

    [ObservableProperty]
    private double currentStepProgressPercent;

    [ObservableProperty]
    private string schedulingStatus = "空闲";

    [ObservableProperty]
    private string materialSummary = "视频 0 / 海报 无 / 工程图 0 / 报表 无";

    [ObservableProperty]
    private string dramaInfoSummary = "未知";

    [ObservableProperty]
    private string sourceSummary = "本地项目";

    [ObservableProperty]
    private string episodeCountText = "-";

    [ObservableProperty]
    private string videoSizeSummary = "-";

    [ObservableProperty]
    private string createdAtSummary = "-";

    [ObservableProperty]
    private string downloadStepStatus = "未开始";

    [ObservableProperty]
    private string transcodeStepStatus = "未开始";

    [ObservableProperty]
    private string rewriteStepStatus = "未开始";

    [ObservableProperty]
    private string posterRenameStepStatus = "未开始";

    [ObservableProperty]
    private string costReportStepStatus = "未开始";

    [ObservableProperty]
    private string projectImageStepStatus = "未开始";

    [ObservableProperty]
    private string batchFileRenameStepStatus = "未开始";

    [ObservableProperty]
    private string materialConvertStepStatus = "未开始";

    [ObservableProperty]
    private string episodeUploadStepStatus = "未开始";

    [ObservableProperty]
    private string materialUploadStepStatus = "未接入";

    [ObservableProperty]
    private string projectMaterialNodeStatus = "未完成";

    public IBrush SchedulingStatusBrush => ResolveStatusBrush(SchedulingStatus);
    public IBrush DownloadStepStatusBrush => ResolveStatusBrush(DownloadStepStatus);
    public IBrush TranscodeStepStatusBrush => ResolveStatusBrush(TranscodeStepStatus);
    public IBrush RewriteStepStatusBrush => ResolveStatusBrush(RewriteStepStatus);
    public IBrush PosterRenameStepStatusBrush => ResolveStatusBrush(PosterRenameStepStatus);
    public IBrush CostReportStepStatusBrush => ResolveStatusBrush(CostReportStepStatus);
    public IBrush ProjectImageStepStatusBrush => ResolveStatusBrush(ProjectImageStepStatus);
    public IBrush BatchFileRenameStepStatusBrush => ResolveStatusBrush(BatchFileRenameStepStatus);
    public IBrush MaterialConvertStepStatusBrush => ResolveStatusBrush(MaterialConvertStepStatus);
    public IBrush EpisodeUploadStepStatusBrush => ResolveStatusBrush(EpisodeUploadStepStatus);
    public IBrush MaterialUploadStepStatusBrush => ResolveStatusBrush(MaterialUploadStepStatus);
    public string DownloadNodeStatus => NormalizeNodeStatus(DownloadStepStatus);
    public string EpisodeUploadNodeStatus => NormalizeNodeStatus(EpisodeUploadStepStatus);
    public string MaterialUploadNodeStatus => NormalizeNodeStatus(MaterialUploadStepStatus);
    public IBrush DownloadNodeBackgroundBrush => ResolveNodeBackgroundBrush(DownloadNodeStatus);
    public IBrush ProjectMaterialNodeBackgroundBrush => ResolveNodeBackgroundBrush(ProjectMaterialNodeStatus);
    public IBrush EpisodeUploadNodeBackgroundBrush => ResolveNodeBackgroundBrush(EpisodeUploadNodeStatus);
    public IBrush MaterialUploadNodeBackgroundBrush => ResolveNodeBackgroundBrush(MaterialUploadNodeStatus);
    public IBrush DownloadNodeForegroundBrush => ResolveNodeForegroundBrush(DownloadNodeStatus);
    public IBrush ProjectMaterialNodeForegroundBrush => ResolveNodeForegroundBrush(ProjectMaterialNodeStatus);
    public IBrush EpisodeUploadNodeForegroundBrush => ResolveNodeForegroundBrush(EpisodeUploadNodeStatus);
    public IBrush MaterialUploadNodeForegroundBrush => ResolveNodeForegroundBrush(MaterialUploadNodeStatus);

    public string ProgressText => TotalSteps <= 0 ? "0 / 0" : $"{CompletedSteps} / {TotalSteps}";
    public int DownloadPendingCount => DownloadEpisodes.Count(item => !string.Equals(item.StatusText, "完成", StringComparison.Ordinal) && !string.Equals(item.StatusText, "下载中", StringComparison.Ordinal));
    public int DownloadRunningCount => DownloadEpisodes.Count(item => string.Equals(item.StatusText, "下载中", StringComparison.Ordinal));
    public int DownloadCompletedCount => DownloadEpisodes.Count(item => string.Equals(item.StatusText, "完成", StringComparison.Ordinal));
    public int DownloadConcurrency => ResolveDownloadConcurrency(SourceProjectDir);
    public int EpisodeUploadPendingCount => EpisodeUploadEpisodes.Count(item => string.Equals(item.StatusText, "待上传", StringComparison.Ordinal));
    public int EpisodeUploadRunningCount => EpisodeUploadEpisodes.Count(item => string.Equals(item.StatusText, "上传中", StringComparison.Ordinal) || string.Equals(item.StatusText, "等待人工", StringComparison.Ordinal));
    public int EpisodeUploadCompletedCount => EpisodeUploadEpisodes.Count(item => string.Equals(item.StatusText, "已完成", StringComparison.Ordinal));
    public int EpisodeUploadFailedCount => EpisodeUploadEpisodes.Count(item => string.Equals(item.StatusText, "失败", StringComparison.Ordinal));
    public string EpisodeUploadStageText => _episodeUploadStageText;
    public string EpisodeUploadSubmitStatusText => _episodeUploadSubmitStatusText;
    public IReadOnlyCollection<int> SkippedEpisodeUploadEpisodes => _skippedEpisodeUploadEpisodes;
    public string MaterialUploadStrategySummary => _materialUploadStrategySummary;
    public string MaterialUploadSelectionSummary => _materialUploadSelectionSummary;

    public string GetProjectMaterialStepStatus(string stepKey)
    {
        stepKey = stepKey.Trim();
        return stepKey switch
        {
            "transcode" => TranscodeStepStatus,
            "rewrite" => RewriteStepStatus,
            "poster-rename" => PosterRenameStepStatus,
            "project-image" => ProjectImageStepStatus,
            "cost-report" => CostReportStepStatus,
            "batch-file-rename" => BatchFileRenameStepStatus,
            "material-convert" => MaterialConvertStepStatus,
            _ => "未开始"
        };
    }

    public string GetProjectMaterialStepSummary(string stepKey)
    {
        return stepKey switch
        {
            "transcode" => _transcodeProgressText,
            "rewrite" => File.Exists(Path.Combine(WorkflowProjectDir ?? string.Empty, "短剧信息.txt")) ? "短剧信息已生成" : "等待生成短剧信息",
            "poster-rename" => File.Exists(Path.Combine(WorkflowProjectDir ?? string.Empty, "海报图片.jpg")) ? "海报已生成" : "等待生成海报",
            "project-image" => CountProjectImages(WorkflowProjectDir) switch
            {
                > 0 and var count => $"已生成 {count} 张工程图",
                _ => "等待生成工程图"
            },
            "cost-report" => File.Exists(Path.Combine(WorkflowProjectDir ?? string.Empty, "成本报表.png")) ? "成本报表已生成" : "等待生成成本报表",
            "batch-file-rename" => HasRenamedWorkflowVideos() ? "视频文件名已同步" : "等待重命名视频文件",
            "material-convert" => CountVideoFiles(Path.Combine(WorkflowProjectDir ?? string.Empty, "material-videos")) switch
            {
                > 0 and var count => $"素材视频 {count} 个",
                _ => "等待转换素材视频"
            },
            _ => "待开始"
        };
    }

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke(this, EventArgs.Empty);
    partial void OnSchedulingStatusChanged(string value) => OnPropertyChanged(nameof(SchedulingStatusBrush));
    partial void OnDownloadStepStatusChanged(string value)
    {
        OnPropertyChanged(nameof(DownloadStepStatusBrush));
        OnPropertyChanged(nameof(DownloadNodeStatus));
        OnPropertyChanged(nameof(DownloadNodeBackgroundBrush));
        OnPropertyChanged(nameof(DownloadNodeForegroundBrush));
    }
    partial void OnTranscodeStepStatusChanged(string value) => OnPropertyChanged(nameof(TranscodeStepStatusBrush));
    partial void OnRewriteStepStatusChanged(string value) => OnPropertyChanged(nameof(RewriteStepStatusBrush));
    partial void OnPosterRenameStepStatusChanged(string value) => OnPropertyChanged(nameof(PosterRenameStepStatusBrush));
    partial void OnCostReportStepStatusChanged(string value) => OnPropertyChanged(nameof(CostReportStepStatusBrush));
    partial void OnProjectImageStepStatusChanged(string value) => OnPropertyChanged(nameof(ProjectImageStepStatusBrush));
    partial void OnBatchFileRenameStepStatusChanged(string value) => OnPropertyChanged(nameof(BatchFileRenameStepStatusBrush));
    partial void OnMaterialConvertStepStatusChanged(string value) => OnPropertyChanged(nameof(MaterialConvertStepStatusBrush));
    partial void OnEpisodeUploadStepStatusChanged(string value)
    {
        OnPropertyChanged(nameof(EpisodeUploadStepStatusBrush));
        OnPropertyChanged(nameof(EpisodeUploadNodeStatus));
        OnPropertyChanged(nameof(EpisodeUploadNodeBackgroundBrush));
        OnPropertyChanged(nameof(EpisodeUploadNodeForegroundBrush));
    }
    partial void OnMaterialUploadStepStatusChanged(string value)
    {
        OnPropertyChanged(nameof(MaterialUploadStepStatusBrush));
        OnPropertyChanged(nameof(MaterialUploadNodeStatus));
        OnPropertyChanged(nameof(MaterialUploadNodeBackgroundBrush));
        OnPropertyChanged(nameof(MaterialUploadNodeForegroundBrush));
    }
    partial void OnProjectMaterialNodeStatusChanged(string value)
    {
        OnPropertyChanged(nameof(ProjectMaterialNodeBackgroundBrush));
        OnPropertyChanged(nameof(ProjectMaterialNodeForegroundBrush));
    }

    public void Refresh(ScannedProject project)
    {
        DisplayName = project.DisplayName;
        Status = project.Status;
        VideoCount = project.VideoCount;
        CompletedSteps = project.CompletedSteps;
        TotalSteps = project.TotalSteps;
        ResumeFrom = project.ResumeFrom;
        FailedStep = project.FailedStep;
        HasFailure = project.HasFailure;
        WorkflowProjectDir = project.WorkflowProjectDir;
        SchedulingStatus = "空闲";
        _downloadEpisodeProgress.Clear();
        _downloadEpisodeSpeeds.Clear();
        _transcodedEpisodes.Clear();
        _transcodeTotalEpisodes = 0;
        _downloadExpectedEpisodes = 0;
        _episodeUploadTotalEpisodes = 0;
        _episodeUploadCompletedEpisodes = 0;
        _episodeUploadActiveEpisode = 0;
        _episodeUploadStageText = "等待开始";
        _episodeUploadSubmitStatusText = "未开始";
        _episodeUploadPercent = 0;
        _materialUploadPercent = 0;
        _transcodeProgressText = "未开始";
        _episodeUploadProgressText = "未开始";
        _materialUploadProgressText = "未开始";
        DownloadEpisodes.Clear();
        EpisodeUploadEpisodes.Clear();
        _manuallyCompletedEpisodeUploadEpisodes.Clear();
        ApplyFileSystemState(project);
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DownloadPendingCount));
        OnPropertyChanged(nameof(DownloadRunningCount));
        OnPropertyChanged(nameof(DownloadCompletedCount));
        OnPropertyChanged(nameof(DownloadConcurrency));
        OnPropertyChanged(nameof(EpisodeUploadPendingCount));
        OnPropertyChanged(nameof(EpisodeUploadRunningCount));
        OnPropertyChanged(nameof(EpisodeUploadCompletedCount));
        OnPropertyChanged(nameof(EpisodeUploadFailedCount));
        OnPropertyChanged(nameof(EpisodeUploadStageText));
        OnPropertyChanged(nameof(EpisodeUploadSubmitStatusText));
    }

    public void ApplyProgress(WorkRunEvent evt)
    {
        if (!string.Equals(SchedulingStatus, "运行中", StringComparison.Ordinal))
        {
            SchedulingStatus = "运行中";
        }

        if (!string.IsNullOrWhiteSpace(evt.Message))
        {
            LastEvent = $"{evt.StepType ?? evt.Kind}: {evt.Message}";
        }

        ApplyStepProgress(evt);

        if (string.Equals(evt.Kind, "step-cancelled", StringComparison.Ordinal) ||
            string.Equals(evt.Kind, "project-cancelled", StringComparison.Ordinal))
        {
            Status = "已停止可恢复";
            FailedStep = evt.StepType ?? FailedStep;
            HasFailure = false;
            SchedulingStatus = "已停止";
            UpdateOverallProgress();
            return;
        }

        Status = evt.Ok switch
        {
            true when string.Equals(evt.Kind, "step-completed", StringComparison.Ordinal) => "处理中",
            false => "失败",
            _ => "处理中"
        };
        UpdateOverallProgress();
    }

    public void MarkQueued()
    {
        SchedulingStatus = "排队中";
    }

    public void MarkRunning(string? stepLabel = null)
    {
        SchedulingStatus = "运行中";
        if (!string.IsNullOrWhiteSpace(stepLabel))
        {
            CurrentStepLabel = stepLabel;
        }
    }

    public void MarkCompleted()
    {
        SchedulingStatus = "已完成";
    }

    public void MarkFailed()
    {
        SchedulingStatus = "失败";
    }

    public void MarkStopped()
    {
        SchedulingStatus = "已停止";
    }

    private void ApplyFileSystemState(ScannedProject project)
    {
        var metadata = ReadMetadata(SourceProjectDir);
        var downloadInspection = LocalProjectDownloadInspector.Inspect(SourceProjectDir);
        var expectedEpisodes = downloadInspection.ExpectedEpisodeCount > 0
            ? downloadInspection.ExpectedEpisodeCount
            : Math.Max(0, metadata.EpisodeCount);
        _downloadExpectedEpisodes = expectedEpisodes;
        var sourcePosterExists = FindSourcePoster(SourceProjectDir, metadata.Title ?? DisplayName);
        var workflowVideoCount = CountVideoFiles(Path.Combine(WorkflowProjectDir ?? string.Empty, "videos"));
        var materialVideoCount = CountVideoFiles(Path.Combine(WorkflowProjectDir ?? string.Empty, "material-videos"));
        var workflowPosterExists = File.Exists(Path.Combine(WorkflowProjectDir ?? string.Empty, "海报图片.jpg"));
        var workflowProjectImageCount = CountProjectImages(WorkflowProjectDir);
        var workflowCostExists = File.Exists(Path.Combine(WorkflowProjectDir ?? string.Empty, "成本报表.png"));

        SourceSummary = ResolveSourceSummary(metadata);
        EpisodeCountText = expectedEpisodes > 0 ? expectedEpisodes.ToString() : "-";
        VideoSizeSummary = FormatFileSize(CountVideoBytes(SourceProjectDir));
        CreatedAtSummary = FormatCreatedAt(project.CreatedAt);
        MaterialSummary =
            $"视频 {VideoCount} / 海报 {(sourcePosterExists || workflowPosterExists ? "有" : "无")} / 工程图 {workflowProjectImageCount} / 报表 {(workflowCostExists ? "有" : "无")}";
        DramaInfoSummary = BuildDramaInfoSummary(metadata);
        RefreshDownloadEpisodes(downloadInspection);
        RefreshEpisodeUploadEpisodes(metadata);

        if (expectedEpisodes > 0)
        {
            DownloadPercent = Math.Min(100, Math.Round(downloadInspection.DownloadedEpisodeCount * 100d / expectedEpisodes, 1));
            DownloadProgressText = $"{DownloadPercent:0.#}% ({downloadInspection.DownloadedEpisodeCount}/{expectedEpisodes} 集)";
        }
        else if (downloadInspection.DownloadedEpisodeCount > 0)
        {
            DownloadPercent = 100;
            DownloadProgressText = $"已下载 {downloadInspection.DownloadedEpisodeCount} 集";
        }
        else
        {
            DownloadPercent = 0;
            DownloadProgressText = "0%";
        }

        var stepStates = ReadStepStates(WorkflowProjectDir) ?? ReadStepStates(SourceProjectDir);
        DownloadStepStatus = ResolveDownloadStepStatus(stepStates, downloadInspection);
        TranscodeStepStatus = ResolveStepStatus("transcode", stepStates, workflowVideoCount > 0);
        RewriteStepStatus = ResolveStepStatus("rewrite", stepStates, File.Exists(Path.Combine(WorkflowProjectDir ?? string.Empty, "短剧信息.txt")));
        PosterRenameStepStatus = ResolveStepStatus("poster-rename", stepStates, File.Exists(Path.Combine(WorkflowProjectDir ?? string.Empty, "海报图片.jpg")));
        ProjectImageStepStatus = ResolveStepStatus("project-image", stepStates, workflowProjectImageCount > 0);
        CostReportStepStatus = ResolveStepStatus("cost-report", stepStates, workflowCostExists);
        BatchFileRenameStepStatus = ResolveStepStatus("batch-file-rename", stepStates, HasRenamedWorkflowVideos());
        MaterialConvertStepStatus = ResolveStepStatus("material-convert", stepStates, materialVideoCount > 0);
        EpisodeUploadStepStatus = ResolveStepStatus("weixin-upload", stepStates, false);
        MaterialUploadStepStatus = ResolveStepStatus("weixin-material-upload", stepStates, false);
        RefreshProjectMaterialNodeStatus();

        _transcodeTotalEpisodes = expectedEpisodes > 0 ? expectedEpisodes : workflowVideoCount;
        for (var index = 1; index <= workflowVideoCount; index++)
        {
            _transcodedEpisodes.Add(index);
        }

        _transcodeProgressText = _transcodeTotalEpisodes > 0
            ? $"{Math.Min(100, workflowVideoCount * 100d / Math.Max(1, _transcodeTotalEpisodes)):0.#}% ({workflowVideoCount}/{_transcodeTotalEpisodes} 集)"
            : (workflowVideoCount > 0 ? $"已转码 {workflowVideoCount} 集" : "未开始");

        _episodeUploadPercent = EpisodeUploadStepStatus == "已完成" ? 100d : 0d;
        _episodeUploadProgressText = EpisodeUploadStepStatus switch
        {
            "已完成" => "100%",
            "进行中" => "上传中",
            "待继续" => "待继续",
            "已停止" => "已停止",
            "失败" => "失败",
            _ => "未开始"
        };
        _episodeUploadCompletedEpisodes = EpisodeUploadStepStatus == "已完成" ? _episodeUploadTotalEpisodes : 0;
        _episodeUploadStageText = EpisodeUploadStepStatus switch
        {
            "进行中" => "上传中",
            "已完成" => "上传完成",
            "失败" => "上传失败",
            "已停止" => "已停止",
            "待继续" => "待继续",
            _ => "等待开始"
        };
        _episodeUploadSubmitStatusText = EpisodeUploadStepStatus == "已完成" ? "提审完成" : "未开始";
        UpdateEpisodeUploadRows();
        _materialUploadPercent = MaterialUploadStepStatus == "已完成" ? 100d : 0d;
        _materialUploadProgressText = MaterialUploadStepStatus switch
        {
            "已完成" => "100%",
            "进行中" => "上传中",
            "待继续" => "待继续",
            "已停止" => "已停止",
            "失败" => "失败",
            _ => "未开始"
        };
        (_materialUploadStrategySummary, _materialUploadSelectionSummary) = ReadMaterialUploadPublishSummary(WorkflowProjectDir);
        RefreshMaterialPublishVideos();
        OnPropertyChanged(nameof(MaterialUploadStrategySummary));
        OnPropertyChanged(nameof(MaterialUploadSelectionSummary));

        UpdateOverallProgress();
    }

    private void ApplyStepProgress(WorkRunEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.StepType))
        {
            return;
        }

        switch (evt.StepType)
        {
            case "download":
                ApplyDownloadProgress(evt);
                break;
            case "transcode":
                ApplyTranscodeProgress(evt);
                break;
            case "weixin-upload":
                ApplyUploadProgress(evt);
                break;
            case "weixin-material-upload":
                ApplyMaterialUploadProgress(evt);
                break;
            default:
                if (ProjectMaterialStepKeys.Contains(evt.StepType, StringComparer.Ordinal))
                {
                    ApplyProjectMaterialProgress(evt);
                }
                break;
        }
    }

    private void ApplyDownloadProgress(WorkRunEvent evt)
    {
        DownloadStepStatus = ResolveRuntimeStepStatus(evt, DownloadStepStatus);

        if (string.Equals(evt.Kind, "step-completed", StringComparison.Ordinal) ||
            string.Equals(evt.Kind, "step-skipped", StringComparison.Ordinal))
        {
            DownloadPercent = 100;
            DownloadProgressText = "100%";
            foreach (var item in DownloadEpisodes)
            {
                item.StatusText = "完成";
                item.ProgressPercent = 100;
                item.SpeedText = "0 KB/s";
            }
            OnPropertyChanged(nameof(DownloadPendingCount));
            OnPropertyChanged(nameof(DownloadRunningCount));
            OnPropertyChanged(nameof(DownloadCompletedCount));
            return;
        }

        if (string.Equals(evt.Kind, "step-failed", StringComparison.Ordinal))
        {
            foreach (var item in DownloadEpisodes.Where(item => string.Equals(item.StatusText, "下载中", StringComparison.Ordinal)))
            {
                item.StatusText = "失败";
                item.SpeedText = "--";
            }
            OnPropertyChanged(nameof(DownloadPendingCount));
            OnPropertyChanged(nameof(DownloadRunningCount));
            OnPropertyChanged(nameof(DownloadCompletedCount));
            return;
        }

        if (string.Equals(evt.Kind, "step-cancelled", StringComparison.Ordinal))
        {
            foreach (var item in DownloadEpisodes.Where(item => string.Equals(item.StatusText, "下载中", StringComparison.Ordinal)))
            {
                item.StatusText = "待下载";
                item.SpeedText = "--";
            }
            OnPropertyChanged(nameof(DownloadPendingCount));
            OnPropertyChanged(nameof(DownloadRunningCount));
            OnPropertyChanged(nameof(DownloadCompletedCount));
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.Message))
        {
            return;
        }

        var match = DownloadProgressRegex.Match(evt.Message);
        if (!match.Success)
        {
            return;
        }

        var episodeIndex = int.Parse(match.Groups[1].Value);
        var totalEpisodes = int.Parse(match.Groups[2].Value);
        var episodePercent = double.Parse(match.Groups[3].Value);
        _downloadEpisodeProgress[episodeIndex] = Math.Clamp(episodePercent, 0, 100);
        _downloadExpectedEpisodes = Math.Max(_downloadExpectedEpisodes, totalEpisodes);
        EnsureDownloadEpisodeRows(totalEpisodes);

        var speedMatch = DownloadSpeedRegex.Match(evt.Message);
        if (speedMatch.Success)
        {
            _downloadEpisodeSpeeds[episodeIndex] = $"{speedMatch.Groups[1].Value} {speedMatch.Groups[2].Value}/s";
        }

        var episodeRow = DownloadEpisodes.FirstOrDefault(item => item.EpisodeNumber == episodeIndex);
        if (episodeRow is not null)
        {
            episodeRow.ProgressPercent = Math.Clamp(episodePercent, 0, 100);
            episodeRow.SpeedText = _downloadEpisodeSpeeds.GetValueOrDefault(episodeIndex, "--");
            episodeRow.StatusText = episodePercent >= 100 ? "完成" : "下载中";
            if (episodePercent >= 100)
            {
                episodeRow.SpeedText = "0 KB/s";
            }
        }

        var totalProgress = 0d;
        for (var index = 1; index <= totalEpisodes; index++)
        {
            totalProgress += _downloadEpisodeProgress.GetValueOrDefault(index);
        }

        DownloadPercent = Math.Round(totalProgress / totalEpisodes, 1);
        var completedEpisodes = _downloadEpisodeProgress.Count(item => item.Value >= 100);
        DownloadProgressText = $"{DownloadPercent:0.#}% ({completedEpisodes}/{totalEpisodes} 集)";
        OnPropertyChanged(nameof(DownloadPendingCount));
        OnPropertyChanged(nameof(DownloadRunningCount));
        OnPropertyChanged(nameof(DownloadCompletedCount));
    }

    private void ApplyTranscodeProgress(WorkRunEvent evt)
    {
        TranscodeStepStatus = ResolveRuntimeStepStatus(evt, TranscodeStepStatus);
        RefreshProjectMaterialNodeStatus();

        if (string.Equals(evt.Kind, "step-completed", StringComparison.Ordinal) ||
            string.Equals(evt.Kind, "step-skipped", StringComparison.Ordinal))
        {
            if (_transcodeTotalEpisodes <= 0)
            {
                _transcodeTotalEpisodes = _transcodedEpisodes.Count;
            }

            _transcodeProgressText = _transcodeTotalEpisodes > 0
                ? $"100% ({_transcodeTotalEpisodes}/{_transcodeTotalEpisodes} 集)"
                : "100%";
            return;
        }

        if (string.Equals(evt.Kind, "step-cancelled", StringComparison.Ordinal))
        {
            _transcodeProgressText = "已停止";
            return;
        }

        if (string.Equals(evt.Kind, "step-failed", StringComparison.Ordinal))
        {
            _transcodeProgressText = "失败";
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.Message))
        {
            return;
        }

        var match = FractionProgressRegex.Match(evt.Message);
        if (!match.Success)
        {
            if (string.Equals(evt.Kind, "step-started", StringComparison.Ordinal))
            {
                _transcodeProgressText = "准备中";
            }

            return;
        }

        var index = int.Parse(match.Groups[1].Value);
        var total = int.Parse(match.Groups[2].Value);
        if (total > 0)
        {
            _transcodeTotalEpisodes = Math.Max(_transcodeTotalEpisodes, total);
        }

        if (string.Equals(evt.Kind, "file-completed", StringComparison.Ordinal) ||
            string.Equals(evt.Kind, "file-skipped", StringComparison.Ordinal))
        {
            _transcodedEpisodes.Add(index);
        }

        var completed = _transcodedEpisodes.Count;
        var percent = _transcodeTotalEpisodes > 0
            ? Math.Round(completed * 100d / _transcodeTotalEpisodes, 1)
            : 0d;
        _transcodeProgressText = _transcodeTotalEpisodes > 0
            ? $"{percent:0.#}% ({completed}/{_transcodeTotalEpisodes} 集)"
            : $"已转码 {completed} 集";
    }

    private void ApplyUploadProgress(WorkRunEvent evt)
    {
        EpisodeUploadStepStatus = ResolveRuntimeStepStatus(evt, EpisodeUploadStepStatus);

        if (string.Equals(evt.Kind, "step-completed", StringComparison.Ordinal) ||
            string.Equals(evt.Kind, "step-skipped", StringComparison.Ordinal))
        {
            _episodeUploadPercent = 100d;
            _episodeUploadProgressText = "100%";
            _episodeUploadCompletedEpisodes = _episodeUploadTotalEpisodes;
            _episodeUploadStageText = "上传完成";
            _episodeUploadSubmitStatusText = "提审完成";
            UpdateEpisodeUploadRows();
            return;
        }

        if (string.Equals(evt.Kind, "step-cancelled", StringComparison.Ordinal))
        {
            _episodeUploadProgressText = "已停止";
            _episodeUploadStageText = "已停止";
            UpdateEpisodeUploadRows();
            return;
        }

        if (string.Equals(evt.Kind, "step-failed", StringComparison.Ordinal))
        {
            _episodeUploadProgressText = "失败";
            _episodeUploadStageText = "上传失败";
            _episodeUploadSubmitStatusText = "失败";
            UpdateEpisodeUploadRows(markFailure: true);
            return;
        }

        if (string.Equals(evt.Kind, "step-started", StringComparison.Ordinal))
        {
            _episodeUploadPercent = Math.Max(_episodeUploadPercent, 0d);
            _episodeUploadProgressText = "准备上传";
            _episodeUploadStageText = "准备上传";
            UpdateEpisodeUploadRows();
            return;
        }

        if (string.Equals(evt.Kind, "step-output", StringComparison.Ordinal))
        {
            var match = string.IsNullOrWhiteSpace(evt.Message) ? null : FractionProgressRegex.Match(evt.Message);
            if (match is { Success: true })
            {
                var current = int.Parse(match.Groups[1].Value);
                var total = int.Parse(match.Groups[2].Value);
                if (total > 0)
                {
                    _episodeUploadTotalEpisodes = Math.Max(_episodeUploadTotalEpisodes, total);
                    EnsureEpisodeUploadRows(total);
                    _episodeUploadActiveEpisode = Math.Clamp(current, 1, total);
                    _episodeUploadCompletedEpisodes = Math.Max(0, Math.Min(total, current - 1));
                    _episodeUploadPercent = Math.Round(Math.Clamp(current * 100d / total, 0d, 99d), 1);
                    _episodeUploadProgressText = $"{_episodeUploadPercent:0.#}% ({current}/{total})";
                    _episodeUploadStageText = ExtractEpisodeUploadStage(evt.Message) ?? "上传中";
                    if (evt.Message.Contains("提审页已就绪", StringComparison.Ordinal) ||
                        evt.Message.Contains("等待人工", StringComparison.Ordinal) ||
                        evt.Message.Contains("人工", StringComparison.Ordinal))
                    {
                        _episodeUploadSubmitStatusText = "待人工确认";
                    }
                    else if (evt.Message.Contains("提审", StringComparison.Ordinal))
                    {
                        _episodeUploadSubmitStatusText = "提审中";
                    }

                    UpdateEpisodeUploadRows();
                    return;
                }
            }

            _episodeUploadPercent = Math.Max(_episodeUploadPercent, 5d);
            _episodeUploadProgressText = "上传中";
            _episodeUploadStageText = ExtractEpisodeUploadStage(evt.Message) ?? "上传中";
            if (!string.IsNullOrWhiteSpace(evt.Message) && evt.Message.Contains("提审", StringComparison.Ordinal))
            {
                _episodeUploadSubmitStatusText = evt.Message.Contains("人工", StringComparison.Ordinal)
                    ? "待人工确认"
                    : "提审中";
            }
            UpdateEpisodeUploadRows();
        }
    }

    private void ApplyMaterialUploadProgress(WorkRunEvent evt)
    {
        MaterialUploadStepStatus = ResolveRuntimeStepStatus(evt, MaterialUploadStepStatus);

        if (string.Equals(evt.Kind, "step-completed", StringComparison.Ordinal) ||
            string.Equals(evt.Kind, "step-skipped", StringComparison.Ordinal))
        {
            _materialUploadPercent = 100d;
            _materialUploadProgressText = "100%";
            return;
        }

        if (string.Equals(evt.Kind, "step-cancelled", StringComparison.Ordinal))
        {
            _materialUploadProgressText = "已停止";
            return;
        }

        if (string.Equals(evt.Kind, "step-failed", StringComparison.Ordinal))
        {
            _materialUploadProgressText = "失败";
            return;
        }

        _materialUploadPercent = Math.Max(_materialUploadPercent, 5d);
        _materialUploadProgressText = "上传中";
    }

    private void ApplyProjectMaterialProgress(WorkRunEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.StepType))
        {
            return;
        }

        var nextStatus = ResolveRuntimeStepStatus(evt, GetProjectMaterialStepStatus(evt.StepType));
        switch (evt.StepType)
        {
            case "rewrite":
                RewriteStepStatus = nextStatus;
                break;
            case "poster-rename":
                PosterRenameStepStatus = nextStatus;
                break;
            case "project-image":
                ProjectImageStepStatus = nextStatus;
                break;
            case "cost-report":
                CostReportStepStatus = nextStatus;
                break;
            case "batch-file-rename":
                BatchFileRenameStepStatus = nextStatus;
                break;
            case "material-convert":
                MaterialConvertStepStatus = nextStatus;
                break;
        }

        RefreshProjectMaterialNodeStatus();
    }

    private void RefreshProjectMaterialNodeStatus()
    {
        var statuses = new[]
        {
            TranscodeStepStatus,
            RewriteStepStatus,
            PosterRenameStepStatus,
            ProjectImageStepStatus,
            CostReportStepStatus,
            BatchFileRenameStepStatus,
            MaterialConvertStepStatus
        };

        if (statuses.All(status => string.Equals(status, "已完成", StringComparison.Ordinal)))
        {
            ProjectMaterialNodeStatus = "已完成";
            return;
        }

        if (statuses.Any(status => string.Equals(status, "失败", StringComparison.Ordinal)))
        {
            ProjectMaterialNodeStatus = "失败";
            return;
        }

        if (statuses.Any(status => string.Equals(status, "进行中", StringComparison.Ordinal)))
        {
            ProjectMaterialNodeStatus = "处理中";
            return;
        }

        if (statuses.Any(status => string.Equals(status, "待继续", StringComparison.Ordinal) || string.Equals(status, "已停止", StringComparison.Ordinal)))
        {
            ProjectMaterialNodeStatus = "待继续";
            return;
        }

        ProjectMaterialNodeStatus = "未完成";
    }

    private void UpdateOverallProgress()
    {
        var downloadProgress = ResolveStepProgress(DownloadStepStatus, DownloadPercent);
        var transcodeProgress = ResolveStepProgress(
            TranscodeStepStatus,
            _transcodeTotalEpisodes > 0 ? Math.Round(_transcodedEpisodes.Count * 100d / _transcodeTotalEpisodes, 1) : 0d);
        var uploadProgress = ResolveStepProgress(EpisodeUploadStepStatus, _episodeUploadPercent);

        OverallPercent = Math.Round(
            downloadProgress * DownloadWeight / 100d +
            transcodeProgress * TranscodeWeight / 100d +
            uploadProgress * UploadWeight / 100d,
            1);
        OverallProgressText = $"{OverallPercent:0.#}%";

        var (stepLabel, stepProgress, stepPercent) = ResolveCurrentStepDisplay();
        CurrentStepLabel = stepLabel;
        CurrentStepProgressText = stepProgress;
        CurrentStepProgressPercent = stepPercent;
    }

    private (string Label, string Progress, double Percent) ResolveCurrentStepDisplay()
    {
        var steps = new[]
        {
            ("download", "下载剧集", DownloadStepStatus, DownloadProgressText, DownloadPercent),
            ("transcode", "视频转码", TranscodeStepStatus, _transcodeProgressText, ResolveTranscodePercent()),
            ("weixin-upload", "微信上传剧集", EpisodeUploadStepStatus, _episodeUploadProgressText, _episodeUploadPercent),
            ("weixin-material-upload", "微信上传素材", MaterialUploadStepStatus, _materialUploadProgressText, _materialUploadPercent)
        };

        var running = steps.FirstOrDefault(step => string.Equals(step.Item3, "进行中", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(running.Item1))
        {
            return (running.Item2, running.Item4, ClampProgressForDisplay(running.Item5, running.Item3));
        }

        var blocked = steps.FirstOrDefault(step =>
            string.Equals(step.Item3, "已停止", StringComparison.Ordinal) ||
            string.Equals(step.Item3, "待继续", StringComparison.Ordinal) ||
            string.Equals(step.Item3, "失败", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(blocked.Item1))
        {
            return (blocked.Item2, $"{blocked.Item3} · {blocked.Item4}".Trim(' ', '·'), ClampProgressForDisplay(blocked.Item5, blocked.Item3));
        }

        var pending = steps.FirstOrDefault(step => !string.Equals(step.Item3, "已完成", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(pending.Item1))
        {
            return (
                pending.Item2,
                pending.Item3 == "未开始" ? "待开始" : pending.Item4,
                ClampProgressForDisplay(pending.Item5, pending.Item3));
        }

        return ("全流程完成", "100%", 100d);
    }

    private double ResolveTranscodePercent()
    {
        return _transcodeTotalEpisodes > 0
            ? Math.Round(_transcodedEpisodes.Count * 100d / _transcodeTotalEpisodes, 1)
            : 0d;
    }

    private static double ClampProgressForDisplay(double progress, string status)
    {
        return status switch
        {
            "已完成" => 100d,
            "进行中" or "已停止" or "待继续" or "失败" => Math.Clamp(progress, 0d, 99d),
            _ => Math.Clamp(progress, 0d, 100d)
        };
    }

    private static IBrush ResolveStatusBrush(string status)
    {
        return status switch
        {
            "已完成" or "运行中" or "处理中" => Brushes.LimeGreen,
            "排队中" or "待继续" => Brushes.Gold,
            "已停止" or "未开始" or "未接入" or "空闲" => Brushes.LightGray,
            "失败" => Brushes.IndianRed,
            _ => Brushes.LightGray
        };
    }

    private static string NormalizeNodeStatus(string stepStatus)
    {
        return stepStatus switch
        {
            "已完成" => "已完成",
            "进行中" => "处理中",
            "待继续" => "待继续",
            "失败" => "失败",
            _ => "未完成"
        };
    }

    private static string ResolveProjectMaterialNodeStatus(IReadOnlyDictionary<string, string>? stepStates)
    {
        if (stepStates is null || stepStates.Count == 0)
        {
            return "未完成";
        }

        if (ProjectMaterialStepKeys.All(step =>
                stepStates.TryGetValue(step, out var state) &&
                string.Equals(state, "已完成", StringComparison.Ordinal)))
        {
            return "已完成";
        }

        if (ProjectMaterialStepKeys.Any(step =>
                stepStates.TryGetValue(step, out var state) &&
                string.Equals(state, "失败", StringComparison.Ordinal)))
        {
            return "失败";
        }

        if (ProjectMaterialStepKeys.Any(step =>
                stepStates.TryGetValue(step, out var state) &&
                string.Equals(state, "进行中", StringComparison.Ordinal)))
        {
            return "处理中";
        }

        return "未完成";
    }

    private static IBrush ResolveNodeBackgroundBrush(string status)
    {
        return status switch
        {
            "已完成" => new SolidColorBrush(Color.Parse("#1D6B3A")),
            _ => new SolidColorBrush(Color.Parse("#111A2E"))
        };
    }

    private static IBrush ResolveNodeForegroundBrush(string status)
    {
        return status switch
        {
            "已完成" => Brushes.White,
            "失败" => Brushes.IndianRed,
            "处理中" => Brushes.LightSkyBlue,
            _ => Brushes.White
        };
    }

    private static double ResolveStepProgress(string status, double progress)
    {
        return status switch
        {
            "已完成" => 100d,
            "进行中" => Math.Clamp(progress, 0d, 99d),
            "已停止" => Math.Clamp(progress, 0d, 99d),
            "待继续" => Math.Clamp(progress, 0d, 99d),
            "失败" => Math.Clamp(progress, 0d, 99d),
            _ => 0d
        };
    }

    private static string ResolveRuntimeStepStatus(WorkRunEvent evt, string currentStatus)
    {
        return evt.Kind switch
        {
            "step-started" => "进行中",
            "step-output" => currentStatus == "已完成" ? currentStatus : "进行中",
            "step-skipped" => "已完成",
            "step-completed" => "已完成",
            "step-cancelled" => "已停止",
            "step-failed" => "失败",
            "step-deferred" => "待继续",
            _ => currentStatus
        };
    }

    private static string ResolveStepStatus(string stepType, IReadOnlyDictionary<string, string>? states, bool hasOutputs)
    {
        if (states is not null && states.TryGetValue(stepType, out var state))
        {
            return state;
        }

        return hasOutputs ? "已完成" : "未开始";
    }

    private static string ResolveDownloadStepStatus(IReadOnlyDictionary<string, string>? states, LocalDownloadInspection downloadInspection)
    {
        if (downloadInspection.IsComplete)
        {
            return "已完成";
        }

        if (states is not null && states.TryGetValue("download", out var state))
        {
            return state;
        }

        return downloadInspection.HasAnyDownloads ? "待继续" : "未开始";
    }

    private static IReadOnlyDictionary<string, string>? ReadStepStates(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            return null;
        }

        var statePath = Path.Combine(projectDir, "states.json");
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var steps = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement,
                JsonValueKind.Object when document.RootElement.TryGetProperty("steps", out var nestedSteps) => nestedSteps,
                _ => default
            };

            if (steps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in steps.EnumerateArray())
            {
                if (!step.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var stepType = typeElement.GetString();
                if (string.IsNullOrWhiteSpace(stepType))
                {
                    continue;
                }

                var isCompleted = step.TryGetProperty("isCompleted", out var completedElement) &&
                    completedElement.ValueKind == JsonValueKind.True;
                var error = step.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;

                states[stepType] = isCompleted
                    ? "已完成"
                    : string.IsNullOrWhiteSpace(error)
                        ? "未开始"
                        : error.Contains("可继续运行", StringComparison.Ordinal) || error.Contains("未下载完成", StringComparison.Ordinal)
                            ? "待继续"
                            : "失败";
            }

            return states;
        }
        catch
        {
            return null;
        }
    }

    private static ProjectMetadataSnapshot ReadMetadata(string sourceProjectDir)
    {
        var metadataPath = Path.Combine(sourceProjectDir, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
        {
            return ProjectMetadataSnapshot.Empty with
            {
                EpisodeCount = InferEpisodeCountFromLocalVideos(sourceProjectDir)
            };
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var root = document.RootElement;
            var episodeCount = GetInt(root, "episodeCount") ?? GetInt(root, "episode_count") ?? 0;
            if (episodeCount <= 0)
            {
                episodeCount = InferEpisodeCountFromLocalVideos(sourceProjectDir);
            }

            var sourceLabel = GetString(root, "source")
                ?? GetString(root, "sourceName")
                ?? GetString(root, "source_name")
                ?? GetString(root, "sourceType")
                ?? GetString(root, "source_type")
                ?? GetString(root, "downloadSource")
                ?? GetString(root, "download_source");
            var bookId = GetString(root, "bookId") ?? GetString(root, "book_id");

            return new ProjectMetadataSnapshot(
                Title: GetString(root, "title") ?? GetString(root, "originalTitle"),
                Category: GetString(root, "category"),
                EpisodeCount: episodeCount,
                SourceLabel: sourceLabel,
                HasBookId: !string.IsNullOrWhiteSpace(bookId));
        }
        catch
        {
            return ProjectMetadataSnapshot.Empty with
            {
                EpisodeCount = InferEpisodeCountFromLocalVideos(sourceProjectDir)
            };
        }
    }

    private static string BuildDramaInfoSummary(ProjectMetadataSnapshot metadata)
    {
        var parts = new List<string>();
        if (metadata.EpisodeCount > 0)
        {
            parts.Add($"{metadata.EpisodeCount} 集");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Category))
        {
            parts.Add(metadata.Category);
        }

        return parts.Count == 0 ? "信息待补充" : string.Join(" / ", parts);
    }

    private static string ResolveSourceSummary(ProjectMetadataSnapshot metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata.SourceLabel))
        {
            return metadata.SourceLabel!;
        }

        return metadata.HasBookId ? "下载页" : "本地项目";
    }

    private static string FormatCreatedAt(DateTimeOffset? createdAt)
    {
        return createdAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    private static bool FindSourcePoster(string sourceProjectDir, string displayName)
    {
        if (!Directory.Exists(sourceProjectDir))
        {
            return false;
        }

        foreach (var extension in ImageExtensions)
        {
            if (File.Exists(Path.Combine(sourceProjectDir, $"{displayName}{extension}")))
            {
                return true;
            }
        }

        return Directory.EnumerateFiles(sourceProjectDir, "*.*", SearchOption.TopDirectoryOnly)
            .Any(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static int CountVideoFiles(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Count(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    private static long CountVideoBytes(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0L;
        }

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(path => VideoExtensions.Any(extension => string.Equals(extension, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch
            {
            }
        }

        return total;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:0} {units[unitIndex]}"
            : $"{value:0.#} {units[unitIndex]}";
    }

    private static int CountProjectImages(string? workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir) || !Directory.Exists(workflowProjectDir))
        {
            return 0;
        }

        return Directory.EnumerateFiles(workflowProjectDir, "工程图*.png", SearchOption.TopDirectoryOnly).Count();
    }

    private bool HasRenamedWorkflowVideos()
    {
        var videosDir = Path.Combine(WorkflowProjectDir ?? string.Empty, "videos");
        if (!Directory.Exists(videosDir))
        {
            return false;
        }

        var files = Directory.EnumerateFiles(videosDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0)
        {
            return false;
        }

        return files.All(path => Path.GetFileNameWithoutExtension(path).StartsWith(DisplayName + "-", StringComparison.Ordinal));
    }

    private static (string Strategy, string Selection) ReadMaterialUploadPublishSummary(string? workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir) || !Directory.Exists(workflowProjectDir))
        {
            return ("未配置", "未配置");
        }

        foreach (var fileName in new[] { "weixin-channel-publish-test.json", "weixin-channel-publish.json" })
        {
            var path = Path.Combine(workflowProjectDir, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("video_publish", out var publish) || publish.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var strategy = GetString(publish, "run_strategy") ?? "all";
                var mode = GetString(publish, "episode_selection_mode") ?? "range";
                var start = GetInt(publish, "start_episode_index") ?? 2;
                var count = GetInt(publish, "publish_count") ?? 4;
                var explicitIndexes = publish.TryGetProperty("episode_indexes", out var indexes) && indexes.ValueKind == JsonValueKind.Array
                    ? string.Join(",", indexes.EnumerateArray().Select(item => item.ToString()))
                    : string.Empty;

                var strategyLabel = strategy switch
                {
                    "resume" => "断点续跑",
                    "retry_failed" => "只重试失败集",
                    _ => "全部重跑"
                };

                var selectionLabel = mode switch
                {
                    "explicit" when !string.IsNullOrWhiteSpace(explicitIndexes) => $"指定集数：{explicitIndexes}",
                    _ => $"连续 {count} 集，从第 {start} 集开始"
                };

                return (strategyLabel, selectionLabel);
            }
            catch
            {
                return ("配置异常", "配置异常");
            }
        }

        return ("未配置", "未配置");
    }

    private void RefreshMaterialPublishVideos()
    {
        MaterialPublishVideos.Clear();
        if (string.IsNullOrWhiteSpace(WorkflowProjectDir) || !Directory.Exists(WorkflowProjectDir))
        {
            return;
        }

        var configPath = new[] { "weixin-channel-publish-test.json", "weixin-channel-publish.json", "weixin-channel-material.json" }
            .Select(name => Path.Combine(WorkflowProjectDir, name))
            .FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("video_publish", out var publish) || publish.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var stateFile = GetString(publish, "state_file") ?? ".weixin-channel-publish-state.json";
            var strategy = GetString(publish, "run_strategy") ?? "all";
            var mode = GetString(publish, "episode_selection_mode") ?? "range";
            var start = GetInt(publish, "start_episode_index") ?? 2;
            var count = GetInt(publish, "publish_count") ?? 4;
            var explicitIndexes = publish.TryGetProperty("episode_indexes", out var indexesElement) && indexesElement.ValueKind == JsonValueKind.Array
                ? indexesElement.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.Number ? item.GetInt32() : int.TryParse(item.GetString(), out var parsed) ? parsed : 0)
                    .Where(value => value > 0)
                    .Distinct()
                    .ToArray()
                : [];

            var materialVideosDir = Path.Combine(WorkflowProjectDir, "material-videos");
            var videosDir = Path.Combine(WorkflowProjectDir, "videos");
            var baseDir = Directory.Exists(materialVideosDir) &&
                          Directory.EnumerateFiles(materialVideosDir, "*.*", SearchOption.TopDirectoryOnly).Any(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                ? materialVideosDir
                : videosDir;
            if (!Directory.Exists(baseDir))
            {
                return;
            }

            var files = Directory.EnumerateFiles(baseDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                return;
            }

            var selectedIndexes = string.Equals(mode, "explicit", StringComparison.OrdinalIgnoreCase) && explicitIndexes.Length > 0
                ? explicitIndexes
                : Enumerable.Range(Math.Max(1, start), Math.Max(1, count)).Where(index => index <= files.Length).ToArray();

            var statePath = Path.IsPathRooted(stateFile) ? stateFile : Path.Combine(WorkflowProjectDir, stateFile);
            var stateEntries = ReadMaterialPublishStateEntries(statePath);

            foreach (var episodeIndex in selectedIndexes)
            {
                if (episodeIndex <= 0 || episodeIndex > files.Length)
                {
                    continue;
                }

                var filePath = files[episodeIndex - 1];
                if (ShouldSkipMaterialPublishVideoByStrategy(strategy, stateEntries, episodeIndex))
                {
                    continue;
                }

                var status = stateEntries.TryGetValue(episodeIndex.ToString(), out var state)
                    ? state
                    : "待发";
                MaterialPublishVideos.Add(new MaterialPublishVideoItemViewModel(
                    episodeIndex,
                    Path.GetFileName(filePath),
                    NormalizeMaterialPublishStatus(status)));
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> ReadMaterialPublishStateEntries(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!document.RootElement.TryGetProperty("Entries", out var entriesElement) &&
                !document.RootElement.TryGetProperty("entries", out entriesElement))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            if (entriesElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in entriesElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var status = GetString(property.Value, "Status") ?? GetString(property.Value, "status");
                if (!string.IsNullOrWhiteSpace(status))
                {
                    entries[property.Name] = status;
                }
            }

            return entries;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool ShouldSkipMaterialPublishVideoByStrategy(string strategy, IReadOnlyDictionary<string, string> stateEntries, int episodeIndex)
    {
        if (!stateEntries.TryGetValue(episodeIndex.ToString(), out var status))
        {
            return false;
        }

        return strategy switch
        {
            "resume" => string.Equals(status, "success", StringComparison.OrdinalIgnoreCase),
            "retry_failed" => !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string NormalizeMaterialPublishStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "success" => "已成功",
            "failed" => "失败",
            "running" => "进行中",
            "interrupted" => "已中断",
            _ => "待发"
        };
    }

    private void RefreshDownloadEpisodes(LocalDownloadInspection downloadInspection)
    {
        DownloadEpisodes.Clear();

        var downloadedEpisodes = downloadInspection.DownloadedEpisodeNumbers.ToHashSet();
        var expectedEpisodes = downloadInspection.ExpectedEpisodeCount > 0
            ? downloadInspection.ExpectedEpisodeCount
            : downloadInspection.MaxEpisodeNumber;

        _downloadExpectedEpisodes = expectedEpisodes;
        for (var episode = 1; episode <= expectedEpisodes; episode++)
        {
            var completed = downloadedEpisodes.Contains(episode);
            DownloadEpisodes.Add(new DownloadEpisodeItemViewModel(episode, DisplayName)
            {
                StatusText = completed ? "完成" : "待下载",
                ProgressPercent = completed ? 100d : 0d,
                SpeedText = completed ? "0 KB/s" : "--"
            });
        }
    }

    private void EnsureDownloadEpisodeRows(int totalEpisodes)
    {
        if (DownloadEpisodes.Count >= totalEpisodes)
        {
            return;
        }

        for (var episode = DownloadEpisodes.Count + 1; episode <= totalEpisodes; episode++)
        {
            DownloadEpisodes.Add(new DownloadEpisodeItemViewModel(episode, DisplayName));
        }
    }

    private static int InferEpisodeCountFromLocalVideos(string sourceProjectDir)
    {
        return LocalProjectDownloadInspector.Inspect(sourceProjectDir).MaxEpisodeNumber;
    }

    private static int ResolveDownloadConcurrency(string sourceProjectDir)
    {
        return LocalProjectDownloadInspector.ResolveConfiguredConcurrency(sourceProjectDir);
    }

    private void RefreshEpisodeUploadEpisodes(ProjectMetadataSnapshot metadata)
    {
        EpisodeUploadEpisodes.Clear();
        _episodeUploadTotalEpisodes = metadata.EpisodeCount;
        for (var episode = 1; episode <= metadata.EpisodeCount; episode++)
        {
            EpisodeUploadEpisodes.Add(new EpisodeUploadItemViewModel(episode, DisplayName));
        }
    }

    private void EnsureEpisodeUploadRows(int totalEpisodes)
    {
        if (EpisodeUploadEpisodes.Count >= totalEpisodes)
        {
            return;
        }

        for (var episode = EpisodeUploadEpisodes.Count + 1; episode <= totalEpisodes; episode++)
        {
            EpisodeUploadEpisodes.Add(new EpisodeUploadItemViewModel(episode, DisplayName));
        }
    }

    private void UpdateEpisodeUploadRows(bool markFailure = false)
    {
        foreach (var item in EpisodeUploadEpisodes)
        {
            if (item.EpisodeNumber <= _episodeUploadCompletedEpisodes)
            {
                item.StatusText = "已完成";
                item.ProgressPercent = 100d;
                item.ActionText = "上传完成";
                item.ResultText = "上传成功";
                continue;
            }

            if (_manuallyCompletedEpisodeUploadEpisodes.Contains(item.EpisodeNumber))
            {
                item.StatusText = "已完成";
                item.ProgressPercent = 100d;
                item.ActionText = "手动完成";
                item.ResultText = "已手动标记完成";
                continue;
            }

            if (_skippedEpisodeUploadEpisodes.Contains(item.EpisodeNumber))
            {
                item.StatusText = "已跳过";
                item.ProgressPercent = 0d;
                item.ActionText = "手动跳过";
                item.ResultText = "已跳过";
                continue;
            }

            if (_episodeUploadActiveEpisode > 0 && item.EpisodeNumber == _episodeUploadActiveEpisode)
            {
                item.StatusText = markFailure ? "失败" : (string.Equals(_episodeUploadSubmitStatusText, "待人工确认", StringComparison.Ordinal) ? "等待人工" : "上传中");
                item.ProgressPercent = markFailure ? 0d : Math.Clamp(_episodeUploadPercent, 0d, 99d);
                item.ActionText = _episodeUploadStageText;
                item.ResultText = markFailure ? "上传失败" : _episodeUploadSubmitStatusText;
                continue;
            }

            item.StatusText = "待上传";
            item.ProgressPercent = 0d;
            item.ActionText = "等待开始";
            item.ResultText = "未开始";
        }

        OnPropertyChanged(nameof(EpisodeUploadPendingCount));
        OnPropertyChanged(nameof(EpisodeUploadRunningCount));
        OnPropertyChanged(nameof(EpisodeUploadCompletedCount));
        OnPropertyChanged(nameof(EpisodeUploadFailedCount));
        OnPropertyChanged(nameof(EpisodeUploadStageText));
        OnPropertyChanged(nameof(EpisodeUploadSubmitStatusText));
    }

    public void MarkEpisodeUploadSkipped(int episodeNumber)
    {
        if (episodeNumber <= 0)
        {
            return;
        }

        _skippedEpisodeUploadEpisodes.Add(episodeNumber);
        UpdateEpisodeUploadRows();
    }

    public void ClearEpisodeUploadSkipped(int episodeNumber)
    {
        if (episodeNumber <= 0)
        {
            return;
        }

        _skippedEpisodeUploadEpisodes.Remove(episodeNumber);
        UpdateEpisodeUploadRows();
    }

    public void MarkEpisodeUploadCompleted(int episodeNumber)
    {
        if (episodeNumber <= 0)
        {
            return;
        }

        _skippedEpisodeUploadEpisodes.Remove(episodeNumber);
        _manuallyCompletedEpisodeUploadEpisodes.Add(episodeNumber);
        SyncManualEpisodeUploadProgress();
        UpdateEpisodeUploadRows();
        UpdateOverallProgress();
    }

    public void ClearEpisodeUploadCompleted(int episodeNumber)
    {
        if (episodeNumber <= 0)
        {
            return;
        }

        _manuallyCompletedEpisodeUploadEpisodes.Remove(episodeNumber);
        SyncManualEpisodeUploadProgress();
        UpdateEpisodeUploadRows();
        UpdateOverallProgress();
    }

    private void SyncManualEpisodeUploadProgress()
    {
        var total = Math.Max(_episodeUploadTotalEpisodes, EpisodeUploadEpisodes.Count);
        if (total <= 0)
        {
            return;
        }

        var completed = Math.Max(
            _episodeUploadCompletedEpisodes,
            EpisodeUploadEpisodes.Count(item => _manuallyCompletedEpisodeUploadEpisodes.Contains(item.EpisodeNumber)));

        completed = Math.Min(total, completed);
        _episodeUploadPercent = Math.Round(completed * 100d / total, 1);
        _episodeUploadProgressText = $"{_episodeUploadPercent:0.#}% ({completed}/{total} 集)";
        _episodeUploadCompletedEpisodes = completed;

        if (completed >= total)
        {
            EpisodeUploadStepStatus = "已完成";
            _episodeUploadStageText = "上传完成";
            _episodeUploadSubmitStatusText = "已手动标记完成";
        }
        else
        {
            EpisodeUploadStepStatus = completed > 0 ? "待继续" : EpisodeUploadStepStatus;
            if (string.Equals(_episodeUploadStageText, "等待开始", StringComparison.Ordinal))
            {
                _episodeUploadStageText = "等待继续";
            }
        }

        OnPropertyChanged(nameof(EpisodeUploadStageText));
        OnPropertyChanged(nameof(EpisodeUploadSubmitStatusText));
    }

    private static string? ExtractEpisodeUploadStage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        if (message.Contains("第一页", StringComparison.Ordinal))
        {
            return "填写第一页";
        }

        if (message.Contains("第二页", StringComparison.Ordinal))
        {
            return "上传视频";
        }

        if (message.Contains("提审页", StringComparison.Ordinal))
        {
            return "进入提审页";
        }

        if (message.Contains("最终提审", StringComparison.Ordinal))
        {
            return "最终提交";
        }

        if (message.Contains("上传", StringComparison.Ordinal))
        {
            return "上传视频";
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
        {
            return intValue;
        }

        return null;
    }

    private sealed record ProjectMetadataSnapshot(
        string? Title,
        string? Category,
        int EpisodeCount,
        string? SourceLabel,
        bool HasBookId)
    {
        public static ProjectMetadataSnapshot Empty { get; } = new(null, null, 0, null, false);
    }
}
