using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Media;

public sealed class FfmpegVideoMaterialConverter : IVideoMaterialConverter
{
    private const int MinShortEdge = 720;
    private const long MinVideoBitrateBps = 4_194_304;
    private const int DefaultAudioBitrateBps = 128_000;
    private const long MaxOutputBytes = 500L * 1024L * 1024L;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".mkv",
        ".avi",
        ".flv",
        ".wmv",
        ".webm"
    };

    private readonly IExternalProcessRunner _processRunner;
    private readonly ILogger<FfmpegVideoMaterialConverter> _logger;

    public FfmpegVideoMaterialConverter(
        IExternalProcessRunner processRunner,
        ILogger<FfmpegVideoMaterialConverter> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<VideoMaterialConvertResult> ConvertAsync(
        VideoMaterialConvertRequest request,
        IProgress<VideoMaterialConvertProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.InputDir))
        {
            throw new DirectoryNotFoundException($"素材视频目录不存在: {request.InputDir}");
        }

        Directory.CreateDirectory(request.OutputDir);
        var settings = LoadSettings(request.ConfigFile);
        if (!settings.Enabled)
        {
            return new VideoMaterialConvertResult(0, 0, 0, 0, [], []);
        }

        var files = Directory.EnumerateFiles(request.InputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skippedOutcomes = new List<MaterialFileOutcome>();
        var pending = new List<(int Index, string InputPath, string OutputPath)>();

        foreach (var (inputPath, index) in files.Select((path, idx) => (path, idx + 1)))
        {
            var outputPath = Path.Combine(request.OutputDir, Path.GetFileName(inputPath));
            if (File.Exists(outputPath) && !request.Overwrite)
            {
                skippedOutcomes.Add(MaterialFileOutcome.Skipped(index, outputPath));
                progress?.Report(new VideoMaterialConvertProgress(
                    index,
                    files.Count,
                    inputPath,
                    outputPath,
                    Kind: "file-skipped",
                    Elapsed: FormatElapsed(TimeSpan.Zero)));
                continue;
            }

            if (File.Exists(outputPath) && request.Overwrite)
            {
                File.Delete(outputPath);
            }

            pending.Add((index, inputPath, outputPath));
        }

        var completed = new List<MaterialFileOutcome>();
        if (pending.Count > 0)
        {
            var concurrency = Math.Clamp(settings.VideoConcurrentCount ?? 1, 1, 4);
            using var gate = new SemaphoreSlim(concurrency);
            var tasks = pending.Select(item => RunConvertWorkItemAsync(
                item.Index,
                files.Count,
                item.InputPath,
                item.OutputPath,
                settings,
                progress,
                gate,
                cancellationToken));
            completed.AddRange(await Task.WhenAll(tasks));
        }

        var all = skippedOutcomes.Concat(completed).OrderBy(item => item.Index).ToList();
        var outputs = all
            .Where(item => item.Kind is "file-completed" or "file-skipped")
            .Select(item => item.OutputPath!)
            .ToList();
        var failures = all
            .Where(item => item.Failure is not null)
            .Select(item => item.Failure!)
            .ToList();

        return new VideoMaterialConvertResult(
            files.Count,
            all.Count(item => item.Kind == "file-completed"),
            all.Count(item => item.Kind == "file-skipped"),
            failures.Count,
            outputs,
            failures);
    }

    private async Task<MaterialFileOutcome> RunConvertWorkItemAsync(
        int index,
        int total,
        string inputPath,
        string outputPath,
        MaterialConvertSettings settings,
        IProgress<VideoMaterialConvertProgress>? progress,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                progress?.Report(new VideoMaterialConvertProgress(index, total, inputPath, outputPath, Kind: "file-started"));
                var stopwatch = Stopwatch.StartNew();
                var inputProbe = await ProbeMediaAsync(inputPath, cancellationToken);
                var plan = BuildPlan(settings, inputProbe);
                await ConvertOnceAsync(inputPath, outputPath, plan, cancellationToken);
                stopwatch.Stop();

                progress?.Report(new VideoMaterialConvertProgress(
                    index,
                    total,
                    inputPath,
                    outputPath,
                    Kind: "file-completed",
                    Elapsed: FormatElapsed(stopwatch.Elapsed)));
                return MaterialFileOutcome.Completed(index, outputPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DeleteFileIfExists(outputPath);
                var failure = new VideoMaterialConvertFailure(inputPath, outputPath, ex.Message);
                _logger.LogError(ex, "Failed to convert material video {Index}/{Total}: {Input} -> {Output}", index, total, inputPath, outputPath);
                progress?.Report(new VideoMaterialConvertProgress(
                    index,
                    total,
                    inputPath,
                    outputPath,
                    Kind: "file-failed",
                    Message: ex.Message));
                return MaterialFileOutcome.Failed(index, outputPath, failure);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ConvertOnceAsync(
        string inputPath,
        string outputPath,
        MaterialConvertPlan plan,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            ResolveBinary("ffmpeg"),
            BuildArguments(inputPath, outputPath, plan),
            Path.GetDirectoryName(outputPath),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg 素材转换失败: {Path.GetFileName(inputPath)}; stderr: {result.StandardError}");
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException($"FFmpeg 素材转换完成但未生成输出文件: {outputPath}");
        }

        await ValidateOutputAsync(outputPath, plan, cancellationToken);
    }

    private async Task ValidateOutputAsync(
        string outputPath,
        MaterialConvertPlan plan,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(outputPath);
        if (fileInfo.Length > MaxOutputBytes)
        {
            throw new InvalidOperationException($"素材转换后文件仍超过 500MB: {outputPath}");
        }

        var probe = await ProbeMediaAsync(outputPath, cancellationToken);
        var shortEdge = Math.Min(probe.Width, probe.Height);
        if (shortEdge < MinShortEdge)
        {
            throw new InvalidOperationException($"素材转换后分辨率低于 720p: {outputPath}");
        }

        var measuredVideoBitrate = probe.VideoBitrateBps
            ?? (probe.FormatBitrateBps is long formatBitrate
                ? (long?)Math.Max(0L, formatBitrate - (probe.AudioBitrateBps ?? 0L))
                : null)
            ?? plan.VideoBitrateBps;

        var minimumAcceptedBitrate = Math.Max(MinVideoBitrateBps, plan.MinAcceptedVideoBitrateBps);
        if (measuredVideoBitrate < minimumAcceptedBitrate)
        {
            throw new InvalidOperationException($"素材转换后视频码率低于上传阈值（4194304 bps）: {outputPath}");
        }
    }

    private static IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, MaterialConvertPlan plan)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", plan.VerboseLogEnabled ? "info" : "error",
            "-y",
            "-i", inputPath
        };

        args.AddRange(["-filter_complex", BuildFilterComplex(plan), "-map", "[vout]"]);
        if (plan.HasAudio)
        {
            args.AddRange(["-map", "[aout]"]);
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(["-c:v", plan.VideoCodec]);
        if (plan.VideoCodec.Equals("libx264", StringComparison.Ordinal))
        {
            args.AddRange(["-preset", plan.VideoPreset]);
            if (plan.UseCbr)
            {
                args.AddRange(["-x264-params", "nal-hrd=cbr:force-cfr=1"]);
            }
        }
        else if (plan.VideoCodec.Equals("h264_nvenc", StringComparison.Ordinal))
        {
            args.AddRange(["-preset", plan.VideoPreset, "-cq", plan.NvencCq.ToString(CultureInfo.InvariantCulture)]);
            args.AddRange(["-rc", plan.UseCbr ? "cbr" : "vbr"]);
        }

        args.AddRange(["-b:v", plan.VideoBitrateBps.ToString(CultureInfo.InvariantCulture)]);
        if (plan.UseCbr)
        {
            var bitrate = plan.VideoBitrateBps.ToString(CultureInfo.InvariantCulture);
            var bufferSize = (plan.VideoBitrateBps * 2L).ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-maxrate", bitrate, "-minrate", bitrate, "-bufsize", bufferSize]);
        }
        else
        {
            var maxBitrate = Math.Max(plan.VideoBitrateBps, plan.MaxVideoBitrateBps);
            args.AddRange([
                "-maxrate", maxBitrate.ToString(CultureInfo.InvariantCulture),
                "-bufsize", (maxBitrate * 2L).ToString(CultureInfo.InvariantCulture)
            ]);
        }

        args.AddRange(["-r", plan.BaseFps.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-pix_fmt", "yuv420p"]);
        if (plan.HasAudio)
        {
            args.AddRange(["-c:a", "aac", "-b:a", plan.AudioBitrateBps.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-movflags", "+faststart", outputPath]);
        return args;
    }

    private static string BuildFilterComplex(MaterialConvertPlan plan)
    {
        var chains = new List<string>();

        if (plan.DynamicSpeedEnabled)
        {
            var segments = BuildDynamicSegments(plan);
            var videoLabels = new List<string>();
            var audioLabels = new List<string>();
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                var videoLabel = $"vseg{index}";
                videoLabels.Add(videoLabel);
                chains.Add($"[0:v]{BuildVideoSegmentFilter(segment.StartSeconds, segment.EndSeconds, segment.VideoSpeedFactor)}[{videoLabel}]");

                if (!plan.HasAudio)
                {
                    continue;
                }

                var audioLabel = $"aseg{index}";
                audioLabels.Add(audioLabel);
                chains.Add($"[0:a]{BuildAudioSegmentFilter(segment.StartSeconds, segment.EndSeconds, segment.AudioSpeedFactor)}[{audioLabel}]");
            }

            if (plan.HasAudio)
            {
                chains.Add(string.Concat(videoLabels.Select(label => $"[{label}]")) +
                           string.Concat(audioLabels.Select(label => $"[{label}]")) +
                           $"concat=n={videoLabels.Count}:v=1:a=1[vbase][abase]");
            }
            else
            {
                chains.Add(string.Concat(videoLabels.Select(label => $"[{label}]")) +
                           $"concat=n={videoLabels.Count}:v=1:a=0[vbase]");
            }
        }
        else
        {
            chains.Add($"[0:v]{BuildStaticVideoFilter(plan)}[vbase]");
            if (plan.HasAudio)
            {
                chains.Add($"[0:a]{BuildStaticAudioFilter(plan)}[abase]");
            }
        }

        chains.Add($"[vbase]{BuildCommonVideoFilter(plan)}[vout]");
        if (plan.HasAudio)
        {
            chains.Add("[abase]anull[aout]");
        }

        return string.Join(";", chains);
    }

    private static string BuildStaticVideoFilter(MaterialConvertPlan plan)
    {
        var filters = new List<string>
        {
            $"trim=start={FormatFilterNumber(plan.TrimHeadSeconds)}:end={FormatFilterNumber(plan.TrimmedEndSeconds)}",
            Math.Abs(plan.StaticSpeedFactor - 1d) > 0.0001d
                ? $"setpts=(PTS-STARTPTS)/{FormatFilterNumber(plan.StaticSpeedFactor)}"
                : "setpts=PTS-STARTPTS"
        };
        return string.Join(",", filters);
    }

    private static string BuildStaticAudioFilter(MaterialConvertPlan plan)
    {
        var filters = new List<string>
        {
            $"atrim=start={FormatFilterNumber(plan.TrimHeadSeconds)}:end={FormatFilterNumber(plan.TrimmedEndSeconds)}",
            "asetpts=PTS-STARTPTS"
        };
        if (Math.Abs(plan.StaticAudioSpeedFactor - 1d) > 0.0001d)
        {
            filters.Add(BuildAudioTempoFilter(plan.StaticAudioSpeedFactor));
        }

        return string.Join(",", filters);
    }

    private static string BuildVideoSegmentFilter(double startSeconds, double endSeconds, double speedFactor)
    {
        return $"trim=start={FormatFilterNumber(startSeconds)}:end={FormatFilterNumber(endSeconds)}," +
               $"setpts=(PTS-STARTPTS)/{FormatFilterNumber(speedFactor)}";
    }

    private static string BuildAudioSegmentFilter(double startSeconds, double endSeconds, double speedFactor)
    {
        return $"atrim=start={FormatFilterNumber(startSeconds)}:end={FormatFilterNumber(endSeconds)}," +
               $"asetpts=PTS-STARTPTS,{BuildAudioTempoFilter(speedFactor)}";
    }

    private static string BuildCommonVideoFilter(MaterialConvertPlan plan)
    {
        var filters = new List<string>();

        if (plan.FrameSamplingEnabled)
        {
            filters.Add(BuildFrameSamplingFilter(plan.FrameSamplingMode, plan.FrameSamplingInterval));
            filters.Add($"setpts=N/({FormatFilterNumber(plan.BaseFps)}*TB)");
        }

        if (plan.CropWidthPercent > 0 || plan.CropHeightPercent > 0)
        {
            var widthRatio = Math.Max(0.1d, 1d - plan.CropWidthPercent / 100d);
            var heightRatio = Math.Max(0.1d, 1d - plan.CropHeightPercent / 100d);
            filters.Add(
                $"crop='floor(iw*{FormatFilterNumber(widthRatio)}/2)*2':'floor(ih*{FormatFilterNumber(heightRatio)}/2)*2':'(iw-ow)/2':'(ih-oh)/2'");
        }

        filters.Add($"scale={plan.ForegroundScaledWidth}:{plan.ForegroundScaledHeight}:force_original_aspect_ratio=increase");
        filters.Add($"crop={plan.ForegroundWidth}:{plan.ForegroundHeight}");
        filters.Add($"pad={plan.OutputWidth}:{plan.OutputHeight}:(ow-iw)/2:(oh-ih)/2:color=black");

        if (plan.WatermarkEnabled && !string.IsNullOrWhiteSpace(plan.WatermarkText))
        {
            filters.Add(BuildWatermarkFilter(plan));
        }

        return string.Join(",", filters);
    }

    private static string BuildAudioTempoFilter(double speedFactor)
    {
        var remaining = speedFactor;
        var filters = new List<string>();

        while (remaining < 0.5d)
        {
            filters.Add("atempo=0.5");
            remaining /= 0.5d;
        }

        while (remaining > 2d)
        {
            filters.Add("atempo=2.0");
            remaining /= 2d;
        }

        filters.Add($"atempo={remaining.ToString("0.######", CultureInfo.InvariantCulture)}");
        return string.Join(",", filters);
    }

    private static string BuildFrameSamplingFilter(string mode, int interval)
    {
        interval = Math.Max(2, interval);
        if (string.Equals(mode, "random", StringComparison.OrdinalIgnoreCase))
        {
            return $"select='not(eq(mod(n\\,{interval})\\,mod(floor(n/{interval})*7+3\\,{interval})))'";
        }

        return BuildFrameDropFilter(interval, 1);
    }

    private static string BuildFrameDropFilter(int everyNFrames, int dropCount)
    {
        var dropIndexes = Enumerable.Range(0, dropCount)
            .Select(offset => everyNFrames - dropCount + offset)
            .Select(index => $"eq(mod(n\\,{everyNFrames})\\,{index})");
        return $"select='not({string.Join("+", dropIndexes)})'";
    }

    private static MaterialConvertPlan BuildPlan(MaterialConvertSettings settings, MediaProbeInfo inputProbe)
    {
        var sourceShortEdge = Math.Min(inputProbe.Width, inputProbe.Height);
        var profiles = UploadTranscodeBitrateProfiles.Parse(settings.UploadBitrateProfilesJson);
        var selectedProfile = UploadTranscodeBitrateProfiles.Select(profiles, Math.Max(1, sourceShortEdge));
        var videoBitrate = selectedProfile is not null
            ? ToBitrateBps(selectedProfile.BitrateMbps)
            : Math.Max(MinVideoBitrateBps, settings.UploadTargetVideoBitrateBps ?? settings.VideoBitrateBps ?? MinVideoBitrateBps);
        var maxVideoBitrate = Math.Max(videoBitrate, settings.UploadMaxVideoBitrateBps ?? videoBitrate);
        var minAcceptedBitrate = Math.Max(MinVideoBitrateBps, settings.UploadMinVideoBitrateBps ?? MinVideoBitrateBps);
        if (settings.SkipBitrateDownscaleForHighBitrate &&
            inputProbe.VideoBitrateBps is long inputVideoBitrate &&
            inputVideoBitrate > videoBitrate)
        {
            videoBitrate = Math.Min(inputVideoBitrate, maxVideoBitrate);
        }

        var audioBitrate = selectedProfile is not null
            ? Math.Max(DefaultAudioBitrateBps, selectedProfile.AudioKbps * 1000)
            : settings.UploadAudioBitrateBps ?? settings.VideoAudioBitrateBps ?? DefaultAudioBitrateBps;
        var baseFps = settings.VideoFps ?? 30;
        var frameSamplingEnabled = settings.FrameSamplingEnabled &&
                                   settings.FrameSamplingInterval >= 2;
        var keepRatio = frameSamplingEnabled
            ? (settings.FrameSamplingInterval - 1d) / settings.FrameSamplingInterval
            : 1d;
        var trimHeadSeconds = Math.Clamp(settings.TrimHeadSeconds, 0d, Math.Max(0d, inputProbe.DurationSeconds));
        var trimTailSeconds = Math.Clamp(settings.TrimTailSeconds, 0d, Math.Max(0d, inputProbe.DurationSeconds - trimHeadSeconds));
        var trimmedEndSeconds = Math.Max(trimHeadSeconds + 0.05d, inputProbe.DurationSeconds - trimTailSeconds);
        var usableDurationSeconds = Math.Max(0.05d, trimmedEndSeconds - trimHeadSeconds);
        var staticSpeedFactor = Math.Max(0.5d, Math.Min(1.5d, 1d + settings.SpeedPercent / 100d));
        var staticAudioSpeedFactor = staticSpeedFactor / keepRatio;
        var dynamicSpeedEnabled = settings.DynamicSpeedEnabled && usableDurationSeconds > 0.1d;
        var headDurationSeconds = dynamicSpeedEnabled
            ? Math.Min(Math.Max(0d, settings.DynamicSpeedHeadSeconds), usableDurationSeconds)
            : 0d;
        var tailDurationSeconds = dynamicSpeedEnabled
            ? Math.Min(Math.Max(0d, settings.DynamicSpeedTailSeconds), Math.Max(0d, usableDurationSeconds - headDurationSeconds))
            : 0d;
        var middleDurationSeconds = dynamicSpeedEnabled
            ? Math.Max(0d, usableDurationSeconds - headDurationSeconds - tailDurationSeconds)
            : usableDurationSeconds;
        var headSpeedFactor = Math.Max(0.5d, Math.Min(1.5d, 1d + settings.DynamicSpeedHeadPercent / 100d));
        var middleSpeedFactor = Math.Max(0.5d, Math.Min(1.5d, 1d + settings.DynamicSpeedMiddlePercent / 100d));
        var tailSpeedFactor = Math.Max(0.5d, Math.Min(1.5d, 1d + settings.DynamicSpeedTailPercent / 100d));
        var outputWidth = EnsureEven(Math.Max(540, settings.OutputWidth));
        var outputHeight = EnsureEven(Math.Max(960, settings.OutputHeight));
        var foregroundWidth = EnsureEven((int)Math.Round(outputWidth * Math.Clamp(settings.PipWidthPercent, 10d, 100d) / 100d, MidpointRounding.AwayFromZero));
        var foregroundHeight = EnsureEven((int)Math.Round(outputHeight * Math.Clamp(settings.PipHeightPercent, 10d, 100d) / 100d, MidpointRounding.AwayFromZero));
        foregroundWidth = Math.Clamp(foregroundWidth, 2, outputWidth);
        foregroundHeight = Math.Clamp(foregroundHeight, 2, outputHeight);
        var foregroundScaledWidth = EnsureEven((int)Math.Round(foregroundWidth * (1d + Math.Clamp(settings.ForegroundZoomPercent, 0d, 20d) / 100d), MidpointRounding.AwayFromZero));
        var foregroundScaledHeight = EnsureEven((int)Math.Round(foregroundHeight * (1d + Math.Clamp(settings.ForegroundZoomPercent, 0d, 20d) / 100d), MidpointRounding.AwayFromZero));
        var videoPreset = string.IsNullOrWhiteSpace(settings.VideoPreset) ? selectedProfile.Preset : settings.VideoPreset!;

        return new MaterialConvertPlan(
            ResolveVideoCodec(settings.VideoEncoder, settings.VideoUseHardwareEncoder),
            string.Equals(settings.VideoBitrateMode, "Cbr", StringComparison.OrdinalIgnoreCase),
            videoBitrate,
            maxVideoBitrate,
            minAcceptedBitrate,
            audioBitrate,
            baseFps,
            trimHeadSeconds,
            trimTailSeconds,
            trimmedEndSeconds,
            usableDurationSeconds,
            staticSpeedFactor,
            staticAudioSpeedFactor,
            dynamicSpeedEnabled,
            headDurationSeconds,
            middleDurationSeconds,
            tailDurationSeconds,
            headSpeedFactor,
            middleSpeedFactor,
            tailSpeedFactor,
            settings.CropWidthPercent,
            settings.CropHeightPercent,
            frameSamplingEnabled,
            string.IsNullOrWhiteSpace(settings.FrameSamplingMode) ? "fixed_interval" : settings.FrameSamplingMode!,
            Math.Max(2, settings.FrameSamplingInterval),
            outputWidth,
            outputHeight,
            foregroundWidth,
            foregroundHeight,
            foregroundScaledWidth,
            foregroundScaledHeight,
            Math.Clamp(settings.ForegroundZoomPercent, 0d, 20d),
            settings.WatermarkEnabled,
            settings.WatermarkText,
            Math.Max(16, settings.WatermarkFontSize),
            string.IsNullOrWhiteSpace(settings.WatermarkPosition) ? "top_right" : settings.WatermarkPosition!,
            Math.Max(0, settings.WatermarkMarginX),
            Math.Max(0, settings.WatermarkMarginY),
            inputProbe.HasAudio,
            videoPreset,
            settings.NvencCq ?? 21,
            settings.VerboseTranscodeLogEnabled);
    }

    private async Task<MediaProbeInfo> ProbeMediaAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            ResolveBinary("ffprobe"),
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path],
            Path.GetDirectoryName(path),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe 分析失败: {path}; stderr: {result.StandardError}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
        var streams = root.TryGetProperty("streams", out var streamsElement) ? streamsElement : default;

        JsonElement? videoStream = null;
        JsonElement? audioStream = null;
        if (streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (!videoStream.HasValue &&
                    stream.TryGetProperty("codec_type", out var codecType) &&
                    string.Equals(codecType.GetString(), "video", StringComparison.Ordinal))
                {
                    videoStream = stream;
                    continue;
                }

                if (!audioStream.HasValue &&
                    stream.TryGetProperty("codec_type", out codecType) &&
                    string.Equals(codecType.GetString(), "audio", StringComparison.Ordinal))
                {
                    audioStream = stream;
                }
            }
        }

        return new MediaProbeInfo(
            ParseNullableDouble(format, "duration") ?? 0d,
            ParseNullableInt(videoStream, "width") ?? 0,
            ParseNullableInt(videoStream, "height") ?? 0,
            ParseNullableLong(format, "bit_rate"),
            ParseNullableLong(videoStream, "bit_rate"),
            ParseNullableLong(audioStream, "bit_rate"),
            audioStream.HasValue);
    }

    private static MaterialConvertSettings LoadSettings(string? configFile)
    {
        if (string.IsNullOrWhiteSpace(configFile) || !File.Exists(configFile))
        {
            return MaterialConvertSettings.Default;
        }

        var map = KeyValueConfigReader.Read(configFile);
        return new MaterialConvertSettings(
            Enabled: ParseNullableBool(map, "MaterialConvertEnabled") ?? true,
            TrimHeadSeconds: ParseNullableDouble(map, "MaterialTrimHeadSeconds") ?? 0.5d,
            TrimTailSeconds: ParseNullableDouble(map, "MaterialTrimTailSeconds") ?? 0.5d,
            SpeedPercent: ParseNullableDouble(map, "MaterialSpeedPercent") ?? 0d,
            DynamicSpeedEnabled: ParseNullableBool(map, "MaterialDynamicSpeedEnabled") ?? false,
            DynamicSpeedPresetName: GetNullable(map, "MaterialDynamicSpeedPresetName"),
            DynamicSpeedHeadSeconds: ParseNullableDouble(map, "MaterialDynamicSpeedHeadSeconds") ?? 2.5d,
            DynamicSpeedHeadPercent: ParseNullableDouble(map, "MaterialDynamicSpeedHeadPercent") ?? 8d,
            DynamicSpeedMiddlePercent: ParseNullableDouble(map, "MaterialDynamicSpeedMiddlePercent") ?? 6d,
            DynamicSpeedTailSeconds: ParseNullableDouble(map, "MaterialDynamicSpeedTailSeconds") ?? 2.5d,
            DynamicSpeedTailPercent: ParseNullableDouble(map, "MaterialDynamicSpeedTailPercent") ?? 8d,
            FrameSamplingEnabled: ParseNullableBool(map, "MaterialFrameSamplingEnabled")
                ?? ((ParseNullableInt(map, "MaterialDropCount") ?? 1) > 0),
            FrameSamplingMode: GetNullable(map, "MaterialFrameSamplingMode"),
            FrameSamplingInterval: ParseNullableInt(map, "MaterialFrameSamplingInterval")
                ?? ParseNullableInt(map, "MaterialDropEveryNFrames")
                ?? 20,
            CropWidthPercent: ParseNullableDouble(map, "MaterialCropWidthPercent") ?? 0d,
            CropHeightPercent: ParseNullableDouble(map, "MaterialCropHeightPercent") ?? 0d,
            ForegroundZoomPercent: ParseNullableDouble(map, "MaterialForegroundZoomPercent") ?? 0d,
            WatermarkEnabled: ParseNullableBool(map, "MaterialWatermarkEnabled") ?? false,
            WatermarkText: GetNullable(map, "MaterialWatermarkText"),
            WatermarkFontSize: ParseNullableInt(map, "MaterialWatermarkFontSize") ?? 35,
            WatermarkPosition: GetNullable(map, "MaterialWatermarkPosition"),
            WatermarkMarginX: ParseNullableInt(map, "MaterialWatermarkMarginX") ?? 30,
            WatermarkMarginY: ParseNullableInt(map, "MaterialWatermarkMarginY") ?? 30,
            OutputWidth: ParseNullableInt(map, "MaterialOutputWidth") ?? 1080,
            OutputHeight: ParseNullableInt(map, "MaterialOutputHeight") ?? 1920,
            PipWidthPercent: ParseNullableDouble(map, "MaterialPipWidthPercent") ?? 100d,
            PipHeightPercent: ParseNullableDouble(map, "MaterialPipHeightPercent") ?? 100d,
            DropEveryNFrames: ParseNullableInt(map, "MaterialDropEveryNFrames") ?? 20,
            DropCount: ParseNullableInt(map, "MaterialDropCount") ?? 1,
            VideoBitrateBps: ParseNullableLong(map, "VideoBitrateBps"),
            VideoBitrateMode: GetNullable(map, "VideoBitrateMode"),
            VideoAudioBitrateBps: ParseNullableInt(map, "VideoAudioBitrateBps"),
            VideoFps: ParseNullableInt(map, "VideoFps"),
            VideoConcurrentCount: ParseNullableInt(map, "VideoConcurrentCount"),
            VideoUseHardwareEncoder: ParseNullableBool(map, "VideoUseHardwareEncoder") ?? true,
            VideoEncoder: GetNullable(map, "VideoEncoder"),
            VideoPreset: GetNullable(map, "VideoPreset"),
            NvencCq: ParseNullableInt(map, "NvencCq"),
            VerboseTranscodeLogEnabled: ParseNullableBool(map, "VerboseTranscodeLogEnabled") ?? false,
            SkipBitrateDownscaleForHighBitrate: ParseNullableBool(map, "SkipBitrateDownscaleForHighBitrate") ?? false,
            UploadTargetVideoBitrateBps: ParseNullableScaledLong(map, "UploadTargetVideoBitrateMbps"),
            UploadMaxVideoBitrateBps: ParseNullableScaledLong(map, "UploadMaxVideoBitrateMbps"),
            UploadMinVideoBitrateBps: ParseNullableScaledLong(map, "UploadMinVideoBitrateMbps"),
            UploadAudioBitrateBps: ParseNullableScaledInt(map, "UploadAudioBitrateKbps", 1000),
            UploadBitrateProfilesJson: GetNullable(map, "UploadBitrateProfilesJson"));
    }

    private static string? GetNullable(IReadOnlyDictionary<string, string> map, string key)
    {
        return map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static int? ParseNullableInt(IReadOnlyDictionary<string, string> map, string key)
    {
        var value = GetNullable(map, key);
        return value is null ? null : int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static long? ParseNullableLong(IReadOnlyDictionary<string, string> map, string key)
    {
        var value = GetNullable(map, key);
        return value is null ? null : long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static long? ParseNullableScaledLong(IReadOnlyDictionary<string, string> map, string key)
    {
        var value = GetNullable(map, key);
        return value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? (long?)Math.Round(parsed * 1_000_000d, MidpointRounding.AwayFromZero)
            : null;
    }

    private static double? ParseNullableDouble(IReadOnlyDictionary<string, string> map, string key)
    {
        var value = GetNullable(map, key);
        return value is null ? null : double.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool? ParseNullableBool(IReadOnlyDictionary<string, string> map, string key)
    {
        var value = GetNullable(map, key);
        return value is null ? null : bool.Parse(value);
    }

    private static int? ParseNullableScaledInt(IReadOnlyDictionary<string, string> map, string key, int multiplier)
    {
        var value = GetNullable(map, key);
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed * multiplier
            : null;
    }

    private static List<MaterialSpeedSegment> BuildDynamicSegments(MaterialConvertPlan plan)
    {
        var segments = new List<MaterialSpeedSegment>();
        var currentStart = plan.TrimHeadSeconds;

        if (plan.HeadDurationSeconds > 0.0001d)
        {
            segments.Add(new MaterialSpeedSegment(
                currentStart,
                currentStart + plan.HeadDurationSeconds,
                plan.HeadSpeedFactor,
                plan.HeadSpeedFactor / plan.FrameKeepRatio));
            currentStart += plan.HeadDurationSeconds;
        }

        if (plan.MiddleDurationSeconds > 0.0001d)
        {
            segments.Add(new MaterialSpeedSegment(
                currentStart,
                currentStart + plan.MiddleDurationSeconds,
                plan.MiddleSpeedFactor,
                plan.MiddleSpeedFactor / plan.FrameKeepRatio));
            currentStart += plan.MiddleDurationSeconds;
        }

        if (plan.TailDurationSeconds > 0.0001d)
        {
            segments.Add(new MaterialSpeedSegment(
                currentStart,
                Math.Min(plan.TrimmedEndSeconds, currentStart + plan.TailDurationSeconds),
                plan.TailSpeedFactor,
                plan.TailSpeedFactor / plan.FrameKeepRatio));
        }

        if (segments.Count == 0)
        {
            segments.Add(new MaterialSpeedSegment(
                plan.TrimHeadSeconds,
                plan.TrimmedEndSeconds,
                plan.StaticSpeedFactor,
                plan.StaticAudioSpeedFactor));
        }

        return segments;
    }

    private static string BuildWatermarkFilter(MaterialConvertPlan plan)
    {
        var text = EscapeDrawTextText(plan.WatermarkText ?? string.Empty);
        var marginX = Math.Max(0, plan.WatermarkMarginX);
        var marginY = Math.Max(0, plan.WatermarkMarginY);
        var xExpression = string.Equals(plan.WatermarkPosition, "top_left", StringComparison.OrdinalIgnoreCase)
            ? marginX.ToString(CultureInfo.InvariantCulture)
            : $"w-tw-{marginX.ToString(CultureInfo.InvariantCulture)}";

        return $"drawtext=text='{text}':fontcolor=white@0.92:fontsize={plan.WatermarkFontSize.ToString(CultureInfo.InvariantCulture)}:" +
               $"x={xExpression}:y={marginY.ToString(CultureInfo.InvariantCulture)}:" +
               "box=1:boxcolor=black@0.18:boxborderw=10";
    }

    private static string EscapeDrawTextText(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal);
    }

    private static string FormatFilterNumber(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static int EnsureEven(int value)
    {
        return value % 2 == 0 ? value : value + 1;
    }

    private static long? ParseNullableLong(JsonElement? element, string propertyName)
    {
        if (element is null ||
            !element.Value.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : long.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    }

    private static int? ParseNullableInt(JsonElement? element, string propertyName)
    {
        if (element is null ||
            !element.Value.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : int.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    }

    private static double? ParseNullableDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : double.Parse(value.GetString()!, CultureInfo.InvariantCulture);
    }

    private static string ResolveBinary(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var extensions = OperatingSystem.IsWindows()
                ? new[] { ".exe", ".cmd", ".bat", string.Empty }
                : new[] { string.Empty };

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var ext in extensions)
                {
                    var fullPath = Path.Combine(dir, name + ext);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
        }

        throw new InvalidOperationException($"未找到 {name}。请安装 ffmpeg，或确保 {name} 在 PATH 中。");
    }

    private static string ResolveVideoCodec(string? configuredEncoder, bool useHardwareEncoder)
    {
        var normalized = (configuredEncoder ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == "libx264")
        {
            return "libx264";
        }

        if (normalized == "h264_nvenc")
        {
            return OperatingSystem.IsWindows() ? "h264_nvenc" : "libx264";
        }

        if (normalized == "h264_videotoolbox")
        {
            return OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "libx264";
        }

        if (!useHardwareEncoder)
        {
            return "libx264";
        }

        return OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "libx264";
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static long ToBitrateBps(double bitrateMbps)
    {
        return (long)Math.Round(bitrateMbps * 1_000_000d, MidpointRounding.AwayFromZero);
    }

    private sealed record MaterialConvertSettings(
        bool Enabled,
        double TrimHeadSeconds,
        double TrimTailSeconds,
        double SpeedPercent,
        bool DynamicSpeedEnabled,
        string? DynamicSpeedPresetName,
        double DynamicSpeedHeadSeconds,
        double DynamicSpeedHeadPercent,
        double DynamicSpeedMiddlePercent,
        double DynamicSpeedTailSeconds,
        double DynamicSpeedTailPercent,
        bool FrameSamplingEnabled,
        string? FrameSamplingMode,
        int FrameSamplingInterval,
        double CropWidthPercent,
        double CropHeightPercent,
        double ForegroundZoomPercent,
        bool WatermarkEnabled,
        string? WatermarkText,
        int WatermarkFontSize,
        string? WatermarkPosition,
        int WatermarkMarginX,
        int WatermarkMarginY,
        int OutputWidth,
        int OutputHeight,
        double PipWidthPercent,
        double PipHeightPercent,
        int DropEveryNFrames,
        int DropCount,
        long? VideoBitrateBps,
        string? VideoBitrateMode,
        int? VideoAudioBitrateBps,
        int? VideoFps,
        int? VideoConcurrentCount,
        bool VideoUseHardwareEncoder,
        string? VideoEncoder,
        string? VideoPreset,
        int? NvencCq,
        bool VerboseTranscodeLogEnabled,
        bool SkipBitrateDownscaleForHighBitrate,
        long? UploadTargetVideoBitrateBps,
        long? UploadMaxVideoBitrateBps,
        long? UploadMinVideoBitrateBps,
        int? UploadAudioBitrateBps,
        string? UploadBitrateProfilesJson)
    {
        public static MaterialConvertSettings Default { get; } = new(
            Enabled: true,
            TrimHeadSeconds: 0.5d,
            TrimTailSeconds: 0.5d,
            SpeedPercent: 0d,
            DynamicSpeedEnabled: false,
            DynamicSpeedPresetName: "light_rhythm",
            DynamicSpeedHeadSeconds: 2.5d,
            DynamicSpeedHeadPercent: 8d,
            DynamicSpeedMiddlePercent: 6d,
            DynamicSpeedTailSeconds: 2.5d,
            DynamicSpeedTailPercent: 8d,
            FrameSamplingEnabled: true,
            FrameSamplingMode: "fixed_interval",
            FrameSamplingInterval: 20,
            CropWidthPercent: 0d,
            CropHeightPercent: 0d,
            ForegroundZoomPercent: 0d,
            WatermarkEnabled: false,
            WatermarkText: string.Empty,
            WatermarkFontSize: 35,
            WatermarkPosition: "top_right",
            WatermarkMarginX: 30,
            WatermarkMarginY: 30,
            OutputWidth: 1080,
            OutputHeight: 1920,
            PipWidthPercent: 100d,
            PipHeightPercent: 100d,
            DropEveryNFrames: 20,
            DropCount: 1,
            VideoBitrateBps: null,
            VideoBitrateMode: null,
            VideoAudioBitrateBps: null,
            VideoFps: 30,
            VideoConcurrentCount: 1,
            VideoUseHardwareEncoder: true,
            VideoEncoder: null,
            VideoPreset: null,
            NvencCq: 21,
            VerboseTranscodeLogEnabled: false,
            SkipBitrateDownscaleForHighBitrate: false,
            UploadTargetVideoBitrateBps: null,
            UploadMaxVideoBitrateBps: null,
            UploadMinVideoBitrateBps: null,
            UploadAudioBitrateBps: null,
            UploadBitrateProfilesJson: null);
    }

    private sealed record MaterialConvertPlan(
        string VideoCodec,
        bool UseCbr,
        long VideoBitrateBps,
        long MaxVideoBitrateBps,
        long MinAcceptedVideoBitrateBps,
        int AudioBitrateBps,
        double BaseFps,
        double TrimHeadSeconds,
        double TrimTailSeconds,
        double TrimmedEndSeconds,
        double UsableDurationSeconds,
        double StaticSpeedFactor,
        double StaticAudioSpeedFactor,
        bool DynamicSpeedEnabled,
        double HeadDurationSeconds,
        double MiddleDurationSeconds,
        double TailDurationSeconds,
        double HeadSpeedFactor,
        double MiddleSpeedFactor,
        double TailSpeedFactor,
        double CropWidthPercent,
        double CropHeightPercent,
        bool FrameSamplingEnabled,
        string FrameSamplingMode,
        int FrameSamplingInterval,
        int OutputWidth,
        int OutputHeight,
        int ForegroundWidth,
        int ForegroundHeight,
        int ForegroundScaledWidth,
        int ForegroundScaledHeight,
        double ForegroundZoomPercent,
        bool WatermarkEnabled,
        string? WatermarkText,
        int WatermarkFontSize,
        string WatermarkPosition,
        int WatermarkMarginX,
        int WatermarkMarginY,
        bool HasAudio,
        string VideoPreset,
        int NvencCq,
        bool VerboseLogEnabled)
    {
        public double FrameKeepRatio => FrameSamplingEnabled
            ? (FrameSamplingInterval - 1d) / FrameSamplingInterval
            : 1d;
    }

    private sealed record MaterialSpeedSegment(
        double StartSeconds,
        double EndSeconds,
        double VideoSpeedFactor,
        double AudioSpeedFactor);

    private sealed record MediaProbeInfo(
        double DurationSeconds,
        int Width,
        int Height,
        long? FormatBitrateBps,
        long? VideoBitrateBps,
        long? AudioBitrateBps,
        bool HasAudio);

    private sealed record MaterialFileOutcome(
        int Index,
        string Kind,
        string? OutputPath,
        VideoMaterialConvertFailure? Failure)
    {
        public static MaterialFileOutcome Completed(int index, string outputPath) =>
            new(index, "file-completed", outputPath, null);

        public static MaterialFileOutcome Skipped(int index, string outputPath) =>
            new(index, "file-skipped", outputPath, null);

        public static MaterialFileOutcome Failed(int index, string outputPath, VideoMaterialConvertFailure failure) =>
            new(index, "file-failed", outputPath, failure);
    }
}
