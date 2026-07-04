using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Remote;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokRemoteCommandParserTests
{
    [Fact]
    public void Parse_text_upload_command_matches_python_shape()
    {
        var command = TikTokRemoteCommandParser.Parse("""
            上传TikTok
            剧名A
            工作目录: E:\tiktok
            账号: 默认
            步骤: download,rewrite_info,upload_series
            自动执行: 否
            """);

        command.Should().NotBeNull();
        command!.Command.Should().Be(TikTokRemoteCommandNames.UploadSeries);
        command.Titles.Should().Equal("剧名A");
        command.WorkspacePath.Should().Be(@"E:\tiktok");
        command.AccountProfileName.Should().Be("默认");
        command.EnabledSteps.Should().Equal(
            QueueStepRegistry.Download,
            QueueStepRegistry.RewriteInfo,
            QueueStepRegistry.UploadSeries);
        command.AutoRun.Should().BeFalse();
    }

    [Fact]
    public void Parse_json_start_queue_command_normalizes_aliases()
    {
        var command = TikTokRemoteCommandParser.Parse("""
            {"command":"start_tiktok_queue","workspace":"E:\\tiktok","account":"acct-a","enabled_steps":["download","bad","upload_series","download"]}
            """);

        command.Should().NotBeNull();
        command!.Command.Should().Be(TikTokRemoteCommandNames.StartQueue);
        command.WorkspacePath.Should().Be(@"E:\tiktok");
        command.AccountProfileId.Should().Be("");
        command.AccountProfileName.Should().Be("acct-a");
        command.EnabledSteps.Should().Equal(QueueStepRegistry.Download, QueueStepRegistry.UploadSeries);
    }

    [Fact]
    public void Build_upload_run_options_uses_python_remote_defaults()
    {
        var options = TikTokRemoteRunOptions.BuildFeishuTikTokUploadRunOptions(new ClientSettings());

        options.EnabledSteps.Should().Equal(
            QueueStepRegistry.Download,
            QueueStepRegistry.RewriteInfo,
            QueueStepRegistry.GeneratePoster,
            QueueStepRegistry.SmallVideoRepair,
            QueueStepRegistry.SilenceDetect,
            QueueStepRegistry.SilenceRepair,
            QueueStepRegistry.MaterialValidate,
            QueueStepRegistry.UploadSeries);
        options.AutoArchiveAfterUpload.Should().BeFalse();
        options.ForceRerunCompletedSteps.Should().BeFalse();
        options.PreferUploadWhenReady.Should().BeFalse();
    }

    [Fact]
    public void Build_upload_run_options_allows_command_steps_and_option_override()
    {
        var settings = new ClientSettings
        {
            FeishuTiktokUploadEnabledStepsJson = """["download","rewrite_info","upload_series"]""",
            FeishuTiktokUploadAutoArchiveAfterUpload = false,
            FeishuTiktokUploadForceRerunCompletedSteps = false,
            FeishuTiktokUploadPreferUploadWhenReady = false,
        };
        var command = new TikTokRemoteCommand(
            TikTokRemoteCommandNames.UploadSeries,
            EnabledSteps: [QueueStepRegistry.MaterialValidate, QueueStepRegistry.UploadSeries],
            QueueOptions: new Dictionary<string, object?>
            {
                ["auto_archive_after_upload"] = true,
                ["force_rerun_completed_steps"] = "true",
                ["prefer_upload_when_ready"] = 1,
                ["sync_management_on_upload_success"] = true,
            });

        var options = TikTokRemoteRunOptions.BuildFeishuTikTokUploadRunOptions(settings, command);

        options.EnabledSteps.Should().Equal(QueueStepRegistry.MaterialValidate, QueueStepRegistry.UploadSeries);
        options.AutoArchiveAfterUpload.Should().BeTrue();
        options.ForceRerunCompletedSteps.Should().BeTrue();
        options.PreferUploadWhenReady.Should().BeTrue();
        options.SyncManagementAfterUpload.Should().BeTrue();
    }
}
