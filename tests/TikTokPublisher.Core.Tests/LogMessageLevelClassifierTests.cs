using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class LogMessageLevelClassifierTests
{
    [Theory]
    [InlineData("已同步管理系统：成功（新增 1，更新 0，失败 0）")]
    [InlineData("sync succeeded (created 1, updated 0, failed: 0)")]
    public void Zero_failure_success_summaries_are_success(string message)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be("success");
    }

    [Theory]
    [InlineData("已同步管理系统：成功（新增 0，更新 0，失败 1）")]
    [InlineData("sync completed, failed: 2")]
    [InlineData("失败 0，但写入数据库错误")]
    public void Real_failures_remain_errors(string message)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be("error");
    }
}
