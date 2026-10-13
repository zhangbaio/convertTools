using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PlatformPublisher.Weixin.Publishing;

namespace PlatformPublisher.Desktop.Views;

public partial class WeixinPublishConfigDialog : Window
{
    private WeixinPublishOptions _options = new();

    public WeixinPublishConfigDialog() => InitializeComponent();

    public static async Task<WeixinPublishOptions?> ShowAsync(
        Window owner,
        WeixinPublishOptions options)
    {
        var dialog = new WeixinPublishConfigDialog();
        dialog.LoadOptions(options);
        return await dialog.ShowDialog<WeixinPublishOptions?>(owner);
    }

    private void LoadOptions(WeixinPublishOptions options)
    {
        _options = options;
        SelectByTag(EpisodeModeCombo, options.EpisodeSelectionMode);
        StartEpisodeInput.Value = options.StartEpisodeIndex;
        EpisodeIndexesInput.Text = options.EpisodeIndexes;
        MergeEnabledCheck.IsChecked = options.MergePublishEnabled;
        MergeGroupInput.Value = options.MergePublishGroupSize;
        SelectByTag(FinalActionCombo, options.FinalAction);
        PauseOnErrorCheck.IsChecked = options.PauseOnError;
        FastModeCheck.IsChecked = options.FastMode;
        CaptureScreenshotsCheck.IsChecked = options.CaptureScreenshots;
        DebugDumpsCheck.IsChecked = options.CaptureDebugDumps;
        FillDescriptionCheck.IsChecked = options.FillDescription;
        AiDescriptionCheck.IsChecked = options.AiDescriptionEnabled;
        AiUseAsrCheck.IsChecked = options.AiDescriptionUseAsr;
        PrependHashCheck.IsChecked = options.PrependHashToDescription;
        DescriptionInput.Text = options.DescriptionTemplate;
        LocationInput.Text = options.LocationOptionText;
        LinkOptionInput.Text = options.LinkOptionText;
        LinkPickerInput.Text = options.LinkPickerButtonText;
        LinkDialogInput.Text = options.LinkDialogTitle;
        LinkSearchInput.Text = options.LinkSearchPlaceholder;
        ActivityInput.Text = options.ActivityOptionText;
        TimingInput.Text = options.TimingOptionText;
        ReplaceCoverCheck.IsChecked = options.ReplaceCoverWithLocalImage;
        CoverPathInput.Text = options.CoverImagePath;
        FillShortTitleCheck.IsChecked = options.FillShortTitle;
        ShortTitleLengthInput.Value = options.ShortTitleMaxLength;
        DeclareOriginalCheck.IsChecked = options.DeclareOriginal;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _options.EpisodeSelectionMode = SelectedTag(EpisodeModeCombo, "range");
        _options.StartEpisodeIndex = Decimal.ToInt32(StartEpisodeInput.Value ?? 1);
        _options.EpisodeIndexes = EpisodeIndexesInput.Text?.Trim() ?? string.Empty;
        _options.MergePublishEnabled = MergeEnabledCheck.IsChecked == true;
        _options.MergePublishGroupSize = Decimal.ToInt32(MergeGroupInput.Value ?? 0);
        _options.FinalAction = SelectedTag(FinalActionCombo, "publish");
        _options.PauseOnError = PauseOnErrorCheck.IsChecked == true;
        _options.FastMode = FastModeCheck.IsChecked == true;
        _options.CaptureScreenshots = CaptureScreenshotsCheck.IsChecked == true;
        _options.CaptureDebugDumps = DebugDumpsCheck.IsChecked == true;
        _options.FillDescription = FillDescriptionCheck.IsChecked == true;
        _options.AiDescriptionEnabled = AiDescriptionCheck.IsChecked == true;
        _options.AiDescriptionUseAsr = AiUseAsrCheck.IsChecked == true;
        _options.PrependHashToDescription = PrependHashCheck.IsChecked == true;
        _options.DescriptionTemplate = DescriptionInput.Text?.Trim() ?? string.Empty;
        _options.LocationOptionText = LocationInput.Text?.Trim() ?? string.Empty;
        _options.LinkOptionText = LinkOptionInput.Text?.Trim() ?? string.Empty;
        _options.LinkPickerButtonText = LinkPickerInput.Text?.Trim() ?? string.Empty;
        _options.LinkDialogTitle = LinkDialogInput.Text?.Trim() ?? string.Empty;
        _options.LinkSearchPlaceholder = LinkSearchInput.Text?.Trim() ?? string.Empty;
        _options.ActivityOptionText = ActivityInput.Text?.Trim() ?? string.Empty;
        _options.TimingOptionText = TimingInput.Text?.Trim() ?? string.Empty;
        _options.ReplaceCoverWithLocalImage = ReplaceCoverCheck.IsChecked == true;
        _options.CoverImagePath = CoverPathInput.Text?.Trim() ?? string.Empty;
        _options.FillShortTitle = FillShortTitleCheck.IsChecked == true;
        _options.ShortTitleMaxLength = Decimal.ToInt32(ShortTitleLengthInput.Value ?? 16);
        _options.DeclareOriginal = DeclareOriginalCheck.IsChecked == true;
        Close(_options);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnBrowseCoverClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择视频号封面图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count > 0)
            CoverPathInput.Text = files[0].Path.LocalPath;
    }

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string SelectedTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
}
