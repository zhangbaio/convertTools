using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Services.VideoDedup;

public static class VideoDedupService
{
    private const string FingerprintFileName = "dedup-fingerprint.json";
    private const string ResumeFileName = "dedup-resume.json";
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

    /// <summary>别的步骤改写了去重成片后，同步记录里的文件大小，免得被当成新下载重新去重。</summary>
    public static void RefreshOutputLength(string projectDir, string videoPath)
    {
        var project = Path.GetFullPath(projectDir);
        var fingerprintPath = FingerprintPath(project);
        var fingerprint = ReadFingerprint(fingerprintPath);
        if (fingerprint is null)
            return;

        var relative = ToRelative(project, Path.GetFullPath(videoPath));
        var index = fingerprint.Outputs.FindIndex(output =>
            string.Equals(output.Path, relative, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        fingerprint.Outputs[index] = fingerprint.Outputs[index] with { Length = new FileInfo(videoPath).Length };
        WriteFingerprint(fingerprintPath, fingerprint.Preset, fingerprint.Outputs);
    }

    public static Task RunAsync(
        QueueProjectItem item,
        TikTokAccountProfile account,
        bool force,
        Action<string>? log,
        CancellationToken ct) =>
        RunWithPresetAsync(
            item,
            account,
            VideoDedupTemplateCatalog.OpenDefault().CaptureRunPreset(VideoDedupTemplateAccount.ForTikTok(account), log),
            OverLimitEpisodePolicy.ResolveEpisodeBounds(ClientSettingsStore.Load()).Minimum,
            force,
            log,
            ct);

    public static async Task<IReadOnlyList<string>> RunStagedSeriesAsync(
        string directory,
        string accountName,
        int episodeCount,
        VideoDedupPreset preset,
        bool force,
        Action<string>? log,
        CancellationToken ct,
        Action<int, int>? progress = null,
        IReadOnlyList<string?>? reusableEpisodes = null)
    {
        var item = new QueueProjectItem { ProjectDir = directory, EpisodeCount = episodeCount };
        var resegment = preset.HasDurationLimit;
        await RunWithPresetAsync(item, new TikTokAccountProfile { Name = accountName }, preset,
            resegment ? 1 : episodeCount, force, log, ct, progress, resegment ? null : reusableEpisodes, stagedSeries: true);
        var outputs = ListVisibleVideos(Path.GetFullPath(directory));
        if (outputs.Count == 0 || outputs.Count != item.EpisodeCount)
            throw new InvalidOperationException("视频去重成片不完整。");
        if (!resegment && outputs.Count != episodeCount)
            throw new InvalidOperationException("处理后的集数与原剧不一致。");
        return outputs;
    }

    public static async Task<IReadOnlyList<string>> RunDirectSeriesAsync(
        string outputDirectory,
        IReadOnlyList<string> originals,
        string accountName,
        VideoDedupPreset preset,
        Action<IReadOnlyList<string>> prepareOutput,
        Action<string>? log,
        CancellationToken ct,
        Action<int, int>? progress = null)
    {
        var item = new QueueProjectItem { ProjectDir = outputDirectory, EpisodeCount = originals.Count };
        IReadOnlyList<string> outputs = [];
        await RunWithPresetAsync(item, new TikTokAccountProfile { Name = accountName }, preset,
            preset.HasDurationLimit ? 1 : originals.Count, true, log, ct, progress,
            directOutput: new DirectSeries(originals, paths =>
            {
                prepareOutput(paths);
                outputs = paths;
            })).ConfigureAwait(false);
        return outputs;
    }

    internal sealed record DirectSeries(IReadOnlyList<string> Originals, Action<IReadOnlyList<string>> PrepareOutput);

    internal static async Task RunWithPresetAsync(
        QueueProjectItem item,
        TikTokAccountProfile account,
        VideoDedupPreset? runPreset,
        int minimumEpisodeCount,
        bool force,
        Action<string>? log,
        CancellationToken ct,
        Action<int, int>? progress = null,
        IReadOnlyList<string?>? reusableEpisodes = null,
        bool stagedSeries = false,
        DirectSeries? directOutput = null)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(item.ProjectDir) || !Directory.Exists(item.ProjectDir))
            throw new InvalidOperationException("项目目录不存在，无法执行视频去重。");

        var preset = (runPreset ?? VideoDedupPreset.CreateDefault()).NormalizedCopy();
        if (runPreset is null)
            log?.Invoke($"账号「{account.DisplayName}」尚未保存视频去重预设，使用内置默认方案。");

        var project = Path.GetFullPath(item.ProjectDir);
        var originalsRoot = Path.Combine(project, ProjectVideoResolver.SourceOriginalsDirectoryName);
        var fingerprintPath = FingerprintPath(project);
        var fingerprint = directOutput is null ? ReadFingerprint(fingerprintPath) : null;
        var visible = directOutput is null ? ListVisibleVideos(project) : [];
        if (directOutput is null && !force && OutputsMatch(project, fingerprint, preset, visible))
        {
            var kept = fingerprint!.Outputs.Count;
            EnsureMinimumEpisodeCount(kept, minimumEpisodeCount);
            item.EpisodeCount = kept;
            progress?.Invoke(kept, kept);
            log?.Invoke($"跳过：去重成片已与账号「{account.DisplayName}」的方案「{preset.Name}」一致，共 {kept.ToString(CultureInfo.InvariantCulture)} 集。");
            return;
        }

        var archived = directOutput is null ? ListArchivedVideos(originalsRoot) : [];
        var (inputs, archiveCopies) = directOutput is null
            ? ResolveInputs(project, originalsRoot, visible, archived,
                stagedSeries && archived.Count > 0 ? null : fingerprint)
            : (directOutput.Originals.Select(path => new DedupInput(path, path)).ToList(), []);
        if (inputs.Count == 0)
            throw new InvalidOperationException("没有可去重的视频。请先完成下载。");
        if (!preset.HasDurationLimit)
            EnsureMinimumEpisodeCount(inputs.Count, minimumEpisodeCount);

        var logGate = new object();
        void Write(string message)
        {
            if (log is null)
                return;
            lock (logGate)
                log(message);
        }

        VideoDedupEffectFilters.Validate(preset);
        Write($"开始：账号「{account.DisplayName}」方案「{preset.Name}」，原片 {inputs.Count.ToString(CultureInfo.InvariantCulture)} 个。");
        Write($"去重操作：{DescribeOperations(preset)}。");
        Write($"正在读取 {inputs.Count.ToString(CultureInfo.InvariantCulture)} 个原片。");
        var clips = new List<VideoDedupClip>(inputs.Count);
        for (var inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var input = inputs[inputIndex];
            var probe = await ProbeAsync(input.ReadPath, ct).ConfigureAwait(false);
            if (probe.DurationSeconds <= 0)
                throw new InvalidOperationException($"无法读取视频时长：{Path.GetFileName(input.ReadPath)}");
            // 用归档路径计算转场，与后续实际编码的切片保持一致。
            clips.Add(new VideoDedupClip(input.ArchivePath, probe));
            Write($"读取第 {(inputIndex + 1).ToString(CultureInfo.InvariantCulture)}/{inputs.Count.ToString(CultureInfo.InvariantCulture)} 个原片：{DescribeClip(clips[^1])}。");
        }

        var clipPaths = clips.Select(clip => clip.Path).ToArray();
        var plan = VideoDedupPlanner.Plan(
            clips.Select(clip => clip.Probe.DurationSeconds).ToArray(),
            preset,
            clipPaths);
        if (preset.HasDurationLimit)
        {
            if (plan.Count == 0)
                throw new InvalidOperationException("视频去重已停止，不会开始编码。没有算出可输出的集。");
            EnsureMinimumEpisodeCount(plan.Count, minimumEpisodeCount);
            if (VideoDedupPlanner.DescribeDurationMismatch(plan, preset, clipPaths) is { } mismatch)
                throw new InvalidOperationException(VideoDedupPlanner.FormatDurationStop(mismatch));
            Write(VideoDedupPlanner.DescribeDurationCheckPassed(clips.Count, plan, preset, clipPaths));
        }

        var encodeParallel = await ApplyGlobalConcurrencyAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        foreach (var input in archiveCopies)
            CopyToArchive(input.ReadPath, input.ArchivePath);
        if (archiveCopies.Count > 0)
        {
            Write(archived.Count == 0
                ? $"已把 {archiveCopies.Count.ToString(CultureInfo.InvariantCulture)} 个下载视频复制到 source-originals。"
                : "检测到新的下载文件，已更新 source-originals 中的原片。");
        }

        var renumber = plan.Count != clips.Count || plan.Any(group => group.Count > 1);
        var outputDirectory = directOutput is null
            ? Path.GetDirectoryName(VisiblePathFor(project, originalsRoot, clips[0].Path)) ?? project
            : project;
        var jobs = new List<RenderJob>();
        for (var index = 0; index < plan.Count; index++)
        {
            var slices = plan[index];
            var episodeNumber = (index + 1).ToString(stagedSeries ? "D4" : "00", CultureInfo.InvariantCulture);
            var destination = directOutput is not null
                ? Path.Combine(outputDirectory, $"第{(index + 1).ToString(CultureInfo.InvariantCulture)}集.mp4")
                : renumber
                    ? Path.Combine(outputDirectory, stagedSeries ? $"第{episodeNumber}集.mp4" : $"{episodeNumber}.mp4")
                    : VisiblePathFor(project, originalsRoot, clips[slices[0].ClipIndex].Path);
            jobs.Add(new RenderJob(slices, destination));
        }

        var workRoot = Path.Combine(originalsRoot, ".work");
        var resumePath = Path.Combine(workRoot, ResumeFileName);
        var resumeIdentity = string.Empty;
        var completed = new System.Collections.Concurrent.ConcurrentDictionary<int, ResumeEpisode>();
        var processedCount = 0;
        if (directOutput is null)
        {
            Directory.CreateDirectory(workRoot);
            resumeIdentity = ResumeIdentity(preset, clips, originalsRoot);
            var resume = force ? null : ReadResume(resumePath);
            if (resume is null || !string.Equals(resume.Identity, resumeIdentity, StringComparison.Ordinal))
                ClearDirectory(workRoot);
            else
                foreach (var episode in resume.Episodes)
                    completed[episode.Index] = episode;
        }
        else
        {
            directOutput.PrepareOutput(jobs.Select(job => job.Destination).ToArray());
        }
        var resumeLock = new object();
        var hevc = preset.VideoCodec is VideoDedupPreset.CodecHevc or VideoDedupPreset.CodecH265;
        var softwareEncoder = hevc ? "libx265" : "libx264";
        string? currentEncoder = null;
        var failedEncoders = new HashSet<string>(StringComparer.Ordinal);
        using var encoderSwitch = new SemaphoreSlim(1);
        using var renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var compatibleEncoder = new Lazy<Task<EncoderChoice>>(() => ResolveEncoderAsync(preset, renderCancellation.Token));
        var hardwareTranscoder = RunProcessForTests is null &&
            OperatingSystem.IsWindows() && preset.EncodingPreference != VideoDedupPreset.EncodingSoftwareOnly
                ? new VideoDedupHardwareTranscoder(preset) : null;

        async Task<string> GetCompatibleEncoderAsync()
        {
            if (Volatile.Read(ref currentEncoder) is { } selected)
                return selected;
            var initial = await compatibleEncoder.Value.ConfigureAwait(false);
            var previous = Interlocked.CompareExchange(ref currentEncoder, initial.Encoder, null);
            if (previous is not null)
                return previous;
            Write($"兼容路径编码器：{initial.Encoder}（{initial.Device}）。{initial.Note}");
            return initial.Encoder;
        }

        async Task<string> SwitchEncoderAsync(string failedEncoder, string failure, CancellationToken token)
        {
            await encoderSwitch.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref currentEncoder) is { } selected &&
                    !string.Equals(failedEncoder, selected, StringComparison.Ordinal))
                    return selected;

                failedEncoders.Add(failedEncoder);
                var replacement = softwareEncoder;
                foreach (var candidate in HardwareCandidates(preset, hevc))
                {
                    if (!failedEncoders.Contains(candidate)
                        && await EncoderWorksAsync(candidate, preset, token).ConfigureAwait(false))
                    {
                        replacement = candidate;
                        break;
                    }
                }

                Volatile.Write(ref currentEncoder, replacement);
                Write($"{failedEncoder} 硬件编码不可用（{FirstLine(failure)}），本次剩余剧集改用 {replacement}。");
                return replacement;
            }
            finally
            {
                encoderSwitch.Release();
            }
        }
        var logicalProcessors = Environment.ProcessorCount;
        var cpuPercent = VideoDedupCpuBudget.CurrentCpuPercent;
        var processorBudget = VideoDedupCpuBudget.ProcessorBudget(logicalProcessors, cpuPercent);
        var limitMode = VideoProcessingConcurrency.Current.Automatic ? "硬件自动" : "手动设置";
        Write($"本机 {logicalProcessors} 个逻辑处理器，去重 CPU 线程预算 {cpuPercent}%（{processorBudget} 个），TikTok 和快手共享视频处理上限 {encodeParallel} 路（{limitMode}）。");
        if (renumber && preset.TransitionsEnabled && plan.Any(group => group.Count > 1))
            Write("并集接缝使用转场；短于 0.8 秒的成片改为直接拼接。");
        var totalEpisodes = jobs.Count.ToString(CultureInfo.InvariantCulture);

        Exception? encodeFailure = null;
        void RecordEncodeFailure(Exception error)
        {
            if (Interlocked.CompareExchange(ref encodeFailure, error, null) is null)
                renderCancellation.Cancel();
        }

        try
        {
            using var series = VideoDedupEncodeGate.BeginSeries();
            await Parallel.ForEachAsync(
                jobs.Select((job, index) => (job, index)),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = RunProcessForTests is null ? logicalProcessors : encodeParallel,
                    CancellationToken = renderCancellation.Token,
                },
                async (entry, token) =>
                {
                    var temp = directOutput is not null ? entry.job.Destination : Path.Combine(
                        workRoot,
                        $"{(entry.index + 1).ToString("00", CultureInfo.InvariantCulture)}.mp4");
                    var seed = Math.Abs(clips[entry.job.Slices[0].ClipIndex].Path.GetHashCode(StringComparison.Ordinal));
                    var episode = (entry.index + 1).ToString(CultureInfo.InvariantCulture);
                    if (completed.TryGetValue(entry.index, out var saved) &&
                        saved.Length > 0 &&
                        File.Exists(temp) &&
                        new FileInfo(temp).Length == saved.Length)
                    {
                        entry.job.TempPath = temp;
                        progress?.Invoke(Interlocked.Increment(ref processedCount), jobs.Count);
                        Write($"第 {episode}/{totalEpisodes} 集已完成，跳过编码。");
                        return;
                    }

                    try
                    {
                        var clock = Stopwatch.StartNew();
                        if (!force && !renumber && reusableEpisodes?.Count == jobs.Count &&
                            reusableEpisodes[entry.index] is { } reusablePath && File.Exists(reusablePath))
                        {
                            File.Copy(reusablePath, temp, overwrite: true);
                            var reusedLength = new FileInfo(temp).Length;
                            lock (resumeLock)
                            {
                                completed[entry.index] = new ResumeEpisode(entry.index, reusedLength);
                                WriteResume(resumePath, resumeIdentity, completed.Values.OrderBy(item => item.Index).ToArray());
                            }
                            entry.job.TempPath = temp;
                            progress?.Invoke(Interlocked.Increment(ref processedCount), jobs.Count);
                            Write($"第 {episode}/{totalEpisodes} 集复用已校验成片。");
                            return;
                        }
                        Write($"第 {episode}/{totalEpisodes} 集开始去重：{DescribeJob(clips, entry.job.Slices, entry.job.Destination, preset)}");
                        var slices = entry.job.Slices;
                        if (VideoDedupPlanner.UsesSourceVideoBitrate(preset))
                        {
                            var (target, fallback) = VideoDedupPlanner.ResolveVideoBitrate(preset, clips, slices);
                            var note = fallback ? $"；缺少视频流码率的片段使用手动值 {Num(preset.VideoBitrateMbps)} Mbps" : "";
                            Write($"第 {episode}/{totalEpisodes} 集原视频码率目标 {Num(target)} Mbps{note}。");
                        }
                        // Always take the shared slot before acquiring a GPU device lease.
                        // A waiting series must not block the preferred series on that GPU.
                        await WithEncodeSlotAsync(series, async () =>
                        {
                            if (hardwareTranscoder is { HasDevices: true } &&
                                VideoDedupPlanner.GetHardwareFrame(preset, clips, slices) is { } frame &&
                                await hardwareTranscoder.TryRenderAsync(
                                    clips[slices[0].ClipIndex], slices[0], frame, temp,
                                    static () => VideoProcessingConcurrency.Current,
                                    (args, timeout, cancellation) => VideoDedupHardwareTranscoder.RunTimedAsync(
                                        FfmpegLocator.ResolveFfmpeg(), args, timeout, cancellation),
                                    cancellation => CheckHardwareOutputAsync(temp, clips[slices[0].ClipIndex].Probe,
                                        frame, preset, cancellation),
                                    message => Write($"第 {episode}/{totalEpisodes} 集 {message}"), token)
                                    .ConfigureAwait(false))
                                return;

                            var jobEncoder = await GetCompatibleEncoderAsync().ConfigureAwait(false);
                            Write($"第 {episode}/{totalEpisodes} 集兼容转码 · CPU 解码和滤镜 · {jobEncoder}");
                            while (true)
                            {
                                var arguments = VideoDedupPlanner.BuildArguments(preset, clips, slices, temp, jobEncoder, seed);
                                try
                                {
                                    await RunFfmpegAsync(arguments, token).ConfigureAwait(false);
                                    if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                                        throw new InvalidOperationException($"去重未生成有效文件：{Path.GetFileName(entry.job.Destination)}");
                                    return;
                                }
                                catch (InvalidOperationException ex) when (
                                    AllowsCpuFallback(preset) && IsHardwareEncoderFailure(jobEncoder, ex.Message))
                                {
                                    jobEncoder = await SwitchEncoderAsync(jobEncoder, ex.Message, token).ConfigureAwait(false);
                                    Write($"第 {episode}/{totalEpisodes} 集改用 {jobEncoder} 重新去重。");
                                }
                            }
                        }, token, RecordEncodeFailure).ConfigureAwait(false);

                        var length = new FileInfo(temp).Length;
                        if (directOutput is null)
                        {
                            lock (resumeLock)
                            {
                                completed[entry.index] = new ResumeEpisode(entry.index, length);
                                WriteResume(resumePath, resumeIdentity, completed.Values.OrderBy(item => item.Index).ToArray());
                            }
                        }

                        entry.job.TempPath = temp;
                        progress?.Invoke(Interlocked.Increment(ref processedCount), jobs.Count);
                        Write($"第 {episode}/{totalEpisodes} 集去重完成，输出 {Path.GetFileName(entry.job.Destination)}，{FormatSize(length)}，耗时 {Num(clock.Elapsed.TotalSeconds)} 秒。");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Write($"第 {episode}/{totalEpisodes} 集去重失败：{ex.Message}");
                        RecordEncodeFailure(ex);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (encodeFailure is not null || ct.IsCancellationRequested)
        {
            if (encodeFailure is null)
                throw;
        }

        if (encodeFailure is not null)
            throw encodeFailure;

        if (directOutput is not null)
            return;

        PublishOutputs(project, visible, fingerprint, jobs);
        if (TikTokUploadStagingService.DiscardCopies(project))
            log?.Invoke("已清除旧的上传副本，后续修复会按新成片重建。");
        var outputs = jobs.Select(job => new FingerprintOutput(
            ToRelative(project, job.Destination),
            new FileInfo(job.Destination).Length)).ToArray();
        WriteFingerprint(fingerprintPath, preset.Fingerprint(), outputs);
        SyncEpisodeCount(item, project, inputs.Count, outputs.Length, renumber, log);
        ClearDirectory(workRoot);
        log?.Invoke($"视频去重完成：{inputs.Count.ToString(CultureInfo.InvariantCulture)} 个原片生成 {outputs.Length.ToString(CultureInfo.InvariantCulture)} 集，原片保留在 source-originals。");
    }

    private static string DescribeOperations(VideoDedupPreset preset)
    {
        var resolution = preset.KeepsOriginalResolution
            ? "分辨率保持裁剪后的尺寸，短边不足 1080 时放大回源尺寸，源片不足 1080 时放到 1080×1920"
            : Math.Min(preset.PortraitWidth, preset.PortraitHeight) < TikTokVideoConstraints.MinShortSide
                ? "分辨率按 1080×1920 输出，横屏改为 1920×1080"
                : $"分辨率 {preset.PortraitWidth.ToString(CultureInfo.InvariantCulture)}×{preset.PortraitHeight.ToString(CultureInfo.InvariantCulture)}，横屏对调宽高";
        var parts = new List<string>
        {
            $"上裁边 {Num(preset.CropTopPercent)}%",
            $"下裁边 {Num(preset.CropBottomPercent)}%",
            $"左裁边 {Num(preset.CropLeftPercent)}%",
            $"右裁边 {Num(preset.CropRightPercent)}%",
            $"变速 {Num(preset.SpeedFactor)} 倍",
            $"抽帧 {Num(preset.FrameDropPercent)}%",
            $"开头裁剪 {Num(preset.TrimStartSeconds)} 秒",
            $"结尾裁剪 {Num(preset.TrimEndSeconds)} 秒",
            resolution,
            $"{preset.Fps.ToString(CultureInfo.InvariantCulture)} fps",
            $"{DescribePreference(preset.EncodingPreference)}，{DescribeCodec(preset.VideoCodec)}，{DescribeRate(preset)}，音频 {DescribeAudio(preset)}",
        };
        parts.AddRange(VideoDedupEffectFilters.Describe(preset));
        if (Math.Abs(preset.AudioLowGainDb) > 0.01 || Math.Abs(preset.AudioMidGainDb) > 0.01 || Math.Abs(preset.AudioHighGainDb) > 0.01)
            parts.Add($"均衡器 低频 {Num(preset.AudioLowGainDb)} dB、中频 {Num(preset.AudioMidGainDb)} dB、高频 {Num(preset.AudioHighGainDb)} dB");
        parts.Add(preset.HasDurationLimit
            ? $"按总时长平均分集，最短 {Num(preset.MinimumDurationSeconds)} 秒，最长 {Num(preset.MaximumDurationSeconds)} 秒。全部原片先接成一条，再切成接近区间中间的等长集，相邻原片可以合在同一集。每一集再做开头、结尾裁剪和变速。转场把某一集拉出范围时向相邻集借时长。无法落在最短和最长之内就停止，不开始编码"
            : "不按单集时长重新分集");
        if (!preset.TransitionsEnabled || !preset.HasAnyTransition)
            parts.Add("转场关");
        else
        {
            var names = new List<string>();
            if (preset.TransitionFadeBlack) names.Add("淡黑");
            if (preset.TransitionDissolve) names.Add("交叉溶解");
            if (preset.TransitionPush) names.Add("推拉");
            if (preset.TransitionSlide) names.Add("滑动");
            if (preset.TransitionZoom) names.Add("缩放");
            parts.Add($"转场 {string.Join("、", names)}，每处 0.3 到 0.6 秒");
        }

        return string.Join("，", parts);
    }

    private static string DescribeJob(
        IReadOnlyList<VideoDedupClip> clips,
        IReadOnlyList<VideoDedupSlice> slices,
        string destination,
        VideoDedupPreset preset)
    {
        var output = Path.GetFileName(destination);
        var ranges = string.Join("、", slices.Select(slice =>
        {
            var clip = clips[slice.ClipIndex];
            var whole = Math.Abs(slice.Length - clip.Probe.DurationSeconds) < 0.05 && slice.StartSeconds <= 0.05;
            var range = whole ? "" : $" {Num(slice.StartSeconds)}–{Num(slice.EndSeconds)} 秒";
            return $"{Path.GetFileName(clip.Path)}（{DescribeProbe(clip)}）{range}";
        }));
        var join = slices.Count > 1
            ? VideoDedupPlanner.CanTransition(preset, slices) ? "，接缝使用转场" : "，直接拼接"
            : "";
        var shared = VideoDedupPlanner.SharedEpisodeFrame(clips, slices, preset);
        var frames = shared is { } forced
            ? [$"{forced.Width.ToString(CultureInfo.InvariantCulture)}×{forced.Height.ToString(CultureInfo.InvariantCulture)}"]
            : slices
                .Select(slice => clips[slice.ClipIndex].Probe)
                .Select(probe => VideoDedupPlanner.ResolveUploadFrame(probe.Width, probe.Height, preset))
                .Where(frame => frame.Width > 0 && frame.Height > 0)
                .Distinct()
                .Select(frame => $"{frame.Width.ToString(CultureInfo.InvariantCulture)}×{frame.Height.ToString(CultureInfo.InvariantCulture)}")
                .ToArray();
        var outputSize = frames.Length == 0 ? "" : $"，成片 {string.Join("、", frames)}";
        return $"{ranges} → {output}{join}{outputSize}。";
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
        if (VideoDedupPlanner.UsesSourceVideoBitrate(preset))
            return $"{preset.BitrateMode.ToUpperInvariant()} 按原视频码率重新编码";
        if (string.Equals(preset.BitrateMode, VideoDedupPreset.BitrateCbr, StringComparison.Ordinal))
            return $"CBR {Num(preset.VideoBitrateMbps)} Mbps";
        return $"VBR {Num(preset.VideoBitrateMbps)} Mbps，最大码率 {Num(preset.VideoBitrateMbps * 1.45)} Mbps";
    }

    private static string DescribeAudio(VideoDedupPreset preset)
    {
        if (string.Equals(preset.AudioCodec, VideoDedupPreset.AudioDefault, StringComparison.Ordinal))
            return "默认";
        if (preset.AudioBitrateKbps <= 0)
            return "AAC 源码率";
        return $"AAC {preset.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture)} kbps";
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

    private static void EnsureMinimumEpisodeCount(int count, int minimum)
    {
        if (count < minimum)
            throw new InvalidOperationException(
                $"预计去重后 {count} 集，低于系统设置的最小集数 {minimum}，未开始编码。");
    }

    private sealed record DedupInput(string ArchivePath, string ReadPath);

    private static (List<DedupInput> Inputs, List<DedupInput> ArchiveCopies) ResolveInputs(
        string project,
        string originalsRoot,
        IReadOnlyList<string> visible,
        IReadOnlyList<string> archived,
        DedupFingerprint? fingerprint)
    {
        if (archived.Count > 0 && fingerprint is null)
            return (archived.Select(path => new DedupInput(path, path)).ToList(), []);

        var inputs = archived.ToDictionary(
            path => path, path => new DedupInput(path, path),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var archiveCopies = new List<DedupInput>();
        foreach (var file in visible)
        {
            var relative = ToRelative(project, file);
            var archivePath = Path.Combine(originalsRoot, relative);
            if (archived.Count > 0)
            {
                var length = new FileInfo(file).Length;
                var recorded = fingerprint!.Outputs.FirstOrDefault(output =>
                    string.Equals(output.Path, relative, StringComparison.OrdinalIgnoreCase));
                if (recorded?.Length == length || (File.Exists(archivePath) && new FileInfo(archivePath).Length == length))
                    continue;
            }

            var input = new DedupInput(archivePath, file);
            archiveCopies.Add(input);
            if (IsArchivedVideo(relative))
                inputs[archivePath] = input;
        }

        return (inputs.Values.OrderBy(input => NaturalKey(input.ArchivePath), StringComparer.OrdinalIgnoreCase).ToList(),
            archiveCopies);
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

    private static void CopyToArchive(string file, string destination)
    {
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
            .Where(path => IsArchivedVideo(Path.GetRelativePath(originalsRoot, path)))
            .OrderBy(NaturalKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsArchivedVideo(string relative) =>
        !relative.StartsWith(".work", StringComparison.OrdinalIgnoreCase) &&
        !relative.Contains($"{Path.DirectorySeparatorChar}.work{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
        IsVideo(relative);

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

        return ReadProbeJson(stdout);
    }

    internal static VideoDedupProbe ReadProbeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
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
        var audioBitrateKbps = 0;
        var averageFramesPerSecond = 0d;
        var videoDuration = 0d;
        var audioDuration = 0d;
        var frameCount = 0L;
        var videoBitrateBps = 0L;
        VideoDedupSourceFormat? videoFormat = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            static string CodecType(JsonElement stream) =>
                stream.TryGetProperty("codec_type", out var type) ? type.GetString() ?? "" : "";
            static double TrackDuration(JsonElement stream) =>
                stream.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                    ? seconds : 0;
            var videos = streams.EnumerateArray().Count(stream => CodecType(stream) == "video");
            var audios = streams.EnumerateArray().Count(stream => CodecType(stream) == "audio");
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = CodecType(stream);
                if (string.Equals(codecType, "video", StringComparison.Ordinal) && width == 0)
                {
                    width = stream.TryGetProperty("width", out var widthElement) ? widthElement.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var heightElement) ? heightElement.GetInt32() : 0;
                    if (stream.TryGetProperty("avg_frame_rate", out var rate))
                        averageFramesPerSecond = ParseFrameRate(rate.GetString());
                    videoFormat = VideoDedupSourceFormat.Read(stream, videos, audios);
                    videoDuration = TrackDuration(stream);
                    videoBitrateBps = ReadStreamBitrateBps(stream);
                    if (stream.TryGetProperty("nb_frames", out var frames) && frames.ValueKind == JsonValueKind.String)
                        long.TryParse(frames.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out frameCount);
                }
                else if (string.Equals(codecType, "audio", StringComparison.Ordinal))
                {
                    if (!hasAudio)
                        audioDuration = TrackDuration(stream);
                    hasAudio = true;
                    if (audioBitrateKbps == 0)
                        audioBitrateKbps = ReadAudioBitrateKbps(stream);
                }
            }
        }

        return new VideoDedupProbe(duration, width, height, hasAudio, audioBitrateKbps, averageFramesPerSecond,
            videoFormat, videoDuration, audioDuration, frameCount, videoBitrateBps);
    }

    private static async Task<bool> CheckHardwareOutputAsync(
        string path, VideoDedupProbe input, VideoDedupPlanner.HardwareFrame frame, VideoDedupPreset preset, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return false;
        var output = await ProbeAsync(path, ct).ConfigureAwait(false);
        var codec = preset.VideoCodec is VideoDedupPreset.CodecHevc or VideoDedupPreset.CodecH265 ? "hevc" : "h264";
        var sameRate = Math.Abs(input.AverageFramesPerSecond - preset.Fps) <= 0.001;
        if (sameRate && input.FrameCount > 0 && output.FrameCount > 0 &&
            Math.Abs(output.FrameCount - input.FrameCount) > 1)
            return false;
        if (input.VideoDuration > 0 && input.AudioDuration > 0 && output.VideoDuration > 0 && output.AudioDuration > 0 &&
            Math.Abs((output.VideoDuration - output.AudioDuration) - (input.VideoDuration - input.AudioDuration)) > 0.125)
            return false;
        return output.Width == frame.Size.Width && output.Height == frame.Size.Height &&
            output.HasAudio == input.HasAudio && Math.Abs(output.AverageFramesPerSecond - preset.Fps) <= 0.02 &&
            Math.Abs(output.DurationSeconds - input.DurationSeconds) <= 0.25 &&
            output.Format is { PixelFormat: "yuv420p", Range: "tv", ColorSpace: "bt709", Transfer: "bt709", Primaries: "bt709" } format &&
            format.Codec == codec && format.SampleAspectRatio == frame.AspectRatio;
    }

    public static async Task<VideoDedupHardwareBenchmark> BenchmarkHardwareAsync(
        int maximum, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var transcoder = new VideoDedupHardwareTranscoder(VideoDedupCalibrationSample.CreatePreset());
        if (!transcoder.HasDevices)
            throw new InvalidOperationException("本机未检测到可用于硬件转码校准的显卡。");
        var samplePrefix = Path.Combine(Path.GetTempPath(), $"tiktok-dedup-calibration-{Guid.NewGuid():N}");
        var h264 = samplePrefix + ".h264.mp4";
        var hevc = samplePrefix + ".hevc.mp4";
        if (!VideoDedupEncodeGate.TryBeginCalibration())
            throw new InvalidOperationException("当前有去重任务或校准在运行，请在队列空闲时再校准。");
        try
        {
            var ffmpeg = FfmpegLocator.ResolveFfmpeg();
            await VideoDedupCalibrationSample.CreateAsync(ffmpeg, "libx264", h264, ct).ConfigureAwait(false);
            var clips = new List<VideoDedupClip>();
            try
            {
                await VideoDedupCalibrationSample.CreateAsync(ffmpeg, "libx265", hevc, ct).ConfigureAwait(false);
                clips.Add(new(hevc, await ProbeAsync(hevc, ct).ConfigureAwait(false)));
            }
            catch (InvalidOperationException) when (!ct.IsCancellationRequested)
            {
                // Builds without the software HEVC encoder can still calibrate H.264.
            }
            clips.Add(new(h264, await ProbeAsync(h264, ct).ConfigureAwait(false)));
            return await transcoder.BenchmarkAsync(clips,
                VideoDedupCpuBudget.Normalize(maximum, Environment.ProcessorCount), ct).ConfigureAwait(false);
        }
        finally
        {
            VideoDedupEncodeGate.EndCalibration();
            DeleteCalibrationSample(h264);
            DeleteCalibrationSample(hevc);
        }
    }

    private static void DeleteCalibrationSample(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static double ParseFrameRate(string? rate)
    {
        var parts = rate?.Split('/');
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator)
            || numerator <= 0 || denominator <= 0)
            return 0;
        return (double)numerator / denominator;
    }

    private static int ReadAudioBitrateKbps(JsonElement stream)
    {
        var bps = ReadStreamBitrateBps(stream);
        return bps > 0 ? (int)Math.Clamp(Math.Round(bps / 1000d, MidpointRounding.AwayFromZero), 1, 512) : 0;
    }

    private static long ReadStreamBitrateBps(JsonElement stream)
    {
        if (!stream.TryGetProperty("bit_rate", out var node))
            return 0;
        var raw = node.ValueKind == JsonValueKind.String ? node.GetString() : node.GetRawText();
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps) || bps <= 0)
            return 0;
        return bps;
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
            if (await EncoderWorksAsync(candidate, preset, ct).ConfigureAwait(false))
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

        foreach (var candidate in VideoDedupHardwareOrder.Encoders(
                     hevc,
                     VideoDedupHardwareOrder.Detect(),
                     OperatingSystem.IsMacOS()))
            yield return candidate;
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

    internal static async Task<bool> HasUsableHardwareEncoderAsync(CancellationToken ct)
    {
        var preset = VideoDedupPreset.CreateDefault();
        foreach (var hevc in new[] { false, true })
        {
            preset.VideoCodec = hevc ? VideoDedupPreset.CodecHevc : VideoDedupPreset.CodecH264;
            foreach (var encoder in HardwareCandidates(preset, hevc))
            {
                ct.ThrowIfCancellationRequested();
                if (await EncoderWorksAsync(encoder, preset, ct).ConfigureAwait(false))
                    return true;
            }
        }
        return false;
    }

    private static async Task<bool> EncoderWorksAsync(string encoder, VideoDedupPreset preset, CancellationToken ct)
    {
        if (EncoderWorksForTests is not null)
            return await EncoderWorksForTests(encoder, ct).ConfigureAwait(false);
        var arguments = VideoDedupPlanner.BuildProbeArguments(preset, encoder);
        var key = string.Join('\u001f', arguments);
        if (EncoderProbeCache.TryGetValue(key, out var cached))
            return cached;

        var (code, _, _) = await FfmpegRunner.RunCaptureAsync(
            FfmpegLocator.ResolveFfmpeg(),
            arguments.ToArray(),
            ct).ConfigureAwait(false);
        var works = code == 0;
        EncoderProbeCache[key] = works;
        return works;
    }

    private static async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (RunProcessForTests is not null)
        {
            await RunProcessForTests(arguments, ct).ConfigureAwait(false);
            return;
        }

        await FfmpegRunner.RunAsync(FfmpegLocator.ResolveFfmpeg(), arguments, ct, belowNormal: true)
            .ConfigureAwait(false);
    }

    private static async Task WithEncodeSlotAsync(VideoDedupEncodeGate.Series series, Func<Task> action,
        CancellationToken ct, Action<Exception> stopSeries)
    {
        if (RunProcessForTests is not null)
        {
            await action().ConfigureAwait(false);
            return;
        }
        var lane = await VideoDedupEncodeGate.WaitAsync(series, ct).ConfigureAwait(false);
        VideoDedupCpuBudget.UseLane(lane);
        try
        {
            ct.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopSeries(ex);
            throw;
        }
        finally
        {
            VideoDedupCpuBudget.UseLane(0);
            VideoDedupEncodeGate.Release(lane);
        }
    }

    private static async Task<int> ApplyGlobalConcurrencyAsync(CancellationToken ct)
    {
        var logical = Math.Max(1, Environment.ProcessorCount);
        if (RunProcessForTests is not null)
        {
            VideoDedupCpuBudget.Configure(VideoDedupCpuBudget.ConcurrencyForTests
                ?? VideoDedupCpuBudget.DefaultConcurrency, logical);
            return VideoDedupCpuBudget.CurrentConcurrency;
        }

        return (await VideoProcessingConcurrency.EnsureAppliedAsync(ct).ConfigureAwait(false)).Limit;
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    internal static bool IsHardwareEncoderFailure(string encoder, string message)
    {
        if (!VideoDedupPlanner.IsHardwareEncoder(encoder))
            return false;

        return message.Split('\n').Any(line =>
            line.Contains(encoder + " @", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("Cannot load", StringComparison.OrdinalIgnoreCase)
                || line.Contains("No capable devices", StringComparison.OrdinalIgnoreCase)
                || line.Contains("No NVENC capable devices", StringComparison.OrdinalIgnoreCase)
                || line.Contains("CUDA_ERROR_", StringComparison.OrdinalIgnoreCase)
                || line.Contains("OpenEncodeSessionEx failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("required nvenc API version", StringComparison.OrdinalIgnoreCase)
                || line.Contains("CreateComponent", StringComparison.OrdinalIgnoreCase)
                   && line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("initializing an internal MFX session", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Error creating a MFX session", StringComparison.OrdinalIgnoreCase)
                || line.Contains("No device available for encoder", StringComparison.OrdinalIgnoreCase)
                || line.Contains("cannot create compression session", StringComparison.OrdinalIgnoreCase)));
    }

    private static void SyncEpisodeCount(
        QueueProjectItem item,
        string project,
        int sourceCount,
        int outputCount,
        bool renumber,
        Action<string>? log)
    {
        var previousCount = item.EpisodeCount;
        if (item.SourceEpisodeCount < sourceCount)
            item.SourceEpisodeCount = sourceCount;
        item.EpisodeCount = outputCount;
        if (!renumber && outputCount == sourceCount && previousCount == outputCount)
            return;

        var sourceDir = project;
        var workflowDir = project;
        try
        {
            var context = ProjectWorkspaceService.LoadContext(project);
            sourceDir = context.SourceProjectDir;
            workflowDir = context.WorkflowProjectDir;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            log?.Invoke($"项目目录未能分开源目录和工作目录，集数写在当前目录：{ex.Message}");
        }

        UpdateDownloadState(sourceDir, project, sourceCount, outputCount, renumber, log);
        UpdateDeclaredEpisodeCount(sourceDir, outputCount, log);
        if (!string.Equals(workflowDir, sourceDir, StringComparison.OrdinalIgnoreCase))
            UpdateDeclaredEpisodeCount(workflowDir, outputCount, log);
        var reset = ResetStepsAfterDedup(item);
        log?.Invoke($"已按新的 {outputCount.ToString(CultureInfo.InvariantCulture)} 集更新下载记录、短剧信息和项目集数。");
        if (reset > 0)
            log?.Invoke($"集数已变化，去重之后 {reset.ToString(CultureInfo.InvariantCulture)} 个已完成步骤会按新成片重新执行。");
    }

    private static void UpdateDownloadState(
        string sourceDir,
        string project,
        int sourceCount,
        int outputCount,
        bool renumber,
        Action<string>? log)
    {
        if (outputCount <= 0)
            return;
        var path = Path.Combine(sourceDir, DownloadStateFileName);
        if (!File.Exists(path))
            path = Path.Combine(project, DownloadStateFileName);
        if (!File.Exists(path))
            return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node is null)
                return;
            if (!node.ContainsKey("source_video_count"))
                node["source_video_count"] = sourceCount;
            node["video_count"] = outputCount;
            if (renumber)
            {
                node["episode_number_mode"] = "continuous";
                var mappings = new JsonArray();
                for (var episode = 1; episode <= outputCount; episode++)
                {
                    mappings.Add(new JsonObject
                    {
                        ["source_episode_number"] = episode,
                        ["sequence_episode_number"] = episode,
                        ["episode_title"] = $"第{episode.ToString(CultureInfo.InvariantCulture)}集",
                    });
                }

                node["episode_mappings"] = mappings;
            }

            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"下载记录集数未更新：{ex.Message}");
        }
    }

    private static void UpdateDeclaredEpisodeCount(string directory, int outputCount, Action<string>? log)
    {
        if (outputCount <= 0 || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        var infoPath = Path.Combine(directory, "短剧信息.txt");
        try
        {
            if (File.Exists(infoPath))
            {
                ProjectWorkspaceService.UpdateProjectInfoField(infoPath, "集数", outputCount.ToString(CultureInfo.InvariantCulture));
                UpdateProjectInfoFieldIfPresent(infoPath, "总集数", outputCount);
                UpdateProjectInfoFieldIfPresent(infoPath, "剧集数", outputCount);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"短剧信息集数未更新：{ex.Message}");
        }

        var metadataPath = Path.Combine(directory, "shortdrama-project.json");
        if (!File.Exists(metadataPath))
            return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
            if (root is null)
                return;
            var count = JsonValue.Create(outputCount);
            foreach (var key in new[] { "episodeCount", "episode_count", "effectiveEpisodeCount", "effective_episode_count" })
                root[key] = count?.DeepClone();
            var truncated = ReadBool(root, "truncatedForTikTokUpload") || ReadBool(root, "truncated_for_tiktok_upload");
            if (truncated || root.ContainsKey("downloadEpisodeLimit") || root.ContainsKey("download_episode_limit"))
            {
                root["downloadEpisodeLimit"] = count?.DeepClone();
                root["download_episode_limit"] = count?.DeepClone();
            }

            File.WriteAllText(metadataPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"项目集数未更新：{ex.Message}");
        }
    }

    private static void UpdateProjectInfoFieldIfPresent(string infoPath, string key, int outputCount)
    {
        var lines = File.ReadAllLines(infoPath);
        if (!lines.Any(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith(key + ":", StringComparison.Ordinal) ||
                       trimmed.StartsWith(key + "：", StringComparison.Ordinal);
            }))
        {
            return;
        }

        ProjectWorkspaceService.UpdateProjectInfoField(infoPath, key, outputCount.ToString(CultureInfo.InvariantCulture));
    }

    private static bool ReadBool(JsonObject root, string key)
    {
        if (root[key] is not JsonValue value)
            return false;
        if (value.TryGetValue<bool>(out var flag))
            return flag;
        return value.TryGetValue<string>(out var text) &&
               bool.TryParse(text, out flag) &&
               flag;
    }

    private static int ResetStepsAfterDedup(QueueProjectItem item)
    {
        var uploadWasCompleted = item.IsUploadCompleted;
        var reset = 0;
        var afterDedup = false;
        foreach (var step in QueueStepRegistry.All)
        {
            if (!afterDedup)
            {
                afterDedup = string.Equals(step.Key, QueueStepRegistry.VideoDedup, StringComparison.Ordinal);
                continue;
            }

            if (item.StepStates.GetValueOrDefault(step.Key) != QueueStepStatus.Completed)
                continue;
            item.StepStates[step.Key] = QueueStepStatus.Pending;
            reset++;
        }

        if (uploadWasCompleted)
            item.StatusText = QueueStepStatus.Pending;
        return reset;
    }

    private static string ResumeIdentity(VideoDedupPreset preset, IReadOnlyList<VideoDedupClip> clips, string originalsRoot)
    {
        var files = string.Join(
            "\n",
            clips.Select(clip =>
            {
                var relative = Path.GetRelativePath(originalsRoot, clip.Path);
                var length = new FileInfo(clip.Path).Length;
                return string.Join(
                    "\t",
                    relative,
                    length.ToString(CultureInfo.InvariantCulture),
                    clip.Probe.DurationSeconds.ToString("0.####", CultureInfo.InvariantCulture));
            }));
        return "square-pixel-v1\n" + preset.Fingerprint() + "\n" + files;
    }

    private static DedupResume? ReadResume(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var resume = JsonSerializer.Deserialize<DedupResume>(File.ReadAllText(path));
            if (resume is null || string.IsNullOrWhiteSpace(resume.Identity))
                return null;
            return resume with { Episodes = resume.Episodes ?? [] };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteResume(string path, string identity, IReadOnlyList<ResumeEpisode> episodes)
    {
        var payload = new DedupResume(identity, episodes.ToList());
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
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

    private sealed class RenderJob(IReadOnlyList<VideoDedupSlice> slices, string destination)
    {
        public IReadOnlyList<VideoDedupSlice> Slices { get; } = slices;
        public string Destination { get; } = destination;
        public string TempPath { get; set; } = "";
    }

    private sealed record DedupFingerprint(string Preset, List<FingerprintOutput> Outputs);

    private sealed record FingerprintOutput(string Path, long Length);

    private sealed record DedupResume(string Identity, List<ResumeEpisode> Episodes);

    private sealed record ResumeEpisode(int Index, long Length);
}
