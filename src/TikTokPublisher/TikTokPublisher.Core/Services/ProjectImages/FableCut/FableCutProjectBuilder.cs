using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

/// <summary>
/// Builds a deterministic, editor-ready FableCut timeline from probed video metadata
/// and caller-supplied ASR cues. This type performs no file IO or ASR work.
/// </summary>
public static partial class FableCutProjectBuilder
{
    public const int DefaultClipCount = 24;
    public const int MinimumClipCount = 12;
    public const int MaximumClipCount = 36;

    private static readonly string[] SceneNames =
        ["主镜头", "人物近景", "人物反应", "双人对话", "环境空镜", "手部特写", "关键道具", "过肩镜头"];

    private static readonly string[] MarkerLabels =
        ["开场", "人物出场", "情节推进", "线索", "转折", "冲突", "高潮", "收束"];

    private static readonly string[] Speeds =
        ["0.9X", "1.0X", "1.1X", "1.2X", "1.4X", "1.6X", "1.7X"];

    private static readonly string[] AdjustNames =
        ["色彩调整", "轻微推近", "镜头稳定", "亮度修正"];

    private static readonly string[] SfxNames = ["轻转场", "情绪点", "切镜音"];

    private static readonly string[] DialogueAssetIds = ["dialogue-f", "dialogue-m", "dialogue-o"];

    private static readonly HashSet<int> PhraseEndingRunes =
        [.. "，。！？；：,.!?;:".EnumerateRunes().Select(rune => rune.Value)];

    /// <summary>
    /// Builds a FableCut project. The video does not need to exist: duration and dimensions
    /// are explicit so media probing remains the caller's responsibility.
    /// </summary>
    public static FableCutProject Build(
        string videoPath,
        string projectTitle,
        int episodeIndex,
        double durationSeconds,
        int width,
        int height,
        int clipCount = DefaultClipCount,
        IReadOnlyList<FableCutSubtitleCue>? subtitleCues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectTitle);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                durationSeconds,
                "Video duration must be a finite positive number.");
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Video width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Video height must be positive.");
        }

        var duration = Math.Max(1.0, durationSeconds);
        var normalizedClipCount = Math.Clamp(
            clipCount == 0 ? DefaultClipCount : clipCount,
            MinimumClipCount,
            MaximumClipCount);
        var fileName = Path.GetFileName(videoPath);
        var episodeNumber = ParseEpisodeNumber(videoPath, episodeIndex);
        var seed = CreateSeed(fileName, duration);
        var sourceTag = (seed % 10_000).ToString("D4", CultureInfo.InvariantCulture);
        var random = new StableRandom(seed);
        var (starts, lengths) = BuildShotRanges(duration, normalizedClipCount, random);

        var media = BuildMediaBin(
            videoPath,
            duration,
            width,
            height,
            episodeNumber,
            lengths);
        var clips = BuildPrimaryTracks(
            starts,
            lengths,
            sourceTag,
            seed,
            random);

        AppendSubtitleAndDialogueTracks(
            clips,
            subtitleCues ?? Array.Empty<FableCutSubtitleCue>(),
            duration,
            width,
            height,
            episodeNumber,
            starts,
            lengths,
            normalizedClipCount);
        AppendMusicAndSfxTracks(
            clips,
            duration,
            starts,
            normalizedClipCount,
            random);

        return new FableCutProject
        {
            Name = $"{projectTitle}-第{episodeNumber}集",
            Width = width,
            Height = height,
            Fps = 30,
            Revision = 1,
            Media = media,
            Clips = clips,
            Markers = MarkerLabels
                .Select((label, index) => new FableCutMarker
                {
                    Time = duration * index / MarkerLabels.Length,
                    Label = label,
                })
                .ToArray(),
        };
    }

    /// <summary>Builds and serializes a project as JSON text.</summary>
    public static string BuildJson(
        string videoPath,
        string projectTitle,
        int episodeIndex,
        double durationSeconds,
        int width,
        int height,
        int clipCount = DefaultClipCount,
        IReadOnlyList<FableCutSubtitleCue>? subtitleCues = null,
        bool indented = false) =>
        JsonSerializer.Serialize(
            Build(
                videoPath,
                projectTitle,
                episodeIndex,
                durationSeconds,
                width,
                height,
                clipCount,
                subtitleCues),
            JsonOptions(indented));

    /// <summary>Builds and serializes a project directly to a UTF-8 HTTP response body.</summary>
    public static byte[] BuildUtf8Json(
        string videoPath,
        string projectTitle,
        int episodeIndex,
        double durationSeconds,
        int width,
        int height,
        int clipCount = DefaultClipCount,
        IReadOnlyList<FableCutSubtitleCue>? subtitleCues = null,
        bool indented = false) =>
        JsonSerializer.SerializeToUtf8Bytes(
            Build(
                videoPath,
                projectTitle,
                episodeIndex,
                durationSeconds,
                width,
                height,
                clipCount,
                subtitleCues),
            JsonOptions(indented));

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = indented,
    };

    private static int ParseEpisodeNumber(string videoPath, int fallback)
    {
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var match = EpisodeNumberRegex().Match(stem);
        return match.Success && int.TryParse(
            match.Groups[1].Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    private static string AssetDisplayName(string videoPath)
    {
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        stem = EpisodePrefixRegex().Replace(stem, "").Trim(' ', '_', '-');
        return $"{(string.IsNullOrEmpty(stem) ? "原始视频" : stem)}{Path.GetExtension(videoPath)}";
    }

    private static ulong CreateSeed(string fileName, double duration)
    {
        var payload = $"{fileName}:{duration.ToString("F3", CultureInfo.InvariantCulture)}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return BinaryPrimitives.ReadUInt64BigEndian(digest.AsSpan(0, sizeof(ulong)));
    }

    private static (double[] Starts, double[] Lengths) BuildShotRanges(
        double duration,
        int clipCount,
        StableRandom random)
    {
        var weights = Enumerable.Range(0, clipCount)
            .Select(_ => random.Uniform(0.58, 1.55))
            .ToArray();
        var scale = duration / weights.Sum();
        var lengths = weights.Select(weight => weight * scale).ToArray();
        var starts = new double[clipCount];
        var cursor = 0.0;
        for (var index = 0; index < clipCount; index++)
        {
            starts[index] = cursor;
            cursor += lengths[index];
        }

        lengths[^1] = duration - starts[^1];
        return (starts, lengths);
    }

    private static List<FableCutMediaItem> BuildMediaBin(
        string videoPath,
        double duration,
        int width,
        int height,
        int episodeNumber,
        IReadOnlyList<double> lengths)
    {
        const string source = "/media/episode";
        var extension = Path.GetExtension(videoPath);
        var media = new List<FableCutMediaItem>
        {
            Media("media-episode", AssetDisplayName(videoPath), "video", source, duration, width, height),
        };

        for (var index = 1; index <= SceneNames.Length; index++)
        {
            var sampleLength = lengths[(index * 3 + episodeNumber) % lengths.Count];
            media.Add(Media(
                $"media-scene-{index}",
                $"{SceneNames[index - 1]}_{index:D2}{extension}",
                "video",
                source,
                sampleLength,
                width,
                height));
        }

        (string Id, string Name, double Duration)[] audioAssets =
        [
            ("dialogue-f", "对白_女声.wav", 5.8),
            ("dialogue-m", "对白_男声.wav", 6.4),
            ("dialogue-o", "对白_长辈.wav", 4.7),
            ("roomtone", "室内环境声.wav", 13.6),
            ("music-a", "背景音乐_温情主题.wav", 24.8),
            ("music-b", "背景音乐_情绪推进.wav", 18.3),
            ("sfx", "转场音效_短.wav", 0.8),
        ];
        foreach (var asset in audioAssets)
        {
            media.Add(Media(
                $"media-{asset.Id}",
                asset.Name,
                "audio",
                source,
                asset.Duration,
                width,
                height));
        }

        return media;
    }

    private static FableCutMediaItem Media(
        string id,
        string name,
        string kind,
        string src,
        double duration,
        int width,
        int height) => new()
    {
        Id = id,
        Name = name,
        Kind = kind,
        Src = src,
        Duration = duration,
        Width = width,
        Height = height,
    };

    private static List<FableCutClip> BuildPrimaryTracks(
        IReadOnlyList<double> starts,
        IReadOnlyList<double> lengths,
        string sourceTag,
        ulong seed,
        StableRandom random)
    {
        var clips = new List<FableCutClip>();
        for (var index = 0; index < starts.Count; index++)
        {
            var (videoName, audioName) = TimelineClipNames(index, sourceTag, seed);
            var linkGroup = $"link-{index + 1}";
            var mediaId = $"media-scene-{index % SceneNames.Length + 1}";
            clips.Add(Clip(
                $"video-{index + 1}",
                mediaId,
                "video",
                "V1",
                starts[index],
                starts[index],
                lengths[index],
                videoName,
                new Dictionary<string, object?> { ["volume"] = 0 },
                linkGroup));
            clips.Add(Clip(
                $"source-audio-{index + 1}",
                mediaId,
                "audio",
                "A1",
                starts[index],
                starts[index],
                lengths[index],
                audioName,
                new Dictionary<string, object?> { ["gain"] = 1.0 },
                linkGroup));

            if (index is 2 or 6 or 11 or 17 or 22 or 29 ||
                index > 3 && random.NextDouble() < 0.16)
            {
                clips.Add(Clip(
                    $"adjust-{index + 1}",
                    null,
                    "adjust",
                    "V3",
                    starts[index],
                    0,
                    Math.Min(lengths[index] * random.Uniform(0.35, 0.75), 1.8),
                    random.Choice(AdjustNames),
                    new Dictionary<string, object?>()));
            }
        }

        return clips;
    }

    private static (string Video, string Audio) TimelineClipNames(
        int clipIndex,
        string sourceTag,
        ulong seed)
    {
        var number = clipIndex + 1;
        if (clipIndex % 10 is 2 or 5 or 9)
        {
            var stem = $"sora2_{sourceTag}_{number:D2}";
            return (stem, stem);
        }

        if (clipIndex % 10 == 7)
        {
            var stem = $"spk_{sourceTag}_{number:D2}";
            return (stem, stem);
        }

        var materialStem = $"素材_{number:D2}";
        var speedIndex = (int)((seed + (ulong)(clipIndex * 3)) % (ulong)Speeds.Length);
        return ($"{materialStem}_{Speeds[speedIndex]}", materialStem);
    }

    private static void AppendSubtitleAndDialogueTracks(
        List<FableCutClip> clips,
        IReadOnlyList<FableCutSubtitleCue> subtitleCues,
        double duration,
        int width,
        int height,
        int episodeNumber,
        IReadOnlyList<double> starts,
        IReadOnlyList<double> lengths,
        int clipCount)
    {
        var subtitleParts = BuildSubtitleParts(subtitleCues, duration);
        var lastEnd = 0.0;
        var subtitleIndex = 0;
        foreach (var part in subtitleParts)
        {
            var start = Math.Max(lastEnd > 0 ? lastEnd + 0.05 : 0, part.Start);
            var end = Math.Min(duration, Math.Max(start + 0.25, part.End));
            if (start >= duration || end <= start)
            {
                continue;
            }

            subtitleIndex++;
            clips.Add(Clip(
                $"subtitle-{subtitleIndex}",
                null,
                "text",
                "V2",
                start,
                0,
                end - start,
                part.Text,
                new Dictionary<string, object?>
                {
                    ["text"] = part.Text,
                    ["fontSize"] = Math.Max(30, (int)Math.Round(Math.Min(width, height) * 0.045)),
                    ["color"] = "#ffffff",
                    ["bold"] = true,
                    ["align"] = "center",
                    ["textShadow"] = 16,
                    ["bgOpacity"] = 0,
                    ["x"] = 0,
                    ["y"] = (int)Math.Round(height * 0.37),
                    ["opacity"] = 0,
                    ["scale"] = 1,
                }));
            lastEnd = end;

            var dialogueAsset = DialogueAssetIds[(subtitleIndex + episodeNumber) % DialogueAssetIds.Length];
            clips.Add(Clip(
                $"dialogue-{subtitleIndex}",
                $"media-{dialogueAsset}",
                "audio",
                "A2",
                start,
                0,
                end - start,
                TakeRunes(part.Text, 18),
                new Dictionary<string, object?> { ["gain"] = 0.92 }));
        }

        if (subtitleCues.Count != 0)
        {
            return;
        }

        for (var index = 1; index < clipCount; index += 3)
        {
            var length = Math.Min(lengths[index] * 0.72, duration - starts[index]);
            clips.Add(Clip(
                $"dialogue-{index}",
                "media-dialogue-f",
                "audio",
                "A2",
                starts[index],
                0,
                length,
                "对白片段",
                new Dictionary<string, object?> { ["gain"] = 0.92 }));
        }
    }

    private static IReadOnlyList<SubtitlePart> BuildSubtitleParts(
        IReadOnlyList<FableCutSubtitleCue> cues,
        double duration)
    {
        var parts = new List<SubtitlePart>();
        foreach (var cue in cues
                     .Where(cue => cue is not null)
                     .OrderBy(cue => cue.StartMilliseconds)
                     .ThenBy(cue => cue.EndMilliseconds))
        {
            var text = NormalizeText(cue.Text);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var segmentStart = Math.Max(0, cue.StartMilliseconds / 1000.0);
            var segmentEnd = Math.Min(
                duration,
                Math.Max(segmentStart + 0.35, cue.EndMilliseconds / 1000.0));
            if (segmentStart >= duration || segmentEnd <= segmentStart)
            {
                continue;
            }

            var chunks = SplitSubtitleChunks(text);
            var textLength = text.EnumerateRunes().Count();
            var required = Math.Min(
                textLength,
                Math.Max(1, (int)Math.Ceiling((segmentEnd - segmentStart) / 1.8)));
            while (chunks.Count < required)
            {
                var longestIndex = Enumerable.Range(0, chunks.Count)
                    .Where(index => chunks[index].EnumerateRunes().Count() > 1)
                    .MaxBy(index => chunks[index].EnumerateRunes().Count());
                var runes = chunks[longestIndex].EnumerateRunes().ToArray();
                var cut = Math.Max(1, runes.Length / 2);
                chunks.RemoveAt(longestIndex);
                chunks.Insert(longestIndex, RunesToString(runes.AsSpan(cut)));
                chunks.Insert(longestIndex, RunesToString(runes.AsSpan(0, cut)));
            }

            var slotDuration = (segmentEnd - segmentStart) / chunks.Count;
            var gap = Math.Min(0.14, slotDuration * 0.08);
            var clipDuration = Math.Min(2.2, Math.Max(0.25, slotDuration - gap));
            for (var index = 0; index < chunks.Count; index++)
            {
                var partStart = segmentStart + index * slotDuration;
                var partEnd = Math.Min(segmentEnd, partStart + clipDuration);
                parts.Add(new SubtitlePart(partStart, partEnd, chunks[index]));
            }
        }

        var maximumCount = Math.Max(12, (int)Math.Ceiling(duration / 60.0 * 40));
        if (parts.Count <= maximumCount)
        {
            return parts;
        }

        return Enumerable.Range(0, maximumCount)
            .Select(index => parts[(int)Math.Round(
                index * (parts.Count - 1.0) / (maximumCount - 1),
                MidpointRounding.ToEven)])
            .ToArray();
    }

    private static List<string> SplitSubtitleChunks(string text)
    {
        var phrases = new List<Rune[]>();
        var current = new List<Rune>();
        foreach (var rune in text.EnumerateRunes())
        {
            current.Add(rune);
            if (!PhraseEndingRunes.Contains(rune.Value))
            {
                continue;
            }

            phrases.Add(current.ToArray());
            current.Clear();
        }

        if (current.Count > 0)
        {
            phrases.Add(current.ToArray());
        }

        var chunks = new List<string>();
        foreach (var phrase in phrases)
        {
            for (var offset = 0; offset < phrase.Length; offset += 9)
            {
                chunks.Add(RunesToString(phrase.AsSpan(offset, Math.Min(9, phrase.Length - offset))));
            }
        }

        return chunks;
    }

    private static string NormalizeText(string? text) =>
        WhitespaceRegex().Replace(text ?? "", " ").Trim();

    private static string TakeRunes(string value, int count)
    {
        var runes = value.EnumerateRunes().Take(count).ToArray();
        return RunesToString(runes);
    }

    private static string RunesToString(ReadOnlySpan<Rune> runes)
    {
        var builder = new StringBuilder(runes.Length);
        foreach (var rune in runes)
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static void AppendMusicAndSfxTracks(
        List<FableCutClip> clips,
        double duration,
        IReadOnlyList<double> starts,
        int clipCount,
        StableRandom random)
    {
        (double Start, double Length)[] musicRanges =
        [
            (random.Uniform(1.0, 3.0), duration * random.Uniform(0.30, 0.42)),
            (duration * random.Uniform(0.55, 0.67), duration * random.Uniform(0.24, 0.34)),
        ];
        for (var index = 0; index < musicRanges.Length; index++)
        {
            var start = Math.Min(musicRanges[index].Start, duration);
            var length = Math.Max(0, Math.Min(musicRanges[index].Length, duration - start));
            clips.Add(Clip(
                $"music-{index + 1}",
                $"media-music-{(index == 0 ? "a" : "b")}",
                "audio",
                "A3",
                start,
                0,
                length,
                index == 0 ? "温情主题" : "情绪推进",
                new Dictionary<string, object?> { ["gain"] = 0.32 }));
        }

        var sampleCount = Math.Min(5, Math.Max(3, clipCount / 6));
        var cutIndexes = random.Sample(2, clipCount - 1, sampleCount);
        Array.Sort(cutIndexes);
        for (var index = 0; index < cutIndexes.Length; index++)
        {
            var start = Math.Max(0, starts[cutIndexes[index]] - 0.12);
            clips.Add(Clip(
                $"sfx-{index + 1}",
                "media-sfx",
                "audio",
                "A4",
                start,
                0,
                Math.Min(random.Uniform(0.35, 0.9), duration - start),
                random.Choice(SfxNames),
                new Dictionary<string, object?> { ["gain"] = 0.62 }));
        }
    }

    private static FableCutClip Clip(
        string id,
        string? mediaId,
        string kind,
        string track,
        double start,
        double sourceIn,
        double duration,
        string name,
        IReadOnlyDictionary<string, object?> props,
        string? linkGroup = null) => new()
    {
        Id = id,
        MediaId = mediaId,
        Kind = kind,
        Track = track,
        Start = start,
        In = sourceIn,
        Duration = duration,
        Name = name,
        Props = props,
        LinkGroup = linkGroup,
    };

    private sealed record SubtitlePart(double Start, double End, string Text);

    /// <summary>Small fixed-algorithm PRNG so output remains stable across .NET versions.</summary>
    private sealed class StableRandom(ulong seed)
    {
        private ulong _state = seed;

        public double NextDouble() =>
            (NextUInt64() >> 11) * (1.0 / 9_007_199_254_740_992.0);

        public double Uniform(double minimum, double maximum) =>
            minimum + (maximum - minimum) * NextDouble();

        public string Choice(IReadOnlyList<string> values) =>
            values[NextInt(values.Count)];

        public int[] Sample(int startInclusive, int endExclusive, int count)
        {
            var values = Enumerable.Range(startInclusive, endExclusive - startInclusive).ToArray();
            count = Math.Min(count, values.Length);
            for (var index = 0; index < count; index++)
            {
                var selected = index + NextInt(values.Length - index);
                (values[index], values[selected]) = (values[selected], values[index]);
            }

            return values[..count];
        }

        private int NextInt(int exclusiveMaximum) =>
            (int)(NextDouble() * exclusiveMaximum);

        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }

    [GeneratedRegex(@"第\s*(\d+)\s*集", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeNumberRegex();

    [GeneratedRegex(@"第\s*\d+\s*集[_\-\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex EpisodePrefixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
