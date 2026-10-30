using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public enum TikTokAccountSyncDirection
{
    PushToCloud,
    PullFromCloud,
}

public enum TikTokAccountSyncRowKind
{
    LocalOnly,
    CloudOnly,
    Different,
}

public enum TikTokAccountSyncContentChoice
{
    Skip,
    PreferLocal,
    PreferCloud,
    MergeLocalFirst,
}

public sealed class TikTokAccountSyncRow
{
    public string TikTokUsername { get; init; } = "";
    public string Title { get; init; } = "";
    public string Difference { get; init; } = "";
    public TikTokAccountSyncRowKind Kind { get; init; }
    public string? LocalAccountId { get; init; }
    public string? CloudAccountId { get; init; }
    public int CloudRevision { get; init; }
    public bool LocalBusy { get; init; }
    public TikTokAccountSyncContentChoice ContentChoice { get; set; } = TikTokAccountSyncContentChoice.Skip;
    public bool UploadLocal { get; set; }
    public bool DeleteLocal { get; set; }
    public bool AddFromCloud { get; set; }
    public bool DeleteCloud { get; set; }
}

public sealed class TikTokAccountSyncPreview
{
    public string Fingerprint { get; init; } = "";
    public string TtUserName { get; init; } = "";
    public TikTokAccountSyncDirection Direction { get; init; }
    public int UnchangedCount { get; init; }
    public IReadOnlyList<TikTokAccountSyncRow> Rows { get; init; } = [];
}

public sealed class TikTokAccountSyncApplyResult
{
    public bool Ok { get; init; }
    public bool Stale { get; init; }
    public string Message { get; init; } = "";
    public TikTokAccountSyncPreview? Preview { get; init; }
    public IReadOnlyList<TikTokAccountProfile> Accounts { get; init; } = [];
    public bool AccountsChanged { get; init; }
}

/// <summary>手动对比本机和当前 TT 用户的云端账号，确认后再写入。</summary>
public static class TikTokAccountConfigManualSync
{
    public static TikTokAccountConfigSyncContext CreateMachineContext(string? workspaceRoot, Func<string, bool> isAccountBusy) =>
        new()
        {
            WorkspaceRoot = workspaceRoot ?? "",
            LoginStatePath = profile => EmbeddedBrowserLoginHelper.ResolveAuthPath(profile),
            SealDestination = (profile, name) =>
            {
                var directory = string.IsNullOrWhiteSpace(profile.ProfileDir)
                    ? AppPaths.ProfileDirFor(profile.Id)
                    : profile.ProfileDir;
                return Path.Combine(directory, name);
            },
            ReadFile = ReadFile,
            WriteFile = WriteFile,
            DeleteFile = DeleteFile,
            FileModifiedAt = path => File.Exists(path) ? File.GetLastWriteTime(path) : null,
            IsAccountBusy = isAccountBusy,
            CreateDirectory = path =>
            {
                if (!string.IsNullOrWhiteSpace(path))
                    Directory.CreateDirectory(path);
            },
        };

    public static async Task<(TikTokAccountSyncPreview? Preview, string Error)> LoadPreviewAsync(
        IReadOnlyList<TikTokAccountProfile> accounts,
        TikTokAccountSyncDirection direction,
        string ttUserName,
        ITikTokAccountConfigSyncClient client,
        TikTokAccountConfigSyncContext context,
        Func<string, bool> isAccountBusy,
        CancellationToken cancellationToken)
    {
        var pull = await client.PullAsync(cancellationToken).ConfigureAwait(false);
        if (!pull.Ok)
            return (null, string.IsNullOrWhiteSpace(pull.Message) ? "读取云端账号失败" : pull.Message);
        return (BuildPreview(accounts, pull.Accounts, direction, ttUserName, context, isAccountBusy), "");
    }

    public static TikTokAccountSyncPreview BuildPreview(
        IReadOnlyList<TikTokAccountProfile> accounts,
        IReadOnlyList<RemoteTikTokAccountConfig> remoteAccounts,
        TikTokAccountSyncDirection direction,
        string ttUserName,
        TikTokAccountConfigSyncContext context,
        Func<string, bool> isAccountBusy)
    {
        var remoteLive = remoteAccounts.Where(account => !account.Deleted && !string.IsNullOrWhiteSpace(account.TikTokUsername)).ToList();
        var remoteByUser = new Dictionary<string, RemoteTikTokAccountConfig>(StringComparer.Ordinal);
        foreach (var remote in remoteLive)
            remoteByUser.TryAdd(UsernameKey(remote.TikTokUsername), remote);

        var matchedRemote = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TikTokAccountSyncRow>();
        var unchanged = 0;
        foreach (var local in accounts)
        {
            var username = TikTokAccountConfigSyncEngine.Username(local);
            if (string.IsNullOrWhiteSpace(username))
            {
                rows.Add(new TikTokAccountSyncRow
                {
                    Kind = TikTokAccountSyncRowKind.LocalOnly,
                    LocalAccountId = local.Id,
                    Title = string.IsNullOrWhiteSpace(local.Name) ? local.Id : local.Name,
                    Difference = "没有 TikTok 用户名，不能同步",
                    LocalBusy = isAccountBusy(local.Id),
                });
                continue;
            }

            if (!remoteByUser.TryGetValue(UsernameKey(username), out var remote))
            {
                rows.Add(new TikTokAccountSyncRow
                {
                    Kind = TikTokAccountSyncRowKind.LocalOnly,
                    TikTokUsername = username,
                    Title = Display(local, username),
                    Difference = "只在本机",
                    LocalAccountId = local.Id,
                    LocalBusy = isAccountBusy(local.Id),
                    UploadLocal = direction == TikTokAccountSyncDirection.PushToCloud,
                });
                continue;
            }

            matchedRemote.Add(remote.ClientAccountId);
            var difference = DescribeDifference(local, remote, context);
            if (difference.Length == 0)
            {
                unchanged++;
                continue;
            }

            rows.Add(new TikTokAccountSyncRow
            {
                Kind = TikTokAccountSyncRowKind.Different,
                TikTokUsername = username,
                Title = Display(local, username),
                Difference = difference,
                LocalAccountId = local.Id,
                CloudAccountId = remote.ClientAccountId,
                CloudRevision = remote.Revision,
                LocalBusy = isAccountBusy(local.Id),
                ContentChoice = TikTokAccountSyncContentChoice.MergeLocalFirst,
            });
        }

        foreach (var remote in remoteLive.Where(account => !matchedRemote.Contains(account.ClientAccountId)))
        {
            rows.Add(new TikTokAccountSyncRow
            {
                Kind = TikTokAccountSyncRowKind.CloudOnly,
                TikTokUsername = remote.TikTokUsername,
                Title = remote.TikTokUsername,
                Difference = "只在管理系统",
                CloudAccountId = remote.ClientAccountId,
                CloudRevision = remote.Revision,
            });
        }

        return new TikTokAccountSyncPreview
        {
            Fingerprint = Fingerprint(remoteLive),
            TtUserName = (ttUserName ?? "").Trim(),
            Direction = direction,
            UnchangedCount = unchanged,
            Rows = rows,
        };
    }

    public static async Task<TikTokAccountSyncApplyResult> ApplyAsync(
        IReadOnlyList<TikTokAccountProfile> accounts,
        TikTokAccountSyncPreview confirmed,
        ITikTokAccountConfigSyncClient client,
        TikTokAccountConfigSyncContext context,
        Func<string, bool> isAccountBusy,
        CancellationToken cancellationToken)
    {
        var pull = await client.PullAsync(cancellationToken).ConfigureAwait(false);
        if (!pull.Ok)
        {
            return new TikTokAccountSyncApplyResult
            {
                Message = string.IsNullOrWhiteSpace(pull.Message) ? "读取云端账号失败" : pull.Message,
            };
        }

        var fresh = BuildPreview(accounts, pull.Accounts, confirmed.Direction, confirmed.TtUserName, context, isAccountBusy);
        if (!string.Equals(fresh.Fingerprint, confirmed.Fingerprint, StringComparison.Ordinal))
        {
            fresh = CopyChoices(fresh, confirmed);
            return new TikTokAccountSyncApplyResult
            {
                Stale = true,
                Preview = fresh,
                Message = "云端账号刚刚有变化，请重新确认",
            };
        }

        var local = accounts.Select(Clone).ToList();
        var remoteById = pull.Accounts.Where(account => !account.Deleted)
            .ToDictionary(account => account.ClientAccountId, StringComparer.Ordinal);
        var notes = new List<string>();
        var changed = false;
        foreach (var row in confirmed.Rows)
        {
            if (row.Kind == TikTokAccountSyncRowKind.LocalOnly)
            {
                changed |= await ApplyLocalOnlyAsync(row, local, client, context, isAccountBusy, notes, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (row.Kind == TikTokAccountSyncRowKind.CloudOnly)
            {
                changed |= await ApplyCloudOnlyAsync(row, local, remoteById, client, context, notes, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            changed |= await ApplyDifferentAsync(
                row,
                confirmed.Direction,
                local,
                remoteById,
                client,
                context,
                notes,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var account in local.Where(item => !string.IsNullOrWhiteSpace(TikTokAccountConfigSyncEngine.Username(item))))
        {
            var assigned = TikTokAccountConfigSyncEngine.EnsureSyncWorkspace(account, local, context.WorkspaceRoot);
            if (!string.IsNullOrWhiteSpace(assigned))
                context.CreateDirectory?.Invoke(assigned);
        }

        var message = changed
            ? "账号配置已按确认结果同步"
            : "没有写入。未勾选需要新增或删除的账号";
        if (notes.Count > 0)
            message += "。" + string.Join("。", notes.Distinct());
        return new TikTokAccountSyncApplyResult
        {
            Ok = notes.Count == 0,
            Message = message,
            Accounts = local,
            AccountsChanged = changed,
        };
    }

    private static async Task<bool> ApplyLocalOnlyAsync(
        TikTokAccountSyncRow row,
        List<TikTokAccountProfile> local,
        ITikTokAccountConfigSyncClient client,
        TikTokAccountConfigSyncContext context,
        Func<string, bool> isAccountBusy,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var account = local.FirstOrDefault(item => item.Id == row.LocalAccountId);
        if (account is null)
            return false;
        if (string.IsNullOrWhiteSpace(TikTokAccountConfigSyncEngine.Username(account)))
            return false;
        var changed = false;
        if (row.UploadLocal)
        {
            var pushed = await PushAccountAsync(account, null, 0, TikTokAccountConfigSyncEngine.ExtractConfig(account), TikTokAccountConfigSyncEngine.ExtractSecrets(account), includeLocalFiles: true, client, context, cancellationToken)
                .ConfigureAwait(false);
            if (!pushed)
                notes.Add($"「{row.Title}」上传失败");
            else
                changed = true;
        }

        if (!row.DeleteLocal)
            return changed;
        if (isAccountBusy(account.Id))
        {
            notes.Add($"「{row.Title}」正在上传，未从本机删除");
            return changed;
        }

        local.Remove(account);
        return true;
    }

    private static async Task<bool> ApplyCloudOnlyAsync(
        TikTokAccountSyncRow row,
        List<TikTokAccountProfile> local,
        Dictionary<string, RemoteTikTokAccountConfig> remoteById,
        ITikTokAccountConfigSyncClient client,
        TikTokAccountConfigSyncContext context,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var changed = false;
        if (row.DeleteCloud && !string.IsNullOrWhiteSpace(row.CloudAccountId))
        {
            var deleted = await client.DeleteAsync(row.CloudAccountId, cancellationToken).ConfigureAwait(false);
            if (!deleted.Ok)
                notes.Add(string.IsNullOrWhiteSpace(deleted.Message) ? $"「{row.Title}」云端删除失败" : deleted.Message);
            else
                changed = true;
        }

        if (!row.AddFromCloud || string.IsNullOrWhiteSpace(row.CloudAccountId) || !remoteById.TryGetValue(row.CloudAccountId, out var remote))
            return changed;
        var created = TikTokAccountConfigSyncEngine.ApplyPortable(new TikTokAccountProfile
        {
            Id = remote.ClientAccountId,
            Name = remote.TikTokUsername,
            TiktokLoginEmail = remote.TikTokUsername,
            CreatedAt = DateTimeOffset.Now.ToString("o"),
        }, remote.Config, remote.Secrets);
        created.Id = remote.ClientAccountId;
        if (string.IsNullOrWhiteSpace(created.TiktokLoginEmail))
            created.TiktokLoginEmail = remote.TikTokUsername;
        if (string.IsNullOrWhiteSpace(created.Name))
            created.Name = remote.TikTokUsername;
        created.TiktokStorageStatePath = context.LoginStatePath(created);
        created.ProfileDir = Path.GetDirectoryName(created.TiktokStorageStatePath) ?? "";
        await WriteCloudFilesAsync(created, remote, context, client, notes, cancellationToken).ConfigureAwait(false);
        local.Add(created);
        return true;
    }

    private static async Task<bool> ApplyDifferentAsync(
        TikTokAccountSyncRow row,
        TikTokAccountSyncDirection direction,
        List<TikTokAccountProfile> local,
        Dictionary<string, RemoteTikTokAccountConfig> remoteById,
        ITikTokAccountConfigSyncClient client,
        TikTokAccountConfigSyncContext context,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (row.ContentChoice == TikTokAccountSyncContentChoice.Skip)
            return false;
        var account = local.FirstOrDefault(item => item.Id == row.LocalAccountId);
        var remote = row.CloudAccountId is not null && remoteById.TryGetValue(row.CloudAccountId, out var found) ? found : null;
        if (account is null || remote is null)
            return false;

        var localConfig = TikTokAccountConfigSyncEngine.ExtractConfig(account);
        var localSecrets = TikTokAccountConfigSyncEngine.ExtractSecrets(account);
        JsonObject config;
        JsonObject secrets;
        var useCloudFiles = false;
        var uploadLocalFiles = false;
        switch (row.ContentChoice)
        {
            case TikTokAccountSyncContentChoice.PreferLocal:
                config = localConfig;
                secrets = localSecrets;
                uploadLocalFiles = direction == TikTokAccountSyncDirection.PushToCloud;
                if (direction == TikTokAccountSyncDirection.PullFromCloud)
                    return false;
                break;
            case TikTokAccountSyncContentChoice.PreferCloud:
                config = remote.Config;
                secrets = remote.Secrets;
                useCloudFiles = direction == TikTokAccountSyncDirection.PullFromCloud;
                if (direction == TikTokAccountSyncDirection.PushToCloud)
                    return false;
                break;
            default:
                config = TikTokAccountConfigSyncEngine.MergePreferLocal(localConfig, remote.Config);
                secrets = TikTokAccountConfigSyncEngine.MergePreferLocal(localSecrets, remote.Secrets);
                uploadLocalFiles = direction == TikTokAccountSyncDirection.PushToCloud;
                break;
        }

        if (direction == TikTokAccountSyncDirection.PushToCloud)
        {
            var pushed = await PushAccountAsync(account, remote.ClientAccountId, remote.Revision, config, secrets, uploadLocalFiles, client, context, cancellationToken)
                .ConfigureAwait(false);
            if (!pushed)
                notes.Add($"「{row.Title}」上传失败");
            return pushed;
        }

        var updated = TikTokAccountConfigSyncEngine.ApplyPortable(account, config, secrets);
        if (useCloudFiles)
            await WriteCloudFilesAsync(updated, remote, context, client, notes, cancellationToken).ConfigureAwait(false);
        var index = local.FindIndex(item => item.Id == account.Id);
        if (index >= 0)
            local[index] = updated;
        return true;
    }

    private static async Task<bool> PushAccountAsync(
        TikTokAccountProfile account,
        string? cloudAccountId,
        int revision,
        JsonObject config,
        JsonObject secrets,
        bool includeLocalFiles,
        ITikTokAccountConfigSyncClient client,
        TikTokAccountConfigSyncContext context,
        CancellationToken cancellationToken)
    {
        var request = new TikTokAccountConfigPushRequest
        {
            CloudAccountId = string.IsNullOrWhiteSpace(cloudAccountId) ? account.Id : cloudAccountId,
            BaseRevision = string.IsNullOrWhiteSpace(cloudAccountId) ? 0 : revision,
            TikTokUsername = TikTokAccountConfigSyncEngine.Username(account),
            Config = config,
            Secrets = secrets,
        };
        if (includeLocalFiles)
        {
            var loginPath = context.LoginStatePath(account);
            var login = string.IsNullOrWhiteSpace(loginPath) ? null : context.ReadFile(loginPath);
            if (login is { Length: > 0 and <= TikTokAccountConfigSyncEngine.MaxLoginStateBytes })
                request.LoginState = login;
            var sealPath = account.TiktokProofSealPath ?? "";
            var seal = string.IsNullOrWhiteSpace(sealPath) ? null : context.ReadFile(sealPath);
            if (seal is { Length: > 0 and <= TikTokAccountConfigSyncEngine.MaxSealBytes })
            {
                request.Seal = seal;
                request.SealFilename = Path.GetFileName(sealPath);
            }
        }

        var result = await client.PushAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Ok;
    }

    private static async Task WriteCloudFilesAsync(
        TikTokAccountProfile account,
        RemoteTikTokAccountConfig remote,
        TikTokAccountConfigSyncContext context,
        ITikTokAccountConfigSyncClient client,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(remote.LoginStateSha256))
        {
            var bytes = await client.DownloadAsync(remote.ClientAccountId, "login-state", cancellationToken).ConfigureAwait(false);
            if (bytes is null || !string.Equals(Sha(bytes), remote.LoginStateSha256, StringComparison.OrdinalIgnoreCase))
                notes.Add($"「{remote.TikTokUsername}」登录态下载失败");
            else
            {
                var path = context.LoginStatePath(account);
                account.TiktokStorageStatePath = path;
                context.WriteFile(path, bytes);
            }
        }

        if (string.IsNullOrWhiteSpace(remote.SealSha256))
            return;
        var seal = await client.DownloadAsync(remote.ClientAccountId, "seal", cancellationToken).ConfigureAwait(false);
        if (seal is null || !string.Equals(Sha(seal), remote.SealSha256, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"「{remote.TikTokUsername}」印章下载失败");
            return;
        }

        var filename = string.IsNullOrWhiteSpace(remote.SealFilename) ? "seal.bin" : remote.SealFilename;
        var destination = context.SealDestination(account, filename);
        account.TiktokProofSealPath = destination;
        context.WriteFile(destination, seal);
    }

    private static string DescribeDifference(
        TikTokAccountProfile local,
        RemoteTikTokAccountConfig remote,
        TikTokAccountConfigSyncContext context)
    {
        var labels = new List<string>();
        var localConfig = TikTokAccountConfigSyncEngine.ExtractConfig(local);
        var remoteConfig = remote.Config;
        foreach (var key in Keys(localConfig, remoteConfig))
        {
            if (!remoteConfig.ContainsKey(key))
                continue;
            if (JsonNode.DeepEquals(localConfig[key], remoteConfig[key]))
                continue;
            if (!TikTokAccountConfigSyncEngine.HasContent(localConfig[key]) && !TikTokAccountConfigSyncEngine.HasContent(remoteConfig[key]))
                continue;
            labels.Add(FieldLabel(key));
        }

        var localSecrets = TikTokAccountConfigSyncEngine.ExtractSecrets(local);
        foreach (var key in Keys(localSecrets, remote.Secrets))
        {
            if (JsonNode.DeepEquals(localSecrets[key], remote.Secrets[key]))
                continue;
            labels.Add(FieldLabel(key));
        }

        var loginPath = context.LoginStatePath(local);
        var localLogin = string.IsNullOrWhiteSpace(loginPath) ? null : context.ReadFile(loginPath);
        if (!string.Equals(Sha(localLogin), remote.LoginStateSha256 ?? "", StringComparison.OrdinalIgnoreCase))
            labels.Add("登录状态");
        var sealPath = local.TiktokProofSealPath ?? "";
        var localSeal = string.IsNullOrWhiteSpace(sealPath) ? null : context.ReadFile(sealPath);
        if (!string.Equals(Sha(localSeal), remote.SealSha256 ?? "", StringComparison.OrdinalIgnoreCase))
            labels.Add("印章");
        if (labels.Count == 0)
            return "";
        var shown = string.Join("、", labels.Take(6));
        return labels.Count > 6 ? shown + "等" : shown;
    }

    private static IEnumerable<string> Keys(JsonObject left, JsonObject right)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in left)
            keys.Add(property.Key);
        foreach (var property in right)
            keys.Add(property.Key);
        return keys;
    }

    private static string FieldLabel(string key) => key switch
    {
        "name" => "名称",
        "tiktokLoginEmail" => "用户名",
        "tiktokLoginPassword" => "密码",
        "tiktokProxyPassword" => "代理密码",
        "tiktokProxyHost" => "代理地址",
        "tiktokContractId" => "合同",
        "xingeRemoteDispatchEnabled" => "远程分发",
        _ => key,
    };

    private static TikTokAccountSyncPreview CopyChoices(TikTokAccountSyncPreview fresh, TikTokAccountSyncPreview confirmed)
    {
        foreach (var row in fresh.Rows)
        {
            var previous = confirmed.Rows.FirstOrDefault(item =>
                item.Kind == row.Kind &&
                string.Equals(item.LocalAccountId, row.LocalAccountId, StringComparison.Ordinal) &&
                string.Equals(item.CloudAccountId, row.CloudAccountId, StringComparison.Ordinal) &&
                string.Equals(UsernameKey(item.TikTokUsername), UsernameKey(row.TikTokUsername), StringComparison.Ordinal));
            if (previous is null)
                continue;
            row.ContentChoice = previous.ContentChoice;
            row.UploadLocal = previous.UploadLocal;
            row.DeleteLocal = previous.DeleteLocal;
            row.AddFromCloud = previous.AddFromCloud;
            row.DeleteCloud = previous.DeleteCloud;
        }

        return fresh;
    }

    private static string Fingerprint(IEnumerable<RemoteTikTokAccountConfig> accounts) =>
        string.Join("\n", accounts
            .OrderBy(account => account.ClientAccountId, StringComparer.Ordinal)
            .Select(account => $"{account.ClientAccountId}\t{account.Revision}"));

    private static string UsernameKey(string value) => (value ?? "").Trim().ToLowerInvariant();

    private static string Display(TikTokAccountProfile profile, string username) =>
        string.IsNullOrWhiteSpace(profile.Name) || string.Equals(profile.Name, username, StringComparison.OrdinalIgnoreCase)
            ? username
            : $"{profile.Name}（{username}）";

    private static byte[]? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, bytes);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Sha(byte[]? data) =>
        data is null ? "" : Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static TikTokAccountProfile Clone(TikTokAccountProfile profile)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var node = JsonSerializer.SerializeToNode(profile, options);
        return node?.Deserialize<TikTokAccountProfile>(options) ?? new TikTokAccountProfile { Id = profile.Id };
    }
}
