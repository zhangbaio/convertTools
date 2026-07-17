using SherpaOnnx;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Services.Asr;

/// <summary>本地离线 ASR：sherpa-onnx Paraformer + silero VAD（对齐 Python <c>_local_intervals</c>）。</summary>
public static class LocalParaformerAsrClient
{
    private static readonly object RecognizerLock = new();
    private static readonly Dictionary<string, OfflineRecognizer> RecognizerCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<char> NonLexicalChars = new("啦啊哦呜嗯哈呀咦唔哼嘿喔噢哎呵嗷啧唉咯喂呐喏嘞咧呣噜啰嗯呃哟");

    public static Task<IReadOnlyList<SpeechInterval>> RecognizeSpeechIntervalsAsync(
        string wavPath,
        ClientSettings settings,
        CancellationToken ct)
        => Task.Run(() => RecognizeSpeechIntervals(wavPath, settings, ct), ct);

    private static IReadOnlyList<SpeechInterval> RecognizeSpeechIntervals(
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
        var intervals = new List<SpeechInterval>();

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
                DrainSegments(detector, recognizer, sampleRate, intervals);
            }

            detector.Flush();
            DrainSegments(detector, recognizer, sampleRate, intervals);
        }

        return intervals;
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
        List<SpeechInterval> intervals)
    {
        while (!detector.IsEmpty())
        {
            var seg = detector.Front();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, seg.Samples);
            recognizer.Decode(stream);
            var text = stream.Result.Text?.Trim() ?? "";
            if (IsLexical(text))
            {
                var start = seg.Start / (double)sampleRate;
                var end = start + seg.Samples.Length / (double)sampleRate;
                intervals.Add(new SpeechInterval(start, end));
            }

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
