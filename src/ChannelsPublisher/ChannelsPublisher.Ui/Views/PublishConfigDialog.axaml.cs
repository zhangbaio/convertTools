using Avalonia.Controls;
using Avalonia.Interactivity;
using ChannelsPublisher.Core.Config;

namespace ChannelsPublisher.Desktop.Views;

public partial class PublishConfigDialog : Window
{
    private readonly PublishConfig _config;

    public PublishConfigDialog() : this(PublishConfig.Load()) { }

    public PublishConfigDialog(PublishConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadToUi();
    }

    private void LoadToUi()
    {
        var c = _config;
        EnabledBox.IsChecked = c.Enabled;
        SelectByTag(SourceModeCombo, c.VideoSourceMode);
        SelectByTag(EpisodeModeCombo, c.EpisodeSelectionMode);
        StartEpisodeBox.Value = c.StartEpisode;
        PublishCountBox.Value = c.PublishCount;
        EpisodeIndexesBox.Text = c.EpisodeIndexes;
        NewDramaMountTitleBox.Text = c.NewDramaMountTitle;
        SelectByTag(RunStrategyCombo, c.RunStrategy);
        SelectByTag(FinalActionCombo, c.FinalAction);
        PauseOnErrorBox.IsChecked = c.PauseOnError;
        FastModeBox.IsChecked = c.FastMode;
        MergePublishBox.IsChecked = c.MergePublish;
        MergeGroupSizeBox.Value = c.MergeGroupSize;
        FillDescriptionBox.IsChecked = c.FillDescription;
        AiDescriptionBox.IsChecked = c.AiDescription;
        AiUseDialogueBox.IsChecked = c.AiUseDialogue;
        PrependHashBox.IsChecked = c.PrependHash;
        DescriptionTemplateBox.Text = c.DescriptionTemplate;
        LocationBox.Text = c.Location;
        ActivityBox.Text = c.Activity;
        LinkBox.Text = c.Link;
        DramaNameBox.Text = c.DramaName;
        ReplaceCoverBox.IsChecked = c.ReplaceCover;
        FillShortTitleBox.IsChecked = c.FillShortTitle;
        ShortTitleMaxBox.Value = c.ShortTitleMax;
        DeclareOriginalBox.IsChecked = c.DeclareOriginal;
        CoverImagePathBox.Text = c.CoverImagePath;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var c = _config;
        c.Enabled = EnabledBox.IsChecked == true;
        c.VideoSourceMode = TagOf(SourceModeCombo, "directory");
        c.EpisodeSelectionMode = TagOf(EpisodeModeCombo, "all");
        c.StartEpisode = (int)(StartEpisodeBox.Value ?? 1);
        c.PublishCount = (int)(PublishCountBox.Value ?? 0);
        c.EpisodeIndexes = EpisodeIndexesBox.Text?.Trim() ?? "";
        c.NewDramaMountTitle = NewDramaMountTitleBox.Text?.Trim() ?? "";
        c.RunStrategy = TagOf(RunStrategyCombo, "all");
        c.FinalAction = TagOf(FinalActionCombo, "none");
        c.PauseOnError = PauseOnErrorBox.IsChecked == true;
        c.FastMode = FastModeBox.IsChecked == true;
        c.MergePublish = MergePublishBox.IsChecked == true;
        c.MergeGroupSize = (int)(MergeGroupSizeBox.Value ?? 0);
        c.FillDescription = FillDescriptionBox.IsChecked == true;
        c.AiDescription = AiDescriptionBox.IsChecked == true;
        c.AiUseDialogue = AiUseDialogueBox.IsChecked == true;
        c.PrependHash = PrependHashBox.IsChecked == true;
        c.DescriptionTemplate = DescriptionTemplateBox.Text ?? "";
        c.Location = LocationBox.Text?.Trim() ?? "";
        c.Activity = ActivityBox.Text?.Trim() ?? "";
        c.Link = LinkBox.Text?.Trim() ?? "";
        c.DramaName = DramaNameBox.Text?.Trim() ?? "";
        c.ReplaceCover = ReplaceCoverBox.IsChecked == true;
        c.FillShortTitle = FillShortTitleBox.IsChecked == true;
        c.ShortTitleMax = (int)(ShortTitleMaxBox.Value ?? 6);
        c.DeclareOriginal = DeclareOriginalBox.IsChecked == true;
        c.CoverImagePath = CoverImagePathBox.Text?.Trim() ?? "";
        c.Save();
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
