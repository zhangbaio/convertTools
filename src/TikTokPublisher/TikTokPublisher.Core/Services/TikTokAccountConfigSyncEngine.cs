using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed class TikTokAccountConfigSyncState
{
    public Dictionary<string, TikTokAccountConfigSyncEntry> Accounts { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TikTokAccountConfigSyncEntry
{
    public string CloudAccountId { get; set; } = "";
    public int Revision { get; set; }
    public string LoginStateSha256 { get; set; } = "";
    public string SealSha256 { get; set; } = "";
    public string BaseConfigJson { get; set; } = "";
    public string BaseSecretsJson { get; set; } = "";
}

public sealed class TikTokAccountConfigSyncContext
{
    public string WorkspaceRoot { get; init; } = "";
    public required Func<TikTokAccountProfile, string> LoginStatePath { get; init; }
    public required Func<TikTokAccountProfile, string, string> SealDestination { get; init; }
    public required Func<string, byte[]?> ReadFile { get; init; }
    public required Action<string, byte[]> WriteFile { get; init; }
    public required Action<string> DeleteFile { get; init; }
    public required Func<string, DateTime?> FileModifiedAt { get; init; }
    public required Func<string, bool> IsAccountBusy { get; init; }
    public Action<string>? CreateDirectory { get; init; }
    public Func<bool>? StillCurrent { get; init; }
}

public sealed record TikTokAccountConfigSyncRun(
    bool Ok,
    bool ShouldRetry,
    bool Committed,
    string Message,
    TikTokAccountConfigSyncState State);

/// <summary>合并本机和云端的 TikTok 账号配置。本机路径、工作目录和浏览器端口留在本机。</summary>
public static class TikTokAccountConfigSyncEngine
{
    public const int MaxLoginStateBytes = 4_000_000;
    public const int MaxSealBytes = 8_000_000;

    private static readonly JsonSerializerOptions ProfileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions StateJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly HashSet<string> LocalOnlyKeys = new(StringComparer.Ordinal)
    {
        "id",
        "profileDir",
        "tiktokStorageStatePath",
        "lastWorkspace",
        "lastDownloadWorkspace",
        "tiktokUploadProfilePath",
        "tiktokArchiveRootDir",
        "tiktokExcelReportPath",
        "tiktokProofSealPath",
        "tiktokExternalBrowserCdpEndpoint",
        "tiktokCopyrightMaterialFilePath",
        "updatedAt",
        "status",
    };

    private static readonly string[] SecretKeys = ["tiktokLoginPassword", "tiktokProxyPassword"];

    public static JsonObject ExtractConfig(TikTokAccountProfile profile)
    {
        var node = JsonSerializer.SerializeToNode(profile, ProfileJson)?.AsObject() ?? new JsonObject();
        foreach (var key in LocalOnlyKeys)
            node.Remove(key);
        foreach (var key in SecretKeys)
            node.Remove(key);
        return node;
    }

    public static JsonObject ExtractSecrets(TikTokAccountProfile profile) => new()
    {
        ["tiktokLoginPassword"] = profile.TiktokLoginPassword ?? "",
        ["tiktokProxyPassword"] = profile.TiktokProxyPassword ?? "",
    };

    public static JsonObject Merge(JsonObject local, JsonObject? baseline, JsonObject remote, bool remoteWinsConflicts)
    {
        var result = new JsonObject();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in local)
            keys.Add(property.Key);
        if (baseline is not null)
        {
            foreach (var property in baseline)
                keys.Add(property.Key);
        }
        foreach (var property in remote)
            keys.Add(property.Key);

        foreach (var key in keys)
        {
            var localValue = local[key];
            var remoteValue = remote[key];
            if (baseline is null)
            {
                if (JsonNode.DeepEquals(localValue, remoteValue))
                    Copy(result, key, localValue);
                else
                    Copy(result, key, remoteWinsConflicts ? remoteValue : localValue);
                continue;
            }

            if (!local.ContainsKey(key) && baseline.ContainsKey(key) && JsonNode.DeepEquals(remoteValue, baseline[key]))
            {
                Copy(result, key, remoteValue);
                continue;
            }

            var localChanged = !JsonNode.DeepEquals(localValue, baseline[key]);
            var remoteChanged = !JsonNode.DeepEquals(remoteValue, baseline[key]);
            if (!localChanged)
                Copy(result, key, remoteValue);
            else if (!remoteChanged)
                Copy(result, key, localValue);
            else
                Copy(result, key, remoteWinsConflicts ? remoteValue : localValue);
        }

        return result;
    }

    /// <summary>本机字段有内容就用本机，本机为空才补云端。</summary>
    public static JsonObject MergePreferLocal(JsonObject local, JsonObject remote)
    {
        var result = new JsonObject();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in local)
            keys.Add(property.Key);
        foreach (var property in remote)
            keys.Add(property.Key);
        foreach (var key in keys)
        {
            var localValue = local[key];
            var remoteValue = remote[key];
            if (HasContent(localValue))
                Copy(result, key, localValue);
            else if (HasContent(remoteValue))
                Copy(result, key, remoteValue);
            else if (localValue is not null)
                Copy(result, key, localValue);
            else
                Copy(result, key, remoteValue);
        }

        return result;
    }

    public static bool HasContent(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return false;
            case JsonObject obj:
                return obj.Count > 0;
            case JsonArray array:
                return array.Count > 0;
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                return !string.IsNullOrWhiteSpace(text);
            case JsonValue jsonValue when jsonValue.TryGetValue<bool>(out _):
                return true;
            case JsonValue jsonValue when jsonValue.TryGetValue<double>(out _):
                return true;
            case JsonValue jsonValue when jsonValue.TryGetValue<JsonElement>(out var element):
                return element.ValueKind switch
                {
                    JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
                    JsonValueKind.Null or JsonValueKind.Undefined => false,
                    JsonValueKind.Array => element.GetArrayLength() > 0,
                    JsonValueKind.Object => element.EnumerateObject().Any(),
                    _ => true,
                };
            default:
                return true;
        }
    }

    public static TikTokAccountProfile ApplyPortable(
        TikTokAccountProfile local,
        JsonObject config,
        JsonObject secrets)
    {
        var updated = Apply(local, config, secrets);
        updated.Id = local.Id;
        updated.Status = local.Status;
        updated.ProfileDir = local.ProfileDir;
        updated.TiktokStorageStatePath = local.TiktokStorageStatePath;
        updated.LastWorkspace = local.LastWorkspace;
        updated.LastDownloadWorkspace = local.LastDownloadWorkspace;
        updated.TiktokUploadProfilePath = local.TiktokUploadProfilePath;
        updated.TiktokArchiveRootDir = local.TiktokArchiveRootDir;
        updated.TiktokExcelReportPath = local.TiktokExcelReportPath;
        updated.TiktokProofSealPath = local.TiktokProofSealPath;
        updated.TiktokExternalBrowserCdpEndpoint = local.TiktokExternalBrowserCdpEndpoint;
        updated.TiktokCopyrightMaterialFilePath = local.TiktokCopyrightMaterialFilePath;
        return updated;
    }

    public static string? EnsureSyncWorkspace(
        TikTokAccountProfile profile,
        IReadOnlyList<TikTokAccountProfile> others,
        string? workspaceRoot) =>
        AssignWorkspace(profile, others, workspaceRoot);

    public static TikTokAccountConfigSyncState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new TikTokAccountConfigSyncState();
            var state = JsonSerializer.Deserialize<TikTokAccountConfigSyncState>(File.ReadAllText(path), StateJson)
                        ?? new TikTokAccountConfigSyncState();
            state.Accounts = new Dictionary<string, TikTokAccountConfigSyncEntry>(
                state.Accounts ?? new Dictionary<string, TikTokAccountConfigSyncEntry>(),
                StringComparer.Ordinal);
            return state;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new TikTokAccountConfigSyncState();
        }
    }

    public static void SaveState(string path, TikTokAccountConfigSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, StateJson));
    }

    public static async Task<TikTokAccountConfigSyncRun> RunAsync(
        IReadOnlyList<TikTokAccountProfile> accounts,
        TikTokAccountConfigSyncState state,
        ITikTokAccountConfigSyncClient client,
        TikTokAccountConfigSyncContext context,
        Func<IReadOnlyList<TikTokAccountProfile>, bool> tryCommit,
        CancellationToken cancellationToken)
    {
        var pull = await client.PullAsync(cancellationToken).ConfigureAwait(false);
        if (!pull.Ok)
        {
            return new TikTokAccountConfigSyncRun(
                false,
                pull.ShouldRetry,
                false,
                string.IsNullOrWhiteSpace(pull.Message) ? "账号同步失败" : $"账号同步失败：{pull.Message}",
                state);
        }

        var local = accounts.Select(CloneProfile).ToList();
        var notes = new List<string>();
        var mutated = false;
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var suppressCloudIds = new HashSet<string>(StringComparer.Ordinal);
        var pushes = new List<PendingPush>();

        foreach (var pair in state.Accounts.ToList())
        {
            if (local.Any(account => account.Id == pair.Key))
                continue;
            suppressCloudIds.Add(pair.Value.CloudAccountId);
            var deleted = await client.DeleteAsync(pair.Value.CloudAccountId, cancellationToken).ConfigureAwait(false);
            if (!deleted.Ok)
            {
                notes.Add(string.IsNullOrWhiteSpace(deleted.Message) ? "删除云端账号失败" : deleted.Message);
                continue;
            }
            state.Accounts.Remove(pair.Key);
        }

        foreach (var remote in pull.Accounts.Where(account => account.Deleted))
        {
            if (suppressCloudIds.Contains(remote.ClientAccountId))
                continue;
            var match = FindLocal(local, state, remote, allowUsername: false);
            if (match is null)
                continue;
            if (context.IsAccountBusy(match.Id))
            {
                notes.Add($"「{DisplayName(match)}」正在上传，暂不删除");
                handled.Add(match.Id);
                continue;
            }
            local.Remove(match);
            state.Accounts.Remove(match.Id);
            handled.Add(match.Id);
            mutated = true;
        }

        foreach (var remote in pull.Accounts.Where(account => !account.Deleted))
        {
            if (suppressCloudIds.Contains(remote.ClientAccountId))
                continue;
            var match = FindLocal(local, state, remote, allowUsername: true);
            if (match is null)
            {
                var created = CreateFromRemote(remote);
                local.Add(created);
                mutated = true;
                var entry = new TikTokAccountConfigSyncEntry
                {
                    CloudAccountId = remote.ClientAccountId,
                    Revision = remote.Revision,
                    BaseConfigJson = remote.Config.ToJsonString(),
                    BaseSecretsJson = remote.Secrets.ToJsonString(),
                };
                state.Accounts[created.Id] = entry;
                handled.Add(created.Id);
                var createdPush = CreatePush(
                    created,
                    entry,
                    remote.ClientAccountId,
                    remote.Revision,
                    remote.Config,
                    remote.Secrets,
                    context,
                    remote,
                    configChanged: false);
                createdPush.Profile = created;
                if (createdPush.NeedsLocalWrite)
                    pushes.Add(createdPush);
                continue;
            }

            handled.Add(match.Id);
            var merged = MergeAccount(match, remote, state, context);
            Replace(local, merged.Profile);
            state.Accounts[merged.Profile.Id] = merged.Entry;
            if (merged.Push is not null)
                pushes.Add(merged.Push);
            if (merged.Changed)
                mutated = true;
        }

        foreach (var account in local.ToList())
        {
            if (handled.Contains(account.Id) || string.IsNullOrWhiteSpace(Username(account)))
                continue;
            var entry = EntryFor(state, account);
            if (entry.CloudAccountId.Length > 0 &&
                state.Accounts.Any(pair => pair.Key != account.Id && pair.Value.CloudAccountId == entry.CloudAccountId))
            {
                notes.Add($"「{DisplayName(account)}」与已同步账号重复，未再上传");
                continue;
            }
            var config = ExtractConfig(account);
            var secrets = ExtractSecrets(account);
            var localPush = CreatePush(
                account,
                entry,
                entry.CloudAccountId.Length > 0 ? entry.CloudAccountId : account.Id,
                entry.Revision,
                config,
                secrets,
                context,
                remote: null,
                configChanged: true);
            localPush.Profile = account;
            pushes.Add(localPush);
            mutated = true;
        }

        var missingWorkspaceRoot = false;
        foreach (var account in local.Where(item => !string.IsNullOrWhiteSpace(Username(item))))
        {
            var assigned = AssignWorkspace(account, local, context.WorkspaceRoot);
            if (assigned is null)
                missingWorkspaceRoot = true;
            else if (assigned.Length > 0)
            {
                mutated = true;
                context.CreateDirectory?.Invoke(assigned);
            }
        }
        if (missingWorkspaceRoot)
            notes.Add("请在系统设置填写账号同步工作根目录");

        if (RemoveDisposableDefault(local, state))
            mutated = true;
        await ApplyBlobTransfersAsync(pushes, client, context, notes, cancellationToken).ConfigureAwait(false);

        if (!mutated && pushes.Count == 0)
        {
            var unchangedCount = local.Count(account => !string.IsNullOrWhiteSpace(Username(account)));
            var unchangedMessage = $"账号同步：已同步 {unchangedCount} 个";
            if (notes.Count > 0)
                unchangedMessage += "。" + string.Join("。", notes.Distinct());
            return new TikTokAccountConfigSyncRun(notes.Count == 0, false, false, unchangedMessage, state);
        }

        if (context.StillCurrent?.Invoke() == false)
        {
            return new TikTokAccountConfigSyncRun(false, true, false, "账号同步：本地账号刚被修改，将重新同步", state);
        }

        foreach (var push in pushes)
        {
            var loginDirectory = Path.GetDirectoryName(push.LoginPath);
            if (!string.IsNullOrWhiteSpace(loginDirectory))
                context.CreateDirectory?.Invoke(loginDirectory);
            if (push.LoginBytes is not null && push.LoginPath.Length > 0)
                context.WriteFile(push.LoginPath, push.LoginBytes);
            if (push.ClearLogin && push.LoginPath.Length > 0)
                context.DeleteFile(push.LoginPath);
            if (push.SealBytes is not null && push.SealPath.Length > 0)
                context.WriteFile(push.SealPath, push.SealBytes);
            if (push.WorkspaceDirectory.Length > 0)
                context.CreateDirectory?.Invoke(push.WorkspaceDirectory);
        }

        var committed = tryCommit(local);
        if (!committed)
        {
            return new TikTokAccountConfigSyncRun(false, true, false, "账号同步：本地账号刚被修改，将重新同步", state);
        }

        for (var index = 0; index < pushes.Count; index++)
        {
            var push = pushes[index];
            if (!push.Upload)
                continue;
            var result = await client.PushAsync(push.Request, cancellationToken).ConfigureAwait(false);
            if (!result.Ok && result.Conflict && result.Account is not null)
            {
                if (result.Account.Deleted)
                {
                    if (!context.IsAccountBusy(push.LocalId))
                    {
                        local.RemoveAll(account => account.Id == push.LocalId);
                        state.Accounts.Remove(push.LocalId);
                        tryCommit(local);
                    }
                    continue;
                }

                var current = local.FirstOrDefault(account => account.Id == push.LocalId);
                if (current is null)
                    continue;
                var retried = MergeAccount(current, result.Account, state, context);
                Replace(local, retried.Profile);
                state.Accounts[retried.Profile.Id] = retried.Entry;
                tryCommit(local);
                if (retried.Push is null)
                    continue;
                push = retried.Push;
                result = await client.PushAsync(push.Request, cancellationToken).ConfigureAwait(false);
            }

            if (!result.Ok)
            {
                notes.Add(string.IsNullOrWhiteSpace(result.Message) ? "上传账号配置失败" : result.Message);
                continue;
            }

            var saved = EntryFor(state, local.First(account => account.Id == push.LocalId));
            saved.CloudAccountId = result.ClientAccountId;
            saved.Revision = result.Revision;
            saved.BaseConfigJson = push.Request.Config.ToJsonString();
            saved.BaseSecretsJson = push.Request.Secrets.ToJsonString();
            saved.LoginStateSha256 = push.ResultLoginSha;
            saved.SealSha256 = push.ResultSealSha;
            state.Accounts[push.LocalId] = saved;
        }

        var syncedCount = local.Count(account => !string.IsNullOrWhiteSpace(Username(account)));
        var message = $"账号同步：已同步 {syncedCount} 个";
        if (notes.Count > 0)
            message += "。" + string.Join("。", notes.Distinct());
        return new TikTokAccountConfigSyncRun(notes.Count == 0, false, true, message, state);
    }

    private static MergedAccount MergeAccount(
        TikTokAccountProfile local,
        RemoteTikTokAccountConfig remote,
        TikTokAccountConfigSyncState state,
        TikTokAccountConfigSyncContext context)
    {
        var entry = EntryFor(state, local);
        if (string.IsNullOrWhiteSpace(entry.CloudAccountId))
            entry.CloudAccountId = remote.ClientAccountId;
        var localConfig = ExtractConfig(local);
        var localSecrets = ExtractSecrets(local);
        var baselineConfig = string.IsNullOrWhiteSpace(entry.BaseConfigJson) ? null : ParseObject(entry.BaseConfigJson);
        var baselineSecrets = string.IsNullOrWhiteSpace(entry.BaseSecretsJson) ? null : ParseObject(entry.BaseSecretsJson);
        var remoteWins = RemoteClockWins(remote.UpdatedAt, local.UpdatedAt);
        var mergedConfig = Merge(localConfig, baselineConfig, remote.Config, remoteWins);
        var mergedSecrets = Merge(localSecrets, baselineSecrets, remote.Secrets, remoteWins);
        var profile = Apply(local, mergedConfig, mergedSecrets);
        profile.Id = local.Id;
        profile.ProfileDir = local.ProfileDir;
        profile.TiktokStorageStatePath = local.TiktokStorageStatePath;
        profile.LastWorkspace = local.LastWorkspace;
        profile.LastDownloadWorkspace = local.LastDownloadWorkspace;
        profile.TiktokUploadProfilePath = local.TiktokUploadProfilePath;
        profile.TiktokArchiveRootDir = local.TiktokArchiveRootDir;
        profile.TiktokExcelReportPath = local.TiktokExcelReportPath;
        profile.TiktokExternalBrowserCdpEndpoint = local.TiktokExternalBrowserCdpEndpoint;
        profile.TiktokCopyrightMaterialFilePath = local.TiktokCopyrightMaterialFilePath;
        profile.Status = local.Status;
        var configChanged = !JsonNode.DeepEquals(mergedConfig, localConfig) || !JsonNode.DeepEquals(mergedSecrets, localSecrets);
        if (configChanged)
        {
            profile.UpdatedAt = JsonNode.DeepEquals(mergedConfig, remote.Config) && JsonNode.DeepEquals(mergedSecrets, remote.Secrets)
                ? remote.UpdatedAt
                : DateTimeOffset.Now.ToString("o");
        }
        else
        {
            profile.TiktokProofSealPath = local.TiktokProofSealPath;
        }

        var uploadConfig = !JsonNode.DeepEquals(mergedConfig, remote.Config) || !JsonNode.DeepEquals(mergedSecrets, remote.Secrets);
        entry.CloudAccountId = remote.ClientAccountId;
        if (!uploadConfig)
        {
            entry.Revision = remote.Revision;
            entry.BaseConfigJson = mergedConfig.ToJsonString();
            entry.BaseSecretsJson = mergedSecrets.ToJsonString();
        }

        var push = CreatePush(
            profile,
            entry,
            remote.ClientAccountId,
            remote.Revision,
            mergedConfig,
            mergedSecrets,
            context,
            remote,
            uploadConfig);
        push.Profile = profile;
        var pending = push.NeedsLocalWrite || push.Upload ? push : null;
        return new MergedAccount(profile, entry, pending, configChanged || pending is not null);
    }

    private static PendingPush CreatePush(
        TikTokAccountProfile profile,
        TikTokAccountConfigSyncEntry entry,
        string cloudAccountId,
        int baseRevision,
        JsonObject config,
        JsonObject secrets,
        TikTokAccountConfigSyncContext? context,
        RemoteTikTokAccountConfig? remote)
    {
        var loginPath = context?.LoginStatePath(profile) ?? "";
        var localLogin = loginPath.Length == 0 ? null : context!.ReadFile(loginPath);
        var localLoginSha = Sha256(localLogin);
        var loginChoice = remote is null
            ? localLogin is null ? BlobChoice.None : BlobChoice.Upload
            : DecideBlob(
                localLoginSha,
                entry.LoginStateSha256,
                remote.LoginStateSha256,
                loginPath.Length == 0 ? null : context!.FileModifiedAt(loginPath),
                remote.LoginStateUpdatedAt);
        var sealPath = profile.TiktokProofSealPath ?? "";
        var localSeal = sealPath.Length == 0 || context is null ? null : context.ReadFile(sealPath);
        var localSealSha = Sha256(localSeal);
        var sealChoice = remote is null
            ? localSeal is null ? BlobChoice.None : BlobChoice.Upload
            : DecideBlob(
                localSealSha,
                entry.SealSha256,
                remote.SealSha256,
                sealPath.Length == 0 || context is null ? null : context.FileModifiedAt(sealPath),
                remote.SealUpdatedAt);

        var request = new TikTokAccountConfigPushRequest
        {
            CloudAccountId = cloudAccountId,
            BaseRevision = baseRevision,
            TikTokUsername = FirstNonEmpty(Username(profile), remote?.TikTokUsername ?? ""),
            Config = config,
            Secrets = secrets,
        };
        var pending = new PendingPush
        {
            LocalId = profile.Id,
            Request = request,
            LoginPath = loginPath,
            LoginChoice = loginChoice,
            SealChoice = sealChoice,
            ResultLoginSha = loginChoice == BlobChoice.Upload ? localLoginSha : remote?.LoginStateSha256 ?? localLoginSha,
            ResultSealSha = sealChoice == BlobChoice.Upload ? localSealSha : remote?.SealSha256 ?? localSealSha,
            WorkspaceDirectory = "",
        };
        if (loginChoice == BlobChoice.Upload && localLogin is not null && localLogin.Length <= MaxLoginStateBytes)
            request.LoginState = localLogin;
        if (sealChoice == BlobChoice.Upload && localSeal is not null && localSeal.Length <= MaxSealBytes)
        {
            request.Seal = localSeal;
            request.SealFilename = Path.GetFileName(sealPath);
        }
        pending.Upload = request.LoginState is not null || request.Seal is not null;
        pending.SealFilename = remote?.SealFilename ?? request.SealFilename;
        if (loginChoice == BlobChoice.Clear)
            pending.ClearLogin = true;
        if (sealChoice == BlobChoice.Clear)
            profile.TiktokProofSealPath = "";
        return pending;
    }

    private static async Task ApplyBlobTransfersAsync(
        List<PendingPush> pushes,
        ITikTokAccountConfigSyncClient client,
        TikTokAccountConfigSyncContext context,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        foreach (var push in pushes)
        {
            if (push.LoginChoice == BlobChoice.Download)
            {
                var bytes = await client.DownloadAsync(push.Request.CloudAccountId, "login-state", cancellationToken)
                    .ConfigureAwait(false);
                if (bytes is null || !string.Equals(Sha256(bytes), push.ResultLoginSha, StringComparison.OrdinalIgnoreCase))
                    notes.Add("登录态下载失败");
                else
                    push.LoginBytes = bytes;
            }

            if (push.SealChoice != BlobChoice.Download)
                continue;
            var seal = await client.DownloadAsync(push.Request.CloudAccountId, "seal", cancellationToken)
                .ConfigureAwait(false);
            if (seal is null || !string.Equals(Sha256(seal), push.ResultSealSha, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("印章下载失败");
                continue;
            }

            var profile = push.Profile;
            if (profile is null)
                continue;
            var destination = context.SealDestination(profile, string.IsNullOrWhiteSpace(push.SealFilename) ? "seal.bin" : push.SealFilename);
            profile.TiktokProofSealPath = destination;
            push.SealPath = destination;
            push.SealBytes = seal;
        }
    }

    private static TikTokAccountProfile CreateFromRemote(RemoteTikTokAccountConfig remote)
    {
        var created = Apply(new TikTokAccountProfile(), remote.Config, remote.Secrets);
        created.Id = remote.ClientAccountId;
        created.CreatedAt = string.IsNullOrWhiteSpace(created.CreatedAt) ? DateTimeOffset.Now.ToString("o") : created.CreatedAt;
        created.UpdatedAt = remote.UpdatedAt;
        if (string.IsNullOrWhiteSpace(created.TiktokLoginEmail))
            created.TiktokLoginEmail = remote.TikTokUsername;
        if (string.IsNullOrWhiteSpace(created.Name))
            created.Name = FirstNonEmpty(remote.TikTokUsername, remote.ClientAccountId);
        return created;
    }

    private static string? AssignWorkspace(TikTokAccountProfile profile, IReadOnlyList<TikTokAccountProfile> others, string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(profile.TiktokUploadProfilePath) || !string.IsNullOrWhiteSpace(profile.LastWorkspace))
            return "";
        var root = (workspaceRoot ?? "").Trim();
        if (root.Length == 0)
            return null;
        var folder = SanitizeFolderName(FirstNonEmpty(profile.Name, profile.TiktokLoginEmail, profile.Id));
        var path = Path.Combine(root, folder);
        if (others.Any(account => account.Id != profile.Id && (PathEquals(account.TiktokUploadProfilePath, path) || PathEquals(account.LastWorkspace, path))))
            path = Path.Combine(root, folder + "-" + profile.Id);
        profile.TiktokUploadProfilePath = path;
        profile.LastWorkspace = path;
        return path;
    }

    private static bool RemoveDisposableDefault(List<TikTokAccountProfile> accounts, TikTokAccountConfigSyncState state)
    {
        if (accounts.Count(account => !string.IsNullOrWhiteSpace(Username(account))) == 0)
            return false;
        var removed = false;
        foreach (var account in accounts.Where(IsDisposableDefault).ToList())
        {
            accounts.Remove(account);
            state.Accounts.Remove(account.Id);
            removed = true;
        }
        return removed;
    }

    private static bool IsDisposableDefault(TikTokAccountProfile profile) =>
        string.Equals(profile.Name, "默认账号", StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(profile.TiktokLoginEmail) &&
        string.IsNullOrWhiteSpace(profile.TiktokLastLoginEmail) &&
        string.IsNullOrWhiteSpace(profile.LastWorkspace) &&
        string.IsNullOrWhiteSpace(profile.TiktokUploadProfilePath);

    private static TikTokAccountProfile Apply(TikTokAccountProfile local, JsonObject config, JsonObject secrets)
    {
        var node = JsonSerializer.SerializeToNode(local, ProfileJson)?.AsObject() ?? new JsonObject();
        foreach (var property in config)
        {
            if (LocalOnlyKeys.Contains(property.Key) || SecretKeys.Contains(property.Key))
                continue;
            node[property.Key] = property.Value?.DeepClone();
        }
        foreach (var key in SecretKeys)
        {
            if (secrets.ContainsKey(key))
                node[key] = secrets[key]?.DeepClone();
        }
        return node.Deserialize<TikTokAccountProfile>(ProfileJson) ?? local;
    }

    private static BlobChoice DecideBlob(
        string localSha,
        string baseSha,
        string remoteSha,
        DateTime? localModified,
        string remoteUpdatedAt)
    {
        if (string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
            return BlobChoice.None;
        if (string.Equals(localSha, baseSha, StringComparison.OrdinalIgnoreCase))
            return remoteSha.Length == 0 ? BlobChoice.Clear : BlobChoice.Download;
        if (remoteSha.Length == 0 || string.Equals(remoteSha, baseSha, StringComparison.OrdinalIgnoreCase))
            return localSha.Length == 0 ? BlobChoice.Clear : BlobChoice.Upload;
        return RemoteClockWins(remoteUpdatedAt, localModified?.ToString("o"))
            ? remoteSha.Length == 0 ? BlobChoice.Clear : BlobChoice.Download
            : localSha.Length == 0 ? BlobChoice.Clear : BlobChoice.Upload;
    }

    private static PendingPush CreatePush(
        TikTokAccountProfile profile,
        TikTokAccountConfigSyncEntry entry,
        string cloudAccountId,
        int baseRevision,
        JsonObject config,
        JsonObject secrets,
        TikTokAccountConfigSyncContext? context,
        RemoteTikTokAccountConfig? remote,
        bool configChanged)
    {
        var pending = CreatePush(profile, entry, cloudAccountId, baseRevision, config, secrets, context, remote);
        pending.Profile = profile;
        if (configChanged)
            pending.Upload = true;
        return pending;
    }

    private static TikTokAccountProfile? FindLocal(
        List<TikTokAccountProfile> accounts,
        TikTokAccountConfigSyncState state,
        RemoteTikTokAccountConfig remote,
        bool allowUsername)
    {
        var mapped = state.Accounts.FirstOrDefault(pair => pair.Value.CloudAccountId == remote.ClientAccountId);
        if (!string.IsNullOrWhiteSpace(mapped.Key))
        {
            var byCloud = accounts.FirstOrDefault(account => account.Id == mapped.Key);
            if (byCloud is not null)
                return byCloud;
        }

        var byId = accounts.FirstOrDefault(account => account.Id == remote.ClientAccountId);
        if (byId is not null)
            return byId;
        if (!allowUsername || string.IsNullOrWhiteSpace(remote.TikTokUsername))
            return null;
        var key = UsernameKey(remote.TikTokUsername);
        return accounts.FirstOrDefault(account => UsernameKey(Username(account)) == key);
    }

    private static TikTokAccountConfigSyncEntry EntryFor(TikTokAccountConfigSyncState state, TikTokAccountProfile account)
    {
        if (!state.Accounts.TryGetValue(account.Id, out var entry))
        {
            entry = new TikTokAccountConfigSyncEntry();
            state.Accounts[account.Id] = entry;
        }
        return entry;
    }

    private static void Replace(List<TikTokAccountProfile> accounts, TikTokAccountProfile profile)
    {
        var index = accounts.FindIndex(account => account.Id == profile.Id);
        if (index >= 0)
            accounts[index] = profile;
        else
            accounts.Add(profile);
    }

    public static string Username(TikTokAccountProfile profile) =>
        FirstNonEmpty(profile.TiktokLoginEmail, profile.TiktokLastLoginEmail);

    private static string UsernameKey(string value) => value.Trim().ToLowerInvariant();

    private static bool RemoteClockWins(string? remoteUpdatedAt, string? localUpdatedAt)
    {
        var remote = ParseTime(remoteUpdatedAt);
        var local = ParseTime(localUpdatedAt);
        if (remote is null)
            return false;
        if (local is null)
            return true;
        return remote >= local;
    }

    private static DateTime? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset))
            return offset.LocalDateTime;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
            return time;
        return null;
    }

    private static TikTokAccountProfile CloneProfile(TikTokAccountProfile profile)
    {
        var json = JsonSerializer.SerializeToNode(profile, ProfileJson);
        return json?.Deserialize<TikTokAccountProfile>(ProfileJson) ?? new TikTokAccountProfile { Id = profile.Id };
    }

    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void Copy(JsonObject target, string key, JsonNode? value)
    {
        if (value is null)
            return;
        target[key] = value.DeepClone();
    }

    private static string Sha256(byte[]? data) =>
        data is null ? "" : Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "account" : cleaned;
    }

    private static bool PathEquals(string? left, string right) =>
        string.Equals((left ?? "").Trim(), right, StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(TikTokAccountProfile profile) =>
        FirstNonEmpty(profile.Name, profile.TiktokLoginEmail, profile.Id);

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (text.Length > 0)
                return text;
        }
        return "";
    }

    private enum BlobChoice { None, Download, Upload, Clear }

    private sealed class PendingPush
    {
        public string LocalId { get; init; } = "";
        public required TikTokAccountConfigPushRequest Request { get; init; }
        public bool Upload { get; set; }
        public string LoginPath { get; init; } = "";
        public byte[]? LoginBytes { get; set; }
        public bool ClearLogin { get; set; }
        public BlobChoice LoginChoice { get; init; }
        public BlobChoice SealChoice { get; init; }
        public string SealPath { get; set; } = "";
        public byte[]? SealBytes { get; set; }
        public string SealFilename { get; set; } = "";
        public string ResultLoginSha { get; init; } = "";
        public string ResultSealSha { get; init; } = "";
        public string WorkspaceDirectory { get; set; } = "";
        public TikTokAccountProfile? Profile { get; set; }
        public bool NeedsLocalWrite =>
            Upload
            || LoginChoice is BlobChoice.Download or BlobChoice.Clear
            || SealChoice is BlobChoice.Download or BlobChoice.Clear
            || WorkspaceDirectory.Length > 0
            || LoginBytes is not null
            || SealBytes is not null;
    }

    private sealed record MergedAccount(
        TikTokAccountProfile Profile,
        TikTokAccountConfigSyncEntry Entry,
        PendingPush? Push,
        bool Changed);
}

internal static class TikTokAccountConfigJson
{
    public static JsonNode? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool? ReadOk(JsonNode? parsed)
    {
        if (parsed?["ok"] is not JsonValue value)
            return null;
        return value.TryGetValue<bool>(out var result) ? result : null;
    }

    public static string ReadMessage(JsonNode? parsed)
    {
        foreach (var key in new[] { "message", "error" })
        {
            var text = Text(parsed as JsonObject, key);
            if (text.Length > 0)
                return text;
        }
        return "";
    }

    public static RemoteTikTokAccountConfig ReadAccount(JsonObject node) => new()
    {
        ClientAccountId = Text(node, "client_account_id"),
        TikTokUsername = Text(node, "tiktok_username"),
        Revision = Int(node, "revision"),
        Deleted = node["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var isDeleted) && isDeleted,
        UpdatedAt = Text(node, "updated_at"),
        UpdatedByMachine = Text(node, "updated_by_machine"),
        Config = Object(node, "config"),
        Secrets = Object(node, "secrets"),
        LoginStateSha256 = Text(node, "login_state_sha256"),
        LoginStateUpdatedAt = Text(node, "login_state_updated_at"),
        SealSha256 = Text(node, "seal_sha256"),
        SealFilename = Text(node, "seal_filename"),
        SealUpdatedAt = Text(node, "seal_updated_at"),
    };

    private static JsonObject Object(JsonObject node, string key) =>
        node[key] as JsonObject ?? new JsonObject();

    public static string Text(JsonObject? node, string key)
    {
        if (node?[key] is not JsonValue value)
            return "";
        return value.TryGetValue<string>(out var text) ? text ?? "" : "";
    }

    public static int Int(JsonObject node, string key)
    {
        if (node[key] is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : 0;
    }

    public static bool Bool(JsonObject? node, string key)
    {
        if (node?[key] is not JsonValue value)
            return false;
        return value.TryGetValue<bool>(out var result) && result;
    }
}
