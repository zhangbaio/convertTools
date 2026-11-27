using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChannelsPublisher.Clip;
using ShortDrama.Core.Interfaces;
using ShortDrama.Infrastructure;
using SixLabors.ImageSharp;

namespace PlatformPublisher.Weixin.Publishing;

public sealed partial class WeixinStoryboardRecordingService(
    IProjectScanner scanner, IWeixinBrowserRuntimeService browserRuntime, HttpClient? http = null)
{
    public const string StepKey = "storyboard-recording";
    public const string StepLabel = "生成分镜录屏";
    public const string OutputDirectoryName = "分镜录屏";
    private const string ManifestName = ".manifest.json";
    private const int MaxSeconds = 28;
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".m4v", ".flv", ".wmv", ".webm" };

    private sealed record FileStamp(long Length, long LastWriteTicks);
    private sealed record Manifest(string Signature, int[] Episodes, int[] RecordingEpisodes, Dictionary<string, FileStamp> Files);

    public async Task<StoryboardRecordingResult> GenerateAsync(string sourceDirectory, string title,
        StoryboardRecordingOptions options, StoryboardArkSettings? arkSettings, IProgress<string>? progress, CancellationToken ct)
    {
        StoryboardRecordingRules.ValidateSelection(options.Episodes);
        sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var parent = Directory.GetParent(sourceDirectory)?.FullName
            ?? throw new InvalidOperationException("无法定位分镜项目的工作根目录。");
        var scan = await scanner.ScanAsync(parent, null, ct).ConfigureAwait(false);
        var project = scan.Projects.FirstOrDefault(item =>
            string.Equals(Path.TrimEndingDirectorySeparator(item.SourceProjectDir), sourceDirectory, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未在工作根目录中找到该视频号项目。");
        var workflow = Path.GetFullPath(project.WorkflowProjectDir ?? Path.Combine(parent, "workflow", "_" + SafeName(project.DisplayName)));
        title = string.IsNullOrWhiteSpace(project.DisplayName) ? title : project.DisplayName;
        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileName(sourceDirectory);
        return await GenerateInWorkflowAsync(sourceDirectory, workflow, title, options, arkSettings, progress, ct)
            .ConfigureAwait(false);
    }

    public async Task<StoryboardRecordingResult> GenerateInWorkflowAsync(string sourceDirectory, string workflowDirectory,
        string title, StoryboardRecordingOptions options, StoryboardArkSettings? arkSettings,
        IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var workflow = Path.GetFullPath(workflowDirectory);
        var output = Path.Combine(workflow, OutputDirectoryName);
        var gate = ProjectLocks.GetOrAdd(output, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sources = ResolveSources(Path.GetFullPath(sourceDirectory), workflow);
            var episodes = StoryboardRecordingRules.SelectEpisodes(options.Episodes, sources.Keys.ToArray()).ToArray();
            var recording = StoryboardRecordingRules.SelectRecordingEpisodes(episodes).ToArray();
            var inputs = episodes.Select(episode =>
            {
                var matches = sources[episode];
                var chosen = matches.Where(path => Path.GetFileNameWithoutExtension(path).StartsWith(title, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (chosen.Length == 0) chosen = matches;
                if (chosen.Length != 1)
                    throw new InvalidOperationException($"找到多个第{episode}集视频：{string.Join("、", chosen.Select(Path.GetFileName))}");
                return (Episode: episode, Path: chosen[0], Stamp: Stamp(chosen[0]));
            }).ToArray();
            var ark = options.UseArk ? new StoryboardArk(
                arkSettings ?? throw new InvalidOperationException("请配置火山方舟 AI。"), http ?? SharedHttp) : null;
            ark?.Validate();
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                Version = 2, Title = title, options.Template, Recording = recording, Inputs = inputs.Select(input => new
                    { input.Episode, input.Path, input.Stamp }), Ark = ark?.Signature,
            }, Json))));
            var screenshots = new[] { "素材资产图.png", "节点画布图.png" }
                .Concat(episodes.Select(episode => $"第{episode}集分镜图.png")).ToArray();
            var videoName = $"{SafeName(title)}操作视频.mp4";
            var required = screenshots.Append(videoName).Concat(ark is null ? [] :
                episodes.Select(episode => $"第{episode}集分镜提示词.json")).ToArray();
            var previous = ReadManifest(output);
            if (!options.Force && previous is not null && previous.Signature == signature &&
                required.All(previous.Files.ContainsKey) &&
                previous.Files.All(file => IsManaged(file.Key) && File.Exists(Path.Combine(output, file.Key)) &&
                    Stamp(Path.Combine(output, file.Key)) == file.Value))
            {
                progress?.Report($"分镜录屏成品齐全且输入未变化，复用：{output}");
                return Result(previous.Files.Keys, true);
            }

            Directory.CreateDirectory(workflow);
            var stage = Path.Combine(workflow, $".分镜录屏-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stage);
            try
            {
                var ffmpeg = BundledToolResolver.TryResolveBinary("ffmpeg") ?? "ffmpeg";
                var ffprobe = BundledToolResolver.TryResolveBinary("ffprobe") ?? "ffprobe";
                var pages = new Dictionary<int, string>();
                var names = required.ToList();
                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"第{input.Episode}集 · 抽帧和切片：{Path.GetFileName(input.Path)}");
                    var duration = await Ffmpeg.ProbeDurationSecondsAsync(ffprobe, input.Path, ct).ConfigureAwait(false);
                    var length = Math.Min(1.8, duration / 3);
                    var starts = new[] { .1, .5, .9 }.Select(ratio => (duration - length) * ratio).ToArray();
                    var shots = new List<StoryboardFrame>();
                    foreach (var start in starts)
                    {
                        var seconds = start + length / 4;
                        shots.Add(await ExtractFrameAsync(ffmpeg, input.Path,
                            Path.Combine(stage, $"shot-{input.Episode}-{shots.Count}.jpg"), seconds, 960, 720, ct).ConfigureAwait(false));
                    }
                    var preview = Path.Combine(stage, $"preview-{input.Episode}.webm");
                    await BuildPreviewAsync(ffmpeg, input.Path, preview, starts, length, ct).ConfigureAwait(false);
                    StoryboardAnalysis? ai = null;
                    if (ark is not null)
                    {
                        var frameIndex = 0;
                        async Task<IReadOnlyList<StoryboardFrame>> SampleAsync(IReadOnlyList<double> times, CancellationToken token)
                        {
                            var frames = new List<StoryboardFrame>();
                            foreach (var seconds in times)
                            {
                                try
                                {
                                    frames.Add(await ExtractFrameAsync(ffmpeg, input.Path,
                                        Path.Combine(stage, $"prop-{input.Episode}-{frameIndex++}.jpg"),
                                        seconds, 1280, 960, token).ConfigureAwait(false));
                                }
                                catch (InvalidOperationException)
                                {
                                    progress?.Report($"第{input.Episode}集 · 跳过无法读取的道具候选帧 {seconds:F2} 秒");
                                }
                            }
                            return frames;
                        }
                        ai = await ark.GenerateAsync(input.Episode, input.Path, stage, shots, duration, SampleAsync, progress, ct)
                            .ConfigureAwait(false);
                        names.AddRange(ai.Assets.Select(asset => asset.Image).OfType<string>());
                    }
                    var page = Path.Combine(stage, $"episode-{input.Episode}.html");
                    await StoryboardPage.WriteAsync(page, title, input.Episode, input.Path, shots, length,
                        preview, options.Template, ai, ct).ConfigureAwait(false);
                    File.Delete(preview);
                    pages.Add(input.Episode, page);
                }

                var recordingPath = await StoryboardPage.CaptureAsync(browserRuntime, pages, recording, stage, progress, ct)
                    .ConfigureAwait(false);
                progress?.Report("正在导出并校验 MP4");
                var rawSeconds = await Ffmpeg.ProbeDurationSecondsAsync(ffprobe, recordingPath, ct).ConfigureAwait(false);
                var filter = rawSeconds > MaxSeconds ? $"setpts={Format(MaxSeconds / rawSeconds)}*PTS,fps=24" : "fps=24";
                var stagedVideo = Path.Combine(stage, videoName);
                await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", recordingPath, "-an",
                    "-vf", filter, "-c:v", "libx264", "-preset", "fast", "-crf", "22", "-pix_fmt", "yuv420p",
                    "-movflags", "+faststart", stagedVideo], ct).ConfigureAwait(false);
                await ValidateAsync(ffprobe, stage, screenshots, stagedVideo, ct).ConfigureAwait(false);
                var files = names.ToDictionary(name => name, name => Stamp(Path.Combine(stage, name)), StringComparer.Ordinal);
                await File.WriteAllTextAsync(Path.Combine(stage, ManifestName),
                    JsonSerializer.Serialize(new Manifest(signature, episodes, recording, files), Json), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (inputs.Any(input => Stamp(input.Path) != input.Stamp))
                    throw new InvalidOperationException("生成期间源视频已变化，请重新执行分镜录屏。");
                Publish(stage, output, previous, files.Keys);
                progress?.Report($"已生成 {episodes.Length} 集分镜图、2 张总览和 1 段 MP4：{output}");
                return Result(files.Keys, false);
            }
            finally
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            StoryboardRecordingResult Result(IEnumerable<string> names, bool reused) =>
                new(output, episodes, recording, names.Select(name => Path.Combine(output, name)).ToArray(), reused);
        }
        finally
        {
            gate.Release();
        }
    }

    private static Dictionary<int, string[]> ResolveSources(string source, string workflow)
    {
        var result = new Dictionary<int, string[]>();
        var directories = new[] { Path.Combine(workflow, "videos"), Path.Combine(source, "videos"), source,
            Path.Combine(workflow, "material-videos") };
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            var groups = Directory.EnumerateFiles(directory)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path)) && new FileInfo(path).Length > 0)
                .Select(path => (Path: path, Episode: StoryboardRecordingRules.EpisodeNumber(path)))
                .Where(item => item.Episode.HasValue).GroupBy(item => item.Episode!.Value);
            foreach (var group in groups)
                result.TryAdd(group.Key, group.Select(item => item.Path).ToArray());
        }
        return result;
    }

    private static async Task<StoryboardFrame> ExtractFrameAsync(string ffmpeg, string source, string path,
        double seconds, int width, int height, CancellationToken ct)
    {
        await Ffmpeg.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-ss", Format(seconds), "-i", source,
            "-map", "0:v:0", "-frames:v", "1", "-vf", $"scale='min(iw,{width})':'min(ih,{height})':force_original_aspect_ratio=decrease",
            "-q:v", "3", "-threads", "1", path], ct).ConfigureAwait(false);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"无法抽取 {seconds:F2} 秒的画面。");
        return new StoryboardFrame(seconds, path);
    }

    private static Task BuildPreviewAsync(string ffmpeg, string source, string preview, double[] starts, double length, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-filter_complex_threads", "1" };
        foreach (var start in starts)
            args.AddRange(["-ss", Format(start), "-t", Format(length), "-i", source]);
        var filters = starts.Select((_, i) =>
            $"[{i}:v:0]scale=640:360:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=640:360:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=24,format=yuv420p,setpts=PTS-STARTPTS[clip{i}]");
        var concat = string.Concat(starts.Select((_, i) => $"[clip{i}]")) + $"concat=n={starts.Length}:v=1:a=0[preview]";
        args.AddRange(["-filter_complex", string.Join(';', filters.Append(concat)), "-map", "[preview]", "-c:v", "libvpx",
            "-deadline", "realtime", "-crf", "20", "-b:v", "800k", preview]);
        return Ffmpeg.RunAsync(ffmpeg, args, ct);
    }

    private static async Task ValidateAsync(string ffprobe, string stage, IEnumerable<string> images, string video, CancellationToken ct)
    {
        foreach (var name in images)
        {
            using var image = await Image.LoadAsync(Path.Combine(stage, name), ct).ConfigureAwait(false);
            if (image.Width != 1920 || image.Height != 1080)
                throw new InvalidDataException($"分镜截图规格不正确：{name}");
        }
        var (exit, stdout, _) = await Ffmpeg.RunCaptureAsync(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height:format=duration",
                "-of", "json", video], ct).ConfigureAwait(false);
        if (exit != 0) throw new InvalidDataException("无法读取分镜录屏成品。");
        using var document = JsonDocument.Parse(stdout);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault();
        var duration = document.RootElement.GetProperty("format").GetProperty("duration").GetString();
        if (stream.ValueKind == JsonValueKind.Undefined || stream.GetProperty("codec_name").GetString() != "h264" ||
            stream.GetProperty("width").GetInt32() != 1280 || stream.GetProperty("height").GetInt32() != 720 ||
            !double.TryParse(duration, CultureInfo.InvariantCulture, out var seconds) || !(seconds >= 25 && seconds < 30))
            throw new InvalidDataException("分镜录屏分辨率、编码或时长不符合要求。");
    }

    private static void Publish(string stage, string output, Manifest? previous, IEnumerable<string> names)
    {
        Directory.CreateDirectory(output);
        var backup = Path.Combine(stage, "previous");
        Directory.CreateDirectory(backup);
        var replacing = names.Append(ManifestName).ToArray();
        var managed = (previous?.Files.Keys.AsEnumerable() ?? []).Where(IsManaged).Concat(replacing).Distinct(StringComparer.Ordinal);
        var moved = new List<string>();
        var written = new List<string>();
        try
        {
            foreach (var name in managed)
            {
                var target = Path.Combine(output, name);
                if (!File.Exists(target)) continue;
                File.Move(target, Path.Combine(backup, name));
                moved.Add(name);
            }
            foreach (var name in replacing)
            {
                File.Move(Path.Combine(stage, name), Path.Combine(output, name));
                written.Add(name);
            }
        }
        catch
        {
            foreach (var name in written) File.Delete(Path.Combine(output, name));
            foreach (var name in moved) File.Move(Path.Combine(backup, name), Path.Combine(output, name));
            throw;
        }
    }

    private static Manifest? ReadManifest(string directory)
    {
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, ManifestName)), Json) is { Files: not null } value ? value : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static FileStamp Stamp(string path)
    {
        var file = new FileInfo(path);
        return new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static bool IsManaged(string name) => Path.GetFileName(name) == name && ManagedPattern().IsMatch(name);
    private static string Format(double seconds) => seconds.ToString("0.########", CultureInfo.InvariantCulture);
    private static string SafeName(string title)
    {
        var name = string.Concat(title.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character))
            .Trim().TrimEnd('.');
        return name.Length == 0 ? "短剧" : name[..Math.Min(name.Length, 80)];
    }

    [GeneratedRegex(@"^(?:素材资产图\.png|节点画布图\.png|.+操作视频\.mp4|第[0-9]+集(?:分镜图\.png|分镜提示词\.json|人物[三四]视图\.png|场景资产图\.png|道具资产图\.png))$")]
    private static partial Regex ManagedPattern();
}
