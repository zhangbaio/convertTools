using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services.VideoDedup;

public static class VideoDedupService
{
    private const string FingerprintFileName = "dedup-fingerprint.json";
    private const string DownloadStateFileName = ".weixin-channel-download-state.json";

    internal static Func<string, CancellationToken, Task<VideoDedupProbe>>? ProbeForTests { get; set; }
    internal static Func<IReadOnlyList<string>, CancellationToken, Task>? RunProcessForTests { get; set; }
    internal static string? EncoderForTests { get; set; }
    internal static Func<string, CancellationToken, Task<bool>>? EncoderWorksForTests { get; set; }

    internal readonly record struct EncoderChoice(string Encoder, string Device, string Note);

    public static bool NeedsRun(string projectDir, VideoDedupPreset? preset)
    {
        try
        {
            var project = Path.GetFullPath(projectDir);
            var normalized = (preset ?? VideoDedupPreset.CreateDefault()).NormalizedCopy();
            var fingerprint = ReadFingerprint(FingerprintPath(project));
            var visible = ListVisibleVideos(project);
            return !OutputsMatch(project, fingerprint, normalized, visible);
        }
        catch
        {
            return true;
        }
    }

    public static async Task RunAsync(
        QueueProjectItem item,
        TikTokAccountProfile account,
        bool force,
        Action<string>? log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.ProjectDir) || !Directory.Exists(item.ProjectDir))
            throw new InvalidOperationException("项目目录不存在，无法执行视频去重。");

        var preset = account.VideoDedupPreset?.NormalizedCopy() ?? VideoDedupPreset.CreateDefault().NormalizedCopy();
        if (account.VideoDedupPreset is null)
            log?.Invoke($"账号「{account.DisplayName}」尚未保存视频去重预设，使用内置默认方案。");

        var project = Path.GetFullPath(item.ProjectDir);
        var originalsRoot = Path.Combine(project, ProjectVideoResolver.SourceOriginalsDirectoryName);
        var fingerprintPath = FingerprintPath(project);
        var fingerprint = ReadFingerprint(fingerprintPath);
        var visible = ListVisibleVideos(project);
        if (!force && OutputsMatch(project, fingerprint, preset, visible))
        {
            var kept = fingerprint!.Outputs.Count;
            item.EpisodeCount = kept;
            log?.Invoke($"跳过：去重成片已与账号「{account.DisplayName}」的方案「{preset.Name}」一致，共 {kept.ToString(CultureInfo.InvariantCulture)} 集。");
            return;
        }

        var inputs = ResolveInputs(project, originalsRoot, visible, fingerprint, log);
        if (inputs.Count == 0)
            throw new InvalidOperationException("没有可去重的视频。请先完成下载。");

        var logGate = new object();
        void Write(string message)
        {
            if (log is null)
                return;
            lock (logGate)
                log(message);
        }

        Write($"开始：账号「{account.DisplayName}」方案「{preset.Name}」，原片 {inputs.Count.ToString(CultureInfo.InvariantCulture)} 个。");
        foreach (var line in DescribeParameters(preset))
            Write(line);
        Write($"正在读取 {inputs.Count.ToString(CultureInfo.InvariantCulture)} 个原片。");
        var clips = new List<VideoDedupClip>(inputs.Count);
        for (var inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var input = inputs[inputIndex];
            var probe = await ProbeAsync(input, ct).ConfigureAwait(false);
            if (probe.DurationSeconds <= 0)
                throw new InvalidOperationException($"无法读取视频时长：{Path.GetFileName(input)}");
            clips.Add(new VideoDedupClip(input, probe));
            Write($"读取第 {(inputIndex + 1).ToString(CultureInfo.InvariantCulture)}/{inputs.Count.ToString(CultureInfo.InvariantCulture)} 个原片：{DescribeClip(clips[^1])}。");
        }

        var groups = VideoDedupPlanner.Group(clips.Select(clip => clip.Probe.DurationSeconds).ToArray(), preset);
        var merged = groups.Any(group => group.Count > 1);
        var outputDirectory = Path.GetDirectoryName(VisiblePathFor(project, originalsRoot, inputs[0])) ?? project;
        var jobs = new List<RenderJob>();
        for (var index = 0; index < groups.Count; index++)
        {
            var groupClips = groups[index].Select(clipIndex => clips[clipIndex]).ToArray();
            var destination = merged
                ? Path.Combine(outputDirectory, $"{(index + 1).ToString("00", CultureInfo.InvariantCulture)}.mp4")
                : VisiblePathFor(project, originalsRoot, groupClips[0].Path);
            jobs.Add(new RenderJob(groupClips, destination));
        }

        var workRoot = Path.Combine(originalsRoot, ".work");
        Directory.CreateDirectory(workRoot);
        ClearDirectory(workRoot);
        var choice = await ResolveEncoderAsync(preset, ct).ConfigureAwait(false);
        var encoder = choice.Encoder;
        Write(string.IsNullOrWhiteSpace(choice.Note)
            ? $"视频编码器：{encoder}（{choice.Device}），并发 {preset.Parallel.ToString(CultureInfo.InvariantCulture)}。"
            : $"视频编码器：{encoder}（{choice.Device}），并发 {preset.Parallel.ToString(CultureInfo.InvariantCulture)}。{choice.Note}");
        if (merged && preset.TransitionsEnabled)
            Write("并集接缝使用转场；短于 0.8 秒的成片改为直接拼接。");
        var totalEpisodes = jobs.Count.ToString(CultureInfo.InvariantCulture);

        Exception? encodeFailure = null;
        using var renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(
                jobs.Select((job, index) => (job, index)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = preset.Parallel,
                    CancellationToken = renderCancellation.Token,
                },
                async (entry, token) =>
                {
                    var temp = Path.Combine(
                        workRoot,
                        $"{(entry.index + 1).ToString("00", CultureInfo.InvariantCulture)}.mp4");
                    var seed = Math.Abs(entry.job.Clips[0].Path.GetHashCode(StringComparison.Ordinal));
                    var episode = (entry.index + 1).ToString(CultureInfo.InvariantCulture);
                    var arguments = VideoDedupPlanner.BuildArguments(
                        preset,
                        entry.job.Clips,
                        temp,
                        encoder,
                        seed);
                    Write($"第 {episode}/{totalEpisodes} 集开始去重：{DescribeJob(entry.job.Clips, entry.job.Destination, preset)}");
                    try
                    {
                        try
                        {
                            await RunFfmpegAsync(arguments, token).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException) when (
                            AllowsCpuFallback(preset) && UsesHardwareEncoder(arguments))
                        {
                            var software = encoder.Contains("265", StringComparison.OrdinalIgnoreCase) ||
                                           encoder.Contains("hevc", StringComparison.OrdinalIgnoreCase)
                                ? "libx265"
                                : "libx264";
                            Write($"第 {episode}/{totalEpisodes} 集 {encoder} 编码失败，改用 {software} 重新去重。");
                            await RunFfmpegAsync(
                                VideoDedupPlanner.BuildArguments(preset, entry.job.Clips, temp, software, seed),
                                token).ConfigureAwait(false);
                        }
                        if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                            throw new InvalidOperationException($"去重未生成有效文件：{Path.GetFileName(entry.job.Destination)}");
                        entry.job.TempPath = temp;
                        Write($"第 {episode}/{totalEpisodes} 集去重完成，输出 {Path.GetFileName(entry.job.Destination)}，{FormatSize(new FileInfo(temp).Length)}。");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Write($"第 {episode}/{totalEpisodes} 集去重失败：{ex.Message}");
                        Interlocked.CompareExchange(ref encodeFailure, ex, null);
                        renderCancellation.Cancel();
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (encodeFailure is not null || ct.IsCancellationRequested)
        {
            if (encodeFailure is null)
            {
                ClearDirectory(workRoot);
                throw;
            }
        }

        if (encodeFailure is not null)
        {
            ClearDirectory(workRoot);
            throw encodeFailure;
        }

        PublishOutputs(project, visible, fingerprint, jobs);
        var outputs = jobs.Select(job => new FingerprintOutput(
            ToRelative(project, job.Destination),
            new FileInfo(job.Destination).Length)).ToArray();
        WriteFingerprint(fingerprintPath, preset.Fingerprint(), outputs);
        if (merged)
            UpdateDownloadStateCount(project, inputs.Count, outputs.Length, log);
        if (item.SourceEpisodeCount < inputs.Count)
            item.SourceEpisodeCount = inputs.Count;
        item.EpisodeCount = outputs.Length;
        ClearDirectory(workRoot);
        log?.Invoke($"视频去重完成：{inputs.Count.ToString(CultureInfo.InvariantCulture)} 个原片生成 {outputs.Length.ToString(CultureInfo.InvariantCulture)} 集，原片保留在 source-originals。");
    }

    private static IEnumerable<string> DescribeParameters(VideoDedupPreset preset)
    {
        yield return $"参数：{DescribePreference(preset.EncodingPreference)}，视频 {DescribeCodec(preset.VideoCodec)}，{preset.Fps.ToString(CultureInfo.InvariantCulture)} fps，{DescribeRate(preset)}，音频 {DescribeAudio(preset)}。";
        var resolution = preset.KeepsOriginalResolution
            ? "分辨率保持原分辨率"
            : $"分辨率 {preset.PortraitWidth.ToString(CultureInfo.InvariantCulture)}×{preset.PortraitHeight.ToString(CultureInfo.InvariantCulture)}，横屏自动转为 {preset.PortraitHeight.ToString(CultureInfo.InvariantCulture)}×{preset.PortraitWidth.ToString(CultureInfo.InvariantCulture)}";
        yield return $"画面：{resolution}，变速 {Num(preset.SpeedFactor)} 倍，抽帧 {Num(preset.FrameDropPercent)}%，开头裁剪 {Num(preset.TrimStartSeconds)} 秒，结尾裁剪 {Num(preset.TrimEndSeconds)} 秒，上下裁边 {Num(preset.CropTopPercent)}%，左右裁边 {Num(preset.CropLeftPercent)}%。";
        var filters = new List<string>();
        if (Math.Abs(preset.Brightness) > 0.0001)
            filters.Add($"亮度 {Num(preset.Brightness)}");
        if (Math.Abs(preset.Contrast - 1) > 0.0001)
            filters.Add($"对比度 {Num(preset.Contrast)}");
        if (Math.Abs(preset.Saturation - 1) > 0.0001)
            filters.Add($"饱和度 {Num(preset.Saturation)}");
        if (preset.Grayscale)
            filters.Add("灰度");
        if (preset.Sharpness > 0.01)
            filters.Add($"锐化 {Num(preset.Sharpness)}");
        if (Math.Abs(preset.AudioLowGainDb) > 0.01 || Math.Abs(preset.AudioMidGainDb) > 0.01 || Math.Abs(preset.AudioHighGainDb) > 0.01)
            filters.Add($"均衡器 低频 {Num(preset.AudioLowGainDb)} dB、中频 {Num(preset.AudioMidGainDb)} dB、高频 {Num(preset.AudioHighGainDb)} dB");
        if (filters.Count > 0)
            yield return $"滤镜：{string.Join("，", filters)}。";
        yield return preset.HasDurationLimit
            ? $"单集时长：最短 {Num(preset.MinimumDurationSeconds)} 秒，最长 {(preset.MaximumDurationSeconds > 0 ? Num(preset.MaximumDurationSeconds) + " 秒" : "不限制")}。"
            : "单集时长：不限制。";
        if (!preset.TransitionsEnabled)
        {
            yield return "转场：关。";
            yield break;
        }

        var names = new List<string>();
        if (preset.TransitionFadeBlack) names.Add("淡黑");
        if (preset.TransitionDissolve) names.Add("交叉溶解");
        if (preset.TransitionPush) names.Add("推拉");
        if (preset.TransitionSlide) names.Add("滑动");
        if (preset.TransitionZoom) names.Add("缩放");
        yield return $"转场：{string.Join("、", names)}，每处 0.3 到 0.6 秒。";
    }

    private static string DescribeJob(IReadOnlyList<VideoDedupClip> clips, string destination, VideoDedupPreset preset)
    {
        var output = Path.GetFileName(destination);
        if (clips.Count == 1)
            return $"{DescribeClip(clips[0])} → {output}。";
        var sources = string.Join("、", clips.Select(clip => $"{Path.GetFileName(clip.Path)}（{DescribeProbe(clip)}）"));
        var join = VideoDedupPlanner.CanTransition(preset, clips) ? "接缝使用转场" : "直接拼接";
        return $"合并 {sources} → {output}，{join}。";
    }

    private static string DescribeClip(VideoDedupClip clip) =>
        $"{Path.GetFileName(clip.Path)}，{DescribeProbe(clip)}";

    private static string DescribeProbe(VideoDedupClip clip) =>
        $"{Num(clip.Probe.DurationSeconds)} 秒，{clip.Probe.Width.ToString(CultureInfo.InvariantCulture)}x{clip.Probe.Height.ToString(CultureInfo.InvariantCulture)}";

    private static string DescribePreference(string preference)
    {
        if (string.Equals(preference, VideoDedupPreset.EncodingSoftwareOnly, StringComparison.Ordinal))
            return "编码器 CPU";
        if (string.Equals(preference, VideoDedupPreset.EncodingNvenc, StringComparison.Ordinal))
            return "编码器指定 NVIDIA";
        if (string.Equals(preference, VideoDedupPreset.EncodingAmf, StringComparison.Ordinal))
            return "编码器指定 AMD";
        if (string.Equals(preference, VideoDedupPreset.EncodingQsv, StringComparison.Ordinal))
            return "编码器指定 Intel";
        if (string.Equals(preference, VideoDedupPreset.EncodingHardwareOnly, StringComparison.Ordinal))
            return "编码器仅硬件";
        return "编码器自动选择";
    }

    private static string DescribeCodec(string codec)
    {
        if (string.Equals(codec, VideoDedupPreset.CodecHevc, StringComparison.Ordinal) ||
            string.Equals(codec, VideoDedupPreset.CodecH265, StringComparison.Ordinal))
            return "HEVC";
        if (string.Equals(codec, VideoDedupPreset.CodecDefault, StringComparison.Ordinal))
            return "默认编码";
        return "H.264";
    }

    private static string DescribeRate(VideoDedupPreset preset)
    {
        if (string.Equals(preset.BitrateMode, VideoDedupPreset.BitrateCrf, StringComparison.Ordinal))
            return $"CRF {preset.Crf.ToString(CultureInfo.InvariantCulture)}";
        if (string.Equals(preset.BitrateMode, VideoDedupPreset.BitrateCbr, StringComparison.Ordinal))
            return $"CBR {Num(preset.VideoBitrateMbps)} Mbps";
        return $"VBR {Num(preset.VideoBitrateMbps)} Mbps，最大码率 {Num(preset.VideoBitrateMbps * 1.45)} Mbps";
    }

    private static string DescribeAudio(VideoDedupPreset preset)
    {
        if (string.Equals(preset.AudioCodec, VideoDedupPreset.AudioDefault, StringComparison.Ordinal))
            return "默认";
        var bitrate = preset.AudioBitrateKbps > 0 ? preset.AudioBitrateKbps : 128;
        return $"AAC {bitrate.ToString(CultureInfo.InvariantCulture)} kbps";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        if (bytes < 1024 * 1024)
            return $"{(bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KB";
        return $"{(bytes / 1048576d).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static List<string> ResolveInputs(
        string project,
        string originalsRoot,
        IReadOnlyList<string> visible,
        DedupFingerprint? fingerprint,
        Action<string>? log)
    {
        var archived = ListArchivedVideos(originalsRoot);
        var refreshed = RefreshArchiveFromNewDownloads(project, originalsRoot, visible, archived, fingerprint);
        if (refreshed)
            log?.Invoke("检测到新的下载文件，已更新 source-originals 中的原片。");
        archived = ListArchivedVideos(originalsRoot);
        if (archived.Count > 0)
            return archived;

        if (visible.Count == 0)
            return [];

        Directory.CreateDirectory(originalsRoot);
        foreach (var file in visible)
            CopyToArchive(project, originalsRoot, file);
        log?.Invoke($"已把 {visible.Count.ToString(CultureInfo.InvariantCulture)} 个下载视频复制到 source-originals。");
        return ListArchivedVideos(originalsRoot);
    }

    private static bool RefreshArchiveFromNewDownloads(
        string project,
        string originalsRoot,
        IReadOnlyList<string> visible,
        IReadOnlyList<string> archived,
        DedupFingerprint? fingerprint)
    {
        if (archived.Count == 0 || visible.Count == 0 || fingerprint is null)
            return false;

        var changed = false;
        foreach (var file in visible)
        {
            var relative = ToRelative(project, file);
            var length = new FileInfo(file).Length;
            var recorded = fingerprint.Outputs.FirstOrDefault(output =>
                string.Equals(output.Path, relative, StringComparison.OrdinalIgnoreCase));
            if (recorded is not null && recorded.Length == length)
                continue;

            var archivePath = Path.Combine(originalsRoot, relative);
            if (File.Exists(archivePath) && new FileInfo(archivePath).Length == length)
                continue;

            CopyToArchive(project, originalsRoot, file);
            changed = true;
        }

        return changed;
    }

    private static void PublishOutputs(
        string project,
        IReadOnlyList<string> visible,
        DedupFingerprint? previous,
        IReadOnlyList<RenderJob> jobs)
    {
        var destinations = jobs.Select(job => Path.GetFullPath(job.Destination)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
            Directory.CreateDirectory(Path.GetDirectoryName(job.Destination)!);

        var removals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in visible)
        {
            if (!destinations.Contains(Path.GetFullPath(file)))
                removals.Add(Path.GetFullPath(file));
        }

        if (previous is not null)
        {
            foreach (var output in previous.Outputs)
            {
                var full = Path.GetFullPath(Path.Combine(project, output.Path));
                if (!destinations.Contains(full))
                    removals.Add(full);
            }
        }

        foreach (var path in removals)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        foreach (var job in jobs)
        {
            if (File.Exists(job.Destination))
                File.Delete(job.Destination);
            File.Move(job.TempPath, job.Destination);
        }
    }

    private static void CopyToArchive(string project, string originalsRoot, string file)
    {
        var destination = Path.Combine(originalsRoot, ToRelative(project, file));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: true);
    }

    private static string VisiblePathFor(string project, string originalsRoot, string archivedPath)
    {
        var relative = Path.GetRelativePath(originalsRoot, archivedPath);
        return Path.GetFullPath(Path.Combine(project, relative));
    }

    private static bool OutputsMatch(
        string project,
        DedupFingerprint? fingerprint,
        VideoDedupPreset preset,
        IReadOnlyList<string> visible)
    {
        if (fingerprint is null ||
            !string.Equals(fingerprint.Preset, preset.Fingerprint(), StringComparison.Ordinal) ||
            fingerprint.Outputs.Count == 0)
        {
            return false;
        }

        foreach (var output in fingerprint.Outputs)
        {
            var full = Path.Combine(project, output.Path);
            if (!File.Exists(full) || new FileInfo(full).Length != output.Length)
                return false;
        }

        if (visible.Count != fingerprint.Outputs.Count)
            return false;
        foreach (var file in visible)
        {
            var relative = ToRelative(project, file);
            if (!fingerprint.Outputs.Any(output =>
                    string.Equals(output.Path, relative, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private static List<string> ListVisibleVideos(string project) =>
        ProjectVideoResolver.ResolveSourceVideos(project, applyOverLimitSlice: false)
            .OrderBy(NaturalKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> ListArchivedVideos(string originalsRoot)
    {
        if (!Directory.Exists(originalsRoot))
            return [];
        return Directory.EnumerateFiles(originalsRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(originalsRoot, path);
                return !relative.StartsWith(".work", StringComparison.OrdinalIgnoreCase) &&
                       !relative.Contains($"{Path.DirectorySeparatorChar}.work{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                       IsVideo(path);
            })
            .OrderBy(NaturalKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<VideoDedupProbe> ProbeAsync(string path, CancellationToken ct)
    {
        if (ProbeForTests is not null)
            return await ProbeForTests(path, ct).ConfigureAwait(false);

        var ffprobe = ResolveFfprobe();
        var (code, stdout, stderr) = await FfmpegRunner.RunCaptureAsync(
            ffprobe,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path],
            ct).ConfigureAwait(false);
        if (code != 0)
            throw new InvalidOperationException($"ffprobe 分析失败：{Path.GetFileName(path)} {stderr.Trim()}");

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        double duration = 0;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationElement) &&
            double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            duration = parsed;
        }

        var width = 0;
        var height = 0;
        var hasAudio = false;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var type) ? type.GetString() : "";
                if (string.Equals(codecType, "video", StringComparison.Ordinal) && width == 0)
                {
                    width = stream.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;
                }
                else if (string.Equals(codecType, "audio", StringComparison.Ordinal))
                {
                    hasAudio = true;
                }
            }
        }

        return new VideoDedupProbe(duration, width, height, hasAudio);
    }

    internal static Task<EncoderChoice> ResolveEncoderForTests(VideoDedupPreset preset, CancellationToken ct) =>
        ResolveEncoderAsync(preset, ct);

    private static async Task<EncoderChoice> ResolveEncoderAsync(VideoDedupPreset preset, CancellationToken ct)
    {
        var hevc = preset.VideoCodec is VideoDedupPreset.CodecHevc or VideoDedupPreset.CodecH265;
        var software = hevc ? "libx265" : "libx264";
        if (!string.IsNullOrWhiteSpace(EncoderForTests))
            return new EncoderChoice(EncoderForTests, Describe(EncoderForTests), "");
        if (string.Equals(preset.EncodingPreference, VideoDedupPreset.EncodingSoftwareOnly, StringComparison.Ordinal))
            return new EncoderChoice(software, "CPU", "");

        var candidates = HardwareCandidates(preset, hevc).ToArray();
        var unavailable = new List<string>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await EncoderWorksAsync(candidate, ct).ConfigureAwait(false))
                return new EncoderChoice(candidate, Describe(candidate), "");
            unavailable.Add(Describe(candidate));
        }

        if (!AllowsCpuFallback(preset))
            throw new InvalidOperationException(UnavailableMessage(preset));
        var note = unavailable.Count == 0
            ? ""
            : string.Join("、", unavailable.Distinct()) + " 试编码不可用，已改用 CPU。";
        return new EncoderChoice(software, "CPU", note);
    }

    private static IEnumerable<string> HardwareCandidates(VideoDedupPreset preset, bool hevc)
    {
        var preference = preset.EncodingPreference;
        if (string.Equals(preference, VideoDedupPreset.EncodingNvenc, StringComparison.Ordinal))
        {
            yield return hevc ? "hevc_nvenc" : "h264_nvenc";
            yield break;
        }

        if (string.Equals(preference, VideoDedupPreset.EncodingAmf, StringComparison.Ordinal))
        {
            yield return hevc ? "hevc_amf" : "h264_amf";
            yield break;
        }

        if (string.Equals(preference, VideoDedupPreset.EncodingQsv, StringComparison.Ordinal))
        {
            yield return hevc ? "hevc_qsv" : "h264_qsv";
            yield break;
        }

        yield return hevc ? "hevc_nvenc" : "h264_nvenc";
        yield return hevc ? "hevc_qsv" : "h264_qsv";
        yield return hevc ? "hevc_amf" : "h264_amf";
        if (OperatingSystem.IsMacOS())
            yield return hevc ? "hevc_videotoolbox" : "h264_videotoolbox";
    }

    private static bool AllowsCpuFallback(VideoDedupPreset preset) =>
        string.Equals(preset.EncodingPreference, VideoDedupPreset.EncodingHardwareFirst, StringComparison.Ordinal);

    private static string UnavailableMessage(VideoDedupPreset preset)
    {
        if (string.Equals(preset.EncodingPreference, VideoDedupPreset.EncodingNvenc, StringComparison.Ordinal))
            return "当前机器没有可用的 NVIDIA NVENC 编码器。";
        if (string.Equals(preset.EncodingPreference, VideoDedupPreset.EncodingAmf, StringComparison.Ordinal))
            return "当前机器没有可用的 AMD AMF 编码器。";
        if (string.Equals(preset.EncodingPreference, VideoDedupPreset.EncodingQsv, StringComparison.Ordinal))
            return "当前机器没有可用的 Intel QSV 编码器。";
        return "当前机器没有可用的硬件编码器。";
    }

    private static string Describe(string encoder)
    {
        if (encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            return "NVIDIA";
        if (encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
            return "Intel";
        if (encoder.Contains("amf", StringComparison.OrdinalIgnoreCase))
            return "AMD";
        if (encoder.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase))
            return "Apple";
        return "CPU";
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> EncoderProbeCache = new(StringComparer.Ordinal);

    private static async Task<bool> EncoderWorksAsync(string encoder, CancellationToken ct)
    {
        if (EncoderWorksForTests is not null)
            return await EncoderWorksForTests(encoder, ct).ConfigureAwait(false);
        if (EncoderProbeCache.TryGetValue(encoder, out var cached))
            return cached;

        var (code, _, _) = await FfmpegRunner.RunCaptureAsync(
            FfmpegLocator.ResolveFfmpeg(),
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=0.2",
                "-frames:v", "3", "-c:v", encoder, "-pix_fmt", "yuv420p", "-f", "null", "-",
            ],
            ct).ConfigureAwait(false);
        var works = code == 0;
        EncoderProbeCache[encoder] = works;
        return works;
    }

    private static async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (RunProcessForTests is not null)
        {
            await RunProcessForTests(arguments, ct).ConfigureAwait(false);
            return;
        }

        await FfmpegRunner.RunAsync(FfmpegLocator.ResolveFfmpeg(), arguments, ct).ConfigureAwait(false);
    }

    private static bool UsesHardwareEncoder(IReadOnlyList<string> arguments) =>
        arguments.Any(argument =>
            argument is "h264_nvenc" or "hevc_nvenc" or "h264_qsv" or "hevc_qsv" or "h264_amf" or "hevc_amf"
                or "h264_videotoolbox" or "hevc_videotoolbox");

    private static void UpdateDownloadStateCount(string project, int sourceCount, int outputCount, Action<string>? log)
    {
        var path = Path.Combine(project, DownloadStateFileName);
        if (!File.Exists(path) || outputCount <= 0)
            return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node is null)
                return;
            if (!node.ContainsKey("source_video_count"))
                node["source_video_count"] = sourceCount;
            node["video_count"] = outputCount;
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log?.Invoke($"已把下载记录中的集数更新为 {outputCount.ToString(CultureInfo.InvariantCulture)}。");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"下载记录集数未更新：{ex.Message}");
        }
    }

    private static string FingerprintPath(string project) =>
        Path.Combine(project, ProjectVideoResolver.SourceOriginalsDirectoryName, FingerprintFileName);

    private static DedupFingerprint? ReadFingerprint(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<DedupFingerprint>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteFingerprint(string path, string presetHash, IReadOnlyList<FingerprintOutput> outputs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new DedupFingerprint(presetHash, outputs.ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ResolveFfprobe()
    {
        var ffmpeg = FfmpegLocator.ResolveFfmpeg();
        var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        return File.Exists(sibling) ? sibling : MediaBinaryResolver.ResolveFfprobe();
    }

    private static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory))
            File.Delete(file);
    }

    private static string ToRelative(string project, string path)
    {
        var relative = Path.GetRelativePath(project, path);
        return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static bool IsVideo(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private static string NaturalKey(string path) =>
        Regex.Replace(Path.GetFileName(path), @"\d+", match => match.Value.PadLeft(8, '0'));

    private sealed class RenderJob(IReadOnlyList<VideoDedupClip> clips, string destination)
    {
        public IReadOnlyList<VideoDedupClip> Clips { get; } = clips;
        public string Destination { get; } = destination;
        public string TempPath { get; set; } = "";
    }

    private sealed record DedupFingerprint(string Preset, List<FingerprintOutput> Outputs);

    private sealed record FingerprintOutput(string Path, long Length);
}
