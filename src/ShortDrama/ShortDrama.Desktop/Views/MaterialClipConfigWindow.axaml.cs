using Avalonia.Controls;
using Avalonia.Interactivity;
using ShortDrama.Desktop.Services;

namespace ShortDrama.Desktop.Views;

public partial class MaterialClipConfigWindow : Window
{
    private readonly GlobalSettingsService _globalSettingsService;

    public MaterialClipConfigWindow(GlobalSettingsService globalSettingsService)
    {
        _globalSettingsService = globalSettingsService;
        InitializeComponent();

        TargetDurationModeComboBox.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("fixed", "固定时长"),
            new KeyValuePair<string, string>("ratio", "按原视频比例"),
            new KeyValuePair<string, string>("adaptive_range", "自适应范围")
        };
        TargetDurationModeComboBox.DisplayMemberBinding = new Avalonia.Data.Binding("Value");
        TargetDurationModeComboBox.SelectionChanged += (_, _) => RefreshTargetDurationState();

        SaveButton.Click += SaveButton_Click;
        CancelButton.Click += (_, _) => Close(false);

        LoadValues();
    }

    private void LoadValues()
    {
        var settings = _globalSettingsService.Load();
        var targetMode = string.IsNullOrWhiteSpace(settings.MaterialClipTargetDurationMode)
            ? "adaptive_range"
            : settings.MaterialClipTargetDurationMode;
        TargetDurationModeComboBox.SelectedItem = ((IEnumerable<KeyValuePair<string, string>>)TargetDurationModeComboBox.ItemsSource!)
            .FirstOrDefault(item => string.Equals(item.Key, targetMode, StringComparison.OrdinalIgnoreCase));

        TargetDurationSecondsUpDown.Value = ParseInt(settings.MaterialClipTargetDurationSec, 30, 10, 180);
        TargetDurationRatioUpDown.Value = (decimal)ParseDouble(settings.MaterialClipTargetDurationRatioPercent, 8.0d, 0.1d, 100d);
        MinOutputDurationUpDown.Value = ParseInt(settings.MaterialClipMinOutputDurationSec, 0, 0, 180);
        MaxOutputDurationUpDown.Value = ParseInt(settings.MaterialClipMaxOutputDurationSec, 45, 1, 300);
        PerEpisodeTopNUpDown.Value = ParseInt(settings.MaterialClipPerEpisodeTopN, 2, 1, 6);
        SplitClipLimitUpDown.Value = ParseInt(settings.MaterialClipSplitClipLimit, 4, 1, 12);
        EnableLlmCheckBox.IsChecked = settings.MaterialClipEnableLlm;
        RefreshTargetDurationState();
    }

    private void RefreshTargetDurationState()
    {
        var mode = (TargetDurationModeComboBox.SelectedItem as KeyValuePair<string, string>?)?.Key ?? "adaptive_range";
        var fixedMode = string.Equals(mode, "fixed", StringComparison.OrdinalIgnoreCase);
        var ratioMode = string.Equals(mode, "ratio", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(mode, "adaptive_range", StringComparison.OrdinalIgnoreCase);
        var adaptiveMode = string.Equals(mode, "adaptive_range", StringComparison.OrdinalIgnoreCase);

        TargetDurationSecondsUpDown.IsEnabled = fixedMode;
        TargetDurationRatioUpDown.IsEnabled = ratioMode;
        MinOutputDurationUpDown.IsEnabled = adaptiveMode;
        MaxOutputDurationUpDown.IsEnabled = adaptiveMode;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var current = _globalSettingsService.Load();
        var mode = (TargetDurationModeComboBox.SelectedItem as KeyValuePair<string, string>?)?.Key ?? "adaptive_range";
        var updated = current with
        {
            MaterialClipTargetDurationMode = mode,
            MaterialClipTargetDurationSec = ((int?)TargetDurationSecondsUpDown.Value ?? 30).ToString(),
            MaterialClipTargetDurationRatioPercent = ((decimal?)TargetDurationRatioUpDown.Value ?? 8.0m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            MaterialClipMinOutputDurationSec = ((int?)MinOutputDurationUpDown.Value ?? 0).ToString(),
            MaterialClipMaxOutputDurationSec = Math.Max(
                ((int?)MinOutputDurationUpDown.Value ?? 0),
                ((int?)MaxOutputDurationUpDown.Value ?? 45)).ToString(),
            MaterialClipPerEpisodeTopN = ((int?)PerEpisodeTopNUpDown.Value ?? 2).ToString(),
            MaterialClipSplitClipLimit = ((int?)SplitClipLimitUpDown.Value ?? 4).ToString(),
            MaterialClipEnableLlm = EnableLlmCheckBox.IsChecked == true
        };
        _globalSettingsService.Save(updated);
        Close(true);
    }

    private static int ParseInt(string raw, int fallback, int minimum, int maximum)
    {
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;
    }

    private static double ParseDouble(string raw, double fallback, double minimum, double maximum)
    {
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }
}
