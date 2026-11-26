using System.Runtime.InteropServices;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal static class VideoDedupMachineLoad
{
    public static bool HasMemoryHeadroom()
    {
        return !TryReadPhysicalMemory(out var memory) ||
            (memory.Load < 90 && memory.AvailablePhysical >= 512L * 1024 * 1024);
    }

    public static ulong TotalPhysicalMemoryBytes() =>
        TryReadPhysicalMemory(out var memory) ? memory.TotalPhysical : 0;

    private static bool TryReadPhysicalMemory(out MemoryStatus memory)
    {
        memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref memory);
    }

    public static bool IsOnBattery() =>
        OperatingSystem.IsWindows() && GetSystemPowerStatus(out var status) && status.AcLineStatus == 0;

    public static CpuTimes? ReadCpu() =>
        OperatingSystem.IsWindows() && GetSystemTimes(out var idle, out var kernel, out var user)
            ? new(idle, kernel + user) : null;

    internal readonly record struct CpuTimes(ulong Idle, ulong Total)
    {
        public int? PercentSince(CpuTimes? earlier) =>
            earlier is { } previous && Total > previous.Total && Idle >= previous.Idle && Idle - previous.Idle <= Total - previous.Total
                ? (int)Math.Round(100d * (1 - (double)(Idle - previous.Idle) / (Total - previous.Total)))
                : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
