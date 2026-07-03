using TikTokPublisher.Core.Queue;
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
    public string NewTitle => string.IsNullOrWhiteSpace(Item.NewTitle) ? Item.Title : Item.NewTitle;
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
}
