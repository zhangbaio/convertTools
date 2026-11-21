using System.ComponentModel;

namespace ChannelsPublisher.Clip;

public static class ClipSpeech
{
    public static async Task<byte[]> SynthesizeAsync(string text, double speed, ClipEngineOptions options, CancellationToken ct)
    {
        if (options.TtsEngine != "edge")
            return await new VolcTtsClient().SynthesizeAsync(text, speed, options, ct);

        var executable = string.IsNullOrWhiteSpace(options.EdgeTtsExecutable)
            ? (OperatingSystem.IsWindows() ? "edge-tts.exe" : "edge-tts") : options.EdgeTtsExecutable;
        var output = Path.Combine(Path.GetTempPath(), $"clip-tts-{Guid.NewGuid():N}.mp3");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var rate = (int)Math.Round((speed - 1) * 100);
            await Ffmpeg.RunAsync(executable,
                ["--voice", options.EdgeVoice, $"--rate={rate:+0;-0;+0}%", "--text", text, "--write-media", output],
                timeout.Token);
            var audio = await File.ReadAllBytesAsync(output, timeout.Token);
            return audio.Length > 0 ? audio : throw new InvalidDataException("Edge TTS 返回空音频。");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("找不到 Edge TTS。请在服务配置选择 edge-tts.exe，或将其加入 PATH。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Edge TTS 合成超时。");
        }
        finally
        {
            File.Delete(output);
        }
    }
}
