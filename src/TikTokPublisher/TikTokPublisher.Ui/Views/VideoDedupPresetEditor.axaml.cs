using Avalonia.Controls;
using Avalonia.Interactivity;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.Views;

public partial class VideoDedupPresetEditor : UserControl
{
    private const string KeepOriginalResolution = "保持原分辨率";

    private static readonly (int Width, int Height)[] Resolutions =
    [
        (1080, 1920),
    ];

    private string _presetName = "";
    private double _brightness;
    private double _contrast = 1;
    private double _saturation = 1;
    private double _sharpness;
    private bool _grayscale;
    private VideoDedupEffects _effects = new();

    private void SelectResolution(int width, int height)
    {
        ResolutionBox.Items.Clear();
        ResolutionBox.Items.Add(KeepOriginalResolution);
        foreach (var size in Resolutions)
            ResolutionBox.Items.Add(FormatResolution(size.Width, size.Height));
        if (width <= 0 && height <= 0)
        {
            ResolutionBox.SelectedItem = KeepOriginalResolution;
            return;
        }

        if (Math.Min(width, height) < 1080)
        {
            width = 1080;
            height = 1920;
        }

        var selected = FormatResolution(width, height);
        if (!ResolutionBox.Items.Contains(selected))
            ResolutionBox.Items.Add(selected);
        ResolutionBox.SelectedItem = selected;
    }

    private (int Width, int Height) SelectedResolution()
    {
        var text = ResolutionBox.SelectedItem?.ToString() ?? FormatResolution(1080, 1920);
        if (string.Equals(text, KeepOriginalResolution, StringComparison.Ordinal))
            return (0, 0);
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

    public event EventHandler? ApplyToAllAccountsRequested;

    public VideoDedupPresetEditor()
    {
        InitializeComponent();
        Load(null);
    }

    private void OnApplyToAllAccountsClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ApplyToAllAccountsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Load(VideoDedupPreset? preset)
    {
        var value = (preset ?? VideoDedupPreset.CreateDefault()).NormalizedCopy();
        _presetName = value.Name;
        ParallelBox.Value = value.Parallel;
            EncoderAutoBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingHardwareFirst;
            EncoderSoftwareBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingSoftwareOnly;
            EncoderNvencBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingNvenc;
            EncoderAmfBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingAmf;
            EncoderQsvBox.IsChecked = value.EncodingPreference == VideoDedupPreset.EncodingQsv;
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
            StoreFilters(value);
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

    public VideoDedupPreset Read()
    {
        var preset = new VideoDedupPreset
        {
            Name = _presetName,
            Parallel = (int)(ParallelBox.Value ?? 2),
            EncodingPreference = EncoderSoftwareBox.IsChecked == true
                ? VideoDedupPreset.EncodingSoftwareOnly
                : EncoderNvencBox.IsChecked == true
                    ? VideoDedupPreset.EncodingNvenc
                    : EncoderAmfBox.IsChecked == true
                        ? VideoDedupPreset.EncodingAmf
                        : EncoderQsvBox.IsChecked == true
                            ? VideoDedupPreset.EncodingQsv
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
            Brightness = _brightness,
            Contrast = _contrast,
            Saturation = _saturation,
            Sharpness = _sharpness,
            Grayscale = _grayscale,
            Effects = _effects.Clone(),
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
            VideoBitrateMbps = (double)(VideoBitrateBox.Value ?? (decimal)VideoDedupPreset.DefaultVideoBitrateMbps),
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
        var durationError = preset.DurationRangeError();
        if (durationError is not null)
            throw new InvalidOperationException(durationError);
        return preset;
    }

    private void StoreFilters(VideoDedupPreset value)
    {
        _brightness = value.Brightness;
        _contrast = value.Contrast;
        _saturation = value.Saturation;
        _sharpness = value.Sharpness;
        _grayscale = value.Grayscale;
        _effects = value.Effects?.Clone() ?? new VideoDedupEffects();
        FilterSummaryText.Text = VideoDedupEffectFilters.Summary(value);
    }

    private async void OnOpenEffectsClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        VideoDedupPreset draft;
        try
        {
            draft = Read();
        }
        catch (InvalidOperationException ex)
        {
            FilterSummaryText.Text = ex.Message;
            return;
        }

        if (!await VideoDedupEffectsDialog.EditAsync(owner, draft))
            return;
        StoreFilters(draft);
    }

    private void OnResetEqualizerClick(object? sender, RoutedEventArgs e)
    {
        LowGainBox.Value = 0;
        MidGainBox.Value = 0;
        HighGainBox.Value = 0;
    }
}
