using System.Globalization;
using System.Runtime.InteropServices;
using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Services.VideoDedup;

internal sealed record VideoDedupHardwareDevice(
    string Name, string Id, string CalibrationId, string Backend, int Index)
{
    private const string Context = "dedup_gpu";

    public string Encoder(bool hevc) => (hevc ? "hevc_" : "h264_") + Backend;

    public void AppendDevice(List<string> args)
    {
        var index = Index.ToString(CultureInfo.InvariantCulture);
        switch (Backend)
        {
            case "amf":
                args.AddRange(["-init_hw_device", $"d3d11va=dedup_dx:{index}",
                    "-init_hw_device", $"amf={Context}@dedup_dx"]);
                break;
            case "qsv":
                args.AddRange(["-init_hw_device", $"qsv={Context}:hw,child_device={index},child_device_type=d3d11va"]);
                break;
            case "nvenc":
                args.AddRange(["-init_hw_device", $"cuda={Context}:{index}"]);
                break;
            default:
                throw new InvalidOperationException($"未知硬件转码后端 {Backend}。");
        }
        args.AddRange(["-filter_hw_device", Context]);
    }

    public void AppendDecode(List<string> args)
    {
        var acceleration = Backend == "nvenc" ? "cuda" : Backend;
        args.AddRange(["-hwaccel", acceleration, "-hwaccel_device", Context,
            "-hwaccel_output_format", acceleration]);
    }

    public string Scale(int width, int height) => Backend switch
    {
        "amf" => FormattableString.Invariant($"vpp_amf=w={width}:h={height}:format=nv12:scale_type=bicubic"),
        "qsv" => FormattableString.Invariant($"scale_qsv=w={width}:h={height}:format=nv12"),
        "nvenc" => FormattableString.Invariant($"scale_cuda=w={width}:h={height}:format=nv12:interp_algo=bicubic"),
        _ => throw new InvalidOperationException($"未知硬件转码后端 {Backend}。"),
    };

    public static IReadOnlyList<VideoDedupHardwareDevice> Read()
    {
        var adapters = WindowsVideoAdapters.Read();
        var cuda = adapters.Any(adapter => adapter.Vendor == 0x10de) ? ReadCudaOrdinals() : [];
        var devices = new List<VideoDedupHardwareDevice>(adapters.Count);
        var models = new Dictionary<(uint, uint, uint, uint), int>();
        foreach (var adapter in adapters)
        {
            var id = FormattableString.Invariant($"{adapter.Luid:X16}");
            var model = (adapter.Vendor, adapter.Device, adapter.Subsystem, adapter.Revision);
            var deviceNumber = models.GetValueOrDefault(model);
            models[model] = deviceNumber + 1;
            var calibrationId = FormattableString.Invariant(
                $"{adapter.Vendor:X4}:{adapter.Device:X4}:{adapter.Subsystem:X8}:{adapter.Revision:X2}:{deviceNumber}");
            var name = adapters.Count > 1 ? $"{adapter.Name}（适配器 {adapter.Index}）" : adapter.Name;
            switch (adapter.Vendor)
            {
                case 0x1002:
                    devices.Add(new(name, id, calibrationId, "amf", adapter.Index));
                    break;
                case 0x8086:
                    devices.Add(new(name, id, calibrationId, "qsv", adapter.Index));
                    break;
                case 0x10de when cuda.TryGetValue(adapter.Luid, out var ordinal):
                    devices.Add(new(name, id, calibrationId, "nvenc", ordinal));
                    break;
            }
        }
        return devices;
    }

    private static Dictionary<long, int> ReadCudaOrdinals()
    {
        var devices = new Dictionary<long, int>();
        try
        {
            if (cuInit(0) != 0 || cuDeviceGetCount(out var count) != 0)
                return devices;
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                if (cuDeviceGet(out var device, ordinal) == 0 &&
                    cuDeviceGetLuid(out var luid, out _, device) == 0)
                    devices.TryAdd(luid, ordinal);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
        }
        return devices;
    }

    [DllImport("nvcuda.dll")]
    private static extern int cuInit(uint flags);

    [DllImport("nvcuda.dll")]
    private static extern int cuDeviceGetCount(out int count);

    [DllImport("nvcuda.dll")]
    private static extern int cuDeviceGet(out int device, int ordinal);

    [DllImport("nvcuda.dll")]
    private static extern int cuDeviceGetLuid(out long luid, out uint nodeMask, int device);
}
