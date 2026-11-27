using ChannelsPublisher.Core.Services;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Desktop.ViewModels;
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
}
