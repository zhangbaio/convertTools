using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
    private static readonly IReadOnlyDictionary<string, DynamicSpeedPreset> DynamicSpeedPresets =
        new Dictionary<string, DynamicSpeedPreset>(StringComparer.OrdinalIgnoreCase)
        {
            ["custom"] = new("custom", "自定义", 2.5d, 8d, 5d, 2.5d, 6d),
            ["light_rhythm"] = new("light_rhythm", "全片轻快（推荐）", 2.5d, 8d, 6d, 2.5d, 8d),
            ["intro_hook"] = new("intro_hook", "开头抓眼", 2.5d, 10d, 7d, 2d, 6d),
            ["ending_pause"] = new("ending_pause", "结尾留停顿", 2d, 6d, 5d, 3d, -8d),
            ["two_side_emphasis"] = new("two_side_emphasis", "两头强调", 2.5d, 8d, 4d, 2.5d, -8d),
            ["full_light_fast"] = new("full_light_fast", "全片稳快", 3d, 6d, 5d, 3d, 6d)
        };

    private DesktopConfigService? _configService;
    private MainWindowViewModel? _mainWindowViewModel;
    private string _currentRootDir = string.Empty;
    private bool _updating;
    private bool _updatingDynamicPreset;

    public TranscodeTemplateView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => ResolveServices();

        ReloadButton.Click += (_, _) => LoadConfig();
        ResetProfilesButton.Click += (_, _) => ResetToDefaults();
        SaveButton.Click += (_, _) => SaveConfig();

        InitializeChoices();
        WireEvents();
    }

    private void InitializeChoices()
    {
        VideoEncoderComboBox.ItemsSource = new[]
        {
            new WorkflowStepOption("auto", "Auto"),
            new WorkflowStepOption("libx264", "CPU x264"),
            new WorkflowStepOption("h264_nvenc", "NVIDIA NVENC"),
            new WorkflowStepOption("h264_videotoolbox", "Apple VideoToolbox")
        };
        VideoEncoderComboBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(WorkflowStepOption.Label));

        VideoPresetComboBox.ItemsSource = new[] { "veryfast", "faster", "fast", "medium" };

        DynamicSpeedPresetComboBox.ItemsSource = DynamicSpeedPresets.Values
            .Select(preset => new WorkflowStepOption(preset.Key, preset.Label))
            .ToArray();
        DynamicSpeedPresetComboBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(WorkflowStepOption.Label));

        FrameSamplingModeComboBox.ItemsSource = new[]
        {
            new WorkflowStepOption("fixed_interval", "固定间隔"),
            new WorkflowStepOption("random", "随机抽帧")
        };
        FrameSamplingModeComboBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(WorkflowStepOption.Label));

        WatermarkPositionComboBox.ItemsSource = new[]
        {
            new WorkflowStepOption("top_left", "左上角"),
            new WorkflowStepOption("top_right", "右上角")
        };
        WatermarkPositionComboBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(WorkflowStepOption.Label));
    }

    private void WireEvents()
    {
        WirePreviewRefresh(MaterialConvertEnabledCheckBox);
        WirePreviewRefresh(VideoEncoderComboBox);
        WirePreviewRefresh(VideoPresetComboBox);
        WirePreviewRefresh(NvencCqTextBox);
        WirePreviewRefresh(NvencMaxParallelTextBox);
        WirePreviewRefresh(VerboseTranscodeLogCheckBox);
        WirePreviewRefresh(SkipBitrateDownscaleCheckBox);
        WirePreviewRefresh(UploadTargetBitrateTextBox);
        WirePreviewRefresh(UploadMaxBitrateTextBox);
        WirePreviewRefresh(UploadMinBitrateTextBox);
        WirePreviewRefresh(UploadAudioBitrateTextBox);
        WirePreviewRefresh(UploadFallbackEnabledCheckBox);
        WirePreviewRefresh(UploadFallbackBitrateTextBox);
        WirePreviewRefresh(Profile720EnabledCheckBox, Profile720MinTextBox, Profile720MaxTextBox, Profile720BitrateTextBox, Profile720AudioTextBox);
        WirePreviewRefresh(Profile1080EnabledCheckBox, Profile1080MinTextBox, Profile1080MaxTextBox, Profile1080BitrateTextBox, Profile1080AudioTextBox);
        WirePreviewRefresh(Profile2kEnabledCheckBox, Profile2kMinTextBox, Profile2kMaxTextBox, Profile2kBitrateTextBox, Profile2kAudioTextBox);
        WirePreviewRefresh(TrimHeadTextBox, TrimTailTextBox, SpeedPercentTextBox);
        WirePreviewRefresh(DynamicSpeedEnabledCheckBox, DynamicSpeedHeadSecondsTextBox, DynamicSpeedHeadPercentTextBox, DynamicSpeedMiddlePercentTextBox, DynamicSpeedTailSecondsTextBox, DynamicSpeedTailPercentTextBox);
        WirePreviewRefresh(FrameSamplingEnabledCheckBox, FrameSamplingModeComboBox, FrameSamplingIntervalTextBox);
        WirePreviewRefresh(CropWidthPercentTextBox, CropHeightPercentTextBox, OutputWidthTextBox, OutputHeightTextBox, ForegroundZoomPercentTextBox, PipWidthPercentTextBox, PipHeightPercentTextBox);
        WirePreviewRefresh(WatermarkEnabledCheckBox, WatermarkTextTextBox, WatermarkFontSizeTextBox, WatermarkPositionComboBox, WatermarkMarginXTextBox, WatermarkMarginYTextBox);

        DynamicSpeedEnabledCheckBox.IsCheckedChanged += (_, _) => RefreshDynamicSpeedState();
        DynamicSpeedPresetComboBox.SelectionChanged += (_, _) => ApplySelectedDynamicSpeedPreset();
        FrameSamplingEnabledCheckBox.IsCheckedChanged += (_, _) => RefreshFrameSamplingState();
        WatermarkEnabledCheckBox.IsCheckedChanged += (_, _) => RefreshWatermarkState();
        WatermarkPositionComboBox.SelectionChanged += (_, _) => RefreshWatermarkState();
        UploadFallbackEnabledCheckBox.IsCheckedChanged += (_, _) => RefreshFallbackState();

        TrimHeadTextBox.TextChanged += (_, _) => { if (!_updating) RefreshPreview(); };
        DynamicSpeedHeadSecondsTextBox.TextChanged += (_, _) => MarkDynamicPresetCustom();
        DynamicSpeedHeadPercentTextBox.TextChanged += (_, _) => MarkDynamicPresetCustom();
        DynamicSpeedMiddlePercentTextBox.TextChanged += (_, _) => MarkDynamicPresetCustom();
        DynamicSpeedTailSecondsTextBox.TextChanged += (_, _) => MarkDynamicPresetCustom();
        DynamicSpeedTailPercentTextBox.TextChanged += (_, _) => MarkDynamicPresetCustom();
    }

    private void WirePreviewRefresh(params Control[] controls)
    {
        foreach (var control in controls)
        {
            switch (control)
            {
                case TextBox textBox:
                    textBox.TextChanged += (_, _) =>
                    {
                        if (!_updating)
                        {
                            RefreshPreview();
                        }
                    };
                    break;
                case ComboBox comboBox:
                    comboBox.SelectionChanged += (_, _) =>
                    {
                        if (!_updating)
                        {
                            RefreshPreview();
                        }
                    };
                    break;
                case ToggleButton toggleButton:
                    toggleButton.IsCheckedChanged += (_, _) =>
                    {
                        if (!_updating)
                        {
                            RefreshPreview();
                        }
                    };
                    break;
            }
        }
    }

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
        _updating = true;
        try
        {
            ConfigPathTextBlock.Text = project.ConfigFilePath;

            MaterialConvertEnabledCheckBox.IsChecked = project.MaterialConvertEnabled;
            SelectComboByKey(VideoEncoderComboBox, string.IsNullOrWhiteSpace(project.VideoEncoder)
                ? (project.VideoUseHardwareEncoder ? "auto" : "libx264")
                : project.VideoEncoder);
            VideoPresetComboBox.SelectedItem = string.IsNullOrWhiteSpace(project.VideoPreset) ? "veryfast" : project.VideoPreset;
            NvencCqTextBox.Text = DefaultText(project.NvencCq, "21");
            NvencMaxParallelTextBox.Text = DefaultText(project.NvencMaxParallel, DefaultText(project.VideoConcurrentCount, "4"));
            VerboseTranscodeLogCheckBox.IsChecked = project.VerboseTranscodeLogEnabled;
            SkipBitrateDownscaleCheckBox.IsChecked = project.SkipBitrateDownscaleForHighBitrate;
            UploadTargetBitrateTextBox.Text = string.IsNullOrWhiteSpace(project.UploadTargetVideoBitrateMbps)
                ? ResolveLegacyBitrateMbps(project.VideoBitrateBps, 4.8d)
                : project.UploadTargetVideoBitrateMbps;
            UploadMaxBitrateTextBox.Text = DefaultText(project.UploadMaxVideoBitrateMbps, "5.6");
            UploadMinBitrateTextBox.Text = DefaultText(project.UploadMinVideoBitrateMbps, "4.5");
            UploadAudioBitrateTextBox.Text = string.IsNullOrWhiteSpace(project.UploadAudioBitrateKbps)
                ? ResolveLegacyAudioKbps(project.VideoAudioBitrateBps, 128)
                : project.UploadAudioBitrateKbps;
            UploadFallbackEnabledCheckBox.IsChecked = project.UploadBitrateFallbackEnabled;
            UploadFallbackBitrateTextBox.Text = DefaultText(project.UploadBitrateFallbackVideoBitrateMbps, "4.8");

            var profiles = UploadTranscodeBitrateProfiles.Parse(project.UploadBitrateProfilesJson);
            ApplyProfile(Profile720EnabledCheckBox, Profile720MinTextBox, Profile720MaxTextBox, Profile720BitrateTextBox, Profile720AudioTextBox, profiles.ElementAtOrDefault(0) ?? UploadTranscodeBitrateProfiles.DefaultProfiles[0]);
            ApplyProfile(Profile1080EnabledCheckBox, Profile1080MinTextBox, Profile1080MaxTextBox, Profile1080BitrateTextBox, Profile1080AudioTextBox, profiles.ElementAtOrDefault(1) ?? UploadTranscodeBitrateProfiles.DefaultProfiles[1]);
            ApplyProfile(Profile2kEnabledCheckBox, Profile2kMinTextBox, Profile2kMaxTextBox, Profile2kBitrateTextBox, Profile2kAudioTextBox, profiles.ElementAtOrDefault(2) ?? UploadTranscodeBitrateProfiles.DefaultProfiles[2]);

            TrimHeadTextBox.Text = DefaultText(project.MaterialTrimHeadSeconds, "0.5");
            TrimTailTextBox.Text = DefaultText(project.MaterialTrimTailSeconds, "0.5");
            SpeedPercentTextBox.Text = DefaultText(project.MaterialSpeedPercent, "0");
            DynamicSpeedEnabledCheckBox.IsChecked = project.MaterialDynamicSpeedEnabled;
            SelectComboByKey(DynamicSpeedPresetComboBox, DefaultText(project.MaterialDynamicSpeedPresetName, "light_rhythm"));
            DynamicSpeedHeadSecondsTextBox.Text = DefaultText(project.MaterialDynamicSpeedHeadSeconds, "2.5");
            DynamicSpeedHeadPercentTextBox.Text = DefaultText(project.MaterialDynamicSpeedHeadPercent, "8");
            DynamicSpeedMiddlePercentTextBox.Text = DefaultText(project.MaterialDynamicSpeedMiddlePercent, "6");
            DynamicSpeedTailSecondsTextBox.Text = DefaultText(project.MaterialDynamicSpeedTailSeconds, "2.5");
            DynamicSpeedTailPercentTextBox.Text = DefaultText(project.MaterialDynamicSpeedTailPercent, "8");

            FrameSamplingEnabledCheckBox.IsChecked = project.MaterialFrameSamplingEnabled;
            SelectComboByKey(FrameSamplingModeComboBox, DefaultText(project.MaterialFrameSamplingMode, "fixed_interval"));
            FrameSamplingIntervalTextBox.Text = DefaultText(project.MaterialFrameSamplingInterval, DefaultText(project.MaterialDropEveryNFrames, "20"));

            CropHeightPercentTextBox.Text = DefaultText(project.MaterialCropHeightPercent, "0");
            CropWidthPercentTextBox.Text = DefaultText(project.MaterialCropWidthPercent, "0");
            ForegroundZoomPercentTextBox.Text = DefaultText(project.MaterialForegroundZoomPercent, "0");
            WatermarkEnabledCheckBox.IsChecked = project.MaterialWatermarkEnabled;
            WatermarkTextTextBox.Text = project.MaterialWatermarkText;
            WatermarkFontSizeTextBox.Text = DefaultText(project.MaterialWatermarkFontSize, "35");
            SelectComboByKey(WatermarkPositionComboBox, DefaultText(project.MaterialWatermarkPosition, "top_right"));
            WatermarkMarginXTextBox.Text = DefaultText(project.MaterialWatermarkMarginX, "30");
            WatermarkMarginYTextBox.Text = DefaultText(project.MaterialWatermarkMarginY, "30");
            OutputWidthTextBox.Text = DefaultText(project.MaterialOutputWidth, "1080");
            OutputHeightTextBox.Text = DefaultText(project.MaterialOutputHeight, "1920");
            PipWidthPercentTextBox.Text = DefaultText(project.MaterialPipWidthPercent, "100");
            PipHeightPercentTextBox.Text = DefaultText(project.MaterialPipHeightPercent, "100");
        }
        finally
        {
            _updating = false;
        }

        RefreshDynamicSpeedState();
        RefreshFrameSamplingState();
        RefreshWatermarkState();
        RefreshFallbackState();
        RefreshPreview();
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
            !TryParseDouble(UploadTargetBitrateTextBox.Text, 1d, 50d, out var targetBitrate) ||
            !TryParseDouble(UploadMaxBitrateTextBox.Text, 1d, 80d, out var maxBitrate) ||
            !TryParseDouble(UploadMinBitrateTextBox.Text, 0d, 80d, out var minBitrate) ||
            !TryParseInteger(UploadAudioBitrateTextBox.Text, 64, 320, out var audioBitrate) ||
            !TryParseDouble(UploadFallbackBitrateTextBox.Text, 1d, 80d, out var fallbackBitrate) ||
            !TryParseDouble(TrimHeadTextBox.Text, 0d, 30d, out var trimHeadSeconds) ||
            !TryParseDouble(TrimTailTextBox.Text, 0d, 30d, out var trimTailSeconds) ||
            !TryParseDouble(SpeedPercentTextBox.Text, -50d, 100d, out var speedPercent) ||
            !TryParseDouble(DynamicSpeedHeadSecondsTextBox.Text, 0d, 15d, out var dynamicHeadSeconds) ||
            !TryParseDouble(DynamicSpeedHeadPercentTextBox.Text, -50d, 100d, out var dynamicHeadPercent) ||
            !TryParseDouble(DynamicSpeedMiddlePercentTextBox.Text, -50d, 100d, out var dynamicMiddlePercent) ||
            !TryParseDouble(DynamicSpeedTailSecondsTextBox.Text, 0d, 15d, out var dynamicTailSeconds) ||
            !TryParseDouble(DynamicSpeedTailPercentTextBox.Text, -50d, 100d, out var dynamicTailPercent) ||
            !TryParseInteger(FrameSamplingIntervalTextBox.Text, 2, 240, out var frameSamplingInterval) ||
            !TryParseDouble(CropHeightPercentTextBox.Text, 0d, 40d, out var cropHeightPercent) ||
            !TryParseDouble(CropWidthPercentTextBox.Text, 0d, 40d, out var cropWidthPercent) ||
            !TryParseDouble(ForegroundZoomPercentTextBox.Text, 0d, 20d, out var foregroundZoomPercent) ||
            !TryParseInteger(WatermarkFontSizeTextBox.Text, 16, 72, out var watermarkFontSize) ||
            !TryParseInteger(WatermarkMarginXTextBox.Text, 0, 300, out var watermarkMarginX) ||
            !TryParseInteger(WatermarkMarginYTextBox.Text, 0, 300, out var watermarkMarginY) ||
            !TryParseInteger(OutputWidthTextBox.Text, 540, 2160, out var outputWidth) ||
            !TryParseInteger(OutputHeightTextBox.Text, 960, 3840, out var outputHeight) ||
            !TryParseDouble(PipWidthPercentTextBox.Text, 10d, 100d, out var pipWidthPercent) ||
            !TryParseDouble(PipHeightPercentTextBox.Text, 10d, 100d, out var pipHeightPercent))
        {
            SetStatus("请检查转码模板中的数值输入。");
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
        var selectedEncoder = GetSelectedOptionKey(VideoEncoderComboBox, "libx264");
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
            UploadMinVideoBitrateMbps = Math.Min(Math.Max(0d, minBitrate), Math.Max(targetBitrate, maxBitrate)).ToString("0.###", CultureInfo.InvariantCulture),
            UploadAudioBitrateKbps = audioBitrate.ToString(CultureInfo.InvariantCulture),
            UploadBitrateFallbackEnabled = UploadFallbackEnabledCheckBox.IsChecked == true,
            UploadBitrateFallbackVideoBitrateMbps = fallbackBitrate.ToString("0.###", CultureInfo.InvariantCulture),
            UploadBitrateProfilesJson = UploadTranscodeBitrateProfiles.Serialize(profiles!),
            VideoBitrateBps = ToBitrateBps(targetBitrate).ToString(CultureInfo.InvariantCulture),
            VideoBitrateMode = string.Equals(selectedEncoder, "libx264", StringComparison.OrdinalIgnoreCase)
                ? "Cbr"
                : DefaultText(project.VideoBitrateMode, "Cbr"),
            VideoAudioBitrateBps = (audioBitrate * 1000).ToString(CultureInfo.InvariantCulture),
            MaterialTrimHeadSeconds = trimHeadSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialTrimTailSeconds = trimTailSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialSpeedPercent = speedPercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialDynamicSpeedEnabled = DynamicSpeedEnabledCheckBox.IsChecked == true,
            MaterialDynamicSpeedPresetName = GetSelectedOptionKey(DynamicSpeedPresetComboBox, "custom"),
            MaterialDynamicSpeedHeadSeconds = dynamicHeadSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialDynamicSpeedHeadPercent = dynamicHeadPercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialDynamicSpeedMiddlePercent = dynamicMiddlePercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialDynamicSpeedTailSeconds = dynamicTailSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialDynamicSpeedTailPercent = dynamicTailPercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialFrameSamplingEnabled = FrameSamplingEnabledCheckBox.IsChecked == true,
            MaterialFrameSamplingMode = GetSelectedOptionKey(FrameSamplingModeComboBox, "fixed_interval"),
            MaterialFrameSamplingInterval = frameSamplingInterval.ToString(CultureInfo.InvariantCulture),
            MaterialDropEveryNFrames = frameSamplingInterval.ToString(CultureInfo.InvariantCulture),
            MaterialDropCount = FrameSamplingEnabledCheckBox.IsChecked == true ? "1" : "0",
            MaterialCropWidthPercent = cropWidthPercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialCropHeightPercent = cropHeightPercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialForegroundZoomPercent = foregroundZoomPercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialWatermarkEnabled = WatermarkEnabledCheckBox.IsChecked == true,
            MaterialWatermarkText = WatermarkTextTextBox.Text?.Trim() ?? string.Empty,
            MaterialWatermarkFontSize = watermarkFontSize.ToString(CultureInfo.InvariantCulture),
            MaterialWatermarkPosition = GetSelectedOptionKey(WatermarkPositionComboBox, "top_right"),
            MaterialWatermarkMarginX = watermarkMarginX.ToString(CultureInfo.InvariantCulture),
            MaterialWatermarkMarginY = watermarkMarginY.ToString(CultureInfo.InvariantCulture),
            MaterialOutputWidth = EnsureEven(outputWidth).ToString(CultureInfo.InvariantCulture),
            MaterialOutputHeight = EnsureEven(outputHeight).ToString(CultureInfo.InvariantCulture),
            MaterialPipWidthPercent = pipWidthPercent.ToString("0.###", CultureInfo.InvariantCulture),
            MaterialPipHeightPercent = pipHeightPercent.ToString("0.###", CultureInfo.InvariantCulture)
        };

        _configService.SaveProject(updated);
        LoadConfig();
        SetStatus("转码模板配置已保存。");
    }

    private void ResetToDefaults()
    {
        _updating = true;
        try
        {
            MaterialConvertEnabledCheckBox.IsChecked = true;
            SelectComboByKey(VideoEncoderComboBox, "auto");
            VideoPresetComboBox.SelectedItem = "veryfast";
            NvencCqTextBox.Text = "21";
            NvencMaxParallelTextBox.Text = "4";
            VerboseTranscodeLogCheckBox.IsChecked = false;
            SkipBitrateDownscaleCheckBox.IsChecked = false;
            UploadTargetBitrateTextBox.Text = "4.8";
            UploadMaxBitrateTextBox.Text = "5.6";
            UploadMinBitrateTextBox.Text = "4.5";
            UploadAudioBitrateTextBox.Text = "128";
            UploadFallbackEnabledCheckBox.IsChecked = true;
            UploadFallbackBitrateTextBox.Text = "4.8";

            var profiles = UploadTranscodeBitrateProfiles.DefaultProfiles;
            ApplyProfile(Profile720EnabledCheckBox, Profile720MinTextBox, Profile720MaxTextBox, Profile720BitrateTextBox, Profile720AudioTextBox, profiles[0]);
            ApplyProfile(Profile1080EnabledCheckBox, Profile1080MinTextBox, Profile1080MaxTextBox, Profile1080BitrateTextBox, Profile1080AudioTextBox, profiles[1]);
            ApplyProfile(Profile2kEnabledCheckBox, Profile2kMinTextBox, Profile2kMaxTextBox, Profile2kBitrateTextBox, Profile2kAudioTextBox, profiles[2]);

            TrimHeadTextBox.Text = "0.5";
            TrimTailTextBox.Text = "0.5";
            SpeedPercentTextBox.Text = "0";
            DynamicSpeedEnabledCheckBox.IsChecked = false;
            SelectComboByKey(DynamicSpeedPresetComboBox, "light_rhythm");
            DynamicSpeedHeadSecondsTextBox.Text = "2.5";
            DynamicSpeedHeadPercentTextBox.Text = "8";
            DynamicSpeedMiddlePercentTextBox.Text = "6";
            DynamicSpeedTailSecondsTextBox.Text = "2.5";
            DynamicSpeedTailPercentTextBox.Text = "8";

            FrameSamplingEnabledCheckBox.IsChecked = true;
            SelectComboByKey(FrameSamplingModeComboBox, "fixed_interval");
            FrameSamplingIntervalTextBox.Text = "20";

            CropHeightPercentTextBox.Text = "0";
            CropWidthPercentTextBox.Text = "0";
            ForegroundZoomPercentTextBox.Text = "0";
            WatermarkEnabledCheckBox.IsChecked = false;
            WatermarkTextTextBox.Text = string.Empty;
            WatermarkFontSizeTextBox.Text = "35";
            SelectComboByKey(WatermarkPositionComboBox, "top_right");
            WatermarkMarginXTextBox.Text = "30";
            WatermarkMarginYTextBox.Text = "30";
            OutputWidthTextBox.Text = "1080";
            OutputHeightTextBox.Text = "1920";
            PipWidthPercentTextBox.Text = "100";
            PipHeightPercentTextBox.Text = "100";
        }
        finally
        {
            _updating = false;
        }

        RefreshDynamicSpeedState();
        RefreshFrameSamplingState();
        RefreshWatermarkState();
        RefreshFallbackState();
        RefreshPreview();
        SetStatus("已恢复转码模板默认配置。");
    }

    private void RefreshDynamicSpeedState()
    {
        var enabled = DynamicSpeedEnabledCheckBox.IsChecked == true;
        SpeedPercentTextBox.IsEnabled = !enabled;
        DynamicSpeedPresetComboBox.IsEnabled = enabled;
        DynamicSpeedHeadSecondsTextBox.IsEnabled = enabled;
        DynamicSpeedHeadPercentTextBox.IsEnabled = enabled;
        DynamicSpeedMiddlePercentTextBox.IsEnabled = enabled;
        DynamicSpeedTailSecondsTextBox.IsEnabled = enabled;
        DynamicSpeedTailPercentTextBox.IsEnabled = enabled;
    }

    private void RefreshFrameSamplingState()
    {
        var enabled = FrameSamplingEnabledCheckBox.IsChecked == true;
        FrameSamplingModeComboBox.IsEnabled = enabled;
        FrameSamplingIntervalTextBox.IsEnabled = enabled;
    }

    private void RefreshWatermarkState()
    {
        var enabled = WatermarkEnabledCheckBox.IsChecked == true;
        WatermarkTextTextBox.IsEnabled = enabled;
        WatermarkFontSizeTextBox.IsEnabled = enabled;
        WatermarkPositionComboBox.IsEnabled = enabled;
        WatermarkMarginXTextBox.IsEnabled = enabled;
        WatermarkMarginYTextBox.IsEnabled = enabled;

        var position = GetSelectedOptionKey(WatermarkPositionComboBox, "top_right");
        WatermarkMarginXLabel.Text = string.Equals(position, "top_left", StringComparison.OrdinalIgnoreCase)
            ? "距左边"
            : "距右边";
    }

    private void RefreshFallbackState()
    {
        UploadFallbackBitrateTextBox.IsEnabled = UploadFallbackEnabledCheckBox.IsChecked == true;
    }

    private void ApplySelectedDynamicSpeedPreset()
    {
        if (_updating || _updatingDynamicPreset)
        {
            return;
        }

        var presetKey = GetSelectedOptionKey(DynamicSpeedPresetComboBox, "custom");
        if (!DynamicSpeedPresets.TryGetValue(presetKey, out var preset) || string.Equals(preset.Key, "custom", StringComparison.Ordinal))
        {
            return;
        }

        _updatingDynamicPreset = true;
        try
        {
            DynamicSpeedHeadSecondsTextBox.Text = preset.HeadSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            DynamicSpeedHeadPercentTextBox.Text = preset.HeadPercent.ToString("0.###", CultureInfo.InvariantCulture);
            DynamicSpeedMiddlePercentTextBox.Text = preset.MiddlePercent.ToString("0.###", CultureInfo.InvariantCulture);
            DynamicSpeedTailSecondsTextBox.Text = preset.TailSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            DynamicSpeedTailPercentTextBox.Text = preset.TailPercent.ToString("0.###", CultureInfo.InvariantCulture);
        }
        finally
        {
            _updatingDynamicPreset = false;
        }

        RefreshPreview();
    }

    private void MarkDynamicPresetCustom()
    {
        if (_updating || _updatingDynamicPreset)
        {
            return;
        }

        if (string.Equals(GetSelectedOptionKey(DynamicSpeedPresetComboBox, "custom"), "custom", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _updatingDynamicPreset = true;
        try
        {
            SelectComboByKey(DynamicSpeedPresetComboBox, "custom");
        }
        finally
        {
            _updatingDynamicPreset = false;
        }
    }

    private void RefreshPreview()
    {
        var outputWidth = EnsureEven(ReadInteger(OutputWidthTextBox.Text, 1080, 540, 2160));
        var outputHeight = EnsureEven(ReadInteger(OutputHeightTextBox.Text, 1920, 960, 3840));
        var pipWidthPercent = ReadDouble(PipWidthPercentTextBox.Text, 100d, 10d, 100d);
        var pipHeightPercent = ReadDouble(PipHeightPercentTextBox.Text, 100d, 10d, 100d);
        var foregroundZoomPercent = ReadDouble(ForegroundZoomPercentTextBox.Text, 0d, 0d, 20d);
        var cropWidthPercent = ReadDouble(CropWidthPercentTextBox.Text, 0d, 0d, 40d);
        var cropHeightPercent = ReadDouble(CropHeightPercentTextBox.Text, 0d, 0d, 40d);
        var watermarkEnabled = WatermarkEnabledCheckBox.IsChecked == true;
        var watermarkText = string.IsNullOrWhiteSpace(WatermarkTextTextBox.Text) ? "水印" : WatermarkTextTextBox.Text!.Trim();
        var watermarkPosition = GetSelectedOptionKey(WatermarkPositionComboBox, "top_right");
        var watermarkMarginX = ReadInteger(WatermarkMarginXTextBox.Text, 30, 0, 300);
        var watermarkMarginY = ReadInteger(WatermarkMarginYTextBox.Text, 30, 0, 300);
        var watermarkFontSize = ReadInteger(WatermarkFontSizeTextBox.Text, 35, 16, 72);

        PreviewCanvas.Width = outputWidth;
        PreviewCanvas.Height = outputHeight;
        PreviewFrameBorder.Width = outputWidth;
        PreviewFrameBorder.Height = outputHeight;

        var foregroundWidth = EnsureEven((int)Math.Round(outputWidth * pipWidthPercent / 100d, MidpointRounding.AwayFromZero));
        var foregroundHeight = EnsureEven((int)Math.Round(outputHeight * pipHeightPercent / 100d, MidpointRounding.AwayFromZero));
        foregroundWidth = Math.Clamp(foregroundWidth, 2, outputWidth);
        foregroundHeight = Math.Clamp(foregroundHeight, 2, outputHeight);
        var foregroundLeft = Math.Max(0, (outputWidth - foregroundWidth) / 2);
        var foregroundTop = Math.Max(0, (outputHeight - foregroundHeight) / 2);

        PreviewForegroundBorder.Width = foregroundWidth;
        PreviewForegroundBorder.Height = foregroundHeight;
        Canvas.SetLeft(PreviewForegroundBorder, foregroundLeft);
        Canvas.SetTop(PreviewForegroundBorder, foregroundTop);

        var cropInsetX = (int)Math.Round(foregroundWidth * cropWidthPercent / 200d, MidpointRounding.AwayFromZero);
        var cropInsetY = (int)Math.Round(foregroundHeight * cropHeightPercent / 200d, MidpointRounding.AwayFromZero);
        var cropWidth = Math.Max(2, foregroundWidth - cropInsetX * 2);
        var cropHeight = Math.Max(2, foregroundHeight - cropInsetY * 2);
        PreviewCropRectangle.Width = cropWidth;
        PreviewCropRectangle.Height = cropHeight;
        Canvas.SetLeft(PreviewCropRectangle, foregroundLeft + cropInsetX);
        Canvas.SetTop(PreviewCropRectangle, foregroundTop + cropInsetY);
        PreviewCropRectangle.IsVisible = cropWidthPercent > 0 || cropHeightPercent > 0;

        var zoomRatio = 1d + foregroundZoomPercent / 100d;
        var visibleWidth = Math.Max(2, (int)Math.Round(foregroundWidth / zoomRatio, MidpointRounding.AwayFromZero));
        var visibleHeight = Math.Max(2, (int)Math.Round(foregroundHeight / zoomRatio, MidpointRounding.AwayFromZero));
        PreviewZoomRectangle.Width = visibleWidth;
        PreviewZoomRectangle.Height = visibleHeight;
        Canvas.SetLeft(PreviewZoomRectangle, foregroundLeft + Math.Max(0, (foregroundWidth - visibleWidth) / 2));
        Canvas.SetTop(PreviewZoomRectangle, foregroundTop + Math.Max(0, (foregroundHeight - visibleHeight) / 2));
        PreviewZoomRectangle.IsVisible = foregroundZoomPercent > 0.001d;

        PreviewWatermarkTextBlock.IsVisible = watermarkEnabled;
        PreviewWatermarkTextBlock.Text = watermarkText;
        PreviewWatermarkTextBlock.FontSize = watermarkFontSize;
        PreviewWatermarkTextBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desiredSize = PreviewWatermarkTextBlock.DesiredSize;
        var textX = watermarkPosition == "top_left"
            ? watermarkMarginX
            : Math.Max(0, outputWidth - desiredSize.Width - watermarkMarginX);
        var textY = Math.Max(0, watermarkMarginY);
        Canvas.SetLeft(PreviewWatermarkTextBlock, textX);
        Canvas.SetTop(PreviewWatermarkTextBlock, textY);

        PreviewDescriptionTextBlock.Text = foregroundZoomPercent > 0.001d
            ? $"Portrait preview\nZoom +{foregroundZoomPercent:0.#}%"
            : "Portrait preview";
        Canvas.SetLeft(PreviewDescriptionTextBlock, 18);
        Canvas.SetTop(PreviewDescriptionTextBlock, 18);
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
            !TryParseDouble(bitrateTextBox.Text, 1d, 80d, out var bitrateMbps) ||
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

    private static void SelectComboByKey(ComboBox comboBox, string key)
    {
        if (comboBox.ItemsSource is not IEnumerable<WorkflowStepOption> options)
        {
            return;
        }

        comboBox.SelectedItem = options.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault();
    }

    private static string GetSelectedOptionKey(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is WorkflowStepOption option
            ? option.Key
            : fallback;
    }

    private static string DefaultText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int EnsureEven(int value)
    {
        return value % 2 == 0 ? value : value + 1;
    }

    private static int ReadInteger(string? text, int fallback, int minimum, int maximum)
    {
        return int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }

    private static double ReadDouble(string? text, double fallback, double minimum, double maximum)
    {
        return double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }

    private static bool TryParseInteger(string? text, int minimum, int maximum, out int value)
    {
        if (int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value = Math.Clamp(value, minimum, maximum);
            return true;
        }

        value = minimum;
        return false;
    }

    private static bool TryParseDouble(string? text, double minimum, double maximum, out double value)
    {
        if (double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            value = Math.Clamp(value, minimum, maximum);
            return true;
        }

        value = minimum;
        return false;
    }

    private static string ResolveLegacyBitrateMbps(string? legacyBitrateBps, double fallbackMbps)
    {
        return long.TryParse((legacyBitrateBps ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? (parsed / 1_000_000d).ToString("0.###", CultureInfo.InvariantCulture)
            : fallbackMbps.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string ResolveLegacyAudioKbps(string? legacyAudioBitrateBps, int fallbackKbps)
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

    private sealed record DynamicSpeedPreset(
        string Key,
        string Label,
        double HeadSeconds,
        double HeadPercent,
        double MiddlePercent,
        double TailSeconds,
        double TailPercent);
}
