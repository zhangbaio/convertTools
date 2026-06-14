using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using ShortDrama.Desktop.Models;
using ShortDrama.Desktop.Services;
using ShortDrama.Desktop.ViewModels;
using ShortDrama.Infrastructure.Media;
using System.ComponentModel;
using System.Globalization;

namespace ShortDrama.Desktop.Views;

public partial class TranscodeTemplateView : UserControl
{
    private DesktopConfigService? _configService;
    private MainWindowViewModel? _mainWindowViewModel;
    private string _currentRootDir = string.Empty;

    public TranscodeTemplateView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => ResolveServices();
        ReloadButton.Click += (_, _) => LoadConfig();
        ResetProfilesButton.Click += (_, _) => ResetProfilesToDefault();
        SaveButton.Click += (_, _) => SaveConfig();

        VideoEncoderComboBox.ItemsSource = new[]
        {
            new KeyValuePair<string, string>("CPU x264", "libx264"),
            new KeyValuePair<string, string>("Auto", "auto"),
            new KeyValuePair<string, string>("NVIDIA NVENC", "h264_nvenc"),
            new KeyValuePair<string, string>("Apple VideoToolbox", "h264_videotoolbox"),
        };
        VideoEncoderComboBox.DisplayMemberBinding = new Avalonia.Data.Binding("Key");

        VideoPresetComboBox.ItemsSource = new[] { "veryfast", "fast", "medium" };
    }

    private CheckBox MaterialConvertEnabledCheckBox => this.FindControl<CheckBox>(nameof(MaterialConvertEnabledCheckBox))!;
    private TextBlock ConfigPathTextBlock => this.FindControl<TextBlock>(nameof(ConfigPathTextBlock))!;
    private Button ReloadButton => this.FindControl<Button>(nameof(ReloadButton))!;
    private Button ResetProfilesButton => this.FindControl<Button>(nameof(ResetProfilesButton))!;
    private Button SaveButton => this.FindControl<Button>(nameof(SaveButton))!;
    private ComboBox VideoEncoderComboBox => this.FindControl<ComboBox>(nameof(VideoEncoderComboBox))!;
    private ComboBox VideoPresetComboBox => this.FindControl<ComboBox>(nameof(VideoPresetComboBox))!;
    private TextBox NvencCqTextBox => this.FindControl<TextBox>(nameof(NvencCqTextBox))!;
    private TextBox NvencMaxParallelTextBox => this.FindControl<TextBox>(nameof(NvencMaxParallelTextBox))!;
    private CheckBox VerboseTranscodeLogCheckBox => this.FindControl<CheckBox>(nameof(VerboseTranscodeLogCheckBox))!;
    private CheckBox SkipBitrateDownscaleCheckBox => this.FindControl<CheckBox>(nameof(SkipBitrateDownscaleCheckBox))!;
    private TextBox UploadTargetBitrateTextBox => this.FindControl<TextBox>(nameof(UploadTargetBitrateTextBox))!;
    private TextBox UploadMaxBitrateTextBox => this.FindControl<TextBox>(nameof(UploadMaxBitrateTextBox))!;
    private TextBox UploadMinBitrateTextBox => this.FindControl<TextBox>(nameof(UploadMinBitrateTextBox))!;
    private TextBox UploadAudioBitrateTextBox => this.FindControl<TextBox>(nameof(UploadAudioBitrateTextBox))!;
    private CheckBox UploadFallbackEnabledCheckBox => this.FindControl<CheckBox>(nameof(UploadFallbackEnabledCheckBox))!;
    private TextBox UploadFallbackBitrateTextBox => this.FindControl<TextBox>(nameof(UploadFallbackBitrateTextBox))!;
    private CheckBox Profile720EnabledCheckBox => this.FindControl<CheckBox>(nameof(Profile720EnabledCheckBox))!;
    private TextBox Profile720MinTextBox => this.FindControl<TextBox>(nameof(Profile720MinTextBox))!;
    private TextBox Profile720MaxTextBox => this.FindControl<TextBox>(nameof(Profile720MaxTextBox))!;
    private TextBox Profile720BitrateTextBox => this.FindControl<TextBox>(nameof(Profile720BitrateTextBox))!;
    private TextBox Profile720AudioTextBox => this.FindControl<TextBox>(nameof(Profile720AudioTextBox))!;
    private CheckBox Profile1080EnabledCheckBox => this.FindControl<CheckBox>(nameof(Profile1080EnabledCheckBox))!;
    private TextBox Profile1080MinTextBox => this.FindControl<TextBox>(nameof(Profile1080MinTextBox))!;
    private TextBox Profile1080MaxTextBox => this.FindControl<TextBox>(nameof(Profile1080MaxTextBox))!;
    private TextBox Profile1080BitrateTextBox => this.FindControl<TextBox>(nameof(Profile1080BitrateTextBox))!;
    private TextBox Profile1080AudioTextBox => this.FindControl<TextBox>(nameof(Profile1080AudioTextBox))!;
    private CheckBox Profile2kEnabledCheckBox => this.FindControl<CheckBox>(nameof(Profile2kEnabledCheckBox))!;
    private TextBox Profile2kMinTextBox => this.FindControl<TextBox>(nameof(Profile2kMinTextBox))!;
    private TextBox Profile2kMaxTextBox => this.FindControl<TextBox>(nameof(Profile2kMaxTextBox))!;
    private TextBox Profile2kBitrateTextBox => this.FindControl<TextBox>(nameof(Profile2kBitrateTextBox))!;
    private TextBox Profile2kAudioTextBox => this.FindControl<TextBox>(nameof(Profile2kAudioTextBox))!;
    private TextBlock StatusTextBlock => this.FindControl<TextBlock>(nameof(StatusTextBlock))!;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_mainWindowViewModel is not null)
        {
            _mainWindowViewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        }

        _mainWindowViewModel = DataContext as MainWindowViewModel;
        if (_mainWindowViewModel is not null)
        {
            _mainWindowViewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
            _currentRootDir = _mainWindowViewModel.RootDir;
            LoadConfig();
        }
    }

    private void OnMainWindowViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MainWindowViewModel.RootDir), StringComparison.Ordinal))
        {
            return;
        }

        if (_mainWindowViewModel is null || string.Equals(_currentRootDir, _mainWindowViewModel.RootDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentRootDir = _mainWindowViewModel.RootDir;
        LoadConfig();
    }

    private void ResolveServices()
    {
        if (_configService is not null)
        {
            return;
        }

        if (Application.Current is not App app)
        {
            return;
        }

        _configService = app.Services.GetRequiredService<DesktopConfigService>();
        LoadConfig();
    }

    private void LoadConfig()
    {
        if (_configService is null || string.IsNullOrWhiteSpace(_currentRootDir))
        {
            SetStatus("请先选择工作目录。");
            return;
        }

        var project = _configService.LoadProject(_currentRootDir);
        ConfigPathTextBlock.Text = project.ConfigFilePath;

        MaterialConvertEnabledCheckBox.IsChecked = project.MaterialConvertEnabled;
        SelectVideoEncoder(project.VideoEncoder, project.VideoUseHardwareEncoder);
        VideoPresetComboBox.SelectedItem = string.IsNullOrWhiteSpace(project.VideoPreset) ? "veryfast" : project.VideoPreset;
        NvencCqTextBox.Text = string.IsNullOrWhiteSpace(project.NvencCq) ? "21" : project.NvencCq;
        NvencMaxParallelTextBox.Text = string.IsNullOrWhiteSpace(project.NvencMaxParallel)
            ? (string.IsNullOrWhiteSpace(project.VideoConcurrentCount) ? "4" : project.VideoConcurrentCount)
            : project.NvencMaxParallel;
        VerboseTranscodeLogCheckBox.IsChecked = project.VerboseTranscodeLogEnabled;
        SkipBitrateDownscaleCheckBox.IsChecked = project.SkipBitrateDownscaleForHighBitrate;
        UploadTargetBitrateTextBox.Text = string.IsNullOrWhiteSpace(project.UploadTargetVideoBitrateMbps)
            ? ResolveLegacyBitrateMbps(project.VideoBitrateBps, 4.8d)
            : project.UploadTargetVideoBitrateMbps;
        UploadMaxBitrateTextBox.Text = string.IsNullOrWhiteSpace(project.UploadMaxVideoBitrateMbps) ? "5.6" : project.UploadMaxVideoBitrateMbps;
        UploadMinBitrateTextBox.Text = string.IsNullOrWhiteSpace(project.UploadMinVideoBitrateMbps) ? "4.5" : project.UploadMinVideoBitrateMbps;
        UploadAudioBitrateTextBox.Text = string.IsNullOrWhiteSpace(project.UploadAudioBitrateKbps)
            ? ResolveLegacyAudioKbps(project.VideoAudioBitrateBps, 128)
            : project.UploadAudioBitrateKbps;
        UploadFallbackEnabledCheckBox.IsChecked = project.UploadBitrateFallbackEnabled;
        UploadFallbackBitrateTextBox.Text = string.IsNullOrWhiteSpace(project.UploadBitrateFallbackVideoBitrateMbps) ? "4.8" : project.UploadBitrateFallbackVideoBitrateMbps;

        var profiles = UploadTranscodeBitrateProfiles.Parse(project.UploadBitrateProfilesJson);
        ApplyProfile(Profile720EnabledCheckBox, Profile720MinTextBox, Profile720MaxTextBox, Profile720BitrateTextBox, Profile720AudioTextBox, profiles.ElementAtOrDefault(0) ?? UploadTranscodeBitrateProfiles.DefaultProfiles[0]);
        ApplyProfile(Profile1080EnabledCheckBox, Profile1080MinTextBox, Profile1080MaxTextBox, Profile1080BitrateTextBox, Profile1080AudioTextBox, profiles.ElementAtOrDefault(1) ?? UploadTranscodeBitrateProfiles.DefaultProfiles[1]);
        ApplyProfile(Profile2kEnabledCheckBox, Profile2kMinTextBox, Profile2kMaxTextBox, Profile2kBitrateTextBox, Profile2kAudioTextBox, profiles.ElementAtOrDefault(2) ?? UploadTranscodeBitrateProfiles.DefaultProfiles[2]);

        SetStatus("已加载当前工作目录的转码模板配置。");
    }

    private void SaveConfig()
    {
        if (_configService is null || string.IsNullOrWhiteSpace(_currentRootDir))
        {
            SetStatus("未选择工作目录，无法保存。");
            return;
        }

        if (!TryParseInteger(NvencCqTextBox.Text, 0, 51, out var nvencCq) ||
            !TryParseInteger(NvencMaxParallelTextBox.Text, 1, 16, out var nvencMaxParallel) ||
            !TryParseDouble(UploadTargetBitrateTextBox.Text, 1.0d, 50.0d, out var targetBitrate) ||
            !TryParseDouble(UploadMaxBitrateTextBox.Text, 1.0d, 80.0d, out var maxBitrate) ||
            !TryParseDouble(UploadMinBitrateTextBox.Text, 1.0d, 80.0d, out var minBitrate) ||
            !TryParseInteger(UploadAudioBitrateTextBox.Text, 64, 320, out var audioBitrate) ||
            !TryParseDouble(UploadFallbackBitrateTextBox.Text, 1.0d, 80.0d, out var fallbackBitrate))
        {
            SetStatus("请检查全局策略中的数值输入。");
            return;
        }

        var profiles = new[]
        {
            BuildProfile("720p及以下", Profile720EnabledCheckBox, Profile720MinTextBox, Profile720MaxTextBox, Profile720BitrateTextBox, Profile720AudioTextBox),
            BuildProfile("1080p", Profile1080EnabledCheckBox, Profile1080MinTextBox, Profile1080MaxTextBox, Profile1080BitrateTextBox, Profile1080AudioTextBox),
            BuildProfile("2k+", Profile2kEnabledCheckBox, Profile2kMinTextBox, Profile2kMaxTextBox, Profile2kBitrateTextBox, Profile2kAudioTextBox)
        };

        if (profiles.Any(item => item is null))
        {
            SetStatus("请检查分辨率码率档位中的数值输入。");
            return;
        }

        var project = _configService.LoadProject(_currentRootDir);
        var selectedEncoder = GetSelectedVideoEncoder();
        var bitrateMode = string.Equals(selectedEncoder, "libx264", StringComparison.OrdinalIgnoreCase)
            ? "Cbr"
            : (string.IsNullOrWhiteSpace(project.VideoBitrateMode) ? "Cbr" : project.VideoBitrateMode);

        var updated = project with
        {
            MaterialConvertEnabled = MaterialConvertEnabledCheckBox.IsChecked == true,
            VideoUseHardwareEncoder = !string.Equals(selectedEncoder, "libx264", StringComparison.OrdinalIgnoreCase),
            VideoEncoder = selectedEncoder,
            VideoPreset = VideoPresetComboBox.SelectedItem as string ?? "veryfast",
            NvencCq = nvencCq.ToString(CultureInfo.InvariantCulture),
            NvencMaxParallel = nvencMaxParallel.ToString(CultureInfo.InvariantCulture),
            VideoConcurrentCount = nvencMaxParallel.ToString(CultureInfo.InvariantCulture),
            VerboseTranscodeLogEnabled = VerboseTranscodeLogCheckBox.IsChecked == true,
            SkipBitrateDownscaleForHighBitrate = SkipBitrateDownscaleCheckBox.IsChecked == true,
            UploadTargetVideoBitrateMbps = targetBitrate.ToString("0.###", CultureInfo.InvariantCulture),
            UploadMaxVideoBitrateMbps = Math.Max(targetBitrate, maxBitrate).ToString("0.###", CultureInfo.InvariantCulture),
            UploadMinVideoBitrateMbps = Math.Min(targetBitrate, minBitrate).ToString("0.###", CultureInfo.InvariantCulture),
            UploadAudioBitrateKbps = audioBitrate.ToString(CultureInfo.InvariantCulture),
            UploadBitrateFallbackEnabled = UploadFallbackEnabledCheckBox.IsChecked == true,
            UploadBitrateFallbackVideoBitrateMbps = fallbackBitrate.ToString("0.###", CultureInfo.InvariantCulture),
            UploadBitrateProfilesJson = UploadTranscodeBitrateProfiles.Serialize(profiles!),
            VideoBitrateBps = ToBitrateBps(targetBitrate).ToString(CultureInfo.InvariantCulture),
            VideoBitrateMode = bitrateMode,
            VideoAudioBitrateBps = (audioBitrate * 1000).ToString(CultureInfo.InvariantCulture)
        };

        _configService.SaveProject(updated);
        SetStatus("转码模板配置已保存。");
    }

    private void ResetProfilesToDefault()
    {
        var profiles = UploadTranscodeBitrateProfiles.DefaultProfiles;
        ApplyProfile(Profile720EnabledCheckBox, Profile720MinTextBox, Profile720MaxTextBox, Profile720BitrateTextBox, Profile720AudioTextBox, profiles[0]);
        ApplyProfile(Profile1080EnabledCheckBox, Profile1080MinTextBox, Profile1080MaxTextBox, Profile1080BitrateTextBox, Profile1080AudioTextBox, profiles[1]);
        ApplyProfile(Profile2kEnabledCheckBox, Profile2kMinTextBox, Profile2kMaxTextBox, Profile2kBitrateTextBox, Profile2kAudioTextBox, profiles[2]);
        UploadTargetBitrateTextBox.Text = "4.8";
        UploadMaxBitrateTextBox.Text = "5.6";
        UploadMinBitrateTextBox.Text = "4.5";
        UploadAudioBitrateTextBox.Text = "128";
        UploadFallbackEnabledCheckBox.IsChecked = true;
        UploadFallbackBitrateTextBox.Text = "4.8";
        SelectVideoEncoder("libx264", false);
        VideoPresetComboBox.SelectedItem = "veryfast";
        NvencCqTextBox.Text = "21";
        NvencMaxParallelTextBox.Text = "4";
        VerboseTranscodeLogCheckBox.IsChecked = false;
        SkipBitrateDownscaleCheckBox.IsChecked = false;
        SetStatus("已恢复默认档位。");
    }

    private void SelectVideoEncoder(string configured, bool legacyHardware)
    {
        var target = string.IsNullOrWhiteSpace(configured)
            ? (legacyHardware ? "auto" : "libx264")
            : configured;
        var items = (VideoEncoderComboBox.ItemsSource as IEnumerable<KeyValuePair<string, string>>)
            ?? Enumerable.Empty<KeyValuePair<string, string>>();
        VideoEncoderComboBox.SelectedItem = items.FirstOrDefault(item =>
            string.Equals(item.Value, target, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyProfile(
        CheckBox enabledCheckBox,
        TextBox minTextBox,
        TextBox maxTextBox,
        TextBox bitrateTextBox,
        TextBox audioTextBox,
        UploadTranscodeBitrateProfile profile)
    {
        enabledCheckBox.IsChecked = profile.Enabled;
        minTextBox.Text = profile.MinShortEdge.ToString(CultureInfo.InvariantCulture);
        maxTextBox.Text = profile.MaxShortEdge <= 0 ? "0" : profile.MaxShortEdge.ToString(CultureInfo.InvariantCulture);
        bitrateTextBox.Text = profile.BitrateMbps.ToString("0.###", CultureInfo.InvariantCulture);
        audioTextBox.Text = profile.AudioKbps.ToString(CultureInfo.InvariantCulture);
    }

    private static UploadTranscodeBitrateProfile? BuildProfile(
        string name,
        CheckBox enabledCheckBox,
        TextBox minTextBox,
        TextBox maxTextBox,
        TextBox bitrateTextBox,
        TextBox audioTextBox)
    {
        if (!TryParseInteger(minTextBox.Text, 1, 10000, out var minShortEdge) ||
            !TryParseInteger(maxTextBox.Text, 0, 10000, out var maxShortEdge) ||
            !TryParseDouble(bitrateTextBox.Text, 1.0d, 80.0d, out var bitrateMbps) ||
            !TryParseInteger(audioTextBox.Text, 64, 320, out var audioKbps))
        {
            return null;
        }

        if (maxShortEdge > 0 && maxShortEdge < minShortEdge)
        {
            maxShortEdge = minShortEdge;
        }

        return new UploadTranscodeBitrateProfile(
            Name: name,
            MinShortEdge: minShortEdge,
            MaxShortEdge: maxShortEdge,
            BitrateMbps: bitrateMbps,
            AudioKbps: audioKbps,
            Enabled: enabledCheckBox.IsChecked == true,
            VideoEncoder: "auto",
            Preset: "veryfast");
    }

    private static bool TryParseInteger(string text, int minimum, int maximum, out int value)
    {
        if (int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value = Math.Clamp(value, minimum, maximum);
            return true;
        }

        value = minimum;
        return false;
    }

    private static bool TryParseDouble(string text, double minimum, double maximum, out double value)
    {
        if (double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            value = Math.Clamp(value, minimum, maximum);
            return true;
        }

        value = minimum;
        return false;
    }

    private static string ResolveLegacyBitrateMbps(string legacyBitrateBps, double fallbackMbps)
    {
        return long.TryParse((legacyBitrateBps ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? (parsed / 1_000_000d).ToString("0.###", CultureInfo.InvariantCulture)
            : fallbackMbps.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string ResolveLegacyAudioKbps(string legacyAudioBitrateBps, int fallbackKbps)
    {
        return int.TryParse((legacyAudioBitrateBps ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(64, parsed / 1000).ToString(CultureInfo.InvariantCulture)
            : fallbackKbps.ToString(CultureInfo.InvariantCulture);
    }

    private static long ToBitrateBps(double bitrateMbps)
    {
        return (long)Math.Round(bitrateMbps * 1_000_000d, MidpointRounding.AwayFromZero);
    }

    private void SetStatus(string text)
    {
        StatusTextBlock.Text = text;
    }

    private string GetSelectedVideoEncoder()
    {
        return VideoEncoderComboBox.SelectedItem is KeyValuePair<string, string> pair
            ? pair.Value
            : "libx264";
    }
}
