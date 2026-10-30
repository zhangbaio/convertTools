using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouPrepareStepEventTests
{
    [Theory]
    [InlineData("STEP\trewrite_info\tstarted\t", "rewrite_info", "started", "")]
    [InlineData("STEP\tgenerate_poster\tsuccess\t", "generate_poster", "success", "")]
    [InlineData("STEP\tdownload\tfailed\t网络中断", "download", "failed", "网络中断")]
    public void TryParseStepEvent_ReadsTheStepThatJustChanged(string line, string key, string status, string error)
    {
        var parsed = KuaishouSettlementPrepareRunner.TryParseStepEvent(line, out var step);

        Assert.True(parsed);
        Assert.Equal(key, step.StepKey);
        Assert.Equal(status, step.Status);
        Assert.Equal(error, step.Error);
    }

    [Theory]
    [InlineData("LOG\t开始改写")]
    [InlineData("STEP\t")]
    [InlineData("")]
    public void TryParseStepEvent_IgnoresLinesThatAreNotAStep(string line) =>
        Assert.False(KuaishouSettlementPrepareRunner.TryParseStepEvent(line, out _));
}
