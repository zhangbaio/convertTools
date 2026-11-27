using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Ui.Views;

public partial class ProjectImageOptionsDialog : Window
{
    private readonly TikTokProjectImageOptions _options;

    public ProjectImageOptionsDialog(TikTokProjectImageOptions options)
    {
        _options = options;
        InitializeComponent();
        SubtitleModeBox.SelectedItem = SubtitleModeBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, options.SubtitleAiMode)) ?? SubtitleModeBox.Items[0];
        CountBox.Value = Math.Clamp(options.Count, 1, 20);
        EpisodeLimitBox.Value = Math.Clamp(options.EpisodeLimit, 1, 200);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(_options with
    {
        SubtitleAiMode = (SubtitleModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? _options.SubtitleAiMode,
        Count = (int)(CountBox.Value ?? _options.Count),
        EpisodeLimit = (int)(EpisodeLimitBox.Value ?? _options.EpisodeLimit),
    });
}
