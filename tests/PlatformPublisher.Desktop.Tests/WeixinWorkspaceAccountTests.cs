using System.Text.Json.Nodes;
using ChannelsPublisher.Core.Services;
using Microsoft.Data.Sqlite;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.Services;
using PlatformPublisher.Desktop.ViewModels;
using PlatformPublisher.Persistence;
using PlatformPublisher.Weixin.Publishing;
using Xunit;
using PublishAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.Tests;

public sealed class WeixinWorkspaceAccountTests
{
    [Fact]
    public void WorkspaceSeparatesKuaishouOnlyLegacyDualAndNewWeixinAccounts()
    {
        var session = Path.GetTempFileName();
        try
        {
            var kuaishou = new PublishAccount
            {
                Id = "kuaishou", Name = "快手分账个人", Nickname = "快手昵称",
                KuaishouAccountEdition = "personal", LastLoginAt = DateTimeOffset.Now,
            };
            var dual = new PublishAccount
            {
                Id = "dual", Name = "共享名称", Nickname = "快手昵称", WeixinNickname = "视频号昵称",
                KuaishouAccountEdition = "personal", WeixinAuthStatePath = session,
            };
            var pending = new PublishAccount { Id = "new", Name = "新视频号", IsWeixinWorkspaceAccount = true };
            Assert.False(WeixinWorkspaceAccounts.IsVisible(kuaishou, hasWeixinJobs: true));
            Assert.Null(WeixinWorkspaceAccounts.LastLogin(kuaishou));
            Assert.True(WeixinWorkspaceAccounts.IsVisible(dual));
            Assert.Equal("视频号昵称", WeixinWorkspaceAccounts.DisplayName(dual));
            Assert.True(WeixinWorkspaceAccounts.IsVisible(pending));
            Assert.False(WeixinWorkspaceAccounts.HasSavedSession(pending));
            Assert.True(WeixinWorkspaceAccounts.IsVisible(new PublishAccount { Id = "legacy" }, hasWeixinJobs: true));
        }
        finally { File.Delete(session); }
    }

    [Fact]
    public void LegacyDirectoriesOnlyCarryOverWhenTheyDoNotOverlapKuaishou()
    {
        var old = Path.Combine(Path.GetTempPath(), "legacy-drama-root");
        var separate = Path.Combine(Path.GetTempPath(), "video-channel-root");
        var kuaishou = new PublishAccount { WorkRootDirectory = old, KuaishouAccountEdition = "personal" };
        var weixin = new PublishAccount { IsWeixinWorkspaceAccount = true, WorkRootDirectory = old + Path.DirectorySeparatorChar };
        Assert.Equal("", WeixinWorkspaceAccounts.WorkDirectory(weixin, [kuaishou, weixin]));
        Assert.Equal(old + Path.DirectorySeparatorChar, weixin.WorkRootDirectory);

        weixin.WeixinWorkRootDirectory = old;
        Assert.Equal(old, WeixinWorkspaceAccounts.WorkDirectory(weixin, [kuaishou, weixin]));
        Assert.Equal(separate, WeixinWorkspaceAccounts.WorkDirectory(new PublishAccount { WorkRootDirectory = separate }, [kuaishou]));

        weixin.WeixinWorkRootDirectory = "";
        Assert.Equal("", WeixinWorkspaceAccounts.WorkDirectory(weixin, []));
    }

    [Fact]
    public void PendingUploadOnlyIncludesValidatedProjectsWithoutAnUploadInProgress()
    {
        var job = new PublishJob
        {
            Platform = PublishPlatform.WeixinChannel,
            StepStates = new()
            {
                ["material-validate"] = new() { Key = "material-validate", Label = "素材校验", Status = PublishJobStepStatus.Succeeded },
                ["weixin-upload"] = new() { Key = "weixin-upload", Label = "上传剧集", Status = PublishJobStepStatus.Pending },
            },
        };
        var row = new PublishJobRowViewModel(job, loadArtifacts: false, loadTableInfo: false);
        Assert.True(row.IsReadyForWeixinUpload);
        Assert.Equal("等待上传", row.WeixinCurrentStep);
        job.StepStates["weixin-upload"].Status = PublishJobStepStatus.Running;
        Assert.False(row.IsReadyForWeixinUpload);
        Assert.Equal("上传剧集", row.WeixinCurrentStep);
        job.StepStates["weixin-upload"].Status = PublishJobStepStatus.Pending;
        job.Status = PublishJobStatus.Blocked;
        Assert.False(row.IsReadyForWeixinUpload);
    }

    [Theory]
    [InlineData(PublishJobStepStatus.Succeeded, true)]
    [InlineData(PublishJobStepStatus.Pending, false)]
    public void RetiredRemuxFailureUsesRecordedValidationAndDoesNotReschedule(
        PublishJobStepStatus validationStatus, bool readyForUpload)
    {
        var job = CreateLegacyRemuxJob();
        job.StepStates["material-validate"] = new()
            { Key = "material-validate", Label = "素材校验", Status = validationStatus };

        Assert.True(WeixinPublishQueueSteps.RecoverRetiredRemuxFailure(job));
        Assert.False(WeixinPublishQueueSteps.RecoverRetiredRemuxFailure(job));
        Assert.Equal(PublishJobStatus.Pending, job.Status);
        Assert.Null(job.ScheduledAt);
        Assert.Equal(PublishJobStepStatus.Failed, job.StepStates["upload-remux"].Status);
        Assert.Equal("重封装失败", job.StepStates["upload-remux"].Message);
        var row = new PublishJobRowViewModel(job, loadArtifacts: false, loadTableInfo: false);
        Assert.Equal(readyForUpload, row.IsReadyForWeixinUpload);
        Assert.Equal(readyForUpload ? "等待上传" : "0/1", row.WeixinCurrentStep);
        Assert.DoesNotContain(row.WeixinStepDetails, line => line.Contains("重封装"));
    }

    [Fact]
    public void RetiredRemuxDoesNotHideAnActiveFailureOrAnUnrelatedJobFailure()
    {
        var job = CreateLegacyRemuxJob();
        var validation = new PublishJobStepState
            { Key = "material-validate", Label = "素材校验", Status = PublishJobStepStatus.Failed };
        job.StepStates[validation.Key] = validation;

        Assert.False(WeixinPublishQueueSteps.RecoverRetiredRemuxFailure(job));
        var row = new PublishJobRowViewModel(job, loadArtifacts: false, loadTableInfo: false);
        Assert.Equal("素材校验", row.WeixinCurrentStep);
        Assert.False(row.IsReadyForWeixinUpload);
        job.StepStates.Remove(validation.Key);
        job.StatusMessage = "上传失败";
        Assert.False(WeixinPublishQueueSteps.RecoverRetiredRemuxFailure(job));
        Assert.Equal(PublishJobStatus.Failed, job.Status);
    }

    [Fact]
    public void RetiredRemuxDoesNotReopenAnUploadedJobOrChangeAnotherPlatform()
    {
        var job = CreateLegacyRemuxJob();
        var uploadedAt = DateTimeOffset.UtcNow.AddDays(-1);
        job.StepStates["weixin-upload"] = new()
        {
            Key = "weixin-upload", Label = "上传剧集",
            Status = PublishJobStepStatus.Succeeded, UpdatedAt = uploadedAt,
        };

        Assert.True(WeixinPublishQueueSteps.RecoverRetiredRemuxFailure(job));
        Assert.Equal(PublishJobStatus.Succeeded, job.Status);
        Assert.Equal(uploadedAt, job.LastCompletedAt);
        Assert.False(new PublishJobRowViewModel(job, loadArtifacts: false, loadTableInfo: false).IsReadyForWeixinUpload);

        var kuaishou = CreateLegacyRemuxJob();
        kuaishou.Platform = PublishPlatform.KuaishouPersonalRevenue;
        Assert.False(WeixinPublishQueueSteps.RecoverRetiredRemuxFailure(kuaishou));
        Assert.Equal(PublishJobStatus.Failed, kuaishou.Status);
    }

    [Fact]
    public async Task WorkflowSettingsIgnoreRetiredRemuxAndKeepOtherSavedOptions()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var json = new JsonSettingStore(new PlatformDatabase(Path.Combine(directory.FullName, "settings.db")));
            json.Save("weixin.workflow", new JsonObject
            {
                ["remuxEnabled"] = true, ["transcodeEnabled"] = false, ["materialValidateEnabled"] = true,
            });
            var store = new WeixinWorkflowSettingsStore(json);
            var settings = await store.LoadAsync();

            Assert.False(settings.TranscodeEnabled);
            Assert.True(settings.MaterialValidateEnabled);
            await store.SaveAsync(settings);
            Assert.True(json.TryLoad<JsonObject>("weixin.workflow", out var saved));
            Assert.False(saved!.ContainsKey("remuxEnabled"));
            Assert.False(saved["transcodeEnabled"]!.GetValue<bool>());
            Assert.True(saved["materialValidateEnabled"]!.GetValue<bool>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static PublishJob CreateLegacyRemuxJob() => new()
    {
        Platform = PublishPlatform.WeixinChannel,
        Status = PublishJobStatus.Failed,
        StatusMessage = "重封装失败",
        ScheduledAt = DateTimeOffset.UtcNow.AddDays(-1),
        StepStates = new()
        {
            ["upload-remux"] = new()
                { Key = "upload-remux", Label = "无损重封装", Status = PublishJobStepStatus.Failed, Message = "重封装失败" },
        },
    };
}
