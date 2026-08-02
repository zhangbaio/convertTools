using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
    public async Task Start_QueuesCurrentSnapshotImmediately()
    {
        var requestBodies = new ConcurrentQueue<string>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            requestBodies.Enqueue(await request.Content!.ReadAsStringAsync());
            return Json(HttpStatusCode.OK, """{"ok":true}""");
        }));
        var service = CreateService(http);
        var store = CreateStoreWithAccounts([
            new TikTokAccountProfile
            {
                Id = "acct-a",
                TiktokLoginEmail = "account-a@example.test",
                TiktokProofCopyrightCompanyName = "武汉速视科技有限公司",
            },
        ]);

        using var coordinator = new TikTokAccountInventorySyncCoordinator(
            store,
            service,
            static (_, _) => Task.CompletedTask,
            TimeSpan.Zero);
        coordinator.Start();

        await WaitUntilAsync(() => requestBodies.Count >= 1);

        requestBodies.TryPeek(out var body).Should().BeTrue();
        var account = JsonDocument.Parse(body!).RootElement
            .GetProperty("accounts")[0];
        account.GetProperty("client_account_id").GetString().Should().Be("acct-a");
        account.GetProperty("tiktok_username").GetString().Should().Be("account-a@example.test");
        account.GetProperty("subject_company").GetString().Should().Be("武汉速视科技有限公司");
    }

    [Fact]
    public async Task AccountsChanged_AfterSubjectCompanyUpdate_QueuesLatestSnapshot()
    {
        var requestBodies = new ConcurrentQueue<string>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            requestBodies.Enqueue(await request.Content!.ReadAsStringAsync());
            return Json(HttpStatusCode.OK, """{"ok":true}""");
        }));
        var service = CreateService(http);
        var account = new TikTokAccountProfile
        {
            Id = "acct-a",
            TiktokLoginEmail = "account-a@example.test",
            TiktokProofCopyrightCompanyName = "旧主体公司",
        };
        var store = CreateStoreWithAccounts([account]);

        using var coordinator = new TikTokAccountInventorySyncCoordinator(
            store,
            service,
            static (_, _) => Task.CompletedTask,
            TimeSpan.Zero);
        coordinator.Start();
        await WaitUntilAsync(() => requestBodies.Count >= 1);

        account.TiktokProofCopyrightCompanyName = "新主体公司";
        RaiseAccountsChanged(store);

        await WaitUntilAsync(() => requestBodies.Count >= 2);
        var latestBody = requestBodies.Last();
        var latestAccount = JsonDocument.Parse(latestBody).RootElement
            .GetProperty("accounts")[0];
        latestAccount.GetProperty("subject_company").GetString().Should().Be("新主体公司");
    }

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

    private static TikTokManagementAccountSnapshotSyncService CreateService(HttpClient http) =>
        new(
            http,
            () => new ClientSettings { AuthServerUrl = "https://manage.example" },
            () => new LicenseState
            {
                AccountUsername = "software-user",
                MachineId = "machine-a",
                Token = "signed-token",
            });

    private static AccountStore CreateStoreWithAccounts(IReadOnlyList<TikTokAccountProfile> accounts)
    {
        var store = new AccountStore();
        var accountList = (List<TikTokAccountProfile>)typeof(AccountStore)
            .GetField("_accounts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        accountList.Clear();
        accountList.AddRange(accounts);
        typeof(AccountStore)
            .GetField("_activeAccountId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(store, accounts.FirstOrDefault()?.Id ?? "");
        return store;
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

    private static void RaiseAccountsChanged(AccountStore store)
    {
        typeof(AccountStore)
            .GetMethod("NotifyAccountsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
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
