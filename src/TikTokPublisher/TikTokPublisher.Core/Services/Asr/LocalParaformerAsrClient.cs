using SherpaOnnx;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Services.Asr;

/// <summary>本地 ASR 识别到的一段台词及其在音频中的时间区间（秒）。</summary>
public readonly record struct TranscriptSegment(
    double StartSeconds,
    double EndSeconds,
    string Text);

/// <summary>本地离线 ASR：sherpa-onnx Paraformer + silero VAD（对齐 Python <c>_local_intervals</c>）。</summary>
public static class LocalParaformerAsrClient
{
    private static readonly object RecognizerLock = new();
    private static readonly Dictionary<string, OfflineRecognizer> RecognizerCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<char> NonLexicalChars = new("啦啊哦呜嗯哈呀咦唔哼嘿喔噢哎呵嗷啧唉咯喂呐喏嘞咧呣噜啰嗯呃哟");

    /// <summary>识别 WAV 中的台词文本和时间区间。</summary>
    public static Task<IReadOnlyList<TranscriptSegment>> RecognizeTranscriptSegmentsAsync(
        string wavPath,
        ClientSettings settings,
        CancellationToken ct)
        => Task.Run(() => RecognizeTranscriptSegments(wavPath, settings, ct), ct);

    /// <summary>兼容旧静音检测调用，仅保留台词段的时间区间。</summary>
    public static async Task<IReadOnlyList<SpeechInterval>> RecognizeSpeechIntervalsAsync(
        string wavPath,
        ClientSettings settings,
        CancellationToken ct)
    {
        var segments = await RecognizeTranscriptSegmentsAsync(wavPath, settings, ct).ConfigureAwait(false);
        return ToSpeechIntervals(segments);
    }

    internal static IReadOnlyList<SpeechInterval> ToSpeechIntervals(
        IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0)
            return Array.Empty<SpeechInterval>();

        return segments
            .Select(segment => new SpeechInterval(segment.StartSeconds, segment.EndSeconds))
            .ToArray();
    }

    internal static TranscriptSegment? CreateTranscriptSegment(
        long startSample,
        int sampleCount,
        int sampleRate,
        string? recognizedText)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须大于 0");
        if (startSample < 0)
            throw new ArgumentOutOfRangeException(nameof(startSample), "起始采样点不能小于 0");
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "采样点数量不能小于 0");

        var text = recognizedText?.Trim() ?? "";
        if (!IsLexical(text))
            return null;

        var start = startSample / (double)sampleRate;
        var end = start + sampleCount / (double)sampleRate;
        return new TranscriptSegment(start, end, text);
    }

    private static IReadOnlyList<TranscriptSegment> RecognizeTranscriptSegments(
        string wavPath,
        ClientSettings settings,
        CancellationToken ct)
    {
        SherpaOnnxRuntimeBootstrap.PreloadBundledOnnxRuntime();
        var paths = SherpaOnnxModelResolver.TryResolve(settings)
            ?? throw new InvalidOperationException("未找到本地 Paraformer 模型文件");

        var recognizer = GetOrCreateRecognizer(paths);
        var (samples, sampleRate) = ReadPcm16Wav(wavPath);
        ct.ThrowIfCancellationRequested();

        var vadPath = SherpaOnnxModelResolver.ToAsciiSafePath(paths.VadPath);
        var vadConfig = new VadModelConfig();
        vadConfig.SileroVad.Model = vadPath;
        vadConfig.SileroVad.Threshold = 0.5f;
        vadConfig.SileroVad.MinSilenceDuration = 0.25f;
        vadConfig.SileroVad.MinSpeechDuration = 0.25f;
        vadConfig.SampleRate = 16000;
        vadConfig.NumThreads = 1;
        vadConfig.Debug = 0;

        var bufferSeconds = Math.Max(30, (int)(samples.Length / (double)sampleRate) + 5);
        using var detector = new VoiceActivityDetector(vadConfig, bufferSeconds);
        var window = vadConfig.SileroVad.WindowSize > 0 ? vadConfig.SileroVad.WindowSize : 512;
        var segments = new List<TranscriptSegment>();

        lock (RecognizerLock)
        {
            int offset = 0;
            var feedTick = 0;
            while (offset + window <= samples.Length)
            {
                feedTick++;
                if (feedTick % 256 == 0)
                    ct.ThrowIfCancellationRequested();

                var chunk = new float[window];
                Array.Copy(samples, offset, chunk, 0, window);
                detector.AcceptWaveform(chunk);
                offset += window;
                DrainSegments(detector, recognizer, sampleRate, segments, ct);
            }

            detector.Flush();
            DrainSegments(detector, recognizer, sampleRate, segments, ct);
        }

        return segments;
    }

    private static OfflineRecognizer GetOrCreateRecognizer(SherpaOnnxModelPaths paths)
    {
        var cacheKey = Path.GetFullPath(paths.ModelPath);
        lock (RecognizerLock)
        {
            if (RecognizerCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Paraformer.Model = SherpaOnnxModelResolver.ToAsciiSafePath(paths.ModelPath);
            config.ModelConfig.Tokens = SherpaOnnxModelResolver.ToAsciiSafePath(paths.TokensPath);
            config.ModelConfig.NumThreads = 4;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.Debug = 0;
            var recognizer = new OfflineRecognizer(config);
            RecognizerCache[cacheKey] = recognizer;
            return recognizer;
        }
    }

    private static void DrainSegments(
        VoiceActivityDetector detector,
        OfflineRecognizer recognizer,
        int sampleRate,
        List<TranscriptSegment> segments,
        CancellationToken ct)
    {
        while (!detector.IsEmpty())
        {
            ct.ThrowIfCancellationRequested();
            var seg = detector.Front();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, seg.Samples);
            recognizer.Decode(stream);
            var transcript = CreateTranscriptSegment(
                seg.Start,
                seg.Samples.Length,
                sampleRate,
                stream.Result.Text);
            if (transcript is { } value)
                segments.Add(value);

            detector.Pop();
        }
    }

    private static bool IsLexical(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var cjk = text.Where(c => c >= '\u4e00' && c <= '\u9fff').ToList();
        if (cjk.Count == 0)
            return text.Any(char.IsLetterOrDigit);
        return !cjk.All(c => NonLexicalChars.Contains(c));
    }

    private static (float[] Samples, int SampleRate) ReadPcm16Wav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            throw new InvalidOperationException("非法 WAV 文件");

        int sampleRate = 16000, channels = 1, bits = 16, dataOffset = -1, dataLen = 0;
        int p = 12;
        while (p + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, p, 4);
            var size = BitConverter.ToInt32(bytes, p + 4);
            var body = p + 8;
            if (id == "fmt " && body + 16 <= bytes.Length)
            {
                channels = BitConverter.ToInt16(bytes, body + 2);
                sampleRate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLen = Math.Min(size, bytes.Length - body);
                break;
            }

            p = body + size + (size & 1);
        }

        if (dataOffset < 0 || bits != 16)
            throw new InvalidOperationException("仅支持 16bit PCM WAV");

        var bytesPerSample = 2 * Math.Max(1, channels);
        var frames = dataLen / bytesPerSample;
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var s = BitConverter.ToInt16(bytes, dataOffset + i * bytesPerSample);
            samples[i] = s / 32768f;
        }

        return (samples, sampleRate);
    }
}
