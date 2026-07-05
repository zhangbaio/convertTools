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

    public QueueProjectRowViewModel(QueueProjectItem item)
    {
        Item = item;
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
    public string NewTitle => FirstNonEmpty(
        Item.NewTitle,
        ResolveWorkflowDisplayName(NewProjectDir),
        Item.Title,
        OriginalTitle);
    public string OriginalProjectDir => Item.ProjectDir;
    public string NewProjectDir => ResolveWorkflowProjectDir(Item.ProjectDir);
    public int EpisodeCount => Item.EpisodeCount;
    public string QueuedAt => FormatQueuedAt(Item.QueuedAt, compact: true);
    public string QueuedAtTooltip => FormatQueuedAt(Item.QueuedAt, compact: false);
    public string AccountName => string.IsNullOrWhiteSpace(Item.AccountProfileName)
        ? (string.IsNullOrWhiteSpace(Item.AccountProfileId) ? "(未绑定)" : Item.AccountProfileId)
        : Item.AccountProfileName;

    public string DownloadStatus => StepOf(QueueStepKeys.Download);
    public string RewriteStatus => StepOf(QueueStepKeys.RewriteInfo);
    public string PosterStatus => StepOf(QueueStepKeys.GeneratePoster);
    public string RepairStatus => StepOf(QueueStepKeys.SmallVideoRepair);
    public string SilenceDetectStatus => StepOf(QueueStepKeys.SilenceDetect);
    public string SilenceRepairStatus => StepOf(QueueStepKeys.SilenceRepair);
    public string ValidateStatus => StepOf(QueueStepKeys.MaterialValidate);
    public string DeleteSourceStatus => StepOf(QueueStepKeys.DeleteSourceVideos);
    public string UploadStatus => StepOf(QueueStepKeys.UploadSeries);

    public string StatusText => Item.StatusText;
    public IBrush DownloadStatusBrush => BrushOf(DownloadStatus);
    public IBrush RewriteStatusBrush => BrushOf(RewriteStatus);
    public IBrush PosterStatusBrush => BrushOf(PosterStatus);
    public IBrush RepairStatusBrush => BrushOf(RepairStatus);
    public IBrush SilenceDetectStatusBrush => BrushOf(SilenceDetectStatus);
    public IBrush SilenceRepairStatusBrush => BrushOf(SilenceRepairStatus);
    public IBrush ValidateStatusBrush => BrushOf(ValidateStatus);
    public IBrush DeleteSourceStatusBrush => BrushOf(DeleteSourceStatus);
    public IBrush UploadStatusBrush => BrushOf(UploadStatus);
    public IBrush StatusTextBrush => BrushOf(StatusText);
    public IBrush DramaTitleBrush => IsUploadCompleted
        ? CompletedBrush
        : HasFailure
            ? FailedBrush
            : LinkBrush;
    public IBrush DownloadStatusBackgroundBrush => BackgroundOf(DownloadStatus);
    public IBrush RewriteStatusBackgroundBrush => BackgroundOf(RewriteStatus);
    public IBrush PosterStatusBackgroundBrush => BackgroundOf(PosterStatus);
    public IBrush RepairStatusBackgroundBrush => BackgroundOf(RepairStatus);
    public IBrush SilenceDetectStatusBackgroundBrush => BackgroundOf(SilenceDetectStatus);
    public IBrush SilenceRepairStatusBackgroundBrush => BackgroundOf(SilenceRepairStatus);
    public IBrush ValidateStatusBackgroundBrush => BackgroundOf(ValidateStatus);
    public IBrush DeleteSourceStatusBackgroundBrush => BackgroundOf(DeleteSourceStatus);
    public IBrush UploadStatusBackgroundBrush => BackgroundOf(UploadStatus);
    public IBrush StatusTextBackgroundBrush => BackgroundOf(StatusText);
    public IBrush DownloadStatusBorderBrush => BorderOf(DownloadStatus);
    public IBrush RewriteStatusBorderBrush => BorderOf(RewriteStatus);
    public IBrush PosterStatusBorderBrush => BorderOf(PosterStatus);
    public IBrush RepairStatusBorderBrush => BorderOf(RepairStatus);
    public IBrush SilenceDetectStatusBorderBrush => BorderOf(SilenceDetectStatus);
    public IBrush SilenceRepairStatusBorderBrush => BorderOf(SilenceRepairStatus);
    public IBrush ValidateStatusBorderBrush => BorderOf(ValidateStatus);
    public IBrush DeleteSourceStatusBorderBrush => BorderOf(DeleteSourceStatus);
    public IBrush UploadStatusBorderBrush => BorderOf(UploadStatus);
    public IBrush StatusTextBorderBrush => BorderOf(StatusText);
    public string LastError => Item.LastError;
    public bool IsPendingUpload => Item.IsPendingUpload;
    public bool IsUploadCompleted =>
        UploadStatus == QueueStepStatus.Completed ||
        (StatusText == QueueStepStatus.Completed && string.IsNullOrWhiteSpace(LastError));
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

    private static readonly IBrush CompletedBrush = new SolidColorBrush(Color.Parse("#047857"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#B45309"));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#075BC7"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#B42318"));
    private static readonly IBrush StoppedBrush = new SolidColorBrush(Color.Parse("#64748B"));
    private static readonly IBrush UploadSlotBrush = new SolidColorBrush(Color.Parse("#8A4B00"));
    private static readonly IBrush ManualInterventionBrush = new SolidColorBrush(Color.Parse("#C2410C"));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#334155"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#0F63C7"));
    private static readonly IBrush CompletedBackgroundBrush = new SolidColorBrush(Color.Parse("#DDFBEA"));
    private static readonly IBrush PendingBackgroundBrush = new SolidColorBrush(Color.Parse("#FFF2CC"));
    private static readonly IBrush RunningBackgroundBrush = new SolidColorBrush(Color.Parse("#DDEBFF"));
    private static readonly IBrush FailedBackgroundBrush = new SolidColorBrush(Color.Parse("#FFE3E3"));
    private static readonly IBrush StoppedBackgroundBrush = new SolidColorBrush(Color.Parse("#EEF2F6"));
    private static readonly IBrush UploadSlotBackgroundBrush = new SolidColorBrush(Color.Parse("#FFE9A8"));
    private static readonly IBrush ManualInterventionBackgroundBrush = new SolidColorBrush(Color.Parse("#FFEDD5"));
    private static readonly IBrush DefaultBackgroundBrush = new SolidColorBrush(Color.Parse("#F1F5F9"));
    private static readonly IBrush CompletedBorderBrush = new SolidColorBrush(Color.Parse("#67E8A5"));
    private static readonly IBrush PendingBorderBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush RunningBorderBrush = new SolidColorBrush(Color.Parse("#86B7FF"));
    private static readonly IBrush FailedBorderBrush = new SolidColorBrush(Color.Parse("#F97066"));
    private static readonly IBrush StoppedBorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1"));
    private static readonly IBrush UploadSlotBorderBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush ManualInterventionBorderBrush = new SolidColorBrush(Color.Parse("#FB923C"));
    private static readonly IBrush DefaultBorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1"));

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
