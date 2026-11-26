using System.Text.Json;

namespace TikTokPublisher.Core.Services.VideoDedup;

public sealed record VideoProcessingConcurrencySettings(bool Automatic = true, int ManualLimit = 2);

/// <summary>Process-wide video encoding admission limit for the shared deduplication engine.</summary>
public static class VideoProcessingConcurrency
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private const int MaximumLimit = 32;
    private const int HardwareAutomaticLimit = 2;
    private static readonly object Sync = new();
    private static readonly Lazy<Task<int>> MachineLimit = new(() => Task.Run(DetectLimitAsync));
    private static EffectiveLimit? _current;

    public static int ManualMaximum => Math.Clamp(Environment.ProcessorCount, 1, MaximumLimit);

    public static int ActiveLanes => VideoDedupEncodeGate.ReadUsage().Active;

    private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "video-processing-concurrency.json");

    /// <summary>Returns the hardware recommendation for either mode.</summary>
    public static Task<int> GetLimitAsync(CancellationToken cancellationToken = default) =>
        MachineLimit.Value.WaitAsync(cancellationToken);

    internal sealed record EffectiveLimit(int Limit, bool Automatic);

    internal static EffectiveLimit Current =>
        Volatile.Read(ref _current) ?? new EffectiveLimit(VideoDedupCpuBudget.DefaultConcurrency, true);

    public static VideoProcessingConcurrencySettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<VideoProcessingConcurrencySettings>(File.ReadAllText(SettingsPath)) is { } settings)
            {
                return settings with { ManualLimit = Math.Clamp(settings.ManualLimit, 1, ManualMaximum) };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Keep the hardware defaults when the local settings cannot be read.
        }
        return new VideoProcessingConcurrencySettings(ManualLimit: Math.Min(ManualMaximum, 2));
    }

    internal static async Task<EffectiveLimit> EnsureAppliedAsync(CancellationToken ct)
    {
        lock (Sync)
        {
            ct.ThrowIfCancellationRequested();
            if (_current is { } current)
                return current;
            var settings = LoadSettings();
            if (!settings.Automatic)
                return ApplyLocked(settings.ManualLimit, automatic: false);
        }

        var limit = await GetLimitAsync(ct).ConfigureAwait(false);
        lock (Sync)
            return _current ?? ApplyLocked(limit, automatic: true);
    }

    public static async Task SaveSettingsAsync(VideoProcessingConcurrencySettings settings, CancellationToken ct = default)
    {
        if (settings.ManualLimit < 1 || settings.ManualLimit > ManualMaximum)
            throw new ArgumentOutOfRangeException(nameof(settings));
        var limit = settings.Automatic ? await GetLimitAsync(ct).ConfigureAwait(false) : settings.ManualLimit;
        lock (Sync)
        {
            ct.ThrowIfCancellationRequested();
            WriteSettings(SettingsPath, settings);
            ApplyLocked(limit, settings.Automatic);
        }
    }

    private static EffectiveLimit ApplyLocked(int limit, bool automatic)
    {
        var next = new EffectiveLimit(limit, automatic);
        if (_current == next)
            return next;
        VideoDedupCpuBudget.Configure(limit, Environment.ProcessorCount);
        VideoDedupCpuBudget.ConfigurePercent(VideoDedupCpuBudget.DefaultCpuPercent);
        Volatile.Write(ref _current, next);
        VideoDedupEncodeGate.Configure(limit);
        VideoDedupHardwareTranscoder.NotifyConcurrencyChanged();
        return next;
    }

    private static void WriteSettings(string path, VideoProcessingConcurrencySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static int CalculateLimit(int logicalProcessors, ulong physicalMemoryBytes, bool hardwareEncoderAvailable)
    {
        if (physicalMemoryBytes == 0)
            return 1;

        var cpuLimit = Math.Max(1, logicalProcessors / 2);
        var reserved = Math.Max(4 * GiB, physicalMemoryBytes / 4);
        var available = physicalMemoryBytes > reserved ? physicalMemoryBytes - reserved : 0;
        var memoryLimit = Math.Max(1UL, available / (2 * GiB));
        var maximum = hardwareEncoderAvailable ? HardwareAutomaticLimit : MaximumLimit;
        return (int)Math.Min((ulong)Math.Min(cpuLimit, maximum), memoryLimit);
    }

    private static async Task<int> DetectLimitAsync()
    {
        var logicalProcessors = Environment.ProcessorCount;
        var physicalMemoryBytes = VideoDedupMachineLoad.TotalPhysicalMemoryBytes();
        var cpuAndMemoryLimit = CalculateLimit(logicalProcessors, physicalMemoryBytes, hardwareEncoderAvailable: false);
        if (cpuAndMemoryLimit <= HardwareAutomaticLimit)
            return cpuAndMemoryLimit;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var hasEncoder = await VideoDedupService.HasUsableHardwareEncoderAsync(deadline.Token).ConfigureAwait(false);
            return CalculateLimit(logicalProcessors, physicalMemoryBytes, hasEncoder);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // An inconclusive encoder probe should not increase the number of active jobs.
            return Math.Min(cpuAndMemoryLimit, HardwareAutomaticLimit);
        }
    }
}
