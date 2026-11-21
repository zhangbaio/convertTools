using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChannelsPublisher.Clip;

internal static class ConfiguredClipRenderer
{
    private static readonly string? Font = FindFont();

    public static async Task RenderAsync(
        string output, List<ClipCandidate> segments, IReadOnlyDictionary<string, ClipMediaInfo> media,
        List<NarrationLine>? narration, ClipEngineOptions options, string title, string encoder,
        int networkConcurrency, Action<string>? log, CancellationToken ct)
    {
        var working = Path.Combine(Path.GetTempPath(), "material-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        try
        {
            var landscape = segments.Where(segment => media[segment.VideoPath].Width > media[segment.VideoPath].Height)
                .Sum(segment => segment.DurationMs) * 2 >= segments.Sum(segment => segment.DurationMs);
            var width = landscape ? options.Height : options.Width;
            var height = landscape ? options.Width : options.Height;
            var fontName = Font is null ? null : "font" + Path.GetExtension(Font);
            if (fontName is not null)
                File.Copy(Font!, Path.Combine(working, fontName));
            else if (options.TitleCard || (narration is not null && options.BurnSubtitles))
                log?.Invoke("找不到可用的字幕字体，跳过标题和字幕叠加。");

            if (options.TitleCard)
                await File.WriteAllTextAsync(Path.Combine(working, "title.txt"), title, ct);

            var voices = await PrepareNarrationAsync(narration, working, options, networkConcurrency, log, ct);
            var subtitleMasks = await CommentarySubtitleMask.DetectAsync(
                options.BurnSubtitles && narration is not null
                    ? segments.Where((_, index) => voices[index] is not null).ToArray() : [],
                options, working, log, ct);
            var effects = ResolveEffects(options, Path.GetFileName(output));
            var total = segments.Sum(segment => segment.DurationMs);
            try
            {
                await RenderSegmentsAsync(encoder);
            }
            catch (InvalidOperationException ex) when (encoder != ClipVideoEncoding.Software(options) && !ct.IsCancellationRequested)
            {
                log?.Invoke($"硬件编码失败，回退软件编码。{ex.Message.Split('\n')[0]}");
                await RenderSegmentsAsync(ClipVideoEncoding.Software(options));
            }
            if (narration is not null)
            {
                var firstVoice = Array.FindIndex(voices, voice => voice is not null);
                var firstStart = segments.Take(firstVoice).Sum(segment => segment.DurationMs);
                var firstEnd = firstStart + segments[firstVoice].DurationMs;
                var voiceCount = voices.Count(voice => voice is not null);
                var subtitleCount = options.BurnSubtitles && fontName is not null ? voiceCount : 0;
                log?.Invoke($"解说渲染完成，配音 {voiceCount}/{segments.Count} 段，烧录字幕 {subtitleCount} 段，首段旁白 {Seconds(firstStart)}–{Seconds(firstEnd)} 秒。");
            }

            async Task RenderSegmentsAsync(string selectedEncoder)
            {
                var elapsed = 0;
                var listing = new List<string>();
                for (var index = 0; index < segments.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var segment = segments[index];
                    var voice = narration is null ? null : voices[index];
                    var caption = voice is not null && options.BurnSubtitles && fontName is not null ? $"caption-{index}.txt" : null;
                    if (caption is not null)
                        await File.WriteAllTextAsync(Path.Combine(working, caption), WrapCaption(narration![index].Narration), ct);
                    var segmentName = $"seg-{index:D3}.mp4";
                    var subtitleMask = voice is not null && subtitleMasks.TryGetValue(segment.VideoPath, out var regions)
                        ? CommentarySubtitleMask.BuildFilter(regions, media[segment.VideoPath])
                        : "null";
                    await RenderSegmentAsync(segment, media[segment.VideoPath], Path.Combine(working, segmentName),
                        working, voice, caption, fontName, subtitleMask, width, height, elapsed, total, effects, options, selectedEncoder, ct);
                    elapsed += segment.DurationMs;
                    listing.Add($"file '{segmentName}'");
                }

                var concatPath = Path.Combine(working, "segments.txt");
                await File.WriteAllLinesAsync(concatPath, listing, ct);
                await Ffmpeg.RunAsync(options.FfmpegPath,
                    ["-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", concatPath,
                        "-t", Seconds(total), "-c", "copy", "-movflags", "+faststart", output], ct);
            }
        }
        finally
        {
            try { Directory.Delete(working, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string?[]> PrepareNarrationAsync(
        List<NarrationLine>? narration, string working, ClipEngineOptions options, int concurrency,
        Action<string>? log, CancellationToken ct)
    {
        if (narration is null)
            return [];
        var voices = new string?[narration.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, narration.Count),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct }, async (index, token) =>
            {
                var line = narration[index];
                if (line.KeepOriginal || string.IsNullOrWhiteSpace(line.Narration))
                    return;
                try
                {
                    var voice = await ClipSpeech.SynthesizeAsync(line.Narration, CommentaryScripter.SpeedRatio(line, options), options, token);
                    var path = Path.Combine(working, $"voice-{index}.mp3");
                    await File.WriteAllBytesAsync(path, voice, token);
                    voices[index] = path;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    log?.Invoke($"解说第 {index + 1} 段配音失败，保留原声。{ex.Message}");
                }
            });
        if (voices.All(voice => voice is null))
            throw new InvalidOperationException("解说未生成任何旁白，请检查 AI 脚本和 TTS 配置。");
        return voices;
    }

    private static async Task RenderSegmentAsync(
        ClipCandidate segment, ClipMediaInfo media, string output, string working, string? voice, string? caption,
        string? font, string subtitleMask, int width, int height, int elapsed, int total, ClipEffects effects, ClipEngineOptions options,
        string encoder, CancellationToken ct)
    {
        var duration = Seconds(segment.DurationMs);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-filter_complex_threads", "2",
            "-ss", Seconds(segment.StartMs), "-t", duration, "-i", segment.VideoPath,
        };
        if (voice is not null)
            args.AddRange(["-i", voice]);
        else if (!media.HasAudio)
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);

        var graph = new List<string>();
        graph.Add($"[0:v]{subtitleMask}[source]");
        var frame = $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2";
        if (effects.BlurBackground)
        {
            graph.Add("[source]split[background][foreground]");
            graph.Add($"[background]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,boxblur=15[blurred]");
            graph.Add($"[foreground]scale={width * 9 / 20 * 2}:{height * 9 / 20 * 2}:force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1[front]");
            graph.Add("[blurred][front]overlay=(W-w)/2:(H-h)/2[frame]");
        }
        else
        {
            graph.Add($"[source]{frame},pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1[frame]");
        }

        var filters = new List<string>();
        if (effects.Zoom > 1)
            filters.Add($"crop=trunc(iw/{Number(effects.Zoom)}/2)*2:trunc(ih/{Number(effects.Zoom)}/2)*2,scale={width}:{height}");
        if (effects.Color)
            filters.Add($"eq=contrast={Number(1.025 + effects.Tone)}:saturation={Number(1.02 - effects.Tone)}");
        if (effects.Noise)
            filters.Add("noise=alls=3:allf=t");
        if (effects.Speed != 1)
            filters.Add($"setpts=PTS/{Number(effects.Speed)}");
        filters.Add("fps=25");
        filters.Add($"tpad=stop_mode=clone:stop_duration={duration},trim=duration={duration}");
        if (effects.Fade && elapsed == 0)
            filters.Add("fade=t=in:d=0.35");
        if (effects.Fade && elapsed + segment.DurationMs == total)
            filters.Add($"fade=t=out:st={Seconds(Math.Max(0, segment.DurationMs - 350))}:d=0.35");
        graph.Add($"[frame]{string.Join(",", filters)}[graded]");

        var video = "graded";
        var nextInput = voice is not null || !media.HasAudio ? 2 : 1;
        for (var index = 0; index < effects.Stickers.Count; index++)
        {
            args.AddRange(["-i", effects.Stickers[index]]);
            var size = Math.Clamp(width / 10, 48, 240);
            graph.Add($"[{nextInput++}:v]scale={size}:-1,format=rgba,colorchannelmixer=aa=0.65[sticker{index}]");
            var x = effects.Stickers.Count == 1 ? "W-w-20" : $"(W-w)*{index}/{effects.Stickers.Count - 1}";
            graph.Add($"[{video}][sticker{index}]overlay={x}:{(effects.Stickers.Count == 1 ? "H-h-20" : "20")}[graphic{index}]");
            video = $"graphic{index}";
        }

        if (caption is not null && font is not null)
        {
            graph.Add($"[{video}]drawtext=fontfile={font}:textfile={caption}:expansion=none:fontsize={Math.Max(22, height / 29)}:fontcolor=white:borderw=2:x=(w-text_w)/2:y=h-text_h-h/20[caption]");
            video = "caption";
        }

        var titleRemaining = options.TitleCardSeconds * 1000 - elapsed;
        if (options.TitleCard && font is not null && titleRemaining > 0)
        {
            graph.Add($"[{video}]drawtext=fontfile={font}:textfile=title.txt:expansion=none:fontsize={Math.Max(24, height / 24)}:fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=12:x=(w-text_w)/2:y=h/11:enable='lt(t,{Seconds(titleRemaining)})'[title]");
            video = "title";
        }

        var audioInput = voice is not null || !media.HasAudio ? "1:a" : "0:a";
        var audioFilters = new List<string> { "aresample=48000", "aformat=channel_layouts=stereo" };
        if (voice is not null)
        {
            var voiceLength = await Ffmpeg.ProbeDurationSecondsAsync(options.FfprobePath, voice, ct);
            var tempo = voiceLength * 1000 / segment.DurationMs;
            while (tempo > 2)
            {
                audioFilters.Add("atempo=2");
                tempo /= 2;
            }
            if (tempo > 1)
                audioFilters.Add($"atempo={Number(tempo)}");
        }
        else if (media.HasAudio && effects.Speed != 1)
            audioFilters.Add($"atempo={Number(effects.Speed)}");
        audioFilters.Add("apad");
        graph.Add($"[{audioInput}]{string.Join(",", audioFilters)}[audio]");

        args.AddRange(["-filter_complex", string.Join(";", graph), "-map", $"[{video}]", "-map", "[audio]", "-t", duration]);
        args.AddRange(ClipVideoEncoding.Arguments(options, encoder));
        args.AddRange(["-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", output]);
        await Ffmpeg.RunAsync(options.FfmpegPath, args, ct, working);
    }

    private static ClipEffects ResolveEffects(ClipEngineOptions options, string seed)
    {
        if (!options.OrigEnabled)
            return new ClipEffects(1, 1, 0, false, false, false, false, []);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var random = new Random(BitConverter.ToInt32(hash));
        var zoom = options.OrigForegroundZoom ? 1.08 + random.NextDouble() * 0.05 :
            options.OrigZoom ? 1.02 + random.NextDouble() * 0.04 : 1;
        var stickers = Directory.Exists(options.OrigStickerDir)
            ? Directory.EnumerateFiles(options.OrigStickerDir)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".webp")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        return new ClipEffects(zoom, options.OrigSpeed ? 0.97 + random.NextDouble() * 0.06 : 1,
            random.NextDouble() * 0.03, options.OrigColor, options.OrigFade, options.OrigNoise, options.OrigBlurBackground,
            stickers.OrderBy(_ => random.Next()).Take(options.OrigStickerStrip ? 3 : 1).ToArray());
    }

    private static string? FindFont()
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return new[] { Path.Combine(fonts, "msyh.ttc"), Path.Combine(fonts, "simhei.ttf"), Path.Combine(fonts, "arial.ttf"),
                "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf" }
            .FirstOrDefault(File.Exists);
    }

    private static string WrapCaption(string text)
    {
        var trimmed = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return string.Join("\n", Enumerable.Range(0, Math.Min(3, (trimmed.Length + 17) / 18))
            .Select(index => trimmed.Substring(index * 18, Math.Min(18, trimmed.Length - index * 18))));
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Seconds(int milliseconds) => Number(milliseconds / 1000d);

    private sealed record ClipEffects(double Zoom, double Speed, double Tone, bool Color, bool Fade, bool Noise,
        bool BlurBackground, IReadOnlyList<string> Stickers);
}
