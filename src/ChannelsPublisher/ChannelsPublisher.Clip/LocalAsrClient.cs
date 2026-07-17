using SherpaOnnx;

namespace ChannelsPublisher.Clip;

/// <summary>本地离线 ASR：sherpa-onnx SenseVoice + silero VAD。移植自 Python asr_local。
/// 需用户把模型下到 LocalModelDir（model(.int8).onnx + tokens.txt）+ silero_vad.onnx。CPU 运行，同步包进 Task.Run。</summary>
public sealed class LocalAsrClient
{
    // 语言映射：项目用 zh-CN/en/ja/ko → SenseVoice 用 zh/en/ja/ko/yue，其它 auto。
    private static string MapLang(string lang) => (lang ?? "").Trim().ToLowerInvariant() switch
    {
        "zh-cn" or "zh" => "zh",
        "en" or "en-us" => "en",
        "ja" or "ja-jp" => "ja",
        "ko" or "ko-kr" => "ko",
        _ => "auto",
    };

    public Task<List<SubtitleSegment>> TranscribeAsync(string wavPath, ClipEngineOptions opts, Action<string>? log, CancellationToken ct)
        => Task.Run(() => Transcribe(wavPath, opts, log, ct), ct);

    private List<SubtitleSegment> Transcribe(string wavPath, ClipEngineOptions opts, Action<string>? log, CancellationToken ct)
    {
        var (model, tokens, vad) = ResolveModelFiles(opts);

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.SenseVoice.Model = model;
        config.ModelConfig.SenseVoice.Language = MapLang(opts.AsrLanguage);
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = opts.LocalUseItn ? 1 : 0;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        using var recognizer = new OfflineRecognizer(config);

        var vadConfig = new VadModelConfig();
        vadConfig.SileroVad.Model = vad;
        vadConfig.SileroVad.Threshold = 0.5f;
        vadConfig.SileroVad.MinSpeechDuration = 0.25f;
        vadConfig.SileroVad.MinSilenceDuration = 0.5f;
        vadConfig.SampleRate = 16000;
        vadConfig.NumThreads = 1;
        vadConfig.Debug = 0;
        using var detector = new VoiceActivityDetector(vadConfig, 60);

        var (samples, sampleRate) = ReadPcm16Wav(wavPath);
        int window = vadConfig.SileroVad.WindowSize > 0 ? vadConfig.SileroVad.WindowSize : 512;

        var result = new List<SubtitleSegment>();
        int offset = 0;
        while (offset + window <= samples.Length)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = new float[window];
            Array.Copy(samples, offset, chunk, 0, window);
            detector.AcceptWaveform(chunk);
            offset += window;
            DrainSegments(detector, recognizer, sampleRate, result);
        }
        detector.Flush();
        DrainSegments(detector, recognizer, sampleRate, result);

        log?.Invoke($"  本地 ASR：{result.Count} 句");
        return result;
    }

    private static void DrainSegments(VoiceActivityDetector detector, OfflineRecognizer recognizer, int sampleRate, List<SubtitleSegment> result)
    {
        while (!detector.IsEmpty())
        {
            var seg = detector.Front();
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, seg.Samples);
            recognizer.Decode(stream);
            var text = stream.Result.Text?.Trim() ?? "";
            if (text.Length > 0)
            {
                int startMs = (int)(seg.Start / (double)sampleRate * 1000);
                int endMs = startMs + (int)(seg.Samples.Length / (double)sampleRate * 1000);
                result.Add(new SubtitleSegment(startMs, endMs, text));
            }
            detector.Pop();
        }
    }

    // 读 PCM16 WAV（我们抽音固定 16k 单声道 s16le）→ 归一化 float[] + 采样率。多声道取首声道。
    private static (float[] Samples, int SampleRate) ReadPcm16Wav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            throw new Exception("非法 WAV 文件");

        int sampleRate = 16000, channels = 1, bits = 16, dataOffset = -1, dataLen = 0;
        int p = 12;
        while (p + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, p, 4);
            int size = BitConverter.ToInt32(bytes, p + 4);
            int body = p + 8;
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
            p = body + size + (size & 1); // 块按偶数对齐
        }
        if (dataOffset < 0 || bits != 16) throw new Exception("仅支持 16bit PCM WAV");

        int bytesPerSample = 2 * Math.Max(1, channels);
        int frames = dataLen / bytesPerSample;
        var samples = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            short s = BitConverter.ToInt16(bytes, dataOffset + i * bytesPerSample); // 首声道
            samples[i] = s / 32768f;
        }
        return (samples, sampleRate);
    }

    private static (string model, string tokens, string vad) ResolveModelFiles(ClipEngineOptions opts)
    {
        var dir = (opts.LocalModelDir ?? "").Trim();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            throw new Exception($"本地 ASR 模型目录无效：{dir}");

        var model = new[] { "model.int8.onnx", "model.onnx" }
            .Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists)
            ?? throw new Exception($"未找到 SenseVoice 模型（model.int8.onnx / model.onnx）于 {dir}");
        var tokens = Path.Combine(dir, "tokens.txt");
        if (!File.Exists(tokens)) throw new Exception($"未找到 tokens.txt 于 {dir}");

        string vad = (opts.LocalVadPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(vad) || !File.Exists(vad))
        {
            var parent = Directory.GetParent(dir)?.FullName;
            vad = new[]
                {
                    Path.Combine(dir, "silero_vad.onnx"),
                    parent != null ? Path.Combine(parent, "silero_vad.onnx") : "",
                }
                .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                ?? throw new Exception("未找到 silero_vad.onnx（放模型目录或其上级，或在配置里指定 VAD 路径）");
        }
        return (model, tokens, vad);
    }
}
