using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TikTokAccountInventorySyncCoordinatorTestCollection
{
    public const string Name = nameof(TikTokAccountInventorySyncCoordinatorTestCollection);
}

[Collection(TikTokAccountInventorySyncCoordinatorTestCollection.Name)]
public sealed class TikTokAccountInventorySyncCoordinatorTests
{
    [Fact]
    public async Task LicenseStateChanged_AfterCorruptAccountsLoad_DoesNotSendEmptySnapshot()
    {
        var requestCount = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(Json(HttpStatusCode.OK, """{"ok":true}"""));
        }));
        var service = new TikTokManagementAccountSnapshotSyncService(
            http,
            () => new ClientSettings { AuthServerUrl = "https://manage.example" },
            () => new LicenseState
            {
                AccountUsername = "software-user",
                MachineId = "machine-a",
                Token = "signed-token",
            });
        var store = new AccountStore();

        // AccountStore.Load sets this guard to false after accounts.json cannot be parsed.
        // Set that postcondition directly so this test never touches the real user data path.
        SetCanSyncAccountSnapshot(store, false);

        using var coordinator = new TikTokAccountInventorySyncCoordinator(
            store,
            service,
            static (_, _) => Task.CompletedTask,
            TimeSpan.Zero);
        coordinator.Start();

        RaiseLicenseStateChanged();
        await Task.Delay(250);

        Volatile.Read(ref requestCount).Should().Be(0,
            "a corrupt accounts file must quarantine snapshot sync even after authorization changes");
    }

    private static void SetCanSyncAccountSnapshot(AccountStore store, bool value)
    {
        var setter = typeof(AccountStore)
            .GetProperty(nameof(AccountStore.CanSyncAccountSnapshot))!
            .GetSetMethod(nonPublic: true)!;
        setter.Invoke(store, [value]);
    }

    private static void RaiseLicenseStateChanged()
    {
        typeof(LicenseStore)
            .GetMethod("NotifyStateChanged", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
