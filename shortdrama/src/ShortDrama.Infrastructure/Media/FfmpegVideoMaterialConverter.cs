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

        if (plan.TrimHeadSeconds > 0)
        {
            args.AddRange(["-ss", plan.TrimHeadSeconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        if (plan.OutputDurationSeconds is > 0d)
        {
            args.AddRange(["-t", plan.OutputDurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-map", "0:v:0", "-map", "0:a?"]);

        var videoFilters = new List<string>();
        if (Math.Abs(plan.SpeedFactor - 1d) > 0.0001d)
        {
            videoFilters.Add($"setpts={(1d / plan.SpeedFactor).ToString("0.######", CultureInfo.InvariantCulture)}*PTS");
        }

        if (plan.DropEveryNFrames > 0 && plan.DropCount > 0 && plan.DropCount < plan.DropEveryNFrames)
        {
            videoFilters.Add(BuildFrameDropFilter(plan.DropEveryNFrames, plan.DropCount));
            videoFilters.Add($"setpts=N/({plan.BaseFps.ToString("0.###", CultureInfo.InvariantCulture)}*TB)");
        }

        if (plan.CropWidthPercent > 0 || plan.CropHeightPercent > 0)
        {
            var widthRatio = Math.Max(0.1d, 1d - plan.CropWidthPercent / 100d);
            var heightRatio = Math.Max(0.1d, 1d - plan.CropHeightPercent / 100d);
            videoFilters.Add(
                $"crop='floor(iw*{widthRatio.ToString("0.######", CultureInfo.InvariantCulture)}/2)*2':'floor(ih*{heightRatio.ToString("0.######", CultureInfo.InvariantCulture)}/2)*2':'(iw-ow)/2':'(ih-oh)/2'");
        }

        if (videoFilters.Count > 0)
        {
            args.AddRange(["-vf", string.Join(",", videoFilters)]);
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
        args.AddRange(["-c:a", "aac", "-b:a", plan.AudioBitrateBps.ToString(CultureInfo.InvariantCulture)]);

        if (plan.HasAudio && Math.Abs(plan.AudioSpeedFactor - 1d) > 0.0001d)
        {
            args.AddRange(["-af", BuildAudioTempoFilter(plan.AudioSpeedFactor)]);
        }

        args.AddRange(["-movflags", "+faststart", outputPath]);
        return args;
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
        var keepRatio = settings.DropEveryNFrames > 0 && settings.DropCount > 0 && settings.DropCount < settings.DropEveryNFrames
            ? (settings.DropEveryNFrames - settings.DropCount) / (double)settings.DropEveryNFrames
            : 1d;
        var speedFactor = Math.Max(0.5d, Math.Min(1.5d, 1d + settings.SpeedPercent / 100d));
        var audioSpeedFactor = speedFactor / keepRatio;
        var totalTrim = settings.TrimHeadSeconds + settings.TrimTailSeconds;
        var outputDuration = inputProbe.DurationSeconds > totalTrim
            ? inputProbe.DurationSeconds - totalTrim
            : inputProbe.DurationSeconds;
        var videoPreset = string.IsNullOrWhiteSpace(settings.VideoPreset) ? selectedProfile.Preset : settings.VideoPreset!;

        return new MaterialConvertPlan(
            ResolveVideoCodec(settings.VideoEncoder, settings.VideoUseHardwareEncoder),
            string.Equals(settings.VideoBitrateMode, "Cbr", StringComparison.OrdinalIgnoreCase),
            videoBitrate,
            maxVideoBitrate,
            minAcceptedBitrate,
            audioBitrate,
            baseFps,
            speedFactor,
            audioSpeedFactor,
            settings.TrimHeadSeconds,
            outputDuration > 0 ? outputDuration : null,
            settings.CropWidthPercent,
            settings.CropHeightPercent,
            inputProbe.HasAudio,
            settings.DropEveryNFrames,
            settings.DropCount,
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
            Enabled: ParseNullableBool(map, "MaterialConvertEnabled") ?? false,
            TrimHeadSeconds: ParseNullableDouble(map, "MaterialTrimHeadSeconds") ?? 4d,
            TrimTailSeconds: ParseNullableDouble(map, "MaterialTrimTailSeconds") ?? 2d,
            SpeedPercent: ParseNullableDouble(map, "MaterialSpeedPercent") ?? 10d,
            CropWidthPercent: ParseNullableDouble(map, "MaterialCropWidthPercent") ?? 2d,
            CropHeightPercent: ParseNullableDouble(map, "MaterialCropHeightPercent") ?? 2d,
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
        double CropWidthPercent,
        double CropHeightPercent,
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
            Enabled: false,
            TrimHeadSeconds: 4d,
            TrimTailSeconds: 2d,
            SpeedPercent: 10d,
            CropWidthPercent: 2d,
            CropHeightPercent: 2d,
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
        double SpeedFactor,
        double AudioSpeedFactor,
        double TrimHeadSeconds,
        double? OutputDurationSeconds,
        double CropWidthPercent,
        double CropHeightPercent,
        bool HasAudio,
        int DropEveryNFrames,
        int DropCount,
        string VideoPreset,
        int NvencCq,
        bool VerboseLogEnabled);

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
