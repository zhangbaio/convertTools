using TikTokPublisher.Core.Queue;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class WorkspaceProjectItemViewModel : ViewModelBase
{
    public QueueProjectItem Item { get; }

    public WorkspaceProjectItemViewModel(QueueProjectItem item) => Item = item;

    public string DisplayName => Item.DisplayName;
    public string Title => Item.Title;
    public string VideoName => Item.PrimaryVideoPath is null ? "(无视频)" : Path.GetFileName(Item.PrimaryVideoPath);
    public int EpisodeCount => Item.EpisodeCount;
    public string GenreCategory => Item.GenreCategory;
    public string UploadStatus => Item.UploadSeriesStatus;
    public string StatusText => Item.StatusText;
    public bool IsPendingUpload => Item.IsPendingUpload;
}
