using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TikTokPublisher.Core.Models;

/// <summary>账号级视频去重方案。字段对齐短剧处理软件的 video_presets。</summary>
public sealed class VideoDedupPreset
{
    public const string EncodingHardwareFirst = "HardwareFirst";
    public const string EncodingSoftwareOnly = "SoftwareOnly";
    public const string EncodingNvenc = "Nvenc";
    public const string EncodingAmf = "Amf";
    public const string EncodingQsv = "Qsv";

    public const string CodecDefault = "default";
    public const string CodecH264 = "h264";
    public const string CodecHevc = "hevc";
    public const string CodecH265 = "h265";

    public const string BitrateVbr = "Vbr";
    public const string BitrateCbr = "Cbr";
    public const string BitrateCrf = "Crf";

    public const string AudioDefault = "default";
    public const string AudioAac = "aac";

    public const double DefaultVideoBitrateMbps = 2;

    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "默认方案";
    public string EncodingPreference { get; set; } = EncodingHardwareFirst;
    public int Parallel { get; set; } = 2;
    public double MinimumDurationSeconds { get; set; }
    public double MaximumDurationSeconds { get; set; }

    public bool TransitionsEnabled { get; set; }
    public bool TransitionFadeBlack { get; set; }
    public bool TransitionDissolve { get; set; }
    public bool TransitionPush { get; set; }
    public bool TransitionSlide { get; set; }
    public bool TransitionZoom { get; set; }

    public double TrimStartSeconds { get; set; }
    public double TrimEndSeconds { get; set; }
    public double CropTopPercent { get; set; }
    public double CropBottomPercent { get; set; }
    public double CropLeftPercent { get; set; }
    public double CropRightPercent { get; set; }
    public int PortraitWidth { get; set; }
    public int PortraitHeight { get; set; }
    public int LandscapeWidth { get; set; } = 1920;
    public int LandscapeHeight { get; set; } = 1080;
    public double SpeedFactor { get; set; } = 1;
    public double FrameDropPercent { get; set; }

    public double Brightness { get; set; }
    public double Contrast { get; set; } = 1;
    public double Saturation { get; set; } = 1;
    public bool Grayscale { get; set; }
    public double Sharpness { get; set; }
    public VideoDedupEffects Effects { get; set; } = new();

    public string VideoCodec { get; set; } = CodecH264;
    public int Fps { get; set; } = 30;
    public string BitrateMode { get; set; } = BitrateVbr;
    public double VideoBitrateMbps { get; set; } = DefaultVideoBitrateMbps;
    public int Crf { get; set; } = 23;

    public string AudioCodec { get; set; } = AudioAac;
    public int AudioBitrateKbps { get; set; }
    public double AudioLowGainDb { get; set; }
    public double AudioMidGainDb { get; set; }
    public double AudioHighGainDb { get; set; }

    public static VideoDedupPreset CreateDefault() => new();

    /// <summary>把方案各自复制到这些账号上，不改账号的其他配置。</summary>
    public void CopyOnto(IEnumerable<TikTokAccountProfile> accounts)
    {
        foreach (var account in accounts)
            account.VideoDedupPreset = Clone();
    }

    public VideoDedupPreset Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Name = Name,
        EncodingPreference = EncodingPreference,
        Parallel = Parallel,
        MinimumDurationSeconds = MinimumDurationSeconds,
        MaximumDurationSeconds = MaximumDurationSeconds,
        TransitionsEnabled = TransitionsEnabled,
        TransitionFadeBlack = TransitionFadeBlack,
        TransitionDissolve = TransitionDissolve,
        TransitionPush = TransitionPush,
        TransitionSlide = TransitionSlide,
        TransitionZoom = TransitionZoom,
        TrimStartSeconds = TrimStartSeconds,
        TrimEndSeconds = TrimEndSeconds,
        CropTopPercent = CropTopPercent,
        CropBottomPercent = CropBottomPercent,
        CropLeftPercent = CropLeftPercent,
        CropRightPercent = CropRightPercent,
        PortraitWidth = PortraitWidth,
        PortraitHeight = PortraitHeight,
        LandscapeWidth = LandscapeWidth,
        LandscapeHeight = LandscapeHeight,
        SpeedFactor = SpeedFactor,
        FrameDropPercent = FrameDropPercent,
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Grayscale = Grayscale,
        Sharpness = Sharpness,
        Effects = Effects?.Clone() ?? new VideoDedupEffects(),
        VideoCodec = VideoCodec,
        Fps = Fps,
        BitrateMode = BitrateMode,
        VideoBitrateMbps = VideoBitrateMbps,
        Crf = Crf,
        AudioCodec = AudioCodec,
        AudioBitrateKbps = AudioBitrateKbps,
        AudioLowGainDb = AudioLowGainDb,
        AudioMidGainDb = AudioMidGainDb,
        AudioHighGainDb = AudioHighGainDb,
    };

    public VideoDedupPreset NormalizedCopy()
    {
        var copy = Clone();
        copy.Normalize();
        return copy;
    }

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "默认方案" : Name.Trim();
        EncodingPreference = EncodingPreference switch
        {
            EncodingSoftwareOnly or EncodingNvenc or EncodingAmf or EncodingQsv => EncodingPreference,
            _ => EncodingHardwareFirst,
        };
        Parallel = Math.Clamp(Parallel <= 0 ? 2 : Parallel, 1, 4);
        MinimumDurationSeconds = MinimumDurationSeconds <= 0 ? 0 : Math.Clamp(MinimumDurationSeconds, 1, 3600);
        MaximumDurationSeconds = MaximumDurationSeconds <= 0 ? 0 : Math.Clamp(MaximumDurationSeconds, 1, 7200);

        TrimStartSeconds = Math.Clamp(TrimStartSeconds, 0, 30);
        TrimEndSeconds = Math.Clamp(TrimEndSeconds, 0, 30);
        var verticalCrop = Math.Min(39, Math.Max(ClampPercent(CropTopPercent), ClampPercent(CropBottomPercent)));
        var horizontalCrop = Math.Min(39, Math.Max(ClampPercent(CropLeftPercent), ClampPercent(CropRightPercent)));
        CropTopPercent = verticalCrop;
        CropBottomPercent = verticalCrop;
        CropLeftPercent = horizontalCrop;
        CropRightPercent = horizontalCrop;

        if (KeepsOriginalResolution)
        {
            PortraitWidth = 0;
            PortraitHeight = 0;
            LandscapeWidth = 0;
            LandscapeHeight = 0;
        }
        else
        {
            PortraitWidth = EvenAtLeast(PortraitWidth, 1080);
            PortraitHeight = EvenAtLeast(PortraitHeight, 1920);
            LandscapeWidth = PortraitHeight;
            LandscapeHeight = PortraitWidth;
        }
        SpeedFactor = Math.Clamp(SpeedFactor <= 0 ? 1 : SpeedFactor, 0.5, 2);
        FrameDropPercent = Math.Clamp(FrameDropPercent, 0, 40);
        Brightness = Math.Clamp(Brightness, -0.3, 0.3);
        Contrast = Math.Clamp(Contrast <= 0 ? 1 : Contrast, 0.5, 1.5);
        Saturation = Math.Clamp(Saturation <= 0 ? 1 : Saturation, 0, 2);
        Sharpness = Math.Clamp(Sharpness, -2, 2);
        Effects ??= new VideoDedupEffects();
        Effects.Normalize();
        Fps = Math.Clamp(Fps <= 0 ? 30 : Fps, 15, 60);
        VideoCodec = VideoCodec switch
        {
            CodecH264 or CodecHevc or CodecH265 or CodecDefault => VideoCodec,
            _ => CodecH264,
        };
        BitrateMode = BitrateMode switch
        {
            BitrateCbr or BitrateCrf or BitrateVbr => BitrateMode,
            _ => BitrateVbr,
        };
        VideoBitrateMbps = Math.Clamp(VideoBitrateMbps <= 0 ? DefaultVideoBitrateMbps : VideoBitrateMbps, 0.5, 20);
        Crf = Math.Clamp(Crf <= 0 ? 23 : Crf, 18, 32);
        AudioCodec = AudioCodec switch
        {
            AudioAac or AudioDefault => AudioCodec,
            _ => AudioAac,
        };
        AudioBitrateKbps = AudioBitrateKbps <= 0 ? 0 : Math.Clamp(AudioBitrateKbps, 64, 320);
        AudioLowGainDb = Math.Clamp(AudioLowGainDb, -12, 12);
        AudioMidGainDb = Math.Clamp(AudioMidGainDb, -12, 12);
        AudioHighGainDb = Math.Clamp(AudioHighGainDb, -12, 12);
        if (TransitionsEnabled && !HasAnyTransition)
            TransitionsEnabled = false;
    }

    public bool HasAnyTransition =>
        TransitionFadeBlack || TransitionDissolve || TransitionPush || TransitionSlide || TransitionZoom;

    public bool HasDurationLimit =>
        MinimumDurationSeconds > 0 && MaximumDurationSeconds >= MinimumDurationSeconds;

    /// <summary>两个都留 0 表示不重新分集。只填一个，或最小大于最大，都不能保存。</summary>
    public string? DurationRangeError()
    {
        var hasMinimum = MinimumDurationSeconds > 0;
        var hasMaximum = MaximumDurationSeconds > 0;
        if (hasMinimum != hasMaximum)
            return "单集最小时长和最大时长要同时填写。两个都留 0 表示不重新分集。";
        if (hasMinimum && MaximumDurationSeconds < MinimumDurationSeconds)
            return "单集最小时长不能大于最大时长。";
        return null;
    }

    public bool KeepsOriginalResolution => PortraitWidth <= 0 && PortraitHeight <= 0;

    public string Fingerprint()
    {
        var preset = NormalizedCopy();
        var text = string.Join('\n',
            preset.EncodingPreference,
            preset.Parallel.ToString(CultureInfo.InvariantCulture),
            Num(preset.MinimumDurationSeconds),
            Num(preset.MaximumDurationSeconds) +
                (preset.MinimumDurationSeconds > 0 || preset.MaximumDurationSeconds > 0 ? "\npretrim-split" : "") +
                (preset.HasDurationLimit ? "\nupload-min-15" : ""),
            preset.TransitionsEnabled ? "1" : "0",
            Flag(preset.TransitionFadeBlack),
            Flag(preset.TransitionDissolve),
            Flag(preset.TransitionPush),
            Flag(preset.TransitionSlide),
            Flag(preset.TransitionZoom),
            Num(preset.TrimStartSeconds),
            Num(preset.TrimEndSeconds),
            Num(preset.CropTopPercent),
            Num(preset.CropBottomPercent),
            Num(preset.CropLeftPercent),
            Num(preset.CropRightPercent),
            preset.PortraitWidth.ToString(CultureInfo.InvariantCulture),
            preset.PortraitHeight.ToString(CultureInfo.InvariantCulture),
            preset.LandscapeWidth.ToString(CultureInfo.InvariantCulture),
            preset.LandscapeHeight.ToString(CultureInfo.InvariantCulture),
            Num(preset.SpeedFactor),
            Num(preset.FrameDropPercent),
            Num(preset.Brightness),
            Num(preset.Contrast),
            Num(preset.Saturation),
            preset.Grayscale ? "1" : "0",
            Num(preset.Sharpness),
            preset.VideoCodec,
            preset.Fps.ToString(CultureInfo.InvariantCulture),
            preset.BitrateMode,
            Num(preset.VideoBitrateMbps),
            preset.Crf.ToString(CultureInfo.InvariantCulture),
            preset.AudioCodec,
            preset.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture),
            Num(preset.AudioLowGainDb),
            Num(preset.AudioMidGainDb),
            Num(preset.AudioHighGainDb));
        var effects = preset.Effects.FingerprintBody();
        if (effects.Length > 0)
            text += "\n" + effects;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 40);

    private static int EvenAtLeast(int value, int fallback)
    {
        var resolved = value <= 0 ? fallback : value;
        resolved = Math.Clamp(resolved, 2, 4320);
        return resolved % 2 == 0 ? resolved : resolved - 1;
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Flag(bool value) => value ? "1" : "0";
}
