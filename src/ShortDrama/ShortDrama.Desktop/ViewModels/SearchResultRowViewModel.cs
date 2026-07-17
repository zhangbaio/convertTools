using CommunityToolkit.Mvvm.ComponentModel;
using ShortDrama.Core.Models;

namespace ShortDrama.Desktop.ViewModels;

public partial class SearchResultRowViewModel : ViewModelBase
{
    public SearchResultRowViewModel(DramaSearchItem drama)
    {
        Drama = drama;
    }

    public DramaSearchItem Drama { get; }

    public string BookId => Drama.BookId;
    public string Title => Drama.Title;
    public string Category => Drama.Category;
    public int EpisodeTotal => Drama.EpisodeTotal;
    public string Intro => Drama.Intro;
    public string PosterUrl => Drama.PosterUrl;
    public string Author => Drama.Author;
    public string PublishTime => Drama.PublishTime;
    public int FavoriteCount => Drama.FavoriteCount;
    public string FavoriteCountText => FavoriteCount <= 0 ? "-" : FavoriteCount.ToString();

    [ObservableProperty]
    private bool isChecked;

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CheckedChanged;
}
