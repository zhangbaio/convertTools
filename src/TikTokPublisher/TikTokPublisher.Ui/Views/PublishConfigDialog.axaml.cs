using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Config;

namespace TikTokPublisher.Ui.Views;

public partial class PublishConfigDialog : Window
{
    private readonly TikTokPublishConfig _config;

    public PublishConfigDialog() : this(TikTokPublishConfig.Load()) { }

    public PublishConfigDialog(TikTokPublishConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadToUi();
    }

    private void LoadToUi()
    {
        EnabledBox.IsChecked = _config.Enabled;
        SelectByTag(RunStrategyCombo, _config.RunStrategy);
        SelectByTag(FinalActionCombo, _config.FinalAction);
        PauseOnErrorBox.IsChecked = _config.PauseOnError;
        FillDescriptionBox.IsChecked = _config.FillDescription;
        DescriptionTemplateBox.Text = _config.DescriptionTemplate;
        DramaNameBox.Text = _config.DramaName;
        ReplaceCoverBox.IsChecked = _config.ReplaceCover;
        CoverImagePathBox.Text = _config.CoverImagePath;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _config.Enabled = EnabledBox.IsChecked == true;
        _config.RunStrategy = TagOf(RunStrategyCombo, "all");
        _config.FinalAction = TagOf(FinalActionCombo, "none");
        _config.PauseOnError = PauseOnErrorBox.IsChecked == true;
        _config.FillDescription = FillDescriptionBox.IsChecked == true;
        _config.DescriptionTemplate = DescriptionTemplateBox.Text ?? "";
        _config.DramaName = DramaNameBox.Text?.Trim() ?? "";
        _config.ReplaceCover = ReplaceCoverBox.IsChecked == true;
        _config.CoverImagePath = CoverImagePathBox.Text?.Trim() ?? "";
        _config.Save();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag) { combo.SelectedItem = ci; return; }
        if (combo.ItemCount > 0) combo.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
}
