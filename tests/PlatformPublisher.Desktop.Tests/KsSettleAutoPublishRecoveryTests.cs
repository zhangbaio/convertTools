using PlatformPublisher.Desktop.Services;
using Xunit;

namespace PlatformPublisher.Desktop.Tests;

public sealed class KsSettleAutoPublishRecoveryTests
{
    [Theory]
    [InlineData("success", true, "", true, KsSettleAutoPublishRecovery.Action.None)]
    [InlineData("accepted", true, "", true, KsSettleAutoPublishRecovery.Action.Resume)]
    [InlineData("running", true, "success", false, KsSettleAutoPublishRecovery.Action.ReportSuccess)]
    [InlineData("accepted", false, "", false, KsSettleAutoPublishRecovery.Action.ReportInterrupted)]
    [InlineData("dispatched", false, "", false, KsSettleAutoPublishRecovery.Action.None)]
    [InlineData("running", false, "failed", true, KsSettleAutoPublishRecovery.Action.ReportFailed)]
    public void Decide_follows_the_server_and_local_marker(
        string serverStatus,
        bool markerActive,
        string markerFinal,
        bool projectExists,
        KsSettleAutoPublishRecovery.Action expected)
    {
        var actual = KsSettleAutoPublishRecovery.Decide(serverStatus, markerActive, markerFinal, projectExists);
        Assert.Equal(expected, actual);
    }
}
