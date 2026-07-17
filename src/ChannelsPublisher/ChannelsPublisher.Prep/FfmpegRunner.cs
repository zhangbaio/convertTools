using System.Diagnostics;

namespace ChannelsPublisher.Prep;

/// <summary>ffmpeg 子进程执行封装（用 ArgumentList 避免手工转义）。</summary>
public static class FfmpegRunner
{
    public static async Task RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new Exception($"无法启动 ffmpeg：{ffmpeg}");

        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
            throw new Exception($"ffmpeg 退出码 {proc.ExitCode}：{stderr.Trim()}");
    }
}
