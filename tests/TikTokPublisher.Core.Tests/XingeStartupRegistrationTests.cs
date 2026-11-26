using TikTokPublisher.Ui.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class XingeStartupRegistrationTests
{
    [Fact]
    public async Task RegistersBeforeHistoryAndRenewsPresenceWithSyncingState()
    {
        var full = CreateSnapshot();
        var history = new TaskCompletionSource<XingeRemoteRegistrationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await XingeRemoteCommandService.RegisterStartupAsync(
            full,
            ct =>
            {
                Assert.Equal(1, registered);
                return history.Task.WaitAsync(ct);
            },
            (pending, _) =>
            {
                Assert.Equal("profile-1", pending.AccountProfiles[0].Id);
                Assert.True(pending.AccountProfiles[0].AuthReady);
                Assert.True(pending.DispatchSyncing);
                Assert.Null(pending.AccountProfiles[0].UploadedToday);
                if (++registered == 2)
                    history.TrySetResult(full);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(25),
            timeout.Token);

        Assert.Equal(2, registered);
        Assert.Same(full, result);
        Assert.Single(result.AccountProfiles[0].UploadedToday!);
    }

    [Fact]
    public async Task HistoryFailureLeavesDispatchDisabled()
    {
        var registered = new List<XingeRemoteRegistrationSnapshot>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => XingeRemoteCommandService.RegisterStartupAsync(
            CreateSnapshot(),
            _ => Task.FromException<XingeRemoteRegistrationSnapshot>(new IOException("scan failed")),
            (pending, _) =>
            {
                registered.Add(pending);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(30),
            timeout.Token));

        Assert.True(Assert.Single(registered).DispatchSyncing);
    }

    private static XingeRemoteRegistrationSnapshot CreateSnapshot() =>
        new("workspace", "profile-1", "hgoffline",
        [
            new("profile-1", "Test account", true, true,
                [new("series-1", "Test series", "2026-11-23T20:00:00+08:00")]),
        ]);
}
