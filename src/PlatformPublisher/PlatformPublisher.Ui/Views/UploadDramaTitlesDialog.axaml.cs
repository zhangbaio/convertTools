using Avalonia.Controls;
using Avalonia.Interactivity;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Desktop.ViewModels;
using TikTokPublisher.Core.Drama;

namespace PlatformPublisher.Desktop.Views;

public partial class UploadDramaTitlesDialog : Window
{
    internal sealed record UploadRequest(string Titles, string MatchMode);
    private bool _prepareForUpload;

    public UploadDramaTitlesDialog() => InitializeComponent();

    public static Task<string?> ShowAsync(Window owner) => new UploadDramaTitlesDialog().ShowDialog<string?>(owner);

    internal static Task<UploadRequest?> ShowWeixinUploadAsync(Window owner, MainWindowViewModel.WeixinTitleUploadTarget target)
    {
        var dialog = new UploadDramaTitlesDialog { Height = 490, MinHeight = 430, _prepareForUpload = true };
        dialog.TargetText.IsVisible = true;
        dialog.TargetText.Text = $"当前账号 · {target.AccountName} · 搜索链路 · {target.SourceLabel}";
        dialog.InstructionsText.Text = "一行一个剧名。确认后会搜索并入队，校验通过后弹出上传确认。";
        dialog.LimitsText.Text = $"集数 {DramaTitleImportService.MinimumEpisodes}–{DramaTitleImportService.MaximumEpisodes} 集\n"
            + $"准备步骤 · {target.Preparation.DescribeSteps()}";
        dialog.MatchOptions.IsVisible = true;
        dialog.TitlesTextBox.Watermark = "仅剧名匹配：风月无凭\n剧名 + 集数匹配：风月无凭 43";
        dialog.ConfirmButton.Content = "入队并执行";
        return dialog.ShowDialog<UploadRequest?>(owner);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        var text = TitlesTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            TitlesTextBox.Focus();
            return;
        }

        if (_prepareForUpload)
            Close(new UploadRequest(text, MatchModeBox.SelectedIndex == 1
                ? UploadTitleImportService.MatchModeTitleEpisode : UploadTitleImportService.MatchModeTitle));
        else
            Close(text);
    }
}
