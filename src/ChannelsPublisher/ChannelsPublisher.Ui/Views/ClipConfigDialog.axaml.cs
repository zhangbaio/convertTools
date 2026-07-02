using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChannelsPublisher.Core.Config;

namespace ChannelsPublisher.Desktop.Views;

public partial class ClipConfigDialog : Window
{
    private readonly ClipConfig _config;

    public ClipConfigDialog() : this(ClipConfig.Load()) { }

    public ClipConfigDialog(ClipConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadToUi();
    }

    private ClipModeSetting Mode(string key)
        => _config.Modes.FirstOrDefault(m => m.Key == key)
           ?? AddMode(key, key == "mashup" ? "混剪" : "高光");

    private ClipModeSetting AddMode(string key, string label)
    {
        var m = new ClipModeSetting { Key = key, Label = label, Enabled = key == "highlight", Count = 5 };
        _config.Modes.Add(m);
        return m;
    }

    private void LoadToUi()
    {
        var c = _config;
        var hi = Mode("highlight");
        HighlightEnabledBox.IsChecked = hi.Enabled;
        HighlightCountBox.Value = hi.Count;
        HighlightForceBox.IsChecked = hi.Force;
        var ma = Mode("mashup");
        MashupEnabledBox.IsChecked = ma.Enabled;
        MashupCountBox.Value = ma.Count;
        MashupForceBox.IsChecked = ma.Force;
        var sl = Mode("slice");
        SliceEnabledBox.IsChecked = sl.Enabled;
        SliceCountBox.Value = sl.Count;
        SliceForceBox.IsChecked = sl.Force;
        var co = Mode("commentary");
        CommentaryEnabledBox.IsChecked = co.Enabled;
        CommentaryCountBox.Value = co.Count;
        CommentaryForceBox.IsChecked = co.Force;

        EpisodeQuotaBox.IsChecked = c.EpisodeQuota;
        EnableLlmScoreBox.IsChecked = c.EnableLlmScore;
        AudioEnergyBox.IsChecked = c.AudioEnergy;
        ShotDensityBox.IsChecked = c.ShotDensity;
        LlmArrangeBox.IsChecked = c.LlmArrange;
        SmoothSelectionBox.IsChecked = c.SmoothSelection;
        PublishMetaBox.IsChecked = c.PublishMeta;

        PipelineConcurrentBox.Value = c.PipelineConcurrent;
        NetConcurrentBox.Value = c.NetConcurrent;
        SelectByTag(OutputQualityCombo, c.OutputQuality);
        VideoBitrateBox.Value = c.VideoBitrate;
        SelectByTag(EncodeModeCombo, c.EncodeMode);
        SelectByTag(VideoCodecCombo, c.VideoCodec);
        SelectByTag(RenderSpeedCombo, c.RenderSpeed);
        HardwareEncodeBox.IsChecked = c.HardwareEncode;

        TitleCardBox.IsChecked = c.TitleCard;
        TitleCardSecondsBox.Value = c.TitleCardSeconds;

        OrigEnabledBox.IsChecked = c.OrigEnabled;
        OrigZoomBox.IsChecked = c.OrigZoom;
        OrigColorBox.IsChecked = c.OrigColor;
        OrigSpeedBox.IsChecked = c.OrigSpeed;
        OrigFadeBox.IsChecked = c.OrigFade;
        OrigStickerDirBox.Text = c.OrigStickerDir;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var c = _config;
        var hi = Mode("highlight");
        hi.Enabled = HighlightEnabledBox.IsChecked == true;
        hi.Count = (int)(HighlightCountBox.Value ?? 5);
        hi.Force = HighlightForceBox.IsChecked == true;
        var ma = Mode("mashup");
        ma.Enabled = MashupEnabledBox.IsChecked == true;
        ma.Count = (int)(MashupCountBox.Value ?? 5);
        ma.Force = MashupForceBox.IsChecked == true;
        var sl = Mode("slice");
        sl.Enabled = SliceEnabledBox.IsChecked == true;
        sl.Count = (int)(SliceCountBox.Value ?? 3);
        sl.Force = SliceForceBox.IsChecked == true;
        var co = Mode("commentary");
        co.Enabled = CommentaryEnabledBox.IsChecked == true;
        co.Count = (int)(CommentaryCountBox.Value ?? 1);
        co.Force = CommentaryForceBox.IsChecked == true;

        c.EpisodeQuota = EpisodeQuotaBox.IsChecked == true;
        c.EnableLlmScore = EnableLlmScoreBox.IsChecked == true;
        c.AudioEnergy = AudioEnergyBox.IsChecked == true;
        c.ShotDensity = ShotDensityBox.IsChecked == true;
        c.LlmArrange = LlmArrangeBox.IsChecked == true;
        c.SmoothSelection = SmoothSelectionBox.IsChecked == true;
        c.PublishMeta = PublishMetaBox.IsChecked == true;

        c.PipelineConcurrent = (int)(PipelineConcurrentBox.Value ?? 4);
        c.NetConcurrent = (int)(NetConcurrentBox.Value ?? 4);
        c.OutputQuality = TagOf(OutputQualityCombo, "1080P");
        c.VideoBitrate = (int)(VideoBitrateBox.Value ?? 0);
        c.EncodeMode = TagOf(EncodeModeCombo, "auto");
        c.VideoCodec = TagOf(VideoCodecCombo, "h264");
        c.RenderSpeed = TagOf(RenderSpeedCombo, "medium");
        c.HardwareEncode = HardwareEncodeBox.IsChecked == true;

        c.TitleCard = TitleCardBox.IsChecked == true;
        c.TitleCardSeconds = (int)(TitleCardSecondsBox.Value ?? 4);

        c.OrigEnabled = OrigEnabledBox.IsChecked == true;
        c.OrigZoom = OrigZoomBox.IsChecked == true;
        c.OrigColor = OrigColorBox.IsChecked == true;
        c.OrigSpeed = OrigSpeedBox.IsChecked == true;
        c.OrigFade = OrigFadeBox.IsChecked == true;
        c.OrigStickerDir = OrigStickerDirBox.Text?.Trim() ?? "";

        c.Save();
        Close(true);
    }

    private async void OnBrowseStickerDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "选择贴纸/水印 PNG 目录",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null) OrigStickerDirBox.Text = folder.Path.LocalPath;
    }

    private void OnClearStickerDir(object? sender, RoutedEventArgs e) => OrigStickerDirBox.Text = "";

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
