using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChannelsPublisher.Clip;

public sealed partial class ClipEngine
{
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private async Task<ClipEngineResult> GenerateConfiguredAsync(
        string projectDir, IReadOnlyList<EpisodeSource> episodes, ClipEngineOptions options,
        Action<string>? log, CancellationToken ct, Action<string>? outputReady)
    {
        var outputs = new List<string>();
        var issues = new List<string>();
        void Report(string text)
        {
            issues.Add(text);
            log?.Invoke(text);
        }

        try
        {
            var modes = options.ModeSettings!;
            if (modes.Count == 0 || episodes.Count == 0)
                return new ClipEngineResult(false, outputs, "请导入素材并至少启用一种剪辑模式。");

            var root = Path.GetFullPath(string.IsNullOrWhiteSpace(options.OutputDirectory)
                ? Path.Combine(projectDir, "素材剪辑输出") : options.OutputDirectory);
            var title = string.IsNullOrWhiteSpace(options.ProjectTitle)
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(projectDir)) : options.ProjectTitle.Trim();
            var fileTitle = SafeName(title.TrimEnd('.', ' '));
            var sourceSignature = SourceSignature(episodes, projectDir, options);
            var jobs = new List<ConfiguredClipJob>();
            foreach (var mode in modes)
            {
                var label = DisplayMode(mode.Mode);
                var maskVersion = mode.Mode == "commentary" && options.BurnSubtitles ? "|commentary-subtitle-mask-v2" : "";
                var storyVersion = mode.Mode == "highlight" && options.StoryHighlights ? "|story-highlight-v2" : "";
                var signature = Hash(sourceSignature + JsonSerializer.Serialize(mode with { ForceRegenerate = false }) + maskVersion + storyVersion);
                for (var index = 0; index < mode.Ranges.Count; index++)
                {
                    var path = Path.Combine(root, label, $"{fileTitle}-{label}-{index + 1:D2}.mp4");
                    var reusable = mode.ForceRegenerate ? null : ReadReusable(path, signature, options.PublishMetadata);
                    if (reusable is not null)
                    {
                        log?.Invoke($"复用 {Path.GetFileName(path)}");
                        outputs.Add(path);
                        outputReady?.Invoke(path);
                    }
                    jobs.Add(new ConfiguredClipJob(mode, mode.Ranges[index], index + 1, path, signature, reusable));
                }
            }

            if (jobs.All(job => job.Reusable is not null))
                return Finish();
            var needsStoryCuts = options.StoryHighlights && jobs.Any(job => job.Mode.Mode == "highlight" && job.Reusable is null);

            if ((options.EnableLlmScore || options.ArrangeNarrative) && !HasAi(options))
                log?.Invoke("AI 文本接口尚未配置，将使用本地评分和排序。");

            await VerifyToolsAsync(options, ct);
            var networkLimit = Math.Clamp(options.NetworkConcurrency > 0 ? options.NetworkConcurrency :
                options.AsrEngine == "local" ? Math.Max(options.PipelineConcurrency, 8) : options.PipelineConcurrency, 1, 32);
            using var network = new SemaphoreSlim(networkLimit);
            var analyzed = new ClipSourceAnalysis?[episodes.Count];
            var sourceIssues = new string?[episodes.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, episodes.Count), new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Clamp(options.PipelineConcurrency, 1, 16),
            }, async (index, token) =>
            {
                try
                {
                    analyzed[index] = await AnalyzeAsync(episodes[index], projectDir, root, options, needsStoryCuts, network, log, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    sourceIssues[index] = $"{Path.GetFileName(episodes[index].VideoPath)} 分析失败。{ex.Message}";
                }
            });
            foreach (var problem in sourceIssues.OfType<string>())
                Report(problem);
            var sources = analyzed.OfType<ClipSourceAnalysis>().ToArray();
            if (sources.Length == 0)
                return Finish("没有可读取的视频素材。");
            var media = sources.ToDictionary(source => source.Source.VideoPath, source => source.Media, StringComparer.OrdinalIgnoreCase);
            if (options.OrigEnabled && !string.IsNullOrWhiteSpace(options.OrigStickerDir) && !Directory.Exists(options.OrigStickerDir))
                Report("贴纸目录不存在，已跳过贴纸叠加。");
            var encoder = await ClipVideoEncoding.ResolveAsync(options, log, ct);
            var aiReady = HasAi(options);

            foreach (var group in jobs.GroupBy(job => job.Mode.Mode))
            {
                var storyMode = needsStoryCuts && group.Key == "highlight";
                var timelines = storyMode ? sources.ToDictionary(source => source.Source.VideoPath,
                    source => new HighlightCutTimeline(source), StringComparer.OrdinalIgnoreCase) : null;
                var continuity = timelines is null ? null : new StoryHighlightContinuity(sources, timelines);
                var storyAiReady = storyMode && aiReady && sources.Any(source => source.HasDialogue);
                if (storyMode && !storyAiReady)
                    log?.Invoke(aiReady ? "剧情高光未取得对白，使用本地镜头切点选段。" : "剧情高光未配置 AI 接口，使用本地选段并保护对白切点。");
                var past = group.Select(job => job.Reusable?.Segments).OfType<List<ClipCandidate>>().ToList();
                foreach (var job in group.Where(job => job.Reusable is null))
                {
                    ct.ThrowIfCancellationRequested();
                    var label = $"{DisplayMode(group.Key)}第 {job.Index} 条";
                    try
                    {
                        if (group.Key == "commentary" && !aiReady)
                            throw new InvalidOperationException("请配置 AI 文本接口后生成解说。");
                        List<ClipCandidate>? segments = null;
                        if (storyAiReady && timelines is not null && continuity is not null)
                        {
                            try
                            {
                                var candidates = StoryHighlightPlanner.Candidates(sources, timelines, job.Range, past, options, continuity);
                                if (candidates.Count >= 2 && candidates.Sum(clip => (long)clip.DurationMs) >= job.Range.MinSeconds * 1000L)
                                {
                                    var (order, reason) = await new LlmScorer().SelectStoryHighlightsAsync(
                                        candidates, job.Range, past, job.Mode.SimilarityCap, options, continuity, ct);
                                    segments = StoryHighlightPlanner.Validate(order, candidates, job.Range, past, job.Mode.SimilarityCap,
                                        clips => continuity.Improve(clips, job.Range, past, job.Mode.SimilarityCap, options, log));
                                    var episodeCount = segments.Select(clip => clip.EpisodeIndex).Distinct().Count();
                                    log?.Invoke($"{label} 采用 AI 剧情选段，覆盖 {episodeCount} 集。{reason}");
                                }
                                else
                                    log?.Invoke($"{label} 的 AI 候选不足，使用本地选段并保护对白切点。");
                            }
                            catch (Exception ex) when (!ct.IsCancellationRequested)
                            {
                                if (ex is not InvalidDataException)
                                    storyAiReady = false;
                                log?.Invoke($"{label} 的 AI 剧情选段失败，使用本地选段并保护对白切点。{ex.Message}");
                            }
                        }
                        if (segments is null)
                        {
                            segments = ConfiguredClipPlanner.Plan(job.Mode, job.Range, sources, past, options, timelines);
                            if (continuity is not null)
                                segments = continuity.Improve(segments, job.Range, past, job.Mode.SimilarityCap, options, log);
                        }
                        if (segments.Count == 0)
                            throw new InvalidOperationException(group.Key == "commentary" && !sources.Any(s => s.HasDialogue)
                                ? "解说需要素材字幕或有效的 ASR 结果。"
                                : $"可用素材无法满足 {job.Range.MinSeconds}–{job.Range.MaxSeconds} 秒{(storyMode ? "、对白保护" : "")}和相似度上限。");
                        if (options.ArrangeNarrative && aiReady && !storyMode && group.Key != "slice")
                        {
                            try { segments = await new LlmScorer().ArrangeAsync(segments, options, ct); }
                            catch (Exception ex) when (!ct.IsCancellationRequested)
                            {
                                log?.Invoke($"{label} AI 重排失败，保留已选顺序。{ex.Message}");
                            }
                        }
                        var narration = group.Key == "commentary"
                            ? await new CommentaryScripter().BuildAsync(segments, options, title, log, ct)
                            : null;
                        log?.Invoke($"{label} 正在渲染，预选时长 {segments.Sum(c => c.DurationMs) / 1000d:0.##} 秒。");
                        await RenderOutputAsync(job, segments, media, narration, options, fileTitle, encoder, networkLimit, log, ct);
                        past.Add(segments);
                        outputs.Add(job.Path);
                        outputReady?.Invoke(job.Path);
                        log?.Invoke($"{label} 完成 · {job.Path}");
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        Report($"{label} 未出片。{ex.Message}");
                    }
                }
            }
            return Finish();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Report(ex.Message);
            return Finish(ex.Message);
        }

        ClipEngineResult Finish(string? failure = null) => new(outputs.Count > 0, outputs.ToArray(),
            outputs.Count > 0 ? null : failure ?? issues.FirstOrDefault() ?? "没有生成成片。") { Issues = issues.ToArray() };
    }

    private async Task<ClipSourceAnalysis> AnalyzeAsync(
        EpisodeSource source, string projectDir, string outputRoot, ClipEngineOptions options, bool needsStoryCuts, SemaphoreSlim network,
        Action<string>? log, CancellationToken ct)
    {
        var name = Path.GetFileName(source.VideoPath);
        var media = await Ffmpeg.ProbeVideoAsync(options.FfprobePath, source.VideoPath, ct);
        var subtitles = await ClipSubtitles.ReadAsync(source.VideoPath, projectDir, media, options, ct);
        if (subtitles.Count == 0 && media.HasAudio)
        {
            try
            {
                subtitles = await ReadOrTranscribeAsync(source, media, outputRoot, options, network, log, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"{name} 未取得识别字幕，使用视频时间轴选段。{ex.Message}");
            }
        }
        var candidates = CandidateBuilder.Build(source, subtitles, media.DurationMs);
        if (candidates.Count == 0 && subtitles.Count > 0)
            candidates = subtitles.Select(subtitle => new ClipCandidate
            {
                VideoPath = source.VideoPath, EpisodeIndex = source.EpisodeIndex, StartMs = subtitle.StartMs,
                EndMs = subtitle.EndMs, Text = subtitle.Text, Total = 1,
            }).ToList();
        if (candidates.Count == 0)
        {
            const int block = 10000;
            candidates = Enumerable.Range(0, (media.DurationMs - 1) / block + 1)
                .Select(index => new ClipCandidate
                {
                    VideoPath = source.VideoPath, EpisodeIndex = source.EpisodeIndex, StartMs = index * block,
                    EndMs = Math.Min(media.DurationMs, index * block + block), Total = 1,
                }).ToList();
        }
        if (options.AudioEnergy && media.HasAudio)
            await AudioEnergy.ApplyAsync(candidates, options.FfmpegPath, source.VideoPath, log, ct);
        var cuts = options.ShotDensity || needsStoryCuts
            ? await ClipShotCuts.ReadAsync(source.VideoPath, media.DurationMs, outputRoot, options.FfmpegPath, needsStoryCuts, log, ct)
            : [];
        if (options.ShotDensity)
        {
            foreach (var candidate in candidates)
                candidate.ShotDensity = Math.Clamp(cuts.Count(cut => cut >= candidate.StartMs && cut < candidate.EndMs)
                    * 5000d / Math.Max(1, candidate.DurationMs), 0, 10);
        }
        if (options.EnableLlmScore && subtitles.Count > 0 && HasAi(options))
        {
            await network.WaitAsync(ct);
            try { await new LlmScorer().ApplyAsync(candidates, options, log, ct); }
            finally { network.Release(); }
        }
        SignalWeights.Apply(candidates);
        log?.Invoke($"{name} · 字幕 {subtitles.Count} 句，候选 {candidates.Count} 段");
        return new ClipSourceAnalysis(source, media, candidates, subtitles, cuts);
    }

    private async Task<List<SubtitleSegment>> ReadOrTranscribeAsync(
        EpisodeSource source, ClipMediaInfo media, string outputRoot, ClipEngineOptions options,
        SemaphoreSlim network, Action<string>? log, CancellationToken ct)
    {
        var cache = ClipSubtitles.CachePath(outputRoot, source.VideoPath, options);
        if (File.Exists(cache))
        {
            var cached = ClipSubtitles.Parse(await File.ReadAllTextAsync(cache, ct), media.DurationMs);
            if (cached.Count > 0)
                return cached;
        }
        if (options.AsrEngine == "volcengine" && !HasOnlineAsr(options))
            throw new InvalidOperationException("请在服务配置填写在线 ASR 凭据。");

        var wav = Path.Combine(Path.GetTempPath(), $"clip-asr-{Guid.NewGuid():N}.wav");
        List<SubtitleSegment> recognized;
        try
        {
            await Ffmpeg.ExtractAudioAsync(options.FfmpegPath, source.VideoPath, wav, ct);
            if (options.AsrEngine == "local")
                recognized = await RunAsrAsync(wav, options, log, ct);
            else
            {
                await network.WaitAsync(ct);
                try { recognized = await RunAsrAsync(wav, options, log, ct); }
                finally { network.Release(); }
            }
        }
        finally
        {
            File.Delete(wav);
        }

        var result = recognized.Where(segment => segment.StartMs >= 0 && segment.StartMs < media.DurationMs &&
                segment.EndMs > segment.StartMs && !string.IsNullOrWhiteSpace(segment.Text))
            .Select(segment => segment with { EndMs = Math.Min(segment.EndMs, media.DurationMs) }).OrderBy(segment => segment.StartMs).ToList();
        if (result.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var pending = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(pending, Srt.Write(result), ct);
                File.Move(pending, cache, overwrite: true);
            }
            finally { File.Delete(pending); }
        }
        return result;
    }

    private static async Task RenderOutputAsync(
        ConfiguredClipJob job, List<ClipCandidate> segments, IReadOnlyDictionary<string, ClipMediaInfo> media,
        List<NarrationLine>? narration, ClipEngineOptions options, string title, string encoder,
        int networkConcurrency, Action<string>? log, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(job.Path)!;
        var staging = Path.Combine(directory, ".pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var pending = Path.Combine(staging, Path.GetFileName(job.Path));
            var metadata = options.PublishMetadata ? await BuildPublishMetadataAsync(segments, options, title, log, ct) : null;
            var caption = FirstNonEmpty(metadata?.ShortTitle,
                segments.OrderByDescending(c => c.Total).Select(c => c.Title).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                title);
            await ConfiguredClipRenderer.RenderAsync(pending, segments, media, narration, options, caption, encoder, networkConcurrency, log, ct);
            var duration = await Ffmpeg.ProbeDurationSecondsAsync(options.FfprobePath, pending, ct);
            const double muxerTolerance = 0.12;
            if (duration < job.Range.MinSeconds - muxerTolerance || duration > job.Range.MaxSeconds + muxerTolerance)
                throw new InvalidDataException($"成片时长 {duration:0.##} 秒超出目标 {job.Range.MinSeconds}–{job.Range.MaxSeconds} 秒。");

            var manifest = new ConfiguredClipManifest(job.Signature, new FileInfo(pending).Length,
                segments.Select(c => new ClipCandidate
                {
                    VideoPath = c.VideoPath, EpisodeIndex = c.EpisodeIndex, StartMs = c.StartMs, EndMs = c.EndMs,
                }).ToList());
            await File.WriteAllTextAsync(ManifestPath(pending), JsonSerializer.Serialize(manifest, ManifestJson), ct);
            var sidecar = Path.ChangeExtension(pending, ".publish.json");
            if (metadata is not null)
                await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(metadata, ManifestJson), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(pending, job.Path, overwrite: true);
            if (options.PublishMetadata)
                File.Move(sidecar, Path.ChangeExtension(job.Path, ".publish.json"), overwrite: true);
            else
                File.Delete(Path.ChangeExtension(job.Path, ".publish.json"));
            File.Move(ManifestPath(pending), ManifestPath(job.Path), overwrite: true);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<ClipPublishMetadata> BuildPublishMetadataAsync(
        List<ClipCandidate> segments, ClipEngineOptions options, string title, Action<string>? log, CancellationToken ct)
    {
        if (HasAi(options) && segments.Any(c => !string.IsNullOrWhiteSpace(c.Text)))
        {
            try
            {
                var generated = await new LlmScorer().PublishMetadataAsync(segments, title, options, ct);
                if (generated is not null)
                    return generated;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"AI 发表文案生成失败，使用候选文案。{ex.Message}");
            }
        }
        var ranked = segments.OrderByDescending(c => c.Total).ToArray();
        var description = FirstNonEmpty(ranked.Select(c => FirstNonEmpty(c.RecommendReason, c.Summary)).FirstOrDefault(
            value => value.Length > 0), title);
        return ClipPublishMetadata.Create(description, FirstNonEmpty(ranked.Select(c => c.Title)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)), title), ranked.SelectMany(c => c.Tags));
    }

    private static ConfiguredClipManifest? ReadReusable(string path, string signature, bool metadata)
    {
        try
        {
            if (!File.Exists(path) || !File.Exists(ManifestPath(path)) ||
                (metadata && !File.Exists(Path.ChangeExtension(path, ".publish.json"))))
                return null;
            var manifest = JsonSerializer.Deserialize<ConfiguredClipManifest>(File.ReadAllText(ManifestPath(path)), ManifestJson);
            return manifest is not null && manifest.Signature == signature && manifest.Length == new FileInfo(path).Length &&
                manifest.Segments.Count > 0 ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task VerifyToolsAsync(ClipEngineOptions options, CancellationToken ct)
    {
        foreach (var (path, name) in new[] { (options.FfmpegPath, "FFmpeg"), (options.FfprobePath, "FFprobe") })
        {
            try { await Ffmpeg.RunAsync(path, ["-version"], ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"无法运行 {name}，请检查剪辑服务配置中的路径。{ex.Message}", ex);
            }
        }
    }

    private static string SourceSignature(IReadOnlyList<EpisodeSource> episodes, string projectDir, ClipEngineOptions options)
    {
        var settings = JsonSerializer.SerializeToNode(options)!.AsObject();
        settings.Remove(nameof(ClipEngineOptions.ModeSettings));
        settings.Remove(nameof(ClipEngineOptions.StoryHighlights));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(settings.ToJsonString()));
        foreach (var source in episodes)
        {
            foreach (var path in ClipSubtitles.CandidateFiles(source.VideoPath, projectDir).Prepend(source.VideoPath))
            {
                var file = new FileInfo(path);
                if (file.Exists)
                    sha.AppendData(Encoding.UTF8.GetBytes($"{source.EpisodeIndex}|{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}\n"));
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static bool HasOnlineAsr(ClipEngineOptions opts) => opts.AsrProvider == "doubao_subtitle"
        ? !string.IsNullOrWhiteSpace(opts.DoubaoAppId) && !string.IsNullOrWhiteSpace(opts.DoubaoAccessToken)
        : !string.IsNullOrWhiteSpace(opts.VolcAppId) && !string.IsNullOrWhiteSpace(opts.VolcAccessToken);

    private static bool HasAi(ClipEngineOptions opts) => !string.IsNullOrWhiteSpace(opts.AiEndpoint) &&
        !string.IsNullOrWhiteSpace(opts.AiApiKey) && !string.IsNullOrWhiteSpace(opts.AiModel);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string ManifestPath(string path) => Path.ChangeExtension(path, ".material-clips.json");

    private static string DisplayMode(string mode) => mode switch
    {
        "highlight" => "高光",
        "mashup" => "混剪",
        "commentary" => "解说",
        "slice" => "切片",
        _ => throw new InvalidDataException($"未知剪辑模式 {mode}。"),
    };

    private sealed record ConfiguredClipManifest(string Signature, long Length, List<ClipCandidate> Segments);
    private sealed record ConfiguredClipJob(ClipModeOptions Mode, ClipTimeRange Range, int Index,
        string Path, string Signature, ConfiguredClipManifest? Reusable);
}
