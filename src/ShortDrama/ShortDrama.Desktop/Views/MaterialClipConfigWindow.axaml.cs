using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ChannelsPublisher.Core.Config;
using ShortDrama.Desktop.Services;
using System.Globalization;

namespace ShortDrama.Desktop.Views;

public partial class MaterialClipConfigWindow : Window
{
    private static readonly string[] ModeKeys = ["highlight", "mashup", "commentary", "slice"];
    private readonly GlobalSettingsService _globalSettingsService;
    private readonly ClipConfig _clipConfig;
    private readonly Dictionary<string, ModeControls> _modeControls = new(StringComparer.OrdinalIgnoreCase);

    public MaterialClipConfigWindow(GlobalSettingsService globalSettingsService)
    {
        _globalSettingsService = globalSettingsService;
        _clipConfig = ClipConfig.Load();
        InitializeComponent();

        BuildModeTab("highlight", "高光", "逐集精彩片段聚合成片。", HighlightPanel);
        BuildModeTab("mashup", "混剪", "跨集叙事弧混剪（钩子→张力→高潮→悬念）。", MashupPanel);
        BuildModeTab("commentary", "解说", "AI 配音解说成片，并控制配音和字幕。", CommentaryPanel);
        BuildModeTab("slice", "切片", "整段连续片段，开头落在钩子，不打散、无内部跳剪。", SlicePanel);

        SaveButton.Click += SaveButton_Click;
        CancelButton.Click += (_, _) => Close(false);
        BrowseStickerDirButton.Click += BrowseStickerDirButton_Click;
        ClearStickerDirButton.Click += (_, _) => OrigStickerDirTextBox.Text = string.Empty;
        OrigEnabledCheckBox.IsCheckedChanged += (_, _) => RefreshOriginalityState();
        EncodeModeComboBox.SelectionChanged += (_, _) => RefreshEncodeState();
        TtsEngineComboBox.SelectionChanged += (_, _) => RefreshTtsState();

        LoadValues();
    }

    private void BuildModeTab(string key, string title, string description, Panel panel)
    {
        var border = new Border
        {
            Padding = new Avalonia.Thickness(12),
            BorderBrush = Avalonia.Media.Brush.Parse("#e2d3bd"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(10)
        };
        panel.Children.Add(border);

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("120,*"),
            RowSpacing = 10,
            ColumnSpacing = 12
        };
        border.Child = grid;

        var heading = new TextBlock
        {
            Text = $"{title} · {description}",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        Grid.SetColumnSpan(heading, 2);
        grid.Children.Add(heading);

        var enabled = new CheckBox { Content = "启用本模式（勾选后参与一键创作）" };
        AddRow(grid, 1, "启用", enabled);

        var count = new NumericUpDown
        {
            Width = 100,
            Minimum = 1,
            Maximum = 10,
            FormatString = "0"
        };
        AddRow(grid, 2, "剪辑视频", Inline(count, new TextBlock { Text = "个，每个视频统一使用下方时长范围", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }));

        var minMinutes = new NumericUpDown
        {
            Width = 96,
            Minimum = 0.5m,
            Maximum = 30m,
            Increment = 0.5m,
            FormatString = "0.0"
        };
        var maxMinutes = new NumericUpDown
        {
            Width = 96,
            Minimum = 0.5m,
            Maximum = 30m,
            Increment = 0.5m,
            FormatString = "0.0"
        };
        AddRow(grid, 3, "时长范围", Inline(minMinutes, new TextBlock { Text = "~", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }, maxMinutes, new TextBlock { Text = "分钟", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }));

        var similarity = new NumericUpDown
        {
            Width = 96,
            Minimum = 0m,
            Maximum = 1m,
            Increment = 0.05m,
            FormatString = "0.00"
        };
        AddRow(grid, 4, "相似度上限", Inline(similarity, new TextBlock { Text = "两条成片最多共享多少片段（0=不复用，越小越不雷同）", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }));

        var force = new CheckBox { Content = "覆盖重新生成（忽略本模式已有成片，强制重渲）" };
        AddRow(grid, 5, "覆盖重生", force);

        _modeControls[key] = new ModeControls(enabled, count, minMinutes, maxMinutes, similarity, force);
    }

    private static void AddRow(Grid grid, int row, string label, Control control)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetRow(labelBlock, row);
        grid.Children.Add(labelBlock);

        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static StackPanel Inline(params Control[] controls)
    {
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8
        };
        foreach (var control in controls)
        {
            panel.Children.Add(control);
        }

        return panel;
    }

    private void LoadValues()
    {
        var settings = _globalSettingsService.Load();

        EpisodeQuotaCheckBox.IsChecked = _clipConfig.EpisodeQuota;
        EnableLlmScoreCheckBox.IsChecked = _clipConfig.EnableLlmScore || settings.MaterialClipEnableLlm;
        AudioEnergyCheckBox.IsChecked = _clipConfig.AudioEnergy;
        ShotDensityCheckBox.IsChecked = _clipConfig.ShotDensity;
        PipelineConcurrentUpDown.Value = _clipConfig.PipelineConcurrent;
        NetConcurrentUpDown.Value = _clipConfig.NetConcurrent;
        SelectByTag(OutputQualityComboBox, _clipConfig.OutputQuality);
        VideoBitrateUpDown.Value = (decimal)_clipConfig.VideoBitrate;
        SelectByTag(EncodeModeComboBox, _clipConfig.EncodeMode);
        SelectByTag(VideoCodecComboBox, _clipConfig.VideoCodec);
        SelectByTag(RenderSpeedComboBox, _clipConfig.RenderSpeed);
        HardwareEncodeCheckBox.IsChecked = _clipConfig.HardwareEncode;
        LlmArrangeCheckBox.IsChecked = _clipConfig.LlmArrange;
        SmoothSelectionCheckBox.IsChecked = _clipConfig.SmoothSelection;
        PublishMetaCheckBox.IsChecked = _clipConfig.PublishMeta;
        TitleCardCheckBox.IsChecked = _clipConfig.TitleCard;
        TitleCardSecondsUpDown.Value = _clipConfig.TitleCardSeconds;

        foreach (var key in ModeKeys)
        {
            LoadModeValues(key);
        }

        CommentaryBurnSubtitlesCheckBox.IsChecked = _clipConfig.CommentaryBurnSubtitles;
        SelectByTag(TtsEngineComboBox, _clipConfig.TtsEngine);
        TtsVoiceTypeTextBox.Text = _clipConfig.TtsVoiceType;
        TtsEdgeVoiceTextBox.Text = string.IsNullOrWhiteSpace(_clipConfig.TtsEdgeVoice) ? "zh-CN-YunjianNeural" : _clipConfig.TtsEdgeVoice;
        TtsClusterTextBox.Text = _clipConfig.TtsCluster;
        TtsSpeedUpDown.Value = (decimal)_clipConfig.TtsSpeedRatio;
        SelectByTag(CommentaryStyleComboBox, _clipConfig.CommentaryStyleStrength);
        CommentaryNarrationRatioUpDown.Value = (decimal)_clipConfig.CommentaryNarrationRatio;

        OrigEnabledCheckBox.IsChecked = _clipConfig.OrigEnabled;
        OrigZoomCheckBox.IsChecked = _clipConfig.OrigZoom;
        OrigColorCheckBox.IsChecked = _clipConfig.OrigColor;
        OrigSpeedCheckBox.IsChecked = _clipConfig.OrigSpeed;
        OrigFadeCheckBox.IsChecked = _clipConfig.OrigFade;
        OrigStickerDirTextBox.Text = _clipConfig.OrigStickerDir;

        RefreshEncodeState();
        RefreshTtsState();
        RefreshOriginalityState();
    }

    private void LoadModeValues(string key)
    {
        var mode = _clipConfig.Mode(key);
        var controls = _modeControls[key];
        var range = _clipConfig.RangesFor(key).FirstOrDefault() ?? new ClipDurationRange();
        controls.Enabled.IsChecked = mode.Enabled;
        controls.Count.Value = mode.Count;
        controls.MinMinutes.Value = (decimal)(range.MinSeconds / 60.0);
        controls.MaxMinutes.Value = (decimal)(Math.Max(range.MinSeconds, range.MaxSeconds) / 60.0);
        controls.Similarity.Value = (decimal)(_clipConfig.SimilarityCapByMode.TryGetValue(key, out var cap) ? cap : 0.5);
        controls.Force.IsChecked = mode.Force;
    }

    private async void BrowseStickerDirButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择贴纸/水印 PNG 目录",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            OrigStickerDirTextBox.Text = folder.Path.LocalPath;
        }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        _clipConfig.EpisodeQuota = EpisodeQuotaCheckBox.IsChecked == true;
        _clipConfig.EnableLlmScore = EnableLlmScoreCheckBox.IsChecked == true;
        _clipConfig.AudioEnergy = AudioEnergyCheckBox.IsChecked == true;
        _clipConfig.ShotDensity = ShotDensityCheckBox.IsChecked == true;
        _clipConfig.PipelineConcurrent = IntValue(PipelineConcurrentUpDown, 4);
        _clipConfig.NetConcurrent = IntValue(NetConcurrentUpDown, 0);
        _clipConfig.OutputQuality = TagOf(OutputQualityComboBox, "720p");
        _clipConfig.VideoBitrate = DoubleValue(VideoBitrateUpDown, 2.5);
        _clipConfig.EncodeMode = TagOf(EncodeModeComboBox, "bitrate");
        _clipConfig.VideoCodec = TagOf(VideoCodecComboBox, "h264");
        _clipConfig.RenderSpeed = TagOf(RenderSpeedComboBox, "fast");
        _clipConfig.HardwareEncode = HardwareEncodeCheckBox.IsChecked == true;
        _clipConfig.LlmArrange = LlmArrangeCheckBox.IsChecked == true;
        _clipConfig.SmoothSelection = SmoothSelectionCheckBox.IsChecked == true;
        _clipConfig.PublishMeta = PublishMetaCheckBox.IsChecked == true;
        _clipConfig.TitleCard = TitleCardCheckBox.IsChecked == true;
        _clipConfig.TitleCardSeconds = IntValue(TitleCardSecondsUpDown, 4);

        foreach (var key in ModeKeys)
        {
            SaveModeValues(key);
        }

        _clipConfig.CommentaryBurnSubtitles = CommentaryBurnSubtitlesCheckBox.IsChecked == true;
        _clipConfig.TtsEngine = TagOf(TtsEngineComboBox, "volcengine");
        _clipConfig.TtsVoiceType = TtsVoiceTypeTextBox.Text?.Trim() ?? string.Empty;
        _clipConfig.TtsEdgeVoice = TtsEdgeVoiceTextBox.Text?.Trim() ?? string.Empty;
        _clipConfig.TtsCluster = TtsClusterTextBox.Text?.Trim() ?? string.Empty;
        _clipConfig.TtsSpeedRatio = DoubleValue(TtsSpeedUpDown, 1.0);
        _clipConfig.CommentaryStyleStrength = TagOf(CommentaryStyleComboBox, "standard");
        _clipConfig.CommentaryNarrationRatio = DoubleValue(CommentaryNarrationRatioUpDown, 70.0);

        _clipConfig.OrigEnabled = OrigEnabledCheckBox.IsChecked == true;
        _clipConfig.OrigZoom = OrigZoomCheckBox.IsChecked == true;
        _clipConfig.OrigColor = OrigColorCheckBox.IsChecked == true;
        _clipConfig.OrigSpeed = OrigSpeedCheckBox.IsChecked == true;
        _clipConfig.OrigFade = OrigFadeCheckBox.IsChecked == true;
        _clipConfig.OrigStickerDir = OrigStickerDirTextBox.Text?.Trim() ?? string.Empty;

        _clipConfig.Save();
        SaveCompatibilitySnapshot();
        Close(true);
    }

    private void SaveModeValues(string key)
    {
        var controls = _modeControls[key];
        var mode = _clipConfig.Mode(key);
        var count = IntValue(controls.Count, key == "commentary" ? 1 : 2);
        var minSeconds = Math.Max(30, (int)Math.Round(DoubleValue(controls.MinMinutes, 5.0) * 60));
        var maxSeconds = Math.Max(minSeconds, (int)Math.Round(DoubleValue(controls.MaxMinutes, 6.0) * 60));
        mode.Enabled = controls.Enabled.IsChecked == true;
        mode.Count = count;
        mode.Force = controls.Force.IsChecked == true;
        _clipConfig.RangesByMode[key] = Enumerable.Range(0, count)
            .Select(_ => new ClipDurationRange { MinSeconds = minSeconds, MaxSeconds = maxSeconds })
            .ToList();
        _clipConfig.SimilarityCapByMode[key] = DoubleValue(controls.Similarity, 0.5);
    }

    private void SaveCompatibilitySnapshot()
    {
        var current = _globalSettingsService.Load();
        var primaryMode = _clipConfig.Modes.FirstOrDefault(mode => mode.Enabled) ?? _clipConfig.Mode("highlight");
        var range = _clipConfig.RangesFor(primaryMode.Key).FirstOrDefault() ?? new ClipDurationRange();
        var targetSeconds = Math.Max(1, (range.MinSeconds + range.MaxSeconds) / 2);
        var updated = current with
        {
            MaterialClipTargetDurationMode = "fixed",
            MaterialClipTargetDurationSec = targetSeconds.ToString(CultureInfo.InvariantCulture),
            MaterialClipTargetDurationRatioPercent = "8",
            MaterialClipMinOutputDurationSec = range.MinSeconds.ToString(CultureInfo.InvariantCulture),
            MaterialClipMaxOutputDurationSec = range.MaxSeconds.ToString(CultureInfo.InvariantCulture),
            MaterialClipPerEpisodeTopN = Math.Max(1, primaryMode.Count).ToString(CultureInfo.InvariantCulture),
            MaterialClipEnableLlm = _clipConfig.EnableLlmScore,
            MaterialClipSplitClipLimit = Math.Max(1, primaryMode.Count).ToString(CultureInfo.InvariantCulture)
        };
        _globalSettingsService.Save(updated);
    }

    private void RefreshEncodeState()
    {
        VideoBitrateUpDown.IsEnabled = string.Equals(TagOf(EncodeModeComboBox, "bitrate"), "bitrate", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshTtsState()
    {
        var isEdge = string.Equals(TagOf(TtsEngineComboBox, "volcengine"), "edge", StringComparison.OrdinalIgnoreCase);
        TtsVoiceTypeTextBox.IsEnabled = !isEdge;
        TtsClusterTextBox.IsEnabled = !isEdge;
        TtsEdgeVoiceTextBox.IsEnabled = isEdge;
    }

    private void RefreshOriginalityState()
    {
        var enabled = OrigEnabledCheckBox.IsChecked == true;
        OrigZoomCheckBox.IsEnabled = enabled;
        OrigColorCheckBox.IsEnabled = enabled;
        OrigSpeedCheckBox.IsEnabled = enabled;
        OrigFadeCheckBox.IsEnabled = enabled;
        OrigStickerDirTextBox.IsEnabled = enabled;
        BrowseStickerDirButton.IsEnabled = enabled;
        ClearStickerDirButton.IsEnabled = enabled;
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                string.Equals(comboBoxItem.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = comboBoxItem;
                return;
            }
        }

        combo.SelectedIndex = combo.ItemCount > 0 ? 0 : -1;
    }

    private static string TagOf(ComboBox combo, string fallback)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
    }

    private static int IntValue(NumericUpDown control, int fallback)
    {
        return control.Value is { } value ? (int)Math.Round((double)value) : fallback;
    }

    private static double DoubleValue(NumericUpDown control, double fallback)
    {
        return control.Value is { } value ? (double)value : fallback;
    }

    private sealed record ModeControls(
        CheckBox Enabled,
        NumericUpDown Count,
        NumericUpDown MinMinutes,
        NumericUpDown MaxMinutes,
        NumericUpDown Similarity,
        CheckBox Force);
}
