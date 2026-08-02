using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class QueueProjectRowViewModel : ViewModelBase
{
    private int _rowIndex;

    public QueueProjectItem Item { get; private set; }

    /// <summary>用户点击「启用」勾选框改变状态后触发（用于持久化与汇总刷新）。</summary>
    public event Action<QueueProjectRowViewModel>? EnabledChangedByUser;
    public event Action<QueueProjectRowViewModel>? RemarkChangedByUser;

    private string _lastRefreshFingerprint = "";
    // NewProjectDir 解析 workflow 目录（含文件系统/元数据访问），NewTitle 依赖它。
    // 缓存这两个值，避免 OnPropertyChanged("") 触发的多次绑定重求值每次都走 IO；
    // 仅在内容指纹变化时（含标题变化，可能改变 workflow 目录名）重算一次。
    private string _newProjectDir;
    private string _newTitle;

    public QueueProjectRowViewModel(QueueProjectItem item)
    {
        Item = item;
        _newProjectDir = ResolveWorkflowProjectDir(item.ProjectDir);
        _newTitle = ResolveNewTitle(item, _newProjectDir);
        _lastRefreshFingerprint = BuildRefreshFingerprint(item);
    }

    public void RefreshFrom(QueueProjectItem item)
    {
        var fingerprint = BuildRefreshFingerprint(item);
        Item = item;
        // 内容未变时跳过全属性失效（OnPropertyChanged("") 会让整行所有绑定重求值，队列高频刷新时代价很大）。
        if (string.Equals(_lastRefreshFingerprint, fingerprint, StringComparison.Ordinal))
            return;

        _lastRefreshFingerprint = fingerprint;
        _newProjectDir = ResolveWorkflowProjectDir(item.ProjectDir);
        _newTitle = ResolveNewTitle(item, _newProjectDir);
        OnPropertyChanged(string.Empty);
    }

    private static string BuildRefreshFingerprint(QueueProjectItem item)
    {
        var sb = new System.Text.StringBuilder(160);
        sb.Append(item.Enabled ? '1' : '0').Append('|')
          .Append(item.StatusText).Append('|')
          .Append(item.CurrentStep).Append('|')
          .Append(item.LastError).Append('|')
          .Append(item.Remark).Append('|')
          .Append(item.ManualUploadStatus).Append('|')
          .Append(item.UploadCompletedAt).Append('|')
          .Append(item.Title).Append('|')
          .Append(item.NewTitle).Append('|')
          .Append(item.AccountProfileName).Append('|')
          .Append(item.EpisodeCount).Append('|')
          .Append(item.QueuedAt);
        foreach (var (key, value) in item.StepStates.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            sb.Append('|').Append(key).Append('=').Append(value);
        return sb.ToString();
    }

    public int RowIndex
    {
        get => _rowIndex;
        set => SetProperty(ref _rowIndex, value);
    }

    public bool IsEnabled
    {
        get => Item.Enabled;
        set
        {
            if (Item.Enabled == value) return;
            Item.Enabled = value;
            OnPropertyChanged();
            EnabledChangedByUser?.Invoke(this);
        }
    }
    public string Title => Item.Title;
    public string Remark
    {
        get => Item.Remark;
        set
        {
            value ??= "";
            if (string.Equals(Item.Remark, value, StringComparison.Ordinal))
                return;
            Item.Remark = value;
            OnPropertyChanged();
            RemarkChangedByUser?.Invoke(this);
        }
    }
    public string OriginalTitle => string.IsNullOrWhiteSpace(Item.OriginalTitle) ? Item.DisplayName : Item.OriginalTitle;
    public string NewTitle => _newTitle;
    public string OriginalProjectDir => Item.ProjectDir;
    public string NewProjectDir => _newProjectDir;

    private static string ResolveNewTitle(QueueProjectItem item, string newProjectDir) => FirstNonEmpty(
        item.NewTitle,
        ResolveWorkflowDisplayName(newProjectDir),
        item.Title,
        string.IsNullOrWhiteSpace(item.OriginalTitle) ? item.DisplayName : item.OriginalTitle);
    public int EpisodeCount => Item.EpisodeCount;
    public string QueuedAt => FormatQueuedAt(Item.QueuedAt, compact: true);
    public string QueuedAtTooltip => FormatQueuedAt(Item.QueuedAt, compact: false);
    public string AccountName => string.IsNullOrWhiteSpace(Item.AccountProfileName)
        ? (string.IsNullOrWhiteSpace(Item.AccountProfileId) ? "(未绑定)" : Item.AccountProfileId)
        : Item.AccountProfileName;

    public string DownloadStatus => StepOf(QueueStepKeys.Download);
    public string RewriteStatus => StepOf(QueueStepKeys.RewriteInfo);
    public string PosterStatus => StepOf(QueueStepKeys.GeneratePoster);
    public string ProjectImageStatus => StepOf(QueueStepKeys.GenerateProjectImages);
    public string ProofMaterialStatus => StepOf(QueueStepKeys.GenerateProofMaterial);
    public string RepairStatus => StepOf(QueueStepKeys.SmallVideoRepair);
    public string VideoTranslateStatus => StepOf(QueueStepKeys.VideoTranslate);
    public string SilenceDetectStatus => StepOf(QueueStepKeys.SilenceDetect);
    public string SilenceRepairStatus => StepOf(QueueStepKeys.SilenceRepair);
    public string ValidateStatus => StepOf(QueueStepKeys.MaterialValidate);
    public string DeleteSourceStatus => StepOf(QueueStepKeys.DeleteSourceVideos);
    public string UploadStatus => StepOf(QueueStepKeys.UploadSeries);

    public string StatusText => Item.StatusText;
    public IBrush DownloadStatusBrush => BrushOf(DownloadStatus);
    public IBrush RewriteStatusBrush => BrushOf(RewriteStatus);
    public IBrush PosterStatusBrush => BrushOf(PosterStatus);
    public IBrush ProjectImageStatusBrush => BrushOf(ProjectImageStatus);
    public IBrush ProofMaterialStatusBrush => BrushOf(ProofMaterialStatus);
    public IBrush RepairStatusBrush => BrushOf(RepairStatus);
    public IBrush VideoTranslateStatusBrush => BrushOf(VideoTranslateStatus);
    public IBrush SilenceDetectStatusBrush => BrushOf(SilenceDetectStatus);
    public IBrush SilenceRepairStatusBrush => BrushOf(SilenceRepairStatus);
    public IBrush ValidateStatusBrush => BrushOf(ValidateStatus);
    public IBrush DeleteSourceStatusBrush => BrushOf(DeleteSourceStatus);
    public IBrush UploadStatusBrush => BrushOf(UploadStatus);
    public IBrush StatusTextBrush => BrushOf(StatusText);
    public IBrush DramaTitleBrush => IsUploadCompleted
        ? CompletedTitleBrush
        : IsUploadActive
            ? RunningTitleBrush
            : HasFailure
                ? FailedTitleBrush
                : PrimaryTextBrush;
    public IBrush DownloadStatusBackgroundBrush => BackgroundOf(DownloadStatus);
    public IBrush RewriteStatusBackgroundBrush => BackgroundOf(RewriteStatus);
    public IBrush PosterStatusBackgroundBrush => BackgroundOf(PosterStatus);
    public IBrush ProjectImageStatusBackgroundBrush => BackgroundOf(ProjectImageStatus);
    public IBrush ProofMaterialStatusBackgroundBrush => BackgroundOf(ProofMaterialStatus);
    public IBrush RepairStatusBackgroundBrush => BackgroundOf(RepairStatus);
    public IBrush VideoTranslateStatusBackgroundBrush => BackgroundOf(VideoTranslateStatus);
    public IBrush SilenceDetectStatusBackgroundBrush => BackgroundOf(SilenceDetectStatus);
    public IBrush SilenceRepairStatusBackgroundBrush => BackgroundOf(SilenceRepairStatus);
    public IBrush ValidateStatusBackgroundBrush => BackgroundOf(ValidateStatus);
    public IBrush DeleteSourceStatusBackgroundBrush => BackgroundOf(DeleteSourceStatus);
    public IBrush UploadStatusBackgroundBrush => BackgroundOf(UploadStatus);
    public IBrush StatusTextBackgroundBrush => BackgroundOf(StatusText);
    public IBrush DownloadStatusBorderBrush => BorderOf(DownloadStatus);
    public IBrush RewriteStatusBorderBrush => BorderOf(RewriteStatus);
    public IBrush PosterStatusBorderBrush => BorderOf(PosterStatus);
    public IBrush ProjectImageStatusBorderBrush => BorderOf(ProjectImageStatus);
    public IBrush ProofMaterialStatusBorderBrush => BorderOf(ProofMaterialStatus);
    public IBrush RepairStatusBorderBrush => BorderOf(RepairStatus);
    public IBrush VideoTranslateStatusBorderBrush => BorderOf(VideoTranslateStatus);
    public IBrush SilenceDetectStatusBorderBrush => BorderOf(SilenceDetectStatus);
    public IBrush SilenceRepairStatusBorderBrush => BorderOf(SilenceRepairStatus);
    public IBrush ValidateStatusBorderBrush => BorderOf(ValidateStatus);
    public IBrush DeleteSourceStatusBorderBrush => BorderOf(DeleteSourceStatus);
    public IBrush UploadStatusBorderBrush => BorderOf(UploadStatus);
    public IBrush StatusTextBorderBrush => BorderOf(StatusText);
    public string LastError => Item.LastError;
    public bool IsPendingUpload => Item.IsPendingUpload;
    public bool IsUploadActive =>
        string.Equals(Item.CurrentStep, QueueStepRegistry.UploadSeries, StringComparison.Ordinal) ||
        UploadStatus == QueueStepStatus.Running ||
        StatusText == QueueStepStatus.Running;

    public bool IsUploadCompleted => UploadStatus == QueueStepStatus.Completed;
    public bool HasFailure =>
        !IsUploadCompleted &&
        (StatusText == QueueStepStatus.Failed ||
         UploadStatus == QueueStepStatus.Failed ||
         !string.IsNullOrWhiteSpace(LastError) ||
         Item.StepStates.Values.Any(status => status == QueueStepStatus.Failed));

    public string CurrentStepLabel => string.IsNullOrWhiteSpace(Item.CurrentStep)
        ? ""
        : QueueStepRegistry.LabelOf(Item.CurrentStep);

    public string DetailText => string.IsNullOrWhiteSpace(CurrentStepLabel)
        ? LastError
        : string.IsNullOrWhiteSpace(LastError)
            ? CurrentStepLabel
            : $"{CurrentStepLabel} · {LastError}";

    private string StepOf(string key) =>
        Item.StepStates.GetValueOrDefault(key, QueueStepStatus.Pending);

    private static IBrush BrushOf(string status) => status switch
    {
        QueueStepStatus.Completed => CompletedBrush,
        QueueStepStatus.Pending => PendingBrush,
        QueueStepStatus.Running => RunningBrush,
        QueueStepStatus.Failed => FailedBrush,
        QueueStepStatus.Stopped => StoppedBrush,
        QueueStepStatus.WaitingUploadSlot => UploadSlotBrush,
        QueueStepStatus.ManualIntervention => ManualInterventionBrush,
        _ => DefaultBrush,
    };

    private static IBrush BackgroundOf(string status) => status switch
    {
        QueueStepStatus.Completed => CompletedBackgroundBrush,
        QueueStepStatus.Pending => PendingBackgroundBrush,
        QueueStepStatus.Running => RunningBackgroundBrush,
        QueueStepStatus.Failed => FailedBackgroundBrush,
        QueueStepStatus.Stopped => StoppedBackgroundBrush,
        QueueStepStatus.WaitingUploadSlot => UploadSlotBackgroundBrush,
        QueueStepStatus.ManualIntervention => ManualInterventionBackgroundBrush,
        _ => DefaultBackgroundBrush,
    };

    private static IBrush BorderOf(string status) => status switch
    {
        QueueStepStatus.Completed => CompletedBorderBrush,
        QueueStepStatus.Pending => PendingBorderBrush,
        QueueStepStatus.Running => RunningBorderBrush,
        QueueStepStatus.Failed => FailedBorderBrush,
        QueueStepStatus.Stopped => StoppedBorderBrush,
        QueueStepStatus.WaitingUploadSlot => UploadSlotBorderBrush,
        QueueStepStatus.ManualIntervention => ManualInterventionBorderBrush,
        _ => DefaultBorderBrush,
    };

    private static readonly IBrush CompletedBrush = new SolidColorBrush(Color.Parse("#168568"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#B97812"));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#167E94"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#CC4055"));
    private static readonly IBrush StoppedBrush = new SolidColorBrush(Color.Parse("#677086"));
    private static readonly IBrush UploadSlotBrush = new SolidColorBrush(Color.Parse("#8A4B00"));
    private static readonly IBrush ManualInterventionBrush = new SolidColorBrush(Color.Parse("#C2410C"));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#3E465A"));
    private static readonly IBrush PrimaryTextBrush = new SolidColorBrush(Color.Parse("#F7FBFF"));
    // Title colors are tuned for the dark queue row, unlike the darker status-badge
    // foregrounds above which sit on light badge backgrounds.
    private static readonly IBrush CompletedTitleBrush = new SolidColorBrush(Color.Parse("#6EE7B7"));
    private static readonly IBrush RunningTitleBrush = new SolidColorBrush(Color.Parse("#8EDBFF"));
    private static readonly IBrush FailedTitleBrush = new SolidColorBrush(Color.Parse("#FFC2C9"));
    private static readonly IBrush CompletedBackgroundBrush = new SolidColorBrush(Color.Parse("#E4F5EF"));
    private static readonly IBrush PendingBackgroundBrush = new SolidColorBrush(Color.Parse("#FFF4D9"));
    private static readonly IBrush RunningBackgroundBrush = new SolidColorBrush(Color.Parse("#E5F4F6"));
    private static readonly IBrush FailedBackgroundBrush = new SolidColorBrush(Color.Parse("#FFF0F3"));
    private static readonly IBrush StoppedBackgroundBrush = new SolidColorBrush(Color.Parse("#ECEEF4"));
    private static readonly IBrush UploadSlotBackgroundBrush = new SolidColorBrush(Color.Parse("#FFE9A8"));
    private static readonly IBrush ManualInterventionBackgroundBrush = new SolidColorBrush(Color.Parse("#FFEDD5"));
    private static readonly IBrush DefaultBackgroundBrush = new SolidColorBrush(Color.Parse("#F1F2F6"));
    private static readonly IBrush CompletedBorderBrush = new SolidColorBrush(Color.Parse("#8ED0BA"));
    private static readonly IBrush PendingBorderBrush = new SolidColorBrush(Color.Parse("#E9B75B"));
    private static readonly IBrush RunningBorderBrush = new SolidColorBrush(Color.Parse("#8CC9D3"));
    private static readonly IBrush FailedBorderBrush = new SolidColorBrush(Color.Parse("#E89AA8"));
    private static readonly IBrush StoppedBorderBrush = new SolidColorBrush(Color.Parse("#C9CFDF"));
    private static readonly IBrush UploadSlotBorderBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush ManualInterventionBorderBrush = new SolidColorBrush(Color.Parse("#FB923C"));
    private static readonly IBrush DefaultBorderBrush = new SolidColorBrush(Color.Parse("#C9CFDF"));

    private static string ResolveWorkflowProjectDir(string projectDir)
    {
        try
        {
            return ProjectWorkspaceService.ResolveWorkflowProjectDir(projectDir);
        }
        catch
        {
            return "";
        }
    }

    private static string ResolveWorkflowDisplayName(string workflowProjectDir)
    {
        if (string.IsNullOrWhiteSpace(workflowProjectDir)) return "";

        var name = Path.GetFileName(workflowProjectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, "workflow", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return name.TrimStart('_').Trim();
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return "";
    }

    internal static string FormatQueuedAt(string value, bool compact)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return "";

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            var local = dto.ToLocalTime();
            return compact
                ? local.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                : local.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return compact
                ? dt.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        return text.Replace('T', ' ');
    }
}
