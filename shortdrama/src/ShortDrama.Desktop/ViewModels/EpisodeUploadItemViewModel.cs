using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;

namespace ShortDrama.Desktop.ViewModels;

public partial class EpisodeUploadItemViewModel : ViewModelBase
{
    public EpisodeUploadItemViewModel(int episodeNumber, string dramaTitle)
    {
        EpisodeNumber = episodeNumber;
        DramaTitle = dramaTitle;
        EpisodeLabel = $"第{episodeNumber:00}集 第{episodeNumber}集";
    }

    public int EpisodeNumber { get; }
    public string DramaTitle { get; }
    public string EpisodeLabel { get; }

    [ObservableProperty]
    private string statusText = "待上传";

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private string actionText = "等待开始";

    [ObservableProperty]
    private string resultText = "未开始";

    public string ProgressText => $"{ProgressPercent:0.#}%";
    public bool CanRetry => string.Equals(StatusText, "失败", StringComparison.Ordinal);
    public bool CanSkip => !string.Equals(StatusText, "已完成", StringComparison.Ordinal);
    public bool CanMarkCompleted => !string.Equals(StatusText, "已完成", StringComparison.Ordinal);
    public IBrush StatusBrush => ResolveBrush(StatusText);

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanSkip));
        OnPropertyChanged(nameof(CanMarkCompleted));
        OnPropertyChanged(nameof(StatusBrush));
    }

    partial void OnProgressPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    private static IBrush ResolveBrush(string status)
    {
        return status switch
        {
            "已完成" => Brushes.LimeGreen,
            "上传中" => Brushes.LightSkyBlue,
            "失败" => Brushes.IndianRed,
            "等待人工" => Brushes.Gold,
            _ => Brushes.LightGray
        };
    }
}
