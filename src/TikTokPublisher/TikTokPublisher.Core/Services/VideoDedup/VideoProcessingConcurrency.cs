namespace TikTokPublisher.Core.Services.VideoDedup;

/// <summary>Process-wide video encoding admission limit for the shared deduplication engine.</summary>
public static class VideoProcessingConcurrency
{
    private const ulong GiB = 1024UL * 1024 * 1024;
    private const int MaxAutomaticLimit = 32;
    private const int HardwareAutomaticLimit = 2;
    private static readonly Lazy<Task<int>> MachineLimit = new(() => Task.Run(DetectLimitAsync));

    public static Task<int> GetLimitAsync(CancellationToken cancellationToken = default) =>
        MachineLimit.Value.WaitAsync(cancellationToken);

    internal static int CalculateLimit(int logicalProcessors, ulong physicalMemoryBytes, bool hardwareEncoderAvailable)
    {
        if (physicalMemoryBytes == 0)
            return 1;

        var cpuLimit = Math.Max(1, logicalProcessors / 2);
        var reserved = Math.Max(4 * GiB, physicalMemoryBytes / 4);
        var available = physicalMemoryBytes > reserved ? physicalMemoryBytes - reserved : 0;
        var memoryLimit = Math.Max(1UL, available / (2 * GiB));
        var maximum = hardwareEncoderAvailable ? HardwareAutomaticLimit : MaxAutomaticLimit;
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
