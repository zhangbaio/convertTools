using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal sealed class VideoDedupHardwareTranscoder
{
    private static readonly ConcurrentDictionary<string, DeviceGate> Gates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ProbeResult> Probes = new(StringComparer.Ordinal);
    private static readonly object CalibrationLock = new();
    private static Dictionary<string, int>? _calibrations;

    private readonly IReadOnlyList<VideoDedupHardwareDevice> _devices;
    private readonly string _binary;
    private readonly bool _hevc;
    private readonly VideoDedupPreset _preset;
    private readonly string _environment;

    public VideoDedupHardwareTranscoder(VideoDedupPreset preset)
        : this(preset, VideoDedupHardwareDevice.Read(), FfmpegLocator.ResolveFfmpeg())
    {
    }

    internal VideoDedupHardwareTranscoder(
        VideoDedupPreset preset, IReadOnlyList<VideoDedupHardwareDevice> devices, string binaryPath)
    {
        _devices = devices;
        _binary = binaryPath;
        _preset = preset;
        _hevc = preset.VideoCodec is VideoDedupPreset.CodecHevc or VideoDedupPreset.CodecH265;
        var binary = new FileInfo(_binary);
        _environment = FormattableString.Invariant($"1|{_binary}|{binary.LastWriteTimeUtc.Ticks}|{VideoDedupHardwareOrder.DriverVersions()}");
    }

    public bool HasDevices => _devices.Count != 0;

    public async Task<bool> TryRenderAsync(
        VideoDedupClip clip, VideoDedupSlice slice, VideoDedupPlanner.HardwareFrame frame,
        string outputPath, bool automatic, int machineLimit,
        Func<IReadOnlyList<string>, TimeSpan?, CancellationToken, Task<double>> execute,
        Func<CancellationToken, Task<bool>> validate, Action<string> log, CancellationToken ct)
    {
        var selection = await SelectAsync(clip, slice, frame, execute, ct).ConfigureAwait(false);
        if (selection is null)
        {
            log("硬件解码和滤镜链路未通过试转，使用兼容路径。");
            return false;
        }

        var limit = automatic ? Math.Min(machineLimit, CalibratedLimit(selection.Key)) : machineLimit;
        if (automatic && (!VideoDedupMachineLoad.HasMemoryHeadroom() || VideoDedupMachineLoad.IsOnBattery()))
            limit = 1;
        var gate = GateFor(selection.Device);
        using var lease = await gate.EnterAsync(limit, ct).ConfigureAwait(false);
        if (TryGetProbe(selection.ProbeKey, out var latest) && !latest.Success)
            return false;
        var encoder = selection.Device.Encoder(_hevc);
        log($"{selection.Device.Name} · {selection.Device.Backend} 硬解、{(frame.Size.Scaled ? "硬件缩放、" : "")}{encoder} 硬编 · 设备最多 {lease.Limit} 路");
        async Task RunAndCheckAsync(IReadOnlyList<string> args)
        {
            await execute(args, null, ct).ConfigureAwait(false);
            if (!await validate(ct).ConfigureAwait(false))
                throw new InvalidOperationException("硬件转码成片规格与目标不一致。");
        }
        var args = VideoDedupPlanner.BuildHardwareArguments(_preset, clip, slice, frame, selection.Device, encoder, outputPath);
        try
        {
            await RunAndCheckAsync(args).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && !IsInputOrStorageError(ex.Message))
        {
            if (!IsSessionBusy(ex.Message))
            {
                Probes[selection.ProbeKey] = CreateProbe(0);
                log($"{selection.Device.Name} 硬件链路失败（{FirstLine(ex)}），改用同卡硬编。");
                return await TrySameDeviceAsync().ConfigureAwait(false);
            }
            gate.ReduceTemporarily();
            lease.Dispose();
            log($"{selection.Device.Name} 会话繁忙，等待已有转码结束后按单路重试。");
            using var retryLease = await gate.EnterAsync(1, ct).ConfigureAwait(false);
            try
            {
                await RunAndCheckAsync(args).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException retry) when (!ct.IsCancellationRequested && !IsInputOrStorageError(retry.Message))
            {
                log($"{selection.Device.Name} 单路重试失败（{FirstLine(retry)}），改用同卡硬编。");
                return await TrySameDeviceAsync().ConfigureAwait(false);
            }
        }

        async Task<bool> TrySameDeviceAsync()
        {
            var compatible = VideoDedupPlanner.BuildArguments(_preset, [clip],
                [slice with { ClipIndex = 0 }], outputPath, encoder, 0, selection.Device);
            try
            {
                await RunAndCheckAsync(compatible).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException ex) when (!ct.IsCancellationRequested && !IsInputOrStorageError(ex.Message))
            {
                log($"{selection.Device.Name} 同卡硬编失败（{FirstLine(ex)}），使用兼容路径。");
                return false;
            }
        }
    }

    public async Task<VideoDedupHardwareBenchmark> BenchmarkAsync(
        VideoDedupClip clip, VideoDedupSlice slice, VideoDedupPlanner.HardwareFrame frame, int maximum, CancellationToken ct)
    {
        Task<double> Execute(IReadOnlyList<string> args, TimeSpan? timeout, CancellationToken token) =>
            RunTimedAsync(_binary, args, timeout, token);
        var selection = await SelectAsync(clip, slice, frame, Execute, ct, exclusive: true).ConfigureAwait(false)
            ?? throw new InvalidOperationException("本机没有通过该素材试转的硬件链路。");
        var encoder = selection.Device.Encoder(_hevc);
        var sampleFrames = (int)(Math.Min(clip.Probe.DurationSeconds, 25) * _preset.Fps);
        var args = VideoDedupPlanner.BuildHardwareArguments(_preset, clip, slice, frame, selection.Device, encoder, null, sampleFrames);
        var timings = new List<VideoDedupHardwareBenchmarkSample>();
        for (var lanes = 1; lanes <= Math.Clamp(maximum, 1, 3); lanes++)
        {
            ct.ThrowIfCancellationRequested();
            if (lanes > 1 && (!VideoDedupMachineLoad.HasMemoryHeadroom() || VideoDedupMachineLoad.IsOnBattery()))
                break;
            var stopwatch = Stopwatch.StartNew();
            var cpuBefore = VideoDedupMachineLoad.ReadCpu();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                // Six equal jobs divide evenly across one, two or three sessions.
                await Parallel.ForEachAsync(Enumerable.Range(0, 6), new ParallelOptions
                {
                    MaxDegreeOfParallelism = lanes,
                    CancellationToken = timeout.Token,
                }, async (_, token) => { await Execute(args, null, token).ConfigureAwait(false); }).ConfigureAwait(false);
                var cpu = VideoDedupMachineLoad.ReadCpu()?.PercentSince(cpuBefore);
                var room = VideoDedupMachineLoad.HasMemoryHeadroom() && cpu is null or < 85;
                timings.Add(new(lanes, stopwatch.Elapsed.TotalSeconds, cpu, room));
                if (!room)
                    break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is InvalidOperationException or OperationCanceledException)
            {
                if (timings.Count == 0)
                    throw new InvalidOperationException("单路校准未完成，请查看素材和显卡驱动。", ex);
                break;
            }
        }
        var recommended = Recommend(timings);
        var saved = SaveCalibration(selection.Key, recommended);
        return new(selection.Device.Name, encoder, timings, recommended, saved);
    }

    internal static int Recommend(IReadOnlyList<VideoDedupHardwareBenchmarkSample> timings)
    {
        var best = timings[0];
        foreach (var sample in timings.Skip(1))
        {
            if (sample.HasHeadroom && sample.Seconds <= best.Seconds * 0.90)
                best = sample;
        }
        return best.Lanes;
    }

    internal static async Task<double> RunTimedAsync(
        string ffmpeg, IReadOnlyList<string> args, TimeSpan? maximum, CancellationToken ct)
    {
        using var timeout = maximum.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (maximum is { } duration)
            timeout!.CancelAfter(duration);
        var stopwatch = Stopwatch.StartNew();
        await FfmpegRunner.RunAsync(ffmpeg, args, timeout?.Token ?? ct, belowNormal: true).ConfigureAwait(false);
        return stopwatch.Elapsed.TotalSeconds;
    }

    private async Task<Selection?> SelectAsync(
        VideoDedupClip clip, VideoDedupSlice slice, VideoDedupPlanner.HardwareFrame frame,
        Func<IReadOnlyList<string>, TimeSpan?, CancellationToken, Task<double>> execute, CancellationToken ct, bool exclusive = false)
    {
        Selection? best = null;
        foreach (var device in _devices.Where(SatisfiesPreference))
        {
            var encoder = device.Encoder(_hevc);
            var key = BuildKey(device, clip.Probe, frame, encoder);
            var probeKey = key + device.Id;
            if (!TryGetProbe(probeKey, out var probe))
            {
                using var lease = exclusive ? null : await GateFor(device).EnterAsync(1, ct).ConfigureAwait(false);
                if (!TryGetProbe(probeKey, out probe))
                {
                    var args = VideoDedupPlanner.BuildHardwareArguments(_preset, clip, slice, frame, device, encoder, null);
                    try
                    {
                        probe = CreateProbe(await execute(args, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false));
                        Probes[probeKey] = probe;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested &&
                        ex is InvalidOperationException or OperationCanceledException)
                    {
                        probe = CreateProbe(0);
                        if (!IsSessionBusy(ex.Message) && !IsInputOrStorageError(ex.Message))
                            Probes[probeKey] = probe;
                    }
                }
            }
            if (probe.Success && (best is null || probe.Seconds < best.Seconds * 0.90))
                best = new(device, key, probeKey, probe.Seconds);
        }
        return best;
    }

    private string BuildKey(VideoDedupHardwareDevice device, VideoDedupProbe probe,
        VideoDedupPlanner.HardwareFrame frame, string encoder)
    {
        var key = FormattableString.Invariant(
            $"{_environment}|{device.CalibrationId}|{probe.Format?.Codec}|{probe.Format?.Profile}|{probe.Width}x{probe.Height}|{probe.AverageFramesPerSecond:F3}|{frame.Size.Width}x{frame.Size.Height}|{frame.AspectRatio}|{encoder}|{_preset.Fps}|{_preset.BitrateMode}|{_preset.VideoBitrateMbps:F3}|{_preset.Crf}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private bool SatisfiesPreference(VideoDedupHardwareDevice device) => _preset.EncodingPreference switch
    {
        VideoDedupPreset.EncodingNvenc => device.Backend == "nvenc",
        VideoDedupPreset.EncodingAmf => device.Backend == "amf",
        VideoDedupPreset.EncodingQsv => device.Backend == "qsv",
        VideoDedupPreset.EncodingHardwareFirst => true,
        _ => false,
    };

    private static ProbeResult CreateProbe(double seconds) =>
        new(seconds, DateTimeOffset.UtcNow.AddMinutes(seconds > 0 ? 60 : 2));

    private static bool TryGetProbe(string key, out ProbeResult probe) =>
        Probes.TryGetValue(key, out probe) && probe.ValidUntil > DateTimeOffset.UtcNow;

    private static DeviceGate GateFor(VideoDedupHardwareDevice device) =>
        Gates.GetOrAdd(device.Id, _ => new DeviceGate());

    private static bool IsSessionBusy(string message) =>
        message.Contains("too many concurrent", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("device busy", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("encoder busy", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("MFX_WRN_DEVICE_BUSY", StringComparison.OrdinalIgnoreCase);

    private static bool IsInputOrStorageError(string message) =>
        message.Contains("No space left", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Disk quota exceeded", StringComparison.OrdinalIgnoreCase) ||
        (message.Contains("Error opening input file", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)) ||
        message.Contains("Error writing trailer", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(Exception error) =>
        error.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? error.GetType().Name;

    private static string CalibrationPath => Path.Combine(AppPaths.DataRoot, "video-dedup-calibration.json");

    private static int CalibratedLimit(string key)
    {
        lock (CalibrationLock)
            return ReadCalibrations().TryGetValue(key, out var value) ? Math.Clamp(value, 1, 3) : 1;
    }

    private static Dictionary<string, int> ReadCalibrations()
    {
        if (_calibrations is not null)
            return _calibrations;
        try
        {
            if (File.Exists(CalibrationPath))
                return _calibrations = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(CalibrationPath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return _calibrations = [];
    }

    private static bool SaveCalibration(string key, int limit)
    {
        lock (CalibrationLock)
        {
            var data = ReadCalibrations();
            data[key] = limit;
            var path = CalibrationPath;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(data));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                return false;
            }
        }
    }

    private sealed record Selection(VideoDedupHardwareDevice Device, string Key, string ProbeKey, double Seconds);
    private readonly record struct ProbeResult(double Seconds, DateTimeOffset ValidUntil)
    {
        public bool Success => Seconds > 0;
    }

    private sealed class DeviceGate
    {
        private readonly object _sync = new();
        private readonly List<Lease> _active = [];
        private TaskCompletionSource _changed = Signal();
        private DateTimeOffset _reducedUntil;

        public async Task<Lease> EnterAsync(int limit, CancellationToken ct)
        {
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    ct.ThrowIfCancellationRequested();
                    var effective = _reducedUntil > DateTimeOffset.UtcNow ? 1 : Math.Max(1, limit);
                    if (_active.Count < effective && _active.All(lease => _active.Count < lease.Limit))
                    {
                        var lease = new Lease(this, effective);
                        _active.Add(lease);
                        return lease;
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public void ReduceTemporarily()
        {
            lock (_sync)
                _reducedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
        }

        private void Release(Lease lease)
        {
            lock (_sync)
            {
                if (!_active.Remove(lease))
                    return;
                var previous = _changed;
                _changed = Signal();
                previous.TrySetResult();
            }
        }

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public sealed class Lease(DeviceGate owner, int limit) : IDisposable
        {
            public int Limit { get; } = limit;
            public void Dispose() => owner.Release(this);
        }
    }
}

public sealed record VideoDedupHardwareBenchmarkSample(
    int Lanes, double Seconds, int? CpuPercent = null, bool HasHeadroom = true);

public sealed record VideoDedupHardwareBenchmark(
    string Device, string Encoder, IReadOnlyList<VideoDedupHardwareBenchmarkSample> Samples,
    int RecommendedLanes, bool Saved);
