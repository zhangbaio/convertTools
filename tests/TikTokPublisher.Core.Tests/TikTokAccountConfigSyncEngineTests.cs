using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokAccountConfigSyncEngineTests
{
    [Fact]
    public void Extract_keeps_publish_settings_and_moves_secrets_out_of_config()
    {
        var profile = new TikTokAccountProfile
        {
            Id = "acct-local",
            Name = "账号1",
            TiktokLoginEmail = "a@b.com",
            TiktokLoginPassword = "secret-password",
            TiktokProxyPassword = "proxy-secret",
            TiktokContractId = "contract-1",
            ProfileDir = @"C:\Users\me\profile",
            TiktokStorageStatePath = @"C:\Users\me\profile\tiktok_auth_state.json",
            LastWorkspace = @"E:\drama",
            TiktokUploadProfilePath = @"E:\drama",
            TiktokExternalBrowserCdpEndpoint = "http://127.0.0.1:9222",
        };

        var config = TikTokAccountConfigSyncEngine.ExtractConfig(profile);
        var secrets = TikTokAccountConfigSyncEngine.ExtractSecrets(profile);

        config["tiktokContractId"]!.GetValue<string>().Should().Be("contract-1");
        config.ContainsKey("profileDir").Should().BeFalse();
        config.ContainsKey("lastWorkspace").Should().BeFalse();
        config.ContainsKey("tiktokUploadProfilePath").Should().BeFalse();
        config.ContainsKey("tiktokStorageStatePath").Should().BeFalse();
        config.ContainsKey("tiktokExternalBrowserCdpEndpoint").Should().BeFalse();
        config.ContainsKey("tiktokLoginPassword").Should().BeFalse();
        secrets["tiktokLoginPassword"]!.GetValue<string>().Should().Be("secret-password");
        secrets["tiktokProxyPassword"]!.GetValue<string>().Should().Be("proxy-secret");
    }

    [Fact]
    public void Merge_keeps_each_side_change_and_uses_the_newer_value_when_both_changed()
    {
        var baseline = new JsonObject { ["tiktokContractId"] = "old", ["name"] = "旧名称" };
        var local = new JsonObject { ["tiktokContractId"] = "from-local", ["name"] = "旧名称" };
        var remote = new JsonObject { ["tiktokContractId"] = "old", ["name"] = "来自另一台电脑" };

        var merged = TikTokAccountConfigSyncEngine.Merge(local, baseline, remote, remoteWinsConflicts: true);

        merged["tiktokContractId"]!.GetValue<string>().Should().Be("from-local");
        merged["name"]!.GetValue<string>().Should().Be("来自另一台电脑");
    }

    [Fact]
    public async Task New_computer_restores_account_login_state_and_local_workspace()
    {
        var loginState = "{\"cookies\":[{\"name\":\"session\",\"value\":\"abc\"}]}"u8.ToArray();
        var root = Path.Combine(Path.GetTempPath(), "tiktok-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var client = new FakeConfigClient
            {
                Accounts =
                {
                    RemoteAccount(
                        "acct-cloud",
                        "a@b.com",
                        revision: 3,
                        config: new JsonObject
                        {
                            ["name"] = "账号1",
                            ["tiktokLoginEmail"] = "a@b.com",
                            ["tiktokContractId"] = "contract-1",
                        },
                        secrets: new JsonObject
                        {
                            ["tiktokLoginPassword"] = "secret-password",
                            ["tiktokProxyPassword"] = "",
                        },
                        loginSha: Sha(loginState)),
                },
            };
            client.LoginStates["acct-cloud"] = loginState;
            IReadOnlyList<TikTokAccountProfile>? committed = null;

            var result = await TikTokAccountConfigSyncEngine.RunAsync(
                [
                    new TikTokAccountProfile
                    {
                        Id = "acct-default",
                        Name = "默认账号",
                    },
                ],
                new TikTokAccountConfigSyncState(),
                client,
                Context(root, files),
                list =>
                {
                    committed = list;
                    return true;
                },
                CancellationToken.None);

            result.Committed.Should().BeTrue();
            client.Pushes.Should().BeEmpty();
            committed.Should().ContainSingle();
            var account = committed![0];
            account.Id.Should().Be("acct-cloud");
            account.TiktokLoginPassword.Should().Be("secret-password");
            account.TiktokContractId.Should().Be("contract-1");
            account.TiktokUploadProfilePath.Should().Be(Path.Combine(root, "账号1"));
            account.LastWorkspace.Should().Be(account.TiktokUploadProfilePath);
            files.Should().ContainKey(Path.Combine(root, "profiles", account.Id, "tiktok_auth_state.json"));
            files[Path.Combine(root, "profiles", account.Id, "tiktok_auth_state.json")].Should().Equal(loginState);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Local_edit_uploads_config_without_machine_paths()
    {
        var profile = new TikTokAccountProfile
        {
            Id = "acct-a",
            Name = "账号1",
            TiktokLoginEmail = "a@b.com",
            TiktokLoginPassword = "secret-password",
            TiktokContractId = "contract-2",
            LastWorkspace = @"E:\keep",
            TiktokUploadProfilePath = @"E:\keep",
            ProfileDir = @"C:\profile",
            UpdatedAt = "2026-10-30T22:00:00",
        };
        var baselineConfig = TikTokAccountConfigSyncEngine.ExtractConfig(profile);
        baselineConfig["tiktokContractId"] = "contract-1";
        var client = new FakeConfigClient
        {
            Accounts =
            {
                RemoteAccount(
                    "acct-a",
                    "a@b.com",
                    revision: 4,
                    config: baselineConfig,
                    secrets: TikTokAccountConfigSyncEngine.ExtractSecrets(profile)),
            },
        };

        var result = await TikTokAccountConfigSyncEngine.RunAsync(
            [profile],
            new TikTokAccountConfigSyncState
            {
                Accounts =
                {
                    ["acct-a"] = new TikTokAccountConfigSyncEntry
                    {
                        CloudAccountId = "acct-a",
                        Revision = 4,
                        BaseConfigJson = baselineConfig.ToJsonString(),
                        BaseSecretsJson = TikTokAccountConfigSyncEngine.ExtractSecrets(profile).ToJsonString(),
                    },
                },
            },
            client,
            Context(Path.GetTempPath(), new Dictionary<string, byte[]>()),
            _ => true,
            CancellationToken.None);

        result.Committed.Should().BeTrue();
        client.Pushes.Should().ContainSingle();
        var pushed = client.Pushes[0];
        pushed.BaseRevision.Should().Be(4);
        pushed.Config["tiktokContractId"]!.GetValue<string>().Should().Be("contract-2");
        pushed.Config.ContainsKey("profileDir").Should().BeFalse();
        pushed.Config.ContainsKey("lastWorkspace").Should().BeFalse();
        pushed.Secrets["tiktokLoginPassword"]!.GetValue<string>().Should().Be("secret-password");
    }

    [Fact]
    public async Task Same_username_keeps_local_id_and_workspace()
    {
        var local = new TikTokAccountProfile
        {
            Id = "acct-local",
            Name = "本机名称",
            TiktokLoginEmail = "a@b.com",
            TiktokContractId = "local-contract",
            LastWorkspace = @"E:\keep",
            TiktokUploadProfilePath = @"E:\keep",
            UpdatedAt = "2026-10-30T20:00:00",
        };
        IReadOnlyList<TikTokAccountProfile>? committed = null;
        var client = new FakeConfigClient
        {
            Accounts =
            {
                RemoteAccount(
                    "acct-cloud",
                    "A@B.com",
                    revision: 2,
                    updatedAt: "2026-10-30T21:00:00",
                    config: new JsonObject
                    {
                        ["name"] = "云端名称",
                        ["tiktokLoginEmail"] = "a@b.com",
                        ["tiktokContractId"] = "cloud-contract",
                    },
                    secrets: new JsonObject
                    {
                        ["tiktokLoginPassword"] = "from-cloud",
                        ["tiktokProxyPassword"] = "",
                    }),
            },
        };

        var result = await TikTokAccountConfigSyncEngine.RunAsync(
            [local],
            new TikTokAccountConfigSyncState(),
            client,
            Context(Path.GetTempPath(), new Dictionary<string, byte[]>()),
            list =>
            {
                committed = list;
                return true;
            },
            CancellationToken.None);

        result.Committed.Should().BeTrue();
        var account = committed.Should().ContainSingle().Subject;
        account.Id.Should().Be("acct-local");
        account.TiktokUploadProfilePath.Should().Be(@"E:\keep");
        account.TiktokContractId.Should().Be("cloud-contract");
        account.TiktokLoginPassword.Should().Be("from-cloud");
        result.State.Accounts["acct-local"].CloudAccountId.Should().Be("acct-cloud");
    }

    [Fact]
    public async Task Busy_account_is_not_removed_when_cloud_deletes_it()
    {
        var committed = false;
        var client = new FakeConfigClient
        {
            Accounts =
            {
                new RemoteTikTokAccountConfig
                {
                    ClientAccountId = "acct-a",
                    TikTokUsername = "a@b.com",
                    Revision = 5,
                    Deleted = true,
                },
            },
        };

        var result = await TikTokAccountConfigSyncEngine.RunAsync(
            [
                new TikTokAccountProfile
                {
                    Id = "acct-a",
                    TiktokLoginEmail = "a@b.com",
                    LastWorkspace = @"E:\keep",
                    TiktokUploadProfilePath = @"E:\keep",
                },
            ],
            new TikTokAccountConfigSyncState
            {
                Accounts =
                {
                    ["acct-a"] = new TikTokAccountConfigSyncEntry { CloudAccountId = "acct-a", Revision = 4 },
                },
            },
            client,
            Context(Path.GetTempPath(), new Dictionary<string, byte[]>(), busy: _ => true),
            _ =>
            {
                committed = true;
                return true;
            },
            CancellationToken.None);

        committed.Should().BeFalse();
        result.Message.Should().Contain("正在上传，暂不删除");
        client.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_local_account_deletes_the_cloud_copy_and_does_not_restore_it()
    {
        var client = new FakeConfigClient
        {
            Accounts =
            {
                RemoteAccount("acct-a", "a@b.com", revision: 2, config: new JsonObject(), secrets: new JsonObject()),
            },
        };

        var result = await TikTokAccountConfigSyncEngine.RunAsync(
            [],
            new TikTokAccountConfigSyncState
            {
                Accounts =
                {
                    ["acct-a"] = new TikTokAccountConfigSyncEntry { CloudAccountId = "acct-a", Revision = 2 },
                },
            },
            client,
            Context(Path.GetTempPath(), new Dictionary<string, byte[]>()),
            _ => true,
            CancellationToken.None);

        client.Deletes.Should().Equal("acct-a");
        result.State.Accounts.Should().BeEmpty();
        result.Committed.Should().BeFalse();
    }

    private static RemoteTikTokAccountConfig RemoteAccount(
        string id,
        string username,
        int revision,
        JsonObject config,
        JsonObject secrets,
        string? updatedAt = null,
        string loginSha = "") => new()
    {
        ClientAccountId = id,
        TikTokUsername = username,
        Revision = revision,
        UpdatedAt = updatedAt ?? "2026-10-30T21:00:00",
        Config = config,
        Secrets = secrets,
        LoginStateSha256 = loginSha,
    };

    private static TikTokAccountConfigSyncContext Context(
        string root,
        Dictionary<string, byte[]> files,
        Func<string, bool>? busy = null) => new()
    {
        WorkspaceRoot = root,
        LoginStatePath = profile => Path.Combine(root, "profiles", profile.Id, "tiktok_auth_state.json"),
        SealDestination = (profile, name) => Path.Combine(root, "profiles", profile.Id, name),
        ReadFile = path => files.TryGetValue(path, out var bytes) ? bytes : null,
        WriteFile = (path, bytes) => files[path] = bytes,
        DeleteFile = path => files.Remove(path),
        FileModifiedAt = _ => null,
        IsAccountBusy = busy ?? (_ => false),
        CreateDirectory = path => Directory.CreateDirectory(path),
        StillCurrent = () => true,
    };

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private sealed class FakeConfigClient : ITikTokAccountConfigSyncClient
    {
        public List<RemoteTikTokAccountConfig> Accounts { get; } = [];
        public List<TikTokAccountConfigPushRequest> Pushes { get; } = [];
        public List<string> Deletes { get; } = [];
        public Dictionary<string, byte[]> LoginStates { get; } = new(StringComparer.Ordinal);

        public Task<TikTokAccountConfigPullResult> PullAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new TikTokAccountConfigPullResult(true, false, "", Accounts.ToArray()));

        public Task<TikTokAccountConfigPushResult> PushAsync(
            TikTokAccountConfigPushRequest request,
            CancellationToken cancellationToken)
        {
            Pushes.Add(request);
            return Task.FromResult(new TikTokAccountConfigPushResult(
                true,
                false,
                false,
                "",
                "",
                request.CloudAccountId,
                request.BaseRevision + 1,
                false,
                null));
        }

        public Task<TikTokAccountConfigDeleteResult> DeleteAsync(
            string cloudAccountId,
            CancellationToken cancellationToken)
        {
            Deletes.Add(cloudAccountId);
            return Task.FromResult(new TikTokAccountConfigDeleteResult(true, false, ""));
        }

        public Task<byte[]?> DownloadAsync(
            string cloudAccountId,
            string resource,
            CancellationToken cancellationToken) =>
            Task.FromResult(resource == "login-state" && LoginStates.TryGetValue(cloudAccountId, out var bytes)
                ? bytes
                : null);
    }
}
