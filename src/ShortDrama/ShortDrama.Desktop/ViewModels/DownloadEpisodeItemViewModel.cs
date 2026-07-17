using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;

namespace ShortDrama.Desktop.ViewModels;

public partial class DownloadEpisodeItemViewModel : ViewModelBase
{
    public DownloadEpisodeItemViewModel(int episodeNumber, string dramaTitle)
    {
        EpisodeNumber = episodeNumber;
        DramaTitle = dramaTitle;
        EpisodeLabel = $"第{episodeNumber:00}集 第{episodeNumber}集";
    }

    public int EpisodeNumber { get; }
    public string DramaTitle { get; }
    public string EpisodeLabel { get; }

    [ObservableProperty]
    private string statusText = "待下载";

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private string speedText = "--";

    public string ProgressText => $"{ProgressPercent:0.#}%";
    public bool CanRetry => string.Equals(StatusText, "失败", StringComparison.Ordinal);
    public IBrush StatusBrush => ResolveStatusBrush(StatusText);

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(StatusBrush));
    }

    partial void OnProgressPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    private static IBrush ResolveStatusBrush(string status)
    {
        return status switch
        {
            "完成" => Brushes.LimeGreen,
            "下载中" => Brushes.LightSkyBlue,
            "失败" => Brushes.IndianRed,
            _ => Brushes.LightGray
        };
    }
}
