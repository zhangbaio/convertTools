using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class QueueProjectRowViewModel : ViewModelBase
{
    private int _rowIndex;

    public QueueProjectItem Item { get; private set; }

    public QueueProjectRowViewModel(QueueProjectItem item) => Item = item;

    public void RefreshFrom(QueueProjectItem item)
    {
        Item = item;
        OnPropertyChanged(string.Empty);
    }

    public int RowIndex
    {
        get => _rowIndex;
        set => SetProperty(ref _rowIndex, value);
    }

    public bool IsEnabled => Item.Enabled;
    public string Title => Item.Title;
    public string OriginalTitle => string.IsNullOrWhiteSpace(Item.OriginalTitle) ? Item.DisplayName : Item.OriginalTitle;
    public string NewTitle => FirstNonEmpty(
        Item.NewTitle,
        ResolveWorkflowDisplayName(NewProjectDir),
        Item.Title,
        OriginalTitle);
    public string OriginalProjectDir => Item.ProjectDir;
    public string NewProjectDir => ResolveWorkflowProjectDir(Item.ProjectDir);
    public int EpisodeCount => Item.EpisodeCount;
    public string QueuedAt => Item.QueuedAt;
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
        QueueStepStatus.WaitingUploadSlot => PendingBrush,
        QueueStepStatus.ManualIntervention => PendingBrush,
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
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.Parse("#0B63CE"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#B42318"));
    private static readonly IBrush StoppedBrush = new SolidColorBrush(Color.Parse("#6D5A48"));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#231F1A"));
    private static readonly IBrush CompletedBackgroundBrush = new SolidColorBrush(Color.Parse("#DCFCE7"));
    private static readonly IBrush PendingBackgroundBrush = new SolidColorBrush(Color.Parse("#FFF4D6"));
    private static readonly IBrush RunningBackgroundBrush = new SolidColorBrush(Color.Parse("#DBEAFE"));
    private static readonly IBrush FailedBackgroundBrush = new SolidColorBrush(Color.Parse("#FEE2E2"));
    private static readonly IBrush StoppedBackgroundBrush = new SolidColorBrush(Color.Parse("#EFE7DD"));
    private static readonly IBrush UploadSlotBackgroundBrush = new SolidColorBrush(Color.Parse("#FEF3C7"));
    private static readonly IBrush ManualInterventionBackgroundBrush = new SolidColorBrush(Color.Parse("#FFEDD5"));
    private static readonly IBrush DefaultBackgroundBrush = new SolidColorBrush(Color.Parse("#F3F4F6"));
    private static readonly IBrush CompletedBorderBrush = new SolidColorBrush(Color.Parse("#86EFAC"));
    private static readonly IBrush PendingBorderBrush = new SolidColorBrush(Color.Parse("#FCD34D"));
    private static readonly IBrush RunningBorderBrush = new SolidColorBrush(Color.Parse("#93C5FD"));
    private static readonly IBrush FailedBorderBrush = new SolidColorBrush(Color.Parse("#FCA5A5"));
    private static readonly IBrush StoppedBorderBrush = new SolidColorBrush(Color.Parse("#D7C2AA"));
    private static readonly IBrush UploadSlotBorderBrush = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush ManualInterventionBorderBrush = new SolidColorBrush(Color.Parse("#FDBA74"));
    private static readonly IBrush DefaultBorderBrush = new SolidColorBrush(Color.Parse("#D1D5DB"));

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
}
