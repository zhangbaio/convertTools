using System.Globalization;
using System.Text.RegularExpressions;

namespace TikTokPublisher.Ui.Services;

internal readonly record struct SystemTimeSample(ulong Idle, ulong Kernel, ulong User);

internal readonly record struct GpuEngineSample(string InstanceName, double UtilizationPercent);

internal readonly record struct GpuAdapterLoad(
    string AdapterId, string Name, int Percent,
    int? VideoEncode = null, int? VideoDecode = null, int? VideoCodec = null);

internal readonly record struct GpuUtilizationSummary(int? MaxPercent, IReadOnlyList<GpuAdapterLoad> Adapters);

internal readonly record struct MachineResourceSnapshot(
    int? CpuPercent,
    int? GpuPercent,
    int? MemoryPercent,
    IReadOnlyList<GpuAdapterLoad> Adapters);

internal static class MachineResourceMath
{
    private static readonly Regex GpuInstancePattern = new(
        @"^pid_\d+_(luid_0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_(phys_\d+_eng_\d+)_engtype_(.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int? CpuPercent(SystemTimeSample previous, SystemTimeSample current)
    {
        var idle = current.Idle - previous.Idle;
        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (total == 0 || idle > total)
            return null;
        var percent = (double)(total - idle) * 100d / total;
        return ClampPercent(percent);
    }

    public static GpuUtilizationSummary SummarizeGpu(
        IReadOnlyList<GpuEngineSample> samples,
        IReadOnlyDictionary<string, string>? adapterNames = null)
    {
        var byEngine = new Dictionary<(string Adapter, string Engine, string Type), double>();
        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample.UtilizationPercent) || sample.UtilizationPercent < 0)
                continue;
            var match = GpuInstancePattern.Match(sample.InstanceName);
            if (!match.Success)
                continue;
            var key = (match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value, match.Groups[3].Value);
            byEngine.TryGetValue(key, out var used);
            byEngine[key] = used + sample.UtilizationPercent;
        }

        if (byEngine.Count == 0)
            return new GpuUtilizationSummary(null, []);

        var adapters = byEngine
            .GroupBy(pair => pair.Key.Adapter)
            .Select(group =>
            {
                var name = adapterNames is not null && adapterNames.TryGetValue(group.Key, out var found) && found.Length > 0
                    ? found
                    : group.Key;
                int? VideoLoad(string type) => group
                    .Where(engine => engine.Key.Type.StartsWith(type, StringComparison.OrdinalIgnoreCase))
                    .Select(engine => (int?)ClampPercent(engine.Value))
                    .Max();

                return new GpuAdapterLoad(group.Key, name, ClampPercent(group.Max(engine => engine.Value)),
                    VideoLoad("VideoEncode"), VideoLoad("VideoDecode"), VideoLoad("Video Codec"));
            })
            .OrderByDescending(adapter => adapter.Percent)
            .ThenBy(adapter => adapter.Name, StringComparer.Ordinal)
            .ToArray();
        return new GpuUtilizationSummary(adapters.Max(adapter => adapter.Percent), adapters);
    }

    public static string FormatUsage(string label, int? percent) =>
        percent is int value
            ? string.Create(CultureInfo.InvariantCulture, $"{label} {value,3}%")
            : $"{label}  --";

    public static string GpuTooltip(IReadOnlyList<GpuAdapterLoad> adapters)
    {
        const string intro = "整机占用，去重编码也算在里面。";
        if (adapters.Count == 0)
            return intro;
        return intro + Environment.NewLine + string.Join(
            Environment.NewLine,
            adapters.Select(adapter => $"{adapter.Name} {adapter.Percent.ToString(CultureInfo.InvariantCulture)}%"
                + EngineUsage("编码", adapter.VideoEncode) + EngineUsage("解码", adapter.VideoDecode)
                + EngineUsage("视频引擎", adapter.VideoCodec)));
    }

    private static string EngineUsage(string label, int? percent) =>
        percent is int value ? $" · {label} {value.ToString(CultureInfo.InvariantCulture)}%" : "";

    private static int ClampPercent(double percent) =>
        (int)Math.Clamp(Math.Round(percent, MidpointRounding.AwayFromZero), 0d, 100d);
}
