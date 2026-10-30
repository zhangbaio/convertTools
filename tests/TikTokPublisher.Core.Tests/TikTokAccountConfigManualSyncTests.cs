using System.Text.Json.Nodes;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokAccountConfigManualSyncTests
{
    [Fact]
    public void Push_defaults_upload_local_accounts_and_merge_differences()
    {
        var preview = TikTokAccountConfigManualSync.BuildPreview(
            [
                Local("acct-local", "only-local@example.com", "local-contract"),
                Local("acct-both", "both@example.com", "local-contract", "local-secret"),
            ],
            [
                Remote("acct-cloud", "only-cloud@example.com", 1, "cloud-contract"),
                Remote("acct-both-cloud", "both@example.com", 4, "cloud-contract", "cloud-secret"),
            ],
            TikTokAccountSyncDirection.PushToCloud,
            "user-b",
            EmptyContext(),
            _ => false);

        preview.Rows.Should().ContainSingle(row => row.TikTokUsername == "only-local@example.com")
            .Which.UploadLocal.Should().BeTrue();
        preview.Rows.Should().ContainSingle(row => row.TikTokUsername == "only-cloud@example.com")
            .Which.DeleteCloud.Should().BeFalse();
        var different = preview.Rows.Should().ContainSingle(row => row.Kind == TikTokAccountSyncRowKind.Different).Subject;
        different.ContentChoice.Should().Be(TikTokAccountSyncContentChoice.MergeLocalFirst);
        different.Difference.Should().Contain("合同").And.Contain("密码");
    }

    [Fact]
    public void Pull_defaults_do_not_add_cloud_accounts_or_delete_local_accounts()
    {
        var preview = TikTokAccountConfigManualSync.BuildPreview(
            [Local("acct-local", "only-local@example.com", "local-contract")],
            [Remote("acct-cloud", "only-cloud@example.com", 1, "cloud-contract")],
            TikTokAccountSyncDirection.PullFromCloud,
            "user-b",
            EmptyContext(),
            _ => false);

        preview.Rows.Single(row => row.Kind == TikTokAccountSyncRowKind.LocalOnly).UploadLocal.Should().BeFalse();
        preview.Rows.Single(row => row.Kind == TikTokAccountSyncRowKind.LocalOnly).DeleteLocal.Should().BeFalse();
        preview.Rows.Single(row => row.Kind == TikTokAccountSyncRowKind.CloudOnly).AddFromCloud.Should().BeFalse();
    }

    [Fact]
    public void Merge_keeps_local_values_and_fills_empty_fields_from_cloud()
    {
        var merged = TikTokAccountConfigSyncEngine.MergePreferLocal(
            new JsonObject { ["tiktokContractId"] = "local-contract", ["tiktokProxyHost"] = "" },
            new JsonObject { ["tiktokContractId"] = "cloud-contract", ["tiktokProxyHost"] = "10.0.0.8" });

        merged["tiktokContractId"]!.GetValue<string>().Should().Be("local-contract");
        merged["tiktokProxyHost"]!.GetValue<string>().Should().Be("10.0.0.8");
    }

    [Fact]
    public async Task Pull_merge_updates_empty_fields_and_keeps_workspace_and_password()
    {
        var local = Local("acct-local", "both@example.com", "local-contract", "local-secret");
        local.LastWorkspace = @"E:\keep";
        local.TiktokUploadProfilePath = @"E:\keep";
        var remote = Remote("acct-cloud", "both@example.com", 2, "cloud-contract", "cloud-secret");
        remote.Config["tiktokProxyHost"] = "10.0.0.8";
        var preview = TikTokAccountConfigManualSync.BuildPreview(
            [local],
            [remote],
            TikTokAccountSyncDirection.PullFromCloud,
            "user-b",
            EmptyContext(),
            _ => false);
        var client = new FakeClient([remote]);

        var result = await TikTokAccountConfigManualSync.ApplyAsync(
            [local],
            preview,
            client,
            EmptyContext(),
            _ => false,
            CancellationToken.None);

        result.AccountsChanged.Should().BeTrue();
        var account = result.Accounts.Should().ContainSingle().Subject;
        account.TiktokContractId.Should().Be("local-contract");
        account.TiktokLoginPassword.Should().Be("local-secret");
        account.TiktokProxyHost.Should().Be("10.0.0.8");
        account.TiktokUploadProfilePath.Should().Be(@"E:\keep");
        client.Pushes.Should().BeEmpty();
    }

    [Fact]
    public async Task Pull_leaves_cloud_only_accounts_until_they_are_checked()
    {
        var remote = Remote("acct-cloud", "only-cloud@example.com", 1, "cloud-contract");
        var preview = TikTokAccountConfigManualSync.BuildPreview(
            [],
            [remote],
            TikTokAccountSyncDirection.PullFromCloud,
            "user-b",
            EmptyContext(),
            _ => false);
        var client = new FakeClient([remote]);

        var skipped = await TikTokAccountConfigManualSync.ApplyAsync([], preview, client, EmptyContext(), _ => false, CancellationToken.None);
        skipped.Accounts.Should().BeEmpty();

        preview.Rows[0].AddFromCloud = true;
        var added = await TikTokAccountConfigManualSync.ApplyAsync([], preview, client, EmptyContext(@"D:\work"), _ => false, CancellationToken.None);
        added.Accounts.Should().ContainSingle();
        added.Accounts[0].TiktokLoginEmail.Should().Be("only-cloud@example.com");
        added.Accounts[0].TiktokUploadProfilePath.Should().Be(Path.Combine(@"D:\work", "only-cloud@example.com"));
    }

    [Fact]
    public async Task Push_uploads_local_account_and_does_not_delete_cloud_account()
    {
        var local = Local("acct-local", "only-local@example.com", "local-contract", "local-secret");
        var remote = Remote("acct-cloud", "only-cloud@example.com", 3, "cloud-contract");
        var preview = TikTokAccountConfigManualSync.BuildPreview(
            [local],
            [remote],
            TikTokAccountSyncDirection.PushToCloud,
            "user-b",
            EmptyContext(),
            _ => false);
        var client = new FakeClient([remote]);

        var result = await TikTokAccountConfigManualSync.ApplyAsync(
            [local],
            preview,
            client,
            EmptyContext(),
            _ => false,
            CancellationToken.None);

        result.Ok.Should().BeTrue();
        client.Deletes.Should().BeEmpty();
        client.Pushes.Should().ContainSingle();
        client.Pushes[0].CloudAccountId.Should().Be("acct-local");
        client.Pushes[0].TikTokUsername.Should().Be("only-local@example.com");
        client.Pushes[0].Config.ContainsKey("lastWorkspace").Should().BeFalse();
        client.Pushes[0].Secrets["tiktokLoginPassword"]!.GetValue<string>().Should().Be("local-secret");
    }

    [Fact]
    public async Task Apply_asks_again_when_cloud_revision_changes()
    {
        var local = Local("acct-local", "both@example.com", "local-contract");
        var remote = Remote("acct-cloud", "both@example.com", 2, "cloud-contract");
        var preview = TikTokAccountConfigManualSync.BuildPreview(
            [local],
            [remote],
            TikTokAccountSyncDirection.PullFromCloud,
            "user-b",
            EmptyContext(),
            _ => false);
        var client = new FakeClient([Remote("acct-cloud", "both@example.com", 3, "cloud-contract")]);

        var result = await TikTokAccountConfigManualSync.ApplyAsync(
            [local],
            preview,
            client,
            EmptyContext(),
            _ => false,
            CancellationToken.None);

        result.Stale.Should().BeTrue();
        result.Preview!.Rows.Should().ContainSingle();
        result.Preview.Rows[0].ContentChoice.Should().Be(TikTokAccountSyncContentChoice.MergeLocalFirst);
    }

    private static TikTokAccountProfile Local(string id, string email, string contract, string password = "") => new()
    {
        Id = id,
        Name = email,
        TiktokLoginEmail = email,
        TiktokLoginPassword = password,
        TiktokContractId = contract,
    };

    private static RemoteTikTokAccountConfig Remote(string id, string email, int revision, string contract, string password = "") => new()
    {
        ClientAccountId = id,
        TikTokUsername = email,
        Revision = revision,
        UpdatedAt = "2026-10-30T21:00:00",
        Config = new JsonObject
        {
            ["name"] = email,
            ["tiktokLoginEmail"] = email,
            ["tiktokContractId"] = contract,
        },
        Secrets = new JsonObject
        {
            ["tiktokLoginPassword"] = password,
            ["tiktokProxyPassword"] = "",
        },
    };

    private static TikTokAccountConfigSyncContext EmptyContext(string root = "") => new()
    {
        WorkspaceRoot = root,
        LoginStatePath = profile => Path.Combine(root, profile.Id, "tiktok_auth_state.json"),
        SealDestination = (profile, name) => Path.Combine(root, profile.Id, name),
        ReadFile = _ => null,
        WriteFile = (_, _) => { },
        DeleteFile = _ => { },
        FileModifiedAt = _ => null,
        IsAccountBusy = _ => false,
        CreateDirectory = _ => { },
    };

    private sealed class FakeClient(IReadOnlyList<RemoteTikTokAccountConfig> accounts) : ITikTokAccountConfigSyncClient
    {
        public List<TikTokAccountConfigPushRequest> Pushes { get; } = [];
        public List<string> Deletes { get; } = [];
        public List<RemoteTikTokAccountConfig> Accounts { get; } = accounts.ToList();

        public Task<TikTokAccountConfigPullResult> PullAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TikTokAccountConfigPullResult(true, false, "", Accounts.ToArray()));

        public Task<TikTokAccountConfigPushResult> PushAsync(
            TikTokAccountConfigPushRequest request,
            CancellationToken cancellationToken)
        {
            Pushes.Add(request);
            return Task.FromResult(new TikTokAccountConfigPushResult(
                true, false, false, "", "", request.CloudAccountId, request.BaseRevision + 1, false, null));
        }

        public Task<TikTokAccountConfigDeleteResult> DeleteAsync(string cloudAccountId, CancellationToken cancellationToken)
        {
            Deletes.Add(cloudAccountId);
            return Task.FromResult(new TikTokAccountConfigDeleteResult(true, false, ""));
        }

        public Task<byte[]?> DownloadAsync(string cloudAccountId, string resource, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(null);
    }
}
