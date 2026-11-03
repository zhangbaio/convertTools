using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class XingeUploadedTodayScanTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspace;

    public XingeUploadedTodayScanTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "xinge-upload-scan-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // 临时目录被占用时不影响断言。
        }
    }

    [Fact]
    public void Collect_shares_one_workspace_and_keeps_uploads_without_project_directory()
    {
        var missingProject = Path.Combine(_workspace, "missing-project");
        WorkspaceQueueDatabase.Save(_workspace, new[]
        {
            new QueueProjectItem
            {
                ProjectDir = missingProject,
                AccountProfileId = "acct-a",
                OriginalTitle = "原剧",
                DisplayName = "原剧",
                UploadCompletedAt = DateTimeOffset.Now.ToString("o"),
            },
        });

        var result = XingeUploadedTodayScan.Collect(new[]
        {
            Seed("acct-a"),
            Seed("acct-b"),
            new XingeRemoteAccountSeed("acct-c", "无目录", "", false, true, "", "", ""),
        }, includeExecutionHistory: false);

        result["acct-a"].Should().ContainSingle();
        result["acct-a"]![0].Title.Should().Be("原剧");
        result["acct-a"]![0].AccountProfileId.Should().Be("acct-a");
        result["acct-b"].Should().BeEmpty();
        result["acct-c"].Should().BeEmpty();
        Directory.Exists(missingProject).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(3, 30)]
    [InlineData(15, 15)]
    [InlineData(90, 60)]
    public void NormalizeXingePollIntervalSeconds_uses_thirty_second_default(int input, int expected) =>
        ClientSettingsDefaults.NormalizeXingePollIntervalSeconds(input).Should().Be(expected);

    private XingeRemoteAccountSeed Seed(string id) =>
        new(id, id, "", false, true, _workspace, "", "");
}
