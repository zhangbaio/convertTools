using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.Views;

public partial class VideoDedupPresetEditor : UserControl
{
    private static readonly (int Width, int Height)[] Resolutions =
    [
        (1080, 1920),
        (720, 1280),
        (540, 960),
    ];

    private IReadOnlyList<VideoDedupPresetImport> _imports = [];
    private bool _loading;

    private void SelectResolution(int width, int height)
    {
        ResolutionBox.Items.Clear();
        foreach (var size in Resolutions)
            ResolutionBox.Items.Add(FormatResolution(size.Width, size.Height));
        var selected = FormatResolution(width, height);
        if (!ResolutionBox.Items.Contains(selected))
            ResolutionBox.Items.Insert(0, selected);
        ResolutionBox.SelectedItem = selected;
    }

    private (int Width, int Height) SelectedResolution()
    {
        var text = ResolutionBox.SelectedItem?.ToString() ?? FormatResolution(1080, 1920);
        var parts = text.Split('×', 'x', 'X', '*');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var width) &&
            int.TryParse(parts[1], out var height) &&
            width > 0 &&
            height > 0)
        {
            return (width, height);
        }

        return (1080, 1920);
    }

    private static string FormatResolution(int width, int height) =>
        $"{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}×{height.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public VideoDedupPresetEditor()
    {
        InitializeComponent();
        Load(null);
    }

    public void Load(VideoDedupPreset? preset)
    {
        _loading = true;
        try
        {
            var value = (preset ?? VideoDedupPreset.CreateDefault()).NormalizedCopy();
            NameBox.Text = value.Name;
            ParallelBox.Value = value.Parallel;
            EncoderAutoBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingHardwareFirst;
            EncoderSoftwareBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingSoftwareOnly;
            EncoderNvencBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingNvenc;
            EncoderAmfBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingAmf;
            EncoderQsvBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingQsv;
            EncoderHardwareBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingHardwareOnly;
            MinDurationBox.Value = (decimal)value.MinimumDurationSeconds;
            MaxDurationBox.Value = (decimal)value.MaximumDurationSeconds;
            TransitionsEnabledBox.IsChecked = value.TransitionsEnabled;
            FadeBlackBox.IsChecked = value.TransitionFadeBlack;
            DissolveBox.IsChecked = value.TransitionDissolve;
            PushBox.IsChecked = value.TransitionPush;
            SlideBox.IsChecked = value.TransitionSlide;
            ZoomBox.IsChecked = value.TransitionZoom;
            TrimStartBox.Value = (decimal)value.TrimStartSeconds;
            TrimEndBox.Value = (decimal)value.TrimEndSeconds;
            CropVerticalBox.Value = (decimal)Math.Max(value.CropTopPercent, value.CropBottomPercent);
            CropHorizontalBox.Value = (decimal)Math.Max(value.CropLeftPercent, value.CropRightPercent);
            SelectResolution(value.PortraitWidth, value.PortraitHeight);
            SpeedBox.Value = (decimal)value.SpeedFactor;
            FrameDropBox.Value = (decimal)value.FrameDropPercent;
            BrightnessBox.Value = (decimal)value.Brightness;
            ContrastBox.Value = (decimal)value.Contrast;
            SaturationBox.Value = (decimal)value.Saturation;
            SharpnessBox.Value = (decimal)value.Sharpness;
            GrayscaleBox.IsChecked = value.Grayscale;
            CodecDefaultBox.IsChecked = value.VideoCodec == VideoDedupPreset.CodecDefault;
            CodecH264Box.IsChecked = value.VideoCodec == VideoDedupPreset.CodecH264;
            CodecHevcBox.IsChecked = value.VideoCodec == VideoDedupPreset.CodecHevc;
            CodecH265Box.IsChecked = value.VideoCodec == VideoDedupPreset.CodecH265;
            FpsBox.Value = value.Fps;
            RateCbrBox.IsChecked = value.BitrateMode == VideoDedupPreset.BitrateCbr;
            RateVbrBox.IsChecked = value.BitrateMode == VideoDedupPreset.BitrateVbr;
            RateCrfBox.IsChecked = value.BitrateMode == VideoDedupPreset.BitrateCrf;
            VideoBitrateBox.Value = (decimal)value.VideoBitrateMbps;
            CrfBox.Value = value.Crf;
            AudioDefaultBox.IsChecked = value.AudioCodec == VideoDedupPreset.AudioDefault;
            AudioAacBox.IsChecked = value.AudioCodec != VideoDedupPreset.AudioDefault;
            AudioBitrateBox.Value = value.AudioBitrateKbps;
            LowGainBox.Value = (decimal)value.AudioLowGainDb;
            MidGainBox.Value = (decimal)value.AudioMidGainDb;
            HighGainBox.Value = (decimal)value.AudioHighGainDb;
        }
        finally
        {
            _loading = false;
        }
    }

    public VideoDedupPreset Read()
    {
        var preset = new VideoDedupPreset
        {
            Name = NameBox.Text?.Trim() ?? "",
            Parallel = (int)(ParallelBox.Value ?? 2),
            EncodingPreference = EncoderSoftwareBox.IsChecked == true
                ? VideoDedupPreset.EncodingSoftwareOnly
                : EncoderNvencBox.IsChecked == true
                    ? VideoDedupPreset.EncodingNvenc
                    : EncoderAmfBox.IsChecked == true
                        ? VideoDedupPreset.EncodingAmf
                        : EncoderQsvBox.IsChecked == true
                            ? VideoDedupPreset.EncodingQsv
                            : EncoderHardwareBox.IsChecked == true
                                ? VideoDedupPreset.EncodingHardwareOnly
                                : VideoDedupPreset.EncodingHardwareFirst,
            MinimumDurationSeconds = (double)(MinDurationBox.Value ?? 0),
            MaximumDurationSeconds = (double)(MaxDurationBox.Value ?? 0),
            TransitionsEnabled = TransitionsEnabledBox.IsChecked == true,
            TransitionFadeBlack = FadeBlackBox.IsChecked == true,
            TransitionDissolve = DissolveBox.IsChecked == true,
            TransitionPush = PushBox.IsChecked == true,
            TransitionSlide = SlideBox.IsChecked == true,
            TransitionZoom = ZoomBox.IsChecked == true,
            TrimStartSeconds = (double)(TrimStartBox.Value ?? 0),
            TrimEndSeconds = (double)(TrimEndBox.Value ?? 0),
            CropTopPercent = (double)(CropVerticalBox.Value ?? 0),
            CropBottomPercent = (double)(CropVerticalBox.Value ?? 0),
            CropLeftPercent = (double)(CropHorizontalBox.Value ?? 0),
            CropRightPercent = (double)(CropHorizontalBox.Value ?? 0),
            PortraitWidth = SelectedResolution().Width,
            PortraitHeight = SelectedResolution().Height,
            LandscapeWidth = SelectedResolution().Height,
            LandscapeHeight = SelectedResolution().Width,
            SpeedFactor = (double)(SpeedBox.Value ?? 1),
            FrameDropPercent = (double)(FrameDropBox.Value ?? 0),
            Brightness = (double)(BrightnessBox.Value ?? 0),
            Contrast = (double)(ContrastBox.Value ?? 1),
            Saturation = (double)(SaturationBox.Value ?? 1),
            Sharpness = (double)(SharpnessBox.Value ?? 0),
            Grayscale = GrayscaleBox.IsChecked == true,
            VideoCodec = CodecHevcBox.IsChecked == true
                ? VideoDedupPreset.CodecHevc
                : CodecH265Box.IsChecked == true
                    ? VideoDedupPreset.CodecH265
                    : CodecDefaultBox.IsChecked == true
                        ? VideoDedupPreset.CodecDefault
                        : VideoDedupPreset.CodecH264,
            Fps = (int)(FpsBox.Value ?? 30),
            BitrateMode = RateCbrBox.IsChecked == true
                ? VideoDedupPreset.BitrateCbr
                : RateCrfBox.IsChecked == true
                    ? VideoDedupPreset.BitrateCrf
                    : VideoDedupPreset.BitrateVbr,
            VideoBitrateMbps = (double)(VideoBitrateBox.Value ?? 4.2m),
            Crf = (int)(CrfBox.Value ?? 23),
            AudioCodec = AudioDefaultBox.IsChecked == true
                ? VideoDedupPreset.AudioDefault
                : VideoDedupPreset.AudioAac,
            AudioBitrateKbps = (int)(AudioBitrateBox.Value ?? 0),
            AudioLowGainDb = (double)(LowGainBox.Value ?? 0),
            AudioMidGainDb = (double)(MidGainBox.Value ?? 0),
            AudioHighGainDb = (double)(HighGainBox.Value ?? 0),
        };
        preset.Normalize();
        return preset;
    }

    private void OnLoadProcessorPresetsClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _imports = VideoDedupPresetImporter.ReadProcessorDatabase();
            _loading = true;
            ImportPresetCombo.Items.Clear();
            foreach (var import in _imports)
                ImportPresetCombo.Items.Add(import.Name);
            _loading = false;
            if (_imports.Count == 0)
            {
                ImportStatusText.Text = "短剧处理软件里还没有视频方案。";
                return;
            }

            ImportPresetCombo.SelectedIndex = 0;
            ImportStatusText.Text = $"已读取 {_imports.Count} 个方案。选择后填入当前账号，保存账号配置后生效。";
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = ex.Message;
        }
    }

    private void OnImportPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || ImportPresetCombo.SelectedIndex < 0 || ImportPresetCombo.SelectedIndex >= _imports.Count)
            return;
        Load(_imports[ImportPresetCombo.SelectedIndex].Preset);
        ImportStatusText.Text = $"已填入「{_imports[ImportPresetCombo.SelectedIndex].Name}」。请保存账号配置。";
    }

    private void OnResetEqualizerClick(object? sender, RoutedEventArgs e)
    {
        LowGainBox.Value = 0;
        MidGainBox.Value = 0;
        HighGainBox.Value = 0;
    }
}
