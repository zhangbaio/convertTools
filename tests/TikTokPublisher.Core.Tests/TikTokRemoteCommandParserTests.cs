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
            步骤: download,rewrite_info,generate_proof_material,upload_series
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
            QueueStepRegistry.GenerateProofMaterial,
            QueueStepRegistry.UploadSeries);
        command.AutoRun.Should().BeFalse();
    }

    [Fact]
    public void Parse_json_upload_keeps_server_dispatch_mode()
    {
        var command = TikTokRemoteCommandParser.Parse("""
            {"command":"tiktok_upload_series","titles":["剧A"],"dispatch_mode":"fill_first"}
            """);

        command.Should().NotBeNull();
        command!.DispatchMode.Should().Be(QueueProjectSlotCoordinator.ServerFillFirstMode);
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
    public void Parse_text_card_help_command()
    {
        var command = TikTokRemoteCommandParser.Parse("卡片教程");

        command.Should().NotBeNull();
        command!.Command.Should().Be(TikTokRemoteCommandNames.ShowHelpCard);
    }

    [Fact]
    public void Parse_text_start_queue_supports_multiple_accounts()
    {
        var command = TikTokRemoteCommandParser.Parse("""
            执行队列
            账号: 账号A,账号B
            """);

        command.Should().NotBeNull();
        command!.Command.Should().Be(TikTokRemoteCommandNames.StartQueue);
        command.AccountSelectors.Should().Equal("账号A", "账号B");
        command.HasMultiAccountSelection.Should().BeTrue();
    }

    [Fact]
    public void Parse_json_upload_with_profile_id_and_name_is_one_account()
    {
        var command = TikTokRemoteCommandParser.Parse("""
            {"command":"tiktok_upload_series","titles":["剧A"],"account_profile_id":"acc-1","account_profile_name":"张莉","auto_run":true}
            """);

        command.Should().NotBeNull();
        command!.HasExplicitAccountSelection.Should().BeTrue();
        command.HasMultiAccountSelection.Should().BeFalse();
        command.AccountProfileId.Should().Be("acc-1");
        command.AccountProfileName.Should().Be("张莉");
        command.AccountSelectors.Should().Equal("acc-1");
    }

    [Fact]
    public void Parse_json_upload_reads_series_lookup_mode()
    {
        var command = TikTokRemoteCommandParser.Parse("""
            {"command":"tiktok_upload_series","titles":["剧A"],"lookup_mode":"series_id","series":[{"title":"剧A","episode_cnt":50,"series_id":"731"}]}
            """);

        command!.LookupMode.Should().Be("series_id");
        command.DownloadSource.Should().BeEmpty();
        command.Series.Should().ContainSingle();
        command.Series![0].SeriesId.Should().Be("731");
    }

    [Fact]
    public void Parse_json_upload_reads_dramabox_download_source()
    {
        var command = TikTokRemoteCommandParser.Parse("""
            {"command":"tiktok_upload_series","titles":["The Crown"],"download_source":"dramabox","series_id":"dramabox:9001"}
            """);

        command!.DownloadSource.Should().Be("dramabox");
    }

    [Fact]
    public void Parse_json_upload_command_supports_all_accounts()
    {
        var command = TikTokRemoteCommandParser.Parse("""
            {"command":"upload_tiktok_series","titles":["剧名A"],"accounts":"全部"}
            """);

        command.Should().NotBeNull();
        command!.Command.Should().Be(TikTokRemoteCommandNames.UploadSeries);
        command.AllAccounts.Should().BeTrue();
        command.HasMultiAccountSelection.Should().BeTrue();
    }

    [Fact]
    public void Help_card_builder_returns_interactive_card_json()
    {
        var cardJson = TikTokRemoteHelpCardBuilder.BuildCommandTutorialCardJson();

        cardJson.Should().Contain("\"wide_screen_mode\":true");
        cardJson.Should().Contain("TikTok 上传命令教程");
        cardJson.Should().Contain("账号: 全部");
        cardJson.Should().Contain("generate_proof_material");
    }

    [Fact]
    public void Build_upload_run_options_uses_python_remote_defaults()
    {
        var options = TikTokRemoteRunOptions.BuildTikTokUploadRunOptions();

        options.EnabledSteps.Should().Equal(
            QueueStepRegistry.Download,
            QueueStepRegistry.RewriteInfo,
            QueueStepRegistry.GeneratePoster,
            QueueStepRegistry.SmallVideoRepair,
            QueueStepRegistry.MaterialValidate,
            QueueStepRegistry.UploadSeries);
        options.EnabledSteps.Should().NotContain(QueueStepRegistry.GenerateProofMaterial);
        options.AutoArchiveAfterUpload.Should().BeFalse();
        options.ForceRerunCompletedSteps.Should().BeFalse();
    }

    [Fact]
    public void Build_upload_run_options_allows_command_steps_and_option_override()
    {
        var command = new TikTokRemoteCommand(
            TikTokRemoteCommandNames.UploadSeries,
            EnabledSteps:
            [
                QueueStepRegistry.MaterialValidate,
                QueueStepRegistry.GenerateProofMaterial,
                QueueStepRegistry.UploadSeries,
            ],
            QueueOptions: new Dictionary<string, object?>
            {
                ["auto_archive_after_upload"] = true,
                ["force_rerun_completed_steps"] = "true",
                ["prefer_upload_when_ready"] = 1,
                ["sync_management_on_upload_success"] = true,
            });

        var options = TikTokRemoteRunOptions.BuildTikTokUploadRunOptions(command);

        options.EnabledSteps.Should().Equal(
            QueueStepRegistry.GenerateProofMaterial,
            QueueStepRegistry.MaterialValidate,
            QueueStepRegistry.UploadSeries);
        options.AutoArchiveAfterUpload.Should().BeTrue();
        options.ForceRerunCompletedSteps.Should().BeTrue();
        options.ToPersistentDictionary().Should().NotContainKey("prefer_upload_when_ready");
        options.SyncManagementAfterUpload.Should().BeTrue();
    }
}
