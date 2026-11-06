using PlatformPublisher.Kuaishou.Publishing;
using Xunit;

namespace PlatformPublisher.Kuaishou.Tests;

public sealed class KuaishouAutoArchivePolicyTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Archives_only_when_the_option_is_on_and_upload_is_finished(bool option, bool uploaded, bool expected)
    {
        Assert.Equal(expected, KuaishouAutoArchivePolicy.ShouldArchive(option, uploaded));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, "未执行", true)]
    [InlineData(false, "已完成", true)]
    [InlineData(false, "已完成 77/77", true)]
    [InlineData(false, "失败", false)]
    [InlineData(false, "未执行", false)]
    [InlineData(false, null, false)]
    public void Upload_counts_as_finished_from_the_step_or_the_column(bool stepSucceeded, string? column, bool expected)
    {
        Assert.Equal(expected, KuaishouAutoArchivePolicy.UploadCompleted(stepSucceeded, column));
    }
}
