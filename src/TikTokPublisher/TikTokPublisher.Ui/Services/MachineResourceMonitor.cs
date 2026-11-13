using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TikTokPublisher.Ui.Services;

[SupportedOSPlatform("windows")]
internal sealed class MachineResourceMonitor : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;
    private int _disposed;

    public event Action<MachineResourceSnapshot>? Updated;

    public void Start()
    {
        if (_loop is not null || !OperatingSystem.IsWindows())
            return;
        _loop = Task.Run(LoopAsync);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _cancellation.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cancellation.Dispose();
    }

    private async Task LoopAsync()
    {
        using var gpu = GpuEngineQuery.TryCreate();
        var names = DisplayAdapterNames.Read();
        var previous = SystemTimes.Read();
        gpu?.Collect();
        Publish(null, null, PhysicalMemory.ReadPercent(), []);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        int? lastGpu = null;
        IReadOnlyList<GpuAdapterLoad> lastAdapters = [];
        try
        {
            while (await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
            {
                var current = SystemTimes.Read();
                var cpu = previous is SystemTimeSample prior && current is SystemTimeSample next
                    ? MachineResourceMath.CpuPercent(prior, next)
                    : null;
                previous = current;

                int? gpuPercent = gpu is null ? null : lastGpu;
                var adapters = gpu is null ? [] : lastAdapters;
                if (gpu is not null && gpu.Collect())
                {
                    var summary = MachineResourceMath.SummarizeGpu(gpu.Read(), names);
                    if (summary.MaxPercent is int measured)
                    {
                        lastGpu = measured;
                        lastAdapters = summary.Adapters;
                    }

                    gpuPercent = lastGpu;
                    adapters = lastAdapters;
                }

                Publish(cpu, gpuPercent, PhysicalMemory.ReadPercent(), adapters);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Publish(
        int? cpuPercent,
        int? gpuPercent,
        int? memoryPercent,
        IReadOnlyList<GpuAdapterLoad> adapters) =>
        Updated?.Invoke(new MachineResourceSnapshot(cpuPercent, gpuPercent, memoryPercent, adapters));

    private static class SystemTimes
    {
        public static SystemTimeSample? Read()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return null;
            return new SystemTimeSample(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
        }

        private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    }

    private static class PhysicalMemory
    {
        public static int? ReadPercent()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
                return null;
            return (int)Math.Clamp(status.MemoryLoad, 0u, 100u);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }

    private sealed class GpuEngineQuery : IDisposable
    {
        private const uint FormatDouble = 0x00000200;
        private const uint MoreData = 0x800007D2;
        private readonly nint _query;
        private readonly nint _counter;

        private GpuEngineQuery(nint query, nint counter)
        {
            _query = query;
            _counter = counter;
        }

        public static GpuEngineQuery? TryCreate()
        {
            if (PdhOpenQueryW(0, 0, out var query) != 0)
                return null;
            if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", 0, out var counter) != 0)
            {
                PdhCloseQuery(query);
                return null;
            }

            return new GpuEngineQuery(query, counter);
        }

        public bool Collect() => PdhCollectQueryData(_query) is 0 or 0x800007D5;

        public IReadOnlyList<GpuEngineSample> Read()
        {
            uint size = 0;
            uint count = 0;
            var status = PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref size, out count, 0);
            if (status != MoreData || size == 0)
                return [];

            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref size, out count, buffer);
                if (status == MoreData)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal((int)size);
                    status = PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref size, out count, buffer);
                }

                if (status != 0 || count == 0)
                    return [];

                var itemSize = Marshal.SizeOf<CounterItem>();
                var samples = new List<GpuEngineSample>((int)count);
                for (var index = 0; index < count; index++)
                {
                    var item = Marshal.PtrToStructure<CounterItem>(buffer + index * itemSize);
                    if (item.Status != 0)
                        continue;
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    samples.Add(new GpuEngineSample(name, item.Value));
                }

                return samples;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose() => PdhCloseQuery(_query);

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterItem
        {
            public nint Name;
            public uint Status;
            public double Value;
        }

        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW")]
        private static extern uint PdhOpenQueryW(nint dataSource, nint userData, out nint query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

        [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
        private static extern uint PdhCollectQueryData(nint query);

        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
        private static extern uint PdhGetFormattedCounterArrayW(
            nint counter,
            uint format,
            ref uint bufferSize,
            out uint itemCount,
            nint buffer);

        [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
        private static extern uint PdhCloseQuery(nint query);
    }

    private static class DisplayAdapterNames
    {
        public static IReadOnlyDictionary<string, string> Read()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref factoryId, out var factory) != 0 || factory == 0)
                return names;

            try
            {
                var factoryTable = Marshal.ReadIntPtr(factory);
                var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(
                    Marshal.ReadIntPtr(factoryTable, 11 * nint.Size));
                for (uint index = 0; index < 16; index++)
                {
                    if (enumAdapters(factory, index, out var adapter) != 0 || adapter == 0)
                        break;
                    try
                    {
                        var adapterTable = Marshal.ReadIntPtr(adapter);
                        var getDesc = Marshal.GetDelegateForFunctionPointer<GetDescDelegate>(
                            Marshal.ReadIntPtr(adapterTable, 7 * nint.Size));
                        var description = new AdapterDescription();
                        if (getDesc(adapter, ref description) != 0)
                            continue;
                        var id = $"luid_0x{description.LuidHigh:X8}_0x{description.LuidLow:X8}";
                        var name = description.Description?.Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            names.TryAdd(id, name);
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or InvalidOperationException)
            {
                return names;
            }
            finally
            {
                Release(factory);
            }

            return names;
        }

        private static void Release(nint unknown)
        {
            if (unknown == 0)
                return;
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(unknown), 2 * nint.Size));
            release(unknown);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public nuint DedicatedVideoMemory;
            public nuint DedicatedSystemMemory;
            public nuint SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Delegate(nint factory, uint index, out nint adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescDelegate(nint adapter, ref AdapterDescription description);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(nint unknown);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid factoryId, out nint factory);
    }
}
