using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TikTokPublisher.Core.Media;

public static class FfmpegRunner
{
    public static async Task RunAsync(string binary, IReadOnlyList<string> args, CancellationToken ct, bool belowNormal = false)
    {
        ct.ThrowIfCancellationRequested();
        using var proc = Start(binary, args, belowNormal: belowNormal);
        using var cancellation = ct.Register(static state => Stop((Process)state!), proc);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(binary)} 退出码 {proc.ExitCode}：{stderr.Trim()}");
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(
        string binary,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var proc = Start(binary, args, captureStdout: true);
        using var cancellation = ct.Register(static state => Stop((Process)state!), proc);
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        ct.ThrowIfCancellationRequested();
        return (proc.ExitCode, await outTask, await errTask);
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public static async Task<double> ProbeDurationSecondsAsync(string ffprobe, string path, CancellationToken ct)
    {
        var (_, stdout, stderr) = await RunCaptureAsync(ffprobe, new[]
        {
            "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", path,
        }, ct);
        if (!double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
            throw new InvalidOperationException($"ffprobe 读时长失败：{stdout.Trim()} {stderr.Trim()}");
        return duration;
    }

    private static Process Start(string binary, IReadOnlyList<string> args, bool captureStdout = false, bool belowNormal = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardError = true,
            RedirectStandardOutput = captureStdout,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (captureStdout) psi.StandardOutputEncoding = Encoding.UTF8;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var proc = new Process { StartInfo = psi };
        if (!proc.Start()) throw new InvalidOperationException($"无法启动：{binary}");
        if (belowNormal)
        {
            try
            {
                proc.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        return proc;
    }
}
