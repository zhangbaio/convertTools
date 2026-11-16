using FluentAssertions;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class LogMessageLevelClassifierTests
{
    [Theory]
    [InlineData("WARN 首次解析失败，正在自动重试", "warn")]
    [InlineData("[WARNING] 下载失败，已切换本地兜底", "warn")]
    [InlineData("INFO 正在检查错误记录", "info")]
    public void Explicit_level_takes_precedence_over_message_keywords(string message, string expected)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be(expected);
    }

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

    [Fact]
    public void Headless_submit_notice_is_info()
    {
        LogMessageLevelClassifier.InferLevel(
                "当前使用外部浏览器无头模式提交。提交后会校验原创管理状态，并保留无头设置，不自动打开可见浏览器。")
            .Should().Be("info");
    }

    [Theory]
    [InlineData("角色参考图：正在用视觉模型匹配性别并排除重复人物。")]
    [InlineData("开始避免重复角色并选择清晰单人图")]
    public void Duplicate_prevention_progress_is_info(string message)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be("info");
    }

    [Theory]
    [InlineData("分集下载并发: 3，单集分块连接数: 4，同时下载剧数: 1，单集超时: 60s，重试次数: 5")]
    [InlineData("下载配置：单集重试上限=5")]
    [InlineData("下载配置：单集超时时间：90 秒")]
    public void Retry_configuration_is_info(string message)
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

    [Theory]
    [InlineData("第 3 集下载超时，准备重试")]
    [InlineData("接口请求超时")]
    [InlineData("8 路分块响应范围不匹配，将尝试备用地址或刷新地址后重试")]
    public void Actual_timeout_or_retry_events_are_warnings(string message)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be("warn");
    }

    [Fact]
    public void Failure_to_remove_duplicates_is_error()
    {
        LogMessageLevelClassifier.InferLevel("无法排除重复人物").Should().Be("error");
    }

    [Theory]
    [InlineData("[O Bilionário que Vivia como Sem-Teto] 标题区域校验发现文字差异，开始全图复核：识别标题为“OBilionario”；检测到异体字或非标准字符")]
    [InlineData("标题校验发现差异，改为去字后重绘：检测到异体字或异常字符")]
    public void Poster_verification_that_continues_is_info(string message)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be("info");
    }

    [Fact]
    public void List_read_failure_that_continues_from_cache_is_warning()
    {
        LogMessageLevelClassifier.InferLevel(
                "TikTok 原创管理列表暂时无法读取，将继续按本地草稿缓存判断：原创管理页面未找到剧集搜索框。")
            .Should().Be("warn");
        LogMessageLevelClassifier.InferLevel("角色补源：TikTok 已上传视频兜底失败，将继续后续批次：超时。")
            .Should().Be("warn");
    }

    [Fact]
    public void Failure_that_stops_the_run_stays_error_even_if_it_mentions_continuing()
    {
        LogMessageLevelClassifier.InferLevel("上传失败，将继续下一部前已停止：页面崩溃。")
            .Should().Be("error");
    }

    [Theory]
    [InlineData("接口直传：第 37 集已上传（9 秒，5.1MB，1.3MB/s；传输 4 秒，提交 1 秒，已上传 36/78）。")]
    [InlineData("状态记录显示视频已上传。")]
    public void Uploaded_video_lines_are_success(string message)
    {
        LogMessageLevelClassifier.InferLevel(message).Should().Be("success");
    }

    [Fact]
    public void Mentioning_an_uploaded_file_without_a_finished_video_stays_info()
    {
        LogMessageLevelClassifier.InferLevel("未检测到已上传文件").Should().Be("info");
        LogMessageLevelClassifier.InferLevel("未能识别 TikTok 草稿当前已上传视频数，跳过补传检查。")
            .Should().Be("info");
    }

    [Fact]
    public void Api_form_values_stay_info_when_the_copy_contains_failure_words()
    {
        LogMessageLevelClassifier.InferLevel(
                "接口上传：表单「简介」亲情无法用财富弥补，失败与错误都只是正文。")
            .Should().Be("info");
        LogMessageLevelClassifier.InferLevel(
                "TikTok 版权材料表单「授权书」的文件上传失败：超时。")
            .Should().Be("error");
    }

    [Fact]
    public void Poster_verification_that_still_fails_is_error()
    {
        LogMessageLevelClassifier.InferLevel("海报文字清理后标题校验仍未通过：检测到异体字或非标准字符")
            .Should().Be("error");
    }
}
