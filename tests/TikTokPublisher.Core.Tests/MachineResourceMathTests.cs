using FluentAssertions;
using TikTokPublisher.Ui.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class MachineResourceMathTests
{
    [Fact]
    public void Cpu_percent_uses_the_non_idle_portion_of_system_time()
    {
        var previous = new SystemTimeSample(Idle: 100, Kernel: 150, User: 50);
        var current = new SystemTimeSample(Idle: 125, Kernel: 200, User: 100);

        MachineResourceMath.CpuPercent(previous, current).Should().Be(75);
    }

    [Fact]
    public void Cpu_percent_is_empty_when_the_clock_did_not_advance()
    {
        var sample = new SystemTimeSample(10, 20, 30);
        MachineResourceMath.CpuPercent(sample, sample).Should().BeNull();
    }

    [Fact]
    public void Gpu_usage_is_the_busiest_engine_and_never_exceeds_100()
    {
        const string adapter = "luid_0x00000000_0x00001234";
        var summary = MachineResourceMath.SummarizeGpu(
        [
            Sample(adapter, "3D", 60),
            Sample(adapter, "3D", 50),
            Sample(adapter, "VideoEncode", 30),
        ]);

        summary.MaxPercent.Should().Be(100);
        summary.Adapters.Should().ContainSingle();
        summary.Adapters[0].Percent.Should().Be(100);
    }

    [Fact]
    public void Gpu_usage_shows_the_busiest_adapter()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["luid_0x00000000_0x00000010"] = "AMD Radeon",
            ["luid_0x00000000_0x00000020"] = "NVIDIA",
        };
        var summary = MachineResourceMath.SummarizeGpu(
        [
            Sample("luid_0x00000000_0x00000010", "3D", 20),
            Sample("luid_0x00000000_0x00000020", "VideoEncode", 70),
            Sample("luid_0x00000000_0x00000020", "3D", 15),
        ],
        names);

        summary.MaxPercent.Should().Be(70);
        summary.Adapters.Select(adapter => adapter.Name).Should().ContainInOrder("NVIDIA", "AMD Radeon");
    }

    [Fact]
    public void Idle_gpu_samples_still_report_zero()
    {
        var summary = MachineResourceMath.SummarizeGpu(
            [Sample("luid_0x00000000_0x00000001", "3D", 0)]);

        summary.MaxPercent.Should().Be(0);
    }

    [Fact]
    public void Unreadable_gpu_samples_leave_the_value_empty()
    {
        var summary = MachineResourceMath.SummarizeGpu(
            [new GpuEngineSample("not-an-engine", 80)]);

        summary.MaxPercent.Should().BeNull();
        summary.Adapters.Should().BeEmpty();
    }

    [Fact]
    public void Usage_text_keeps_a_stable_width()
    {
        MachineResourceMath.FormatUsage("CPU", 62).Should().Be("CPU  62%");
        MachineResourceMath.FormatUsage("CPU", 100).Should().Be("CPU 100%");
        MachineResourceMath.FormatUsage("GPU", null).Should().Be("GPU  --");
    }

    [Fact]
    public void Gpu_tooltip_lists_each_adapter()
    {
        var text = MachineResourceMath.GpuTooltip(
        [
            new GpuAdapterLoad("a", "AMD Radeon", 11),
            new GpuAdapterLoad("b", "NVIDIA", 4),
        ]);

        text.Should().Contain("整机占用，去重编码也算在里面。");
        text.Should().Contain("AMD Radeon 11%").And.Contain("NVIDIA 4%");
    }

    [Fact]
    public async Task Monitor_reads_cpu_and_memory_on_this_machine()
    {
        OperatingSystem.IsWindows().Should().BeTrue();

        var sampled = new TaskCompletionSource<MachineResourceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new MachineResourceMonitor();
        monitor.Updated += snapshot =>
        {
            if (snapshot.CpuPercent is not null && snapshot.MemoryPercent is not null)
                sampled.TrySetResult(snapshot);
        };
        monitor.Start();
        var latest = await sampled.Task.WaitAsync(TimeSpan.FromSeconds(20));

        latest.MemoryPercent.Should().BeInRange(1, 100);
        latest.CpuPercent.Should().BeInRange(0, 100);
        if (latest.GpuPercent is int gpu)
            gpu.Should().BeInRange(0, 100);
    }

    private static GpuEngineSample Sample(string adapter, string engine, double percent) =>
        new($"pid_10_{adapter}_phys_0_eng_0_engtype_{engine}", percent);
}
