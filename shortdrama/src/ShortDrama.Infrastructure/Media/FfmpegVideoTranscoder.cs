using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Media;

public sealed class FfmpegVideoTranscoder : IVideoTranscoder
{
    private const int MinShortEdge = 720;
    private const long MinVideoBitrateBps = 4_194_304;
    private const long RetryVideoBitrateBps = 5_000_000;
    private const int DefaultAudioBitrateBps = 128_000;
    private const double MinDurationSeconds = 31d;
    private const long MaxOutputBytes = 500L * 1024L * 1024L;
    private static readonly Regex EpisodeIndexRegex = new(
        @"(?:第\s*(\d+)\s*集|episode\s*0*(\d+)|ep\s*0*(\d+)|^0*(\d+)$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    private readonly IProjectInfoParser _projectInfoParser;
    private readonly IExternalProcessRunner _processRunner;
    private readonly ILogger<FfmpegVideoTranscoder> _logger;

    public FfmpegVideoTranscoder(
        IProjectInfoParser projectInfoParser,
        IExternalProcessRunner processRunner,
        ILogger<FfmpegVideoTranscoder> logger)
    {
        _projectInfoParser = projectInfoParser;
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<VideoTranscodeResult> TranscodeAsync(
        VideoTranscodeRequest request,
        IProgress<VideoTranscodeProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(request.InputDir))
        {
            throw new DirectoryNotFoundException($"输入视频目录不存在: {request.InputDir}");
        }

        Directory.CreateDirectory(request.OutputDir);
        var projectTitle = await ResolveProjectTitleAsync(request.ProjectDir, cancellationToken);

        var files = Directory.EnumerateFiles(request.InputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => GetEpisodeSortKey(path))
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var settings = LoadSettings(request.ConfigFile);
        var concurrency = Math.Clamp(settings?.VideoConcurrentCount ?? 1, 1, 4);
        var batchStopwatch = Stopwatch.StartNew();
        var workItems = files
            .Select((inputPath, index) => new VideoWorkItem(index + 1, inputPath, BuildOutputPath(request.OutputDir, projectTitle, index + 1)))
            .ToList();
        var skippedOutcomes = new List<VideoFileOutcome>();
        var pendingWorkItems = new List<VideoWorkItem>();

        foreach (var workItem in workItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputPath = workItem.InputPath;
            var outputPath = workItem.OutputPath;

            if (File.Exists(outputPath) && !request.Overwrite)
            {
                skippedOutcomes.Add(VideoFileOutcome.Skipped(workItem.Index, outputPath));
                _logger.LogInformation(
                    "Skipped transcoding {Index}/{Total}: {Input} -> {Output}; elapsed={Elapsed}",
                    workItem.Index,
                    files.Count,
                    inputPath,
                    outputPath,
                    FormatElapsed(TimeSpan.Zero));
                progress?.Report(new VideoTranscodeProgress(
                    workItem.Index,
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
            pendingWorkItems.Add(workItem);
        }

        var completedOutcomes = new List<VideoFileOutcome>();
        if (pendingWorkItems.Count > 0)
        {
            using var gate = new SemaphoreSlim(concurrency);
            var tasks = pendingWorkItems.Select(item => RunTranscodeWorkItemAsync(
                item,
                files.Count,
                request,
                settings,
                progress,
                gate,
                cancellationToken));
            completedOutcomes.AddRange(await Task.WhenAll(tasks));
        }

        batchStopwatch.Stop();
        var allOutcomes = skippedOutcomes
            .Concat(completedOutcomes)
            .OrderBy(item => item.Index)
            .ToList();
        var outputs = allOutcomes
            .Where(item => item.Kind is "file-completed" or "file-skipped")
            .Where(item => item.OutputPath is not null)
            .Select(item => item.OutputPath!)
            .ToList();
        var failures = allOutcomes
            .Where(item => item.Failure is not null)
            .Select(item => item.Failure!)
            .ToList();
        var transcoded = allOutcomes.Count(item => item.Kind == "file-completed");
        var skipped = allOutcomes.Count(item => item.Kind == "file-skipped");
        _logger.LogInformation(
            "Transcode batch finished. total={Total}, transcoded={Transcoded}, skipped={Skipped}, elapsed={Elapsed}, concurrency={Concurrency}",
            files.Count,
            transcoded,
            skipped,
            FormatElapsed(batchStopwatch.Elapsed),
            concurrency);

        return new VideoTranscodeResult(
            files.Count,
            transcoded,
            skipped,
            failures.Count,
            outputs,
            failures);
    }

    private async Task<VideoFileOutcome> RunTranscodeWorkItemAsync(
        VideoWorkItem workItem,
        int totalFiles,
        VideoTranscodeRequest request,
        VideoTranscodeSettings? settings,
        IProgress<VideoTranscodeProgress>? progress,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                EnsureInputReady(workItem.InputPath);

                progress?.Report(new VideoTranscodeProgress(
                    workItem.Index,
                    totalFiles,
                    workItem.InputPath,
                    workItem.OutputPath,
                    Kind: "file-started"));

                var fileStopwatch = Stopwatch.StartNew();
                var inputProbe = await ProbeMediaAsync(workItem.InputPath, cancellationToken);
                var plan = BuildEncodingPlan(settings, inputProbe);
                await TranscodeWithRetryAsync(workItem.InputPath, workItem.OutputPath, request, inputProbe, plan, cancellationToken);
                fileStopwatch.Stop();

                _logger.LogInformation(
                    "Transcoded video {Index}/{Total}: {Input} -> {Output}; elapsed={Elapsed}",
                    workItem.Index,
                    totalFiles,
                    workItem.InputPath,
                    workItem.OutputPath,
                    FormatElapsed(fileStopwatch.Elapsed));
                progress?.Report(new VideoTranscodeProgress(
                    workItem.Index,
                    totalFiles,
                    workItem.InputPath,
                    workItem.OutputPath,
                    Kind: "file-completed",
                    Elapsed: FormatElapsed(fileStopwatch.Elapsed)));
                return VideoFileOutcome.Completed(workItem.Index, workItem.OutputPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                DeleteFileIfExists(workItem.OutputPath);
                var message = BuildFailureMessage(workItem.InputPath, ex.Message);
                var failure = new VideoTranscodeFailure(workItem.InputPath, workItem.OutputPath, message);
                _logger.LogError(
                    ex,
                    "Failed to transcode video {Index}/{Total}: {Input} -> {Output}",
                    workItem.Index,
                    totalFiles,
                    workItem.InputPath,
                    workItem.OutputPath);
                progress?.Report(new VideoTranscodeProgress(
                    workItem.Index,
                    totalFiles,
                    workItem.InputPath,
                    workItem.OutputPath,
                    Kind: "file-failed",
                    Message: message));
                return VideoFileOutcome.Failed(workItem.Index, workItem.OutputPath, failure);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> ResolveProjectTitleAsync(string projectDir, CancellationToken cancellationToken)
    {
        try
        {
            var project = await _projectInfoParser.ParseAsync(projectDir, cancellationToken);
            if (!string.IsNullOrWhiteSpace(project.Title))
            {
                return project.Title;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Project info unavailable for transcode, falling back to directory name: {ProjectDir}", projectDir);
        }

        var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectDir)).Trim();
        var normalizedName = directoryName.TrimStart('_');
        return string.IsNullOrWhiteSpace(normalizedName) ? "短剧" : normalizedName;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static void EnsureInputReady(string inputPath)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"未找到源视频文件: {inputPath}", inputPath);
        }

        try
        {
            if (new FileInfo(inputPath).Length <= 0)
            {
                throw new InvalidOperationException($"源视频文件为空或未下载完整: {inputPath}");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法读取源视频文件: {inputPath}，{ex.Message}", ex);
        }
    }

    private static string BuildFailureMessage(string inputPath, string originalMessage)
    {
        if (originalMessage.Contains("转码后有效时长低于 31 秒", StringComparison.Ordinal))
        {
            return originalMessage;
        }

        return originalMessage;
    }

    private async Task TranscodeWithRetryAsync(
        string inputPath,
        string outputPath,
        VideoTranscodeRequest request,
        MediaProbeInfo inputProbe,
        VideoEncodingPlan initialPlan,
        CancellationToken cancellationToken)
    {
        try
        {
            await TranscodeOnceAsync(inputPath, outputPath, request, inputProbe, initialPlan, cancellationToken);
        }
        catch (VideoValidationException ex) when (
            ex.Failure == VideoValidationFailure.LowBitrate &&
            ShouldRetryForLowBitrate(initialPlan))
        {
            var retryPlan = BuildLowBitrateRetryPlan(initialPlan);
            _logger.LogWarning(
                "Output bitrate below the upload threshold, retrying with stricter settings. input={Input}, output={Output}, measured={Measured}, retryCodec={Codec}, retryBitrate={Bitrate}, retryCbr={UseCbr}",
                inputPath,
                outputPath,
                ex.MeasuredBitrateBps,
                retryPlan.VideoCodec,
                retryPlan.VideoBitrateBps,
                retryPlan.UseCbr);

            DeleteFileIfExists(outputPath);
            await TranscodeOnceAsync(inputPath, outputPath, request, inputProbe, retryPlan, cancellationToken);
        }
    }

    private async Task TranscodeOnceAsync(
        string inputPath,
        string outputPath,
        VideoTranscodeRequest request,
        MediaProbeInfo inputProbe,
        VideoEncodingPlan plan,
        CancellationToken cancellationToken)
    {
        var ffmpeg = ResolveFfmpegBinary();
        var result = await _processRunner.RunAsync(
            ffmpeg,
            BuildArguments(inputPath, outputPath, request, plan),
            request.OutputDir,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg 转码失败: {Path.GetFileName(inputPath)}; stderr: {result.StandardError}");
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException($"FFmpeg 执行完成但未生成输出文件: {outputPath}");
        }

        await TrimOutputIfNeededAsync(inputPath, outputPath, request, plan, cancellationToken);
        await ValidateOutputAsync(inputPath, outputPath, inputProbe, plan, cancellationToken);
    }

    private async Task TrimOutputIfNeededAsync(
        string inputPath,
        string outputPath,
        VideoTranscodeRequest request,
        VideoEncodingPlan plan,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(outputPath);
        if (fileInfo.Length <= MaxOutputBytes)
        {
            return;
        }

        var outputProbe = await ProbeMediaAsync(outputPath, cancellationToken);
        if (outputProbe.DurationSeconds <= 0d)
        {
            throw new InvalidOperationException($"无法计算截尾时长: {outputPath}");
        }

        var allowedDuration = Math.Floor(outputProbe.DurationSeconds * (MaxOutputBytes / (double)fileInfo.Length) * 0.98d);
        if (allowedDuration < MinDurationSeconds)
        {
            throw new InvalidOperationException(
                $"转码后文件超过 500MB，且即使截尾也无法同时满足至少 31 秒时长要求: {outputPath}");
        }

        File.Delete(outputPath);

        var trimPlan = plan with { TrimDurationSeconds = allowedDuration };
        var result = await _processRunner.RunAsync(
            ResolveFfmpegBinary(),
            BuildArguments(inputPath, outputPath, request, trimPlan),
            request.OutputDir,
            cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"FFmpeg 截尾失败: {Path.GetFileName(inputPath)}; stderr: {result.StandardError}");
        }
    }

    private async Task ValidateOutputAsync(
        string inputPath,
        string outputPath,
        MediaProbeInfo inputProbe,
        VideoEncodingPlan plan,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(outputPath);
        if (fileInfo.Length > MaxOutputBytes)
        {
            throw new InvalidOperationException($"转码后文件仍超过 500MB: {outputPath}");
        }

        var probe = await ProbeMediaAsync(outputPath, cancellationToken);
        var shortEdge = Math.Min(probe.Width, probe.Height);
        if (shortEdge < MinShortEdge)
        {
            throw new InvalidOperationException($"转码后分辨率低于 720p: {outputPath}");
        }

        if (probe.DurationSeconds + 0.1d < MinDurationSeconds)
        {
            var inputDurationText = inputProbe.DurationSeconds > 0d
                ? inputProbe.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                : "未知";
            var outputDurationText = probe.DurationSeconds > 0d
                ? probe.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                : "未知";

            if (inputProbe.DurationSeconds + 0.1d >= MinDurationSeconds)
            {
                throw new InvalidOperationException(
                    $"源视频容器时长约 {inputDurationText} 秒，但转码后仅得到 {outputDurationText} 秒有效内容，源视频疑似损坏或未下载完整: {inputPath}");
            }

            throw new InvalidOperationException(
                $"源视频和转码后有效时长均低于 31 秒（源视频约 {inputDurationText} 秒，转码后约 {outputDurationText} 秒）: {inputPath}");
        }

        var measuredVideoBitrate = probe.VideoBitrateBps
            ?? (probe.FormatBitrateBps is long formatBitrate
                ? (long?)Math.Max(0L, formatBitrate - (probe.AudioBitrateBps ?? 0L))
                : null)
            ?? plan.VideoBitrateBps;

        if (measuredVideoBitrate < MinVideoBitrateBps)
        {
            throw new VideoValidationException(
                VideoValidationFailure.LowBitrate,
                $"转码后视频码率低于视频号上传阈值（4194304 bps）: {outputPath}",
                measuredVideoBitrate);
        }
    }

    private static IReadOnlyList<string> BuildArguments(
        string inputPath,
        string outputPath,
        VideoTranscodeRequest request,
        VideoEncodingPlan plan)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-i", inputPath,
            "-map", "0:v:0",
            "-map", "0:a?"
        };

        var videoFilters = new List<string>();
        if (plan.SpeedFactor > 1.0001d)
        {
            videoFilters.Add($"setpts={plan.SpeedFactor.ToString("0.######", CultureInfo.InvariantCulture)}*PTS");
        }

        if (ShouldApplyScale(plan))
        {
            videoFilters.Add(BuildScaleFilter(plan.TargetShortEdge));
        }

        if (videoFilters.Count > 0)
        {
            arguments.AddRange(["-vf", string.Join(",", videoFilters)]);
        }

        if (plan.TrimDurationSeconds is not null)
        {
            arguments.AddRange(["-t", plan.TrimDurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(["-c:v", plan.VideoCodec]);

        if (plan.VideoCodec.Equals("libx264", StringComparison.Ordinal))
        {
            arguments.AddRange(["-preset", request.Preset]);
            if (!plan.UseCbr)
            {
                arguments.AddRange(["-crf", request.Crf.ToString(CultureInfo.InvariantCulture)]);
            }
            else
            {
                arguments.AddRange(["-x264-params", "nal-hrd=cbr:force-cfr=1"]);
            }
        }

        arguments.AddRange(["-b:v", plan.VideoBitrateBps.ToString(CultureInfo.InvariantCulture)]);

        if (plan.UseCbr)
        {
            var bitrate = plan.VideoBitrateBps.ToString(CultureInfo.InvariantCulture);
            var bufferSize = (plan.VideoBitrateBps * 2L).ToString(CultureInfo.InvariantCulture);
            arguments.AddRange(["-maxrate", bitrate, "-minrate", bitrate, "-bufsize", bufferSize]);
        }

        arguments.AddRange(["-r", plan.Fps.ToString(CultureInfo.InvariantCulture)]);
        arguments.AddRange(["-pix_fmt", "yuv420p"]);
        arguments.AddRange(["-c:a", "aac", "-b:a", plan.AudioBitrateBps.ToString(CultureInfo.InvariantCulture)]);

        if (plan.SpeedFactor > 1.0001d && plan.HasAudio)
        {
            arguments.AddRange(["-af", BuildAudioTempoFilter(plan.SpeedFactor)]);
        }

        arguments.AddRange(["-movflags", "+faststart", outputPath]);
        return arguments;
    }

    private static string BuildAudioTempoFilter(double speedFactor)
    {
        var targetTempo = 1d / speedFactor;
        var filters = new List<string>();

        while (targetTempo < 0.5d)
        {
            filters.Add("atempo=0.5");
            targetTempo /= 0.5d;
        }

        while (targetTempo > 2d)
        {
            filters.Add("atempo=2.0");
            targetTempo /= 2d;
        }

        filters.Add($"atempo={targetTempo.ToString("0.######", CultureInfo.InvariantCulture)}");
        return string.Join(",", filters);
    }

    private static VideoEncodingPlan BuildEncodingPlan(
        VideoTranscodeSettings? settings,
        MediaProbeInfo inputProbe)
    {
        var useHardwareEncoder = settings?.UseHardwareEncoder ?? true;
        var targetShortEdge = Math.Max(MinShortEdge, settings?.VideoRes ?? MinShortEdge);
        var videoBitrate = Math.Max(MinVideoBitrateBps, settings?.VideoBitrateBps ?? MinVideoBitrateBps);
        var audioBitrate = settings?.VideoAudioBitrateBps ?? DefaultAudioBitrateBps;
        var fps = settings?.VideoFps ?? 30;
        var targetDuration = Math.Max(MinDurationSeconds, inputProbe.DurationSeconds);
        var speedFactor = inputProbe.DurationSeconds > 0d
            ? targetDuration / inputProbe.DurationSeconds
            : 1d;

        return new VideoEncodingPlan(
            ResolveVideoCodec(useHardwareEncoder),
            string.Equals(settings?.VideoBitrateMode, "Cbr", StringComparison.OrdinalIgnoreCase),
            targetShortEdge,
            videoBitrate,
            audioBitrate,
            fps,
            speedFactor,
            inputProbe.HasAudio,
            Math.Min(inputProbe.Width, inputProbe.Height));
    }

    private static bool ShouldRetryForLowBitrate(VideoEncodingPlan plan)
    {
        return !plan.VideoCodec.Equals("libx264", StringComparison.Ordinal) ||
               !plan.UseCbr ||
               plan.VideoBitrateBps < RetryVideoBitrateBps;
    }

    private static VideoEncodingPlan BuildLowBitrateRetryPlan(VideoEncodingPlan plan)
    {
        return plan with
        {
            VideoCodec = "libx264",
            UseCbr = true,
            VideoBitrateBps = Math.Max(plan.VideoBitrateBps, RetryVideoBitrateBps)
        };
    }

    private async Task<MediaProbeInfo> ProbeMediaAsync(string path, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            ResolveFfprobeBinary(),
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

        var durationSeconds = ParseNullableDouble(format, "duration") ?? 0d;
        var width = ParseNullableInt(videoStream, "width") ?? 0;
        var height = ParseNullableInt(videoStream, "height") ?? 0;

        return new MediaProbeInfo(
            durationSeconds,
            width,
            height,
            ParseNullableLong(format, "bit_rate"),
            ParseNullableLong(videoStream, "bit_rate"),
            ParseNullableLong(audioStream, "bit_rate"),
            audioStream.HasValue);
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

    private static string ResolveFfmpegBinary()
    {
        var direct = TryResolveBinary("ffmpeg");
        if (direct is not null)
        {
            return direct;
        }

        throw new InvalidOperationException("未找到 ffmpeg。请安装 ffmpeg，或确保 ffmpeg 在 PATH 中。");
    }

    private static string ResolveFfprobeBinary()
    {
        var direct = TryResolveBinary("ffprobe");
        if (direct is not null)
        {
            return direct;
        }

        throw new InvalidOperationException("未找到 ffprobe。请安装 ffmpeg，或确保 ffprobe 在 PATH 中。");
    }

    private static string? TryResolveBinary(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

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

        return null;
    }

    private static string ResolveVideoCodec(bool useHardwareEncoder)
    {
        if (!useHardwareEncoder)
        {
            return "libx264";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "h264_videotoolbox";
        }

        return "libx264";
    }

    private static bool ShouldApplyScale(VideoEncodingPlan plan)
    {
        return plan.SourceShortEdge <= 0 || plan.SourceShortEdge != plan.TargetShortEdge;
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string BuildScaleFilter(int targetShortEdge)
    {
        return $"scale='if(lt(iw,ih),{targetShortEdge},-2)':'if(lt(iw,ih),-2,{targetShortEdge})'";
    }

    private static string BuildOutputPath(string outputDir, string projectTitle, int episodeIndex)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeTitle = new string(projectTitle.Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeTitle))
        {
            safeTitle = "短剧";
        }

        return Path.Combine(outputDir, $"{safeTitle}-第{episodeIndex}集.mp4");
    }

    private static int GetEpisodeSortKey(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var match = EpisodeIndexRegex.Match(fileName);
        if (!match.Success)
        {
            return int.MaxValue;
        }

        for (var index = 1; index < match.Groups.Count; index++)
        {
            if (match.Groups[index].Success &&
                int.TryParse(match.Groups[index].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var episodeIndex))
            {
                return episodeIndex;
            }
        }

        return int.MaxValue;
    }

    private static VideoTranscodeSettings? LoadSettings(string? configFile)
    {
        if (string.IsNullOrWhiteSpace(configFile))
        {
            return null;
        }
        var map = KeyValueConfigReader.Read(configFile);

        return new VideoTranscodeSettings(
            ParseNullableInt(map, "VideoRes"),
            ParseNullableLong(map, "VideoBitrateBps"),
            GetNullable(map, "VideoBitrateMode"),
            ParseNullableInt(map, "VideoAudioBitrateBps"),
            ParseNullableInt(map, "VideoFps"),
            ParseNullableInt(map, "VideoConcurrentCount"),
            ParseNullableBool(map, "VideoUseHardwareEncoder") ?? true);
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
        if (value is null)
        {
            return null;
        }

        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static long? ParseNullableLong(IReadOnlyDictionary<string, string> map, string key)
    {
        var value = GetNullable(map, key);
        if (value is null)
        {
            return null;
        }

        return long.Parse(value, CultureInfo.InvariantCulture);
    }

    private static bool? ParseNullableBool(IReadOnlyDictionary<string, string> map, string key)
    {
        var value = GetNullable(map, key);
        if (value is null)
        {
            return null;
        }

        return bool.Parse(value);
    }

    private sealed record VideoTranscodeSettings(
        int? VideoRes,
        long? VideoBitrateBps,
        string? VideoBitrateMode,
        int? VideoAudioBitrateBps,
        int? VideoFps,
        int? VideoConcurrentCount,
        bool UseHardwareEncoder);

    private sealed record VideoWorkItem(
        int Index,
        string InputPath,
        string OutputPath);

    private sealed record VideoFileOutcome(
        int Index,
        string Kind,
        string? OutputPath,
        VideoTranscodeFailure? Failure)
    {
        public static VideoFileOutcome Completed(int index, string outputPath) =>
            new(index, "file-completed", outputPath, null);

        public static VideoFileOutcome Skipped(int index, string outputPath) =>
            new(index, "file-skipped", outputPath, null);

        public static VideoFileOutcome Failed(int index, string outputPath, VideoTranscodeFailure failure) =>
            new(index, "file-failed", outputPath, failure);
    }

    private sealed record VideoEncodingPlan(
        string VideoCodec,
        bool UseCbr,
        int TargetShortEdge,
        long VideoBitrateBps,
        int AudioBitrateBps,
        int Fps,
        double SpeedFactor,
        bool HasAudio,
        int SourceShortEdge,
        double? TrimDurationSeconds = null);

    private sealed record MediaProbeInfo(
        double DurationSeconds,
        int Width,
        int Height,
        long? FormatBitrateBps,
        long? VideoBitrateBps,
        long? AudioBitrateBps,
        bool HasAudio);

    private enum VideoValidationFailure
    {
        LowBitrate
    }

    private sealed class VideoValidationException : InvalidOperationException
    {
        public VideoValidationException(
            VideoValidationFailure failure,
            string message,
            long? measuredBitrateBps = null)
            : base(message)
        {
            Failure = failure;
            MeasuredBitrateBps = measuredBitrateBps;
        }

        public VideoValidationFailure Failure { get; }
        public long? MeasuredBitrateBps { get; }
    }
}
