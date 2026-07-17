namespace ShortDrama.Desktop.Models;

public sealed record MaterialPublishVideoItemViewModel(
    int EpisodeIndex,
    string VideoName,
    string StatusText)
{
    public string EpisodeLabel => $"第{EpisodeIndex}集";
}
