using System.Runtime.InteropServices;

namespace TikTokPublisher.Core.Media;

public static class WindowsVideoAdapters
{
    public sealed record Adapter(int Index, uint Vendor, uint Device, uint Subsystem, uint Revision, long Luid, string Name)
    {
        public string CounterId => $"luid_0x{Luid >> 32:X8}_0x{unchecked((uint)Luid):X8}";
    }

    public static IReadOnlyList<Adapter> Read()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var adapters = new List<Adapter>();
        var id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        try
        {
            if (CreateDXGIFactory1(ref id, out var factory) != 0 || factory == 0)
                return adapters;
            try
            {
                // IDXGIFactory1::EnumAdapters1 and IDXGIAdapter1::GetDesc1 follow the base interfaces.
                var enumerate = Method<EnumAdapters1>(factory, 12);
                for (var index = 0; enumerate(factory, (uint)index, out var adapter) == 0 && adapter != 0; index++)
                {
                    try
                    {
                        if (Method<GetDesc1>(adapter, 10)(adapter, out var description) != 0 ||
                            (description.Flags & 3) != 0)
                            continue;
                        var luid = ((long)description.LuidHigh << 32) | description.LuidLow;
                        adapters.Add(new(index, description.VendorId, description.DeviceId,
                            description.SubSysId, description.Revision, luid,
                            description.Description?.Trim() ?? $"适配器 {index}"));
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return adapters;
        }

        return adapters;
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

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
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(nint factory, uint index, out nint adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(nint adapter, out AdapterDescription description);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid factoryId, out nint factory);
}
