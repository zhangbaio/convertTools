using System.Buffers.Binary;
using ShortDrama.Core.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ShortDrama.Infrastructure.Imaging;

public sealed record ProjectImageAudioWaveformEpisode(
    double DurationSeconds,
    IReadOnlyList<double> Levels);

public sealed record ProjectImageAudioWaveformData(
    IReadOnlyList<ProjectImageAudioWaveformEpisode> Episodes);

/// <summary>为工程图模板底部音轨绘制源视频的真实音频波形。</summary>
public static class ProjectImageAudioWaveformRenderer
{
    internal const int PcmSampleRate = 8_000;
    internal const int WaveformSamplesPerSecond = 20;

    private const int SamplesPerLevel = PcmSampleRate / WaveformSamplesPerSecond;
    private const int MaximumLevelCount = 2_000_000;

    /// <summary>一次解码所有源视频，供同一批工程图页面重复绘制。</summary>
    public static async Task<ProjectImageAudioWaveformData?> DecodeAsync(
        IExternalProcessRunner runner,
        string ffmpeg,
        IReadOnlyList<string> videos,
        IReadOnlyList<double> durations,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (runner is null ||
            string.IsNullOrWhiteSpace(ffmpeg) ||
            videos is null ||
            videos.Count == 0)
        {
            return null;
        }

        var resolvedDurations = durations ?? Array.Empty<double>();
        var episodes = new List<ProjectImageAudioWaveformEpisode>(videos.Count);
        var decodedAny = false;
        for (var index = 0; index < videos.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = index < resolvedDurations.Count ? resolvedDurations[index] : 0d;
            var episode = await DecodeEpisodeAsync(
                runner,
                ffmpeg,
                videos[index],
                duration,
                cancellationToken).ConfigureAwait(false);
            if (episode is null)
            {
                episodes.Add(new ProjectImageAudioWaveformEpisode(
                    NormalizeDuration(duration),
                    Array.Empty<double>()));
                continue;
            }

            decodedAny = true;
            episodes.Add(episode);
        }

        return decodedAny
            ? new ProjectImageAudioWaveformData(episodes.AsReadOnly())
            : null;
    }

    /// <summary>使用预解码数据绘制单页，不再启动 ffmpeg。</summary>
    public static bool Render(
        Image<Rgba32> canvas,
        ProjectImageAudioWaveformData waveformData,
        int episodeIndex,
        int? playheadX,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (canvas is null ||
            waveformData is null ||
            waveformData.Episodes is null ||
            waveformData.Episodes.Count == 0)
        {
            return false;
        }

        try
        {
            var selectedIndex = Math.Clamp(episodeIndex, 0, waveformData.Episodes.Count - 1);
            var levels = waveformData.Episodes[selectedIndex].Levels;
            if (levels is null || levels.Count == 0 || !levels.Any(level => level > 0d))
                return false;

            var rectangles = DetectWaveformRectangles(canvas, limit: 3);
            if (rectangles.Count == 0)
                rectangles = BuildFallbackRectangles(canvas.Width, canvas.Height);
            if (rectangles.Count == 0)
                return false;

            var markers = CaptureVerticalMarkers(canvas, rectangles);
            for (var laneIndex = 0; laneIndex < rectangles.Count; laneIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rectangle = rectangles[laneIndex];
                var targetCount = Math.Max(1, (rectangle.Width - 3) / 2);
                var shaped = ShapeLaneLevels(levels, laneIndex);
                var displayLevels = ResampleLevels(shaped, targetCount);
                DrawWaveformLane(canvas, rectangle, displayLevels, laneIndex);
            }

            RestoreVerticalMarkers(canvas, rectangles, markers);
            RestorePlayhead(canvas, rectangles, playheadX);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 工程图仍可使用模板原波形；真实波形是 best-effort 增强。
            return false;
        }
    }

    /// <summary>兼容旧调用：仅解码当前页所需的一集，然后立即绘制。</summary>
    public static async Task<bool> RenderAsync(
        Image<Rgba32> canvas,
        IExternalProcessRunner runner,
        string ffmpeg,
        IReadOnlyList<string> videos,
        IReadOnlyList<double> durations,
        int episodeIndex,
        int? playheadX,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (canvas is null ||
            runner is null ||
            string.IsNullOrWhiteSpace(ffmpeg) ||
            videos is null ||
            videos.Count == 0)
        {
            return false;
        }

        var selectedIndex = Math.Clamp(episodeIndex, 0, videos.Count - 1);
        var resolvedDurations = durations ?? Array.Empty<double>();
        var duration = selectedIndex < resolvedDurations.Count
            ? resolvedDurations[selectedIndex]
            : 0d;
        var episode = await DecodeEpisodeAsync(
            runner,
            ffmpeg,
            videos[selectedIndex],
            duration,
            cancellationToken).ConfigureAwait(false);
        if (episode is null)
            return false;

        return Render(
            canvas,
            new ProjectImageAudioWaveformData(Array.AsReadOnly(new[] { episode })),
            episodeIndex: 0,
            playheadX,
            cancellationToken);
    }

    private static async Task<ProjectImageAudioWaveformEpisode?> DecodeEpisodeAsync(
        IExternalProcessRunner runner,
        string ffmpeg,
        string videoPath,
        double duration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return null;

        var pcmPath = Path.Combine(
            Path.GetTempPath(),
            $"shortdrama-project-image-waveform-{Guid.NewGuid():N}.s16le");

        try
        {
            var result = await runner.RunAsync(
                ffmpeg.Trim(),
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-nostdin",
                    "-y",
                    "-i", Path.GetFullPath(videoPath),
                    "-vn",
                    "-ac", "1",
                    "-ar", PcmSampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-f", "s16le",
                    pcmPath,
                ],
                Path.GetTempPath(),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode != 0 || !File.Exists(pcmPath))
                return null;

            var pcm = await File.ReadAllBytesAsync(pcmPath, cancellationToken).ConfigureAwait(false);
            if (!double.IsFinite(duration) || duration <= 0d)
                duration = pcm.Length / 2d / PcmSampleRate;

            var levels = BuildNormalizedLevels(pcm, duration);
            if (levels.Length == 0 || !levels.Any(level => level > 0d))
                return null;

            return new ProjectImageAudioWaveformEpisode(
                NormalizeDuration(duration, pcm.Length),
                Array.AsReadOnly(levels));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 工程图仍可使用模板原波形；真实波形是 best-effort 增强。
            return null;
        }
        finally
        {
            TryDelete(pcmPath);
        }
    }

    private static double NormalizeDuration(double duration, int pcmByteCount = 0)
    {
        if (double.IsFinite(duration) && duration > 0d)
            return duration;
        return pcmByteCount > 0 ? pcmByteCount / 2d / PcmSampleRate : 0d;
    }

    internal static double[] BuildNormalizedLevels(ReadOnlySpan<byte> pcm, double durationSeconds)
    {
        var sampleCount = pcm.Length / 2;
        if (sampleCount <= 0)
            return [];

        var rawLevels = new List<double>((sampleCount + SamplesPerLevel - 1) / SamplesPerLevel);
        for (var sampleOffset = 0; sampleOffset < sampleCount; sampleOffset += SamplesPerLevel)
        {
            var end = Math.Min(sampleCount, sampleOffset + SamplesPerLevel);
            var peak = 0;
            for (var index = sampleOffset; index < end; index++)
            {
                var value = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(index * 2, 2));
                peak = Math.Max(peak, Math.Abs((int)value));
            }

            rawLevels.Add(Math.Min(1d, peak / 32768d));
        }

        var normalized = NormalizeLevels(rawLevels);
        var resolvedDuration = double.IsFinite(durationSeconds) && durationSeconds > 0d
            ? durationSeconds
            : sampleCount / (double)PcmSampleRate;
        var expectedCount = (int)Math.Clamp(
            Math.Round(Math.Max(0.1d, resolvedDuration) * WaveformSamplesPerSecond),
            1d,
            MaximumLevelCount);

        if (normalized.Length == expectedCount)
            return normalized;
        if (normalized.Length > expectedCount)
            return normalized[..expectedCount];

        Array.Resize(ref normalized, expectedCount);
        return normalized;
    }

    internal static double[] NormalizeLevels(IReadOnlyList<double> levels)
    {
        if (levels.Count == 0)
            return [];

        var nonzero = levels
            .Where(level => double.IsFinite(level) && level > 0d)
            .OrderBy(level => level)
            .ToArray();
        if (nonzero.Length == 0)
            return new double[levels.Count];

        var percentileIndex = Math.Clamp(
            (int)Math.Round((nonzero.Length - 1) * 0.95d),
            0,
            nonzero.Length - 1);
        var scale = Math.Max(0.01d, nonzero[percentileIndex]);
        var normalized = new double[levels.Count];
        for (var index = 0; index < levels.Count; index++)
        {
            var level = double.IsFinite(levels[index]) ? Math.Max(0d, levels[index]) : 0d;
            if (level < 0.012d)
            {
                normalized[index] = 0d;
                continue;
            }

            normalized[index] = Math.Clamp(
                Math.Pow(level / scale, 0.72d),
                0.02d,
                1d);
        }

        return normalized;
    }

    internal static double[] ResampleLevels(IReadOnlyList<double> levels, int targetCount)
    {
        if (levels.Count == 0 || targetCount <= 0)
            return [];

        var output = new double[targetCount];
        for (var index = 0; index < targetCount; index++)
        {
            var left = Math.Clamp(
                (int)Math.Floor(index * levels.Count / (double)targetCount),
                0,
                levels.Count - 1);
            var right = Math.Clamp(
                (int)Math.Ceiling((index + 1) * levels.Count / (double)targetCount),
                left + 1,
                levels.Count);
            var peak = 0d;
            for (var sourceIndex = left; sourceIndex < right; sourceIndex++)
            {
                var value = levels[sourceIndex];
                if (double.IsFinite(value))
                    peak = Math.Max(peak, value);
            }

            output[index] = Math.Clamp(peak, 0d, 1d);
        }

        return output;
    }

    internal static IReadOnlyList<Rectangle> DetectWaveformRectangles(
        Image<Rgba32> canvas,
        int limit = 3)
    {
        if (canvas.Width < 64 || canvas.Height < 64)
            return [];

        var yMinimum = (int)(canvas.Height * 0.72d);
        var yExclusive = Math.Max(yMinimum, canvas.Height - 16);
        var rowThreshold = Math.Max(24, canvas.Width / 96);
        var candidateRows = new List<int>();
        for (var y = yMinimum; y < yExclusive; y++)
        {
            var blueCount = 0;
            for (var x = 0; x < canvas.Width; x += 4)
            {
                if (IsWaveformBlue(canvas[x, y], strict: true))
                    blueCount++;
            }

            if (blueCount >= rowThreshold)
                candidateRows.Add(y);
        }

        var rectangles = new List<Rectangle>();
        foreach (var (top, bottom) in GroupConsecutive(candidateRows))
        {
            var height = bottom - top + 1;
            if (height < 28)
                continue;

            var sampleStep = Math.Max(3, height / 12);
            var candidateColumns = new List<int>();
            for (var x = 0; x < canvas.Width; x++)
            {
                var hits = 0;
                for (var y = top; y <= bottom; y += sampleStep)
                {
                    if (IsWaveformBlue(canvas[x, y], strict: false))
                        hits++;
                }

                if (hits >= 3)
                    candidateColumns.Add(x);
            }

            var wideGroups = GroupConsecutive(candidateColumns)
                .Where(group => group.End - group.Start + 1 >= canvas.Width * 0.18d)
                .ToArray();
            if (wideGroups.Length == 0)
                continue;

            var left = wideGroups.Min(group => group.Start);
            var right = wideGroups.Max(group => group.End);
            if (right - left + 1 < canvas.Width * 0.45d)
                continue;

            rectangles.Add(new Rectangle(left, top, right - left + 1, height));
        }

        return rectangles
            .OrderBy(rectangle => rectangle.Y)
            .TakeLast(Math.Clamp(limit, 1, 3))
            .ToArray();
    }

    internal static IReadOnlyList<Rectangle> BuildFallbackRectangles(int width, int height)
    {
        if (width < 64 || height < 64)
            return [];

        var left = Math.Clamp((int)Math.Round(width * 0.02d), 1, width - 2);
        var rightMargin = Math.Max(1, (int)Math.Round(width * 0.008d));
        var trackWidth = Math.Max(1, width - left - rightMargin);
        var bottomExclusive = Math.Clamp(
            height - Math.Max(2, (int)Math.Round(height * 0.026d)),
            1,
            height);
        var totalHeight = Math.Clamp(
            (int)Math.Round(height * 0.165d),
            18,
            bottomExclusive);
        var gap = Math.Max(1, (int)Math.Round(height * 0.003d));
        var usableHeight = Math.Max(12, totalHeight - gap * 2);
        var firstHeight = Math.Max(4, (int)Math.Round(usableHeight * 0.40d));
        var remaining = Math.Max(8, usableHeight - firstHeight);
        var secondHeight = Math.Max(4, remaining / 2);
        var thirdHeight = Math.Max(4, remaining - secondHeight);
        var top = Math.Max(0, bottomExclusive - (firstHeight + secondHeight + thirdHeight + gap * 2));

        return
        [
            new Rectangle(left, top, trackWidth, firstHeight),
            new Rectangle(left, top + firstHeight + gap, trackWidth, secondHeight),
            new Rectangle(left, top + firstHeight + gap + secondHeight + gap, trackWidth, thirdHeight),
        ];
    }

    private static bool IsWaveformBlue(Rgba32 color, bool strict) =>
        color.A > 0 &&
        color.R < (strict ? 90 : 95) &&
        color.G >= (strict ? 35 : 30) &&
        color.G <= (strict ? 205 : 210) &&
        color.B >= (strict ? 75 : 70);

    private static IReadOnlyList<(int Start, int End)> GroupConsecutive(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            return [];

        var groups = new List<(int Start, int End)>();
        var start = values[0];
        var end = start;
        for (var index = 1; index < values.Count; index++)
        {
            var value = values[index];
            if (value > end + 1)
            {
                groups.Add((start, end));
                start = value;
            }

            end = value;
        }

        groups.Add((start, end));
        return groups;
    }

    private static double[] ShapeLaneLevels(IReadOnlyList<double> levels, int laneIndex)
    {
        if (levels.Count == 0)
            return [];

        var lane = Math.Abs(laneIndex % 3);
        var shiftRatios = new[] { 0.028d, 0.137d, 0.293d };
        var directWeights = new[] { 0.76d, 0.34d, 0.28d };
        var shiftedWeights = new[] { 0.18d, 0.48d, 0.42d };
        var powers = new[] { 0.98d, 1.00d, 1.04d };
        var shift = Math.Max(1, (int)Math.Round(levels.Count * shiftRatios[lane]));
        var smoothWeight = Math.Max(0d, 1d - directWeights[lane] - shiftedWeights[lane]);
        var output = new double[levels.Count];
        for (var index = 0; index < levels.Count; index++)
        {
            var direct = Math.Clamp(levels[index], 0d, 1d);
            var shifted = Math.Clamp(levels[(index + shift) % levels.Count], 0d, 1d);
            var previous = Math.Clamp(levels[(index + shift - 1 + levels.Count) % levels.Count], 0d, 1d);
            var next = Math.Clamp(levels[(index + shift + 1) % levels.Count], 0d, 1d);
            var smoothed = (previous + shifted + next) / 3d;
            var mixed = direct * directWeights[lane] +
                        shifted * shiftedWeights[lane] +
                        smoothed * smoothWeight;
            if (mixed > 0d)
                mixed += 0.035d * Math.Sin(index * (0.47d + lane * 0.11d) + lane * 1.7d);
            output[index] = Math.Pow(Math.Clamp(mixed, 0d, 1d), powers[lane]);
        }

        return output;
    }

    private static void DrawWaveformLane(
        Image<Rgba32> canvas,
        Rectangle rectangle,
        IReadOnlyList<double> levels,
        int laneIndex)
    {
        var rect = Rectangle.Intersect(rectangle, new Rectangle(0, 0, canvas.Width, canvas.Height));
        if (rect.Width < 4 || rect.Height < 4 || levels.Count == 0)
            return;

        var palette = SamplePalette(canvas, rect);
        FillRectangle(canvas, rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, palette.Dark);
        var lane = Math.Abs(laneIndex % 3);
        var maxRatios = new[] { 0.78d, 0.58d, 0.54d };
        var floorRatios = new[] { 0.30d, 0.18d, 0.16d };
        var powers = new[] { 0.66d, 0.78d, 0.82d };
        var gains = new[] { 1.18d, 1.02d, 0.98d };
        var capThresholds = new[] { 0.78d, 0.86d, 0.90d };
        var maximumAmplitude = Math.Max(5, (int)Math.Round(rect.Height * maxRatios[lane]));
        var minimumAmplitude = Math.Max(1, (int)Math.Round(rect.Height * floorRatios[lane]));
        var bottom = rect.Bottom - 3;
        var count = Math.Min(levels.Count, Math.Max(1, (rect.Width - 3) / 2));
        var displayed = new double[count];
        var amplitudes = new int[count];
        for (var index = 0; index < count; index++)
        {
            var previous = levels[Math.Max(0, index - 1)];
            var current = levels[index];
            var next = levels[Math.Min(levels.Count - 1, index + 1)];
            var level = Math.Clamp((current * 0.78d + previous * 0.11d + next * 0.11d) * gains[lane], 0d, 1d);
            displayed[index] = level;
            var amplitude = Math.Min(
                maximumAmplitude,
                minimumAmplitude + (int)Math.Round(
                    (maximumAmplitude - minimumAmplitude) * Math.Pow(level, powers[lane])));
            amplitudes[index] = Math.Max(1, amplitude / 2 * 2);
        }

        for (var index = 0; index < count; index++)
        {
            var level = displayed[index];
            if (level <= 0d)
                continue;

            var x = rect.X + 2 + index * 2;
            if (x >= rect.Right - 1)
                break;
            var top = Math.Max(rect.Y + 1, bottom - amplitudes[index]);
            FillRectangle(canvas, x, top, Math.Min(2, rect.Right - 1 - x), bottom - top + 1, palette.Blue);

            var left = displayed[Math.Max(0, index - 1)];
            var right = displayed[Math.Min(count - 1, index + 1)];
            var localPeak = level >= capThresholds[lane] &&
                            level >= left &&
                            level >= right &&
                            level - Math.Min(left, right) >= 0.01d;
            if (localPeak)
            {
                var capHeight = Math.Max(2, (int)Math.Round(rect.Height * 0.055d));
                FillRectangle(canvas, Math.Max(rect.X + 1, x - 1), top, 3, capHeight, palette.Orange);
            }
        }

        DrawHorizontalLine(
            canvas,
            rect.X + 1,
            rect.Right - 2,
            rect.Y + rect.Height / 2,
            palette.CenterLine,
            0.52f,
            2);
        DrawRectangleBorder(canvas, rect, Blend(palette.Dark, palette.Blue, 0.24f));
    }

    private static WaveformPalette SamplePalette(Image<Rgba32> canvas, Rectangle rect)
    {
        var candidates = new List<Rgba32>();
        var stepX = Math.Max(1, rect.Width / 120);
        var stepY = Math.Max(1, rect.Height / 12);
        for (var y = rect.Y; y < rect.Bottom; y += stepY)
        {
            for (var x = rect.X; x < rect.Right; x += stepX)
            {
                var color = canvas[x, y];
                if (IsWaveformBlue(color, strict: false))
                    candidates.Add(color);
            }
        }

        var fallbackDark = new Rgba32(14, 48, 88, 255);
        var fallbackBlue = new Rgba32(14, 129, 179, 255);
        if (candidates.Count < 4)
        {
            return new WaveformPalette(
                fallbackDark,
                fallbackBlue,
                new Rgba32(232, 151, 31, 255),
                Blend(fallbackDark, fallbackBlue, 0.52f));
        }

        var ordered = candidates
            .OrderBy(color => color.R * 0.21d + color.G * 0.72d + color.B * 0.07d)
            .ToArray();
        var dark = ForceOpaque(ordered[(int)Math.Round((ordered.Length - 1) * 0.18d)]);
        var blue = ForceOpaque(ordered[(int)Math.Round((ordered.Length - 1) * 0.82d)]);
        return new WaveformPalette(
            dark,
            blue,
            new Rgba32(232, 151, 31, 255),
            Blend(dark, blue, 0.52f));
    }

    private static IReadOnlyList<VerticalMarker> CaptureVerticalMarkers(
        Image<Rgba32> canvas,
        IReadOnlyList<Rectangle> rectangles)
    {
        if (rectangles.Count == 0)
            return [];

        var xMinimum = Math.Max(0, rectangles.Min(rectangle => rectangle.X));
        var xMaximum = Math.Min(canvas.Width - 1, rectangles.Max(rectangle => rectangle.Right - 1));
        var yMinimum = Math.Max(0, rectangles.Min(rectangle => rectangle.Y) - 4);
        var yMaximum = Math.Min(canvas.Height - 1, rectangles.Max(rectangle => rectangle.Bottom - 1) + 4);
        var height = Math.Max(1, yMaximum - yMinimum + 1);
        var candidates = new List<int>();
        var colors = new Dictionary<int, Rgba32>();
        for (var x = xMinimum; x <= xMaximum; x++)
        {
            var count = 0;
            long red = 0;
            long green = 0;
            long blue = 0;
            long alpha = 0;
            for (var y = yMinimum; y <= yMaximum; y++)
            {
                var color = canvas[x, y];
                if (color.A == 0 || color.R < 150 || color.G < 150 || color.B < 150)
                    continue;
                count++;
                red += color.R;
                green += color.G;
                blue += color.B;
                alpha += color.A;
            }

            if (count < Math.Max(10, (int)Math.Round(height * 0.16d)))
                continue;
            candidates.Add(x);
            colors[x] = new Rgba32(
                (byte)(red / count),
                (byte)(green / count),
                (byte)(blue / count),
                (byte)(alpha / count));
        }

        var markers = new List<VerticalMarker>();
        foreach (var (start, end) in GroupConsecutive(candidates))
        {
            var width = end - start + 1;
            if (width > 8)
                continue;
            var center = (start + end) / 2;
            var samples = Enumerable.Range(start, width)
                .Where(colors.ContainsKey)
                .Select(x => colors[x])
                .ToArray();
            markers.Add(new VerticalMarker(center, width, Average(samples)));
        }

        return markers;
    }

    private static void RestoreVerticalMarkers(
        Image<Rgba32> canvas,
        IReadOnlyList<Rectangle> rectangles,
        IReadOnlyList<VerticalMarker> markers)
    {
        if (rectangles.Count == 0 || markers.Count == 0)
            return;

        var yMinimum = Math.Max(0, rectangles.Min(rectangle => rectangle.Y) - 4);
        var yMaximum = Math.Min(canvas.Height - 1, rectangles.Max(rectangle => rectangle.Bottom - 1) + 4);
        foreach (var marker in markers)
        {
            var left = marker.X - Math.Max(0, marker.Width / 2);
            FillRectangle(canvas, left, yMinimum, Math.Max(1, marker.Width), yMaximum - yMinimum + 1, marker.Color);
        }
    }

    private static void RestorePlayhead(
        Image<Rgba32> canvas,
        IReadOnlyList<Rectangle> rectangles,
        int? playheadX)
    {
        if (rectangles.Count == 0 || playheadX is null)
            return;

        var x = Math.Clamp(playheadX.Value, 0, canvas.Width - 1);
        if (!rectangles.Any(rectangle => x >= rectangle.X - 2 && x <= rectangle.Right + 2))
            return;

        var yMinimum = Math.Max(0, rectangles.Min(rectangle => rectangle.Y) - 12);
        var yMaximum = Math.Min(canvas.Height - 1, rectangles.Max(rectangle => rectangle.Bottom - 1) + 12);
        for (var y = yMinimum; y <= yMaximum; y++)
        {
            if (x > 0)
                canvas[x - 1, y] = Blend(canvas[x - 1, y], new Rgba32(210, 224, 235, 255), 0.35f);
            canvas[x, y] = new Rgba32(232, 236, 240, 225);
            if (x + 1 < canvas.Width)
                canvas[x + 1, y] = new Rgba32(232, 236, 240, 225);
        }
    }

    private static void FillRectangle(
        Image<Rgba32> canvas,
        int x,
        int y,
        int width,
        int height,
        Rgba32 color)
    {
        var left = Math.Clamp(x, 0, canvas.Width);
        var top = Math.Clamp(y, 0, canvas.Height);
        var right = Math.Clamp(x + Math.Max(0, width), left, canvas.Width);
        var bottom = Math.Clamp(y + Math.Max(0, height), top, canvas.Height);
        for (var row = top; row < bottom; row++)
        {
            for (var column = left; column < right; column++)
                canvas[column, row] = color;
        }
    }

    private static void DrawHorizontalLine(
        Image<Rgba32> canvas,
        int xMinimum,
        int xMaximum,
        int y,
        Rgba32 color,
        float opacity,
        int width)
    {
        var left = Math.Clamp(xMinimum, 0, canvas.Width - 1);
        var right = Math.Clamp(xMaximum, left, canvas.Width - 1);
        var top = Math.Max(0, y - Math.Max(0, width / 2));
        var bottom = Math.Min(canvas.Height, top + Math.Max(1, width));
        for (var row = top; row < bottom; row++)
        {
            for (var x = left; x <= right; x++)
                canvas[x, row] = Blend(canvas[x, row], color, opacity);
        }
    }

    private static void DrawRectangleBorder(Image<Rgba32> canvas, Rectangle rect, Rgba32 color)
    {
        for (var x = rect.X; x < rect.Right; x++)
        {
            canvas[x, rect.Y] = color;
            canvas[x, rect.Bottom - 1] = color;
        }
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            canvas[rect.X, y] = color;
            canvas[rect.Right - 1, y] = color;
        }
    }

    private static Rgba32 ForceOpaque(Rgba32 color) =>
        new(color.R, color.G, color.B, 255);

    private static Rgba32 Average(IReadOnlyList<Rgba32> colors)
    {
        if (colors.Count == 0)
            return new Rgba32(218, 218, 220, 210);

        return new Rgba32(
            (byte)colors.Average(color => color.R),
            (byte)colors.Average(color => color.G),
            (byte)colors.Average(color => color.B),
            (byte)colors.Average(color => color.A));
    }

    private static Rgba32 Blend(Rgba32 background, Rgba32 foreground, float opacity)
    {
        var amount = Math.Clamp(opacity, 0f, 1f);
        return new Rgba32(
            (byte)Math.Round(background.R * (1f - amount) + foreground.R * amount),
            (byte)Math.Round(background.G * (1f - amount) + foreground.G * amount),
            (byte)Math.Round(background.B * (1f - amount) + foreground.B * amount),
            (byte)Math.Round(background.A * (1f - amount) + foreground.A * amount));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时 PCM 清理是 best-effort，不能掩盖工程图结果。
        }
    }

    private readonly record struct WaveformPalette(
        Rgba32 Dark,
        Rgba32 Blue,
        Rgba32 Orange,
        Rgba32 CenterLine);

    private readonly record struct VerticalMarker(int X, int Width, Rgba32 Color);
}
