using CommunityToolkit.Mvvm.ComponentModel;
using ShortDrama.Core.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed partial class DramaSearchRowViewModel : ObservableObject
{
    public DramaSearchRowViewModel(DramaSearchItem drama) => Drama = drama;
    public DramaSearchItem Drama { get; }
    public string Title => Drama.Title;
    public string Category => Drama.Category;
    public string EpisodeSummary => $"{Drama.EpisodeTotal} 集";
    public string Intro => Drama.Intro;
    public string BookId => Drama.BookId;
    [ObservableProperty] private bool _isChecked;
}
