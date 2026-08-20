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

    [Theory]
    [InlineData("角色参考图：正在用视觉模型匹配性别并排除重复人物。")]
    [InlineData("开始避免重复角色并选择清晰单人图")]
    public void Duplicate_prevention_progress_is_info(string message)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be("info");
    }

    [Theory]
    [InlineData("发现重复人物，请检查抽帧")]
    [InlineData("候选角色存在重复")]
    public void Actual_duplicate_findings_are_warnings(string message)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be("warn");
    }

    [Fact]
    public void Failure_to_remove_duplicates_is_error()
    {
        LogMessageLevelClassifier.InferLevel("无法排除重复人物").Should().Be("error");
    }
}
