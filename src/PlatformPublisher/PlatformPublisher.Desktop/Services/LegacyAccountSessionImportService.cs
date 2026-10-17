using System.Text.Json;
using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Services;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using ChannelsAccount = ChannelsPublisher.Core.Models.PublishAccount;

namespace PlatformPublisher.Desktop.Services;

public sealed class LegacyAccountSessionImportService
{
    private readonly AccountStore _accounts;
    private readonly string _dataRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LegacyAccountSessionImportService(AccountStore accounts) : this(accounts, PlatformPublisherPaths.DataRoot) { }

    internal LegacyAccountSessionImportService(AccountStore accounts, string dataRoot)
    {
        _accounts = accounts;
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    public string DefaultLegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weixin_channel_tool");

    public IReadOnlyList<LegacyAccountSessionCandidate> Discover(string? sourceRoot = null)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(sourceRoot) ? DefaultLegacyRoot : sourceRoot);
        var profileNames = ReadProfileNames(Path.Combine(root, "settings.json"));
        var profilesRoot = Path.Combine(root, "profiles");
        if (!Directory.Exists(profilesRoot)) return [];
        return Directory.EnumerateDirectories(profilesRoot)
            .Select(directory => BuildCandidate(root, directory, profileNames))
            .Where(candidate => candidate.HasAnySession)
            .OrderBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public ChannelsAccount CreateTargetAccount(string name) => _accounts.Add(name);

    public async Task<int> ImportAllLegacyProfilesAsync(string? sourceRoot = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = Path.GetFullPath(string.IsNullOrWhiteSpace(sourceRoot) ? DefaultLegacyRoot : sourceRoot);
            var settingsPath = Path.Combine(root, "settings.json");
            if (!File.Exists(settingsPath)) return 0;
            using var settingsDocument = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!settingsDocument.RootElement.TryGetProperty("account_profiles_json", out var profilesProperty)) return 0;
            using var profilesDocument = profilesProperty.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(profilesProperty.GetString() ?? "[]")
                : null;
            var profiles = profilesDocument?.RootElement ?? profilesProperty;
            if (profiles.ValueKind != JsonValueKind.Array) return 0;

            _accounts.Load();
            var imported = 0;
            foreach (var profile in profiles.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Text(profile, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var name = Text(profile, "name");
                if (string.IsNullOrWhiteSpace(name)) name = id;
                var target = _accounts.Accounts.FirstOrDefault(account =>
                        string.Equals(account.LegacyProfileId, id, StringComparison.OrdinalIgnoreCase))
                    ?? _accounts.Accounts.FirstOrDefault(account =>
                        string.Equals(account.Name, name, StringComparison.CurrentCultureIgnoreCase) &&
                        string.IsNullOrWhiteSpace(account.LegacyProfileId));
                if (target?.LegacySessionImportedAt is not null)
                {
                    var profileDirectory = Path.Combine(root, "profiles", id);
                    await FillMissingWeixinSessionAsync(target, profileDirectory, cancellationToken);
                    await FillContentPolicyAsync(target, profile, cancellationToken);
                    continue;
                }
                target ??= _accounts.Add(name);
                target.Name = name;
                target.LegacyProfileId = id;
                target.LegacySessionSourceRoot = root;
                ApplyAccountFields(target, profile);
                var directory = Path.Combine(root, "profiles", id);
                var importedPlatforms = new List<string>();
                if (await TryCopySessionAsync(target, ResolveNewestState(directory, "wx_auth_state*.json"),
                        Path.Combine(_dataRoot, "sessions", Safe(target.Id), "weixin-storage-state.json"),
                        "weixin.qq.com", cancellationToken) is string weixinPath)
                {
                    target.WeixinAuthStatePath = weixinPath;
                    await MirrorAsync(weixinPath, Path.Combine(target.ProfileDir, "weixin-series-auth.json"), cancellationToken);
                    await MirrorAsync(weixinPath, Path.Combine(target.ProfileDir, "weixin-auth.json"), cancellationToken);
                    importedPlatforms.Add("视频号");
                }
                if (await TryCopySessionAsync(target, ResolveKuaishouState(directory, personal: true),
                        Path.Combine(_dataRoot, "sessions", Safe(target.Id), "kuaishou-personal-storage-state.json"),
                        "kuaishou.com", cancellationToken) is string personalPath)
                {
                    target.KuaishouPersonalAuthStatePath = personalPath;
                    target.KuaishouPersonalAccount = FirstText(profile, "kuaishou_personal_nickname", "kuaishou_personal_username") ?? name;
                    target.KuaishouPersonalConfigPath = await ApplyProfileConfigAsync(
                        target, PublishPlatform.KuaishouPersonalRevenue, profile, personalPath, cancellationToken);
                    importedPlatforms.Add("快手个人");
                }
                else
                {
                    target.KuaishouPersonalConfigPath = await ApplyProfileConfigAsync(
                        target, PublishPlatform.KuaishouPersonalRevenue, profile, null, cancellationToken);
                }
                if (await TryCopySessionAsync(target, ResolveKuaishouState(directory, personal: false),
                        Path.Combine(_dataRoot, "sessions", Safe(target.Id), "kuaishou-enterprise-storage-state.json"),
                        "kuaishou.com", cancellationToken) is string enterprisePath)
                {
                    target.KuaishouEnterpriseAuthStatePath = enterprisePath;
                    target.KuaishouEnterpriseAccount = FirstText(profile, "kuaishou_enterprise_nickname", "kuaishou_enterprise_username") ?? name;
                    target.KuaishouEnterpriseConfigPath = await ApplyProfileConfigAsync(
                        target, PublishPlatform.KuaishouEnterpriseRevenue, profile, enterprisePath, cancellationToken);
                    importedPlatforms.Add("快手企业");
                }
                target.LegacySessionImportedAt = DateTimeOffset.Now;
                if (target.LastLoginAt is null && importedPlatforms.Count > 0)
                    target.LastLoginAt = DateTimeOffset.Now;
                _accounts.Update(target);
                imported++;
            }
            return imported;
        }
        finally { _gate.Release(); }
    }

    public async Task<LegacySessionImportResult> ImportAsync(LegacyAccountSessionCandidate candidate,
        ChannelsAccount target, LegacySessionImportSelection selection, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var imported = new List<string>();
            var sessionRoot = Path.Combine(_dataRoot, "sessions", Safe(target.Id));
            Directory.CreateDirectory(sessionRoot);
            if (selection.Weixin && candidate.Weixin.Exists)
            {
                var destination = Path.Combine(sessionRoot, "weixin-storage-state.json");
                EnsureLegacySource(candidate, candidate.Weixin.Path);
                await CopyValidatedStateAsync(candidate.Weixin.Path, destination, "weixin.qq.com", cancellationToken);
                target.WeixinAuthStatePath = destination;
                await MirrorAsync(destination, Path.Combine(target.ProfileDir, "weixin-series-auth.json"), cancellationToken);
                await MirrorAsync(destination, Path.Combine(target.ProfileDir, "weixin-auth.json"), cancellationToken);
                await MirrorAsync(destination, Path.Combine(_dataRoot, "accounts", Safe(target.Id), "weixin-auth.json"), cancellationToken);
                imported.Add("视频号");
            }
            if (selection.KuaishouPersonal && candidate.KuaishouPersonal.Exists)
            {
                var destination = Path.Combine(sessionRoot, "kuaishou-personal-storage-state.json");
                EnsureLegacySource(candidate, candidate.KuaishouPersonal.Path);
                await CopyValidatedStateAsync(candidate.KuaishouPersonal.Path, destination, "kuaishou.com", cancellationToken);
                target.KuaishouPersonalAuthStatePath = destination;
                target.KuaishouPersonalAccount = string.IsNullOrWhiteSpace(target.KuaishouPersonalAccount) ? candidate.Name : target.KuaishouPersonalAccount;
                target.KuaishouPersonalConfigPath = await ApplyKuaishouConfigAsync(target, PublishPlatform.KuaishouPersonalRevenue, destination, cancellationToken);
                imported.Add("快手个人");
            }
            if (selection.KuaishouEnterprise && candidate.KuaishouEnterprise.Exists)
            {
                var destination = Path.Combine(sessionRoot, "kuaishou-enterprise-storage-state.json");
                EnsureLegacySource(candidate, candidate.KuaishouEnterprise.Path);
                await CopyValidatedStateAsync(candidate.KuaishouEnterprise.Path, destination, "kuaishou.com", cancellationToken);
                target.KuaishouEnterpriseAuthStatePath = destination;
                target.KuaishouEnterpriseAccount = string.IsNullOrWhiteSpace(target.KuaishouEnterpriseAccount) ? candidate.Name : target.KuaishouEnterpriseAccount;
                target.KuaishouEnterpriseConfigPath = await ApplyKuaishouConfigAsync(target, PublishPlatform.KuaishouEnterpriseRevenue, destination, cancellationToken);
                imported.Add("快手企业");
            }
            if (imported.Count == 0) throw new InvalidOperationException("没有选择可导入的登录状态。");
            target.LegacyProfileId = candidate.Id;
            target.LegacySessionSourceRoot = candidate.SourceRoot;
            target.LegacySessionImportedAt = DateTimeOffset.Now;
            _accounts.Update(target);
            return new LegacySessionImportResult(target, imported, target.LegacySessionImportedAt.Value);
        }
        finally { _gate.Release(); }
    }

    private static LegacyAccountSessionCandidate BuildCandidate(string sourceRoot, string directory,
        IReadOnlyDictionary<string, string> profileNames)
    {
        var id = Path.GetFileName(directory);
        var generic = State(Path.Combine(directory, "kuaishou_kdj_auth_state.json"));
        var personal = State(Path.Combine(directory, "kuaishou_personal_kdj_auth_state.json"));
        var enterprise = State(Path.Combine(directory, "kuaishou_enterprise_kdj_auth_state.json"));
        if (!personal.Exists) personal = generic;
        if (!enterprise.Exists) enterprise = generic;
        return new LegacyAccountSessionCandidate(id, profileNames.GetValueOrDefault(id, id), sourceRoot,
            State(Path.Combine(directory, "wx_auth_state.json")), personal, enterprise);
    }

    private static IReadOnlyDictionary<string, string> ReadProfileNames(string settingsPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(settingsPath)) return result;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!document.RootElement.TryGetProperty("account_profiles_json", out var profiles)) return result;
            using var nested = profiles.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(profiles.GetString() ?? "[]")
                : null;
            var array = nested?.RootElement ?? profiles;
            if (array.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in array.EnumerateArray())
            {
                var id = Text(item, "id");
                if (!string.IsNullOrWhiteSpace(id)) result[id] = Text(item, "name") ?? id;
            }
        }
        catch { /* A malformed settings file must not expose or erase existing sessions. */ }
        return result;
    }

    private static async Task<string> ApplyKuaishouConfigAsync(ChannelsAccount account, PublishPlatform platform,
        string authStatePath, CancellationToken cancellationToken) =>
        await ApplyProfileConfigAsync(account, platform, default, authStatePath, cancellationToken);

    private static async Task<string> ApplyProfileConfigAsync(ChannelsAccount account, PublishPlatform platform,
        JsonElement profile, string? authStatePath, CancellationToken cancellationToken)
    {
        var path = KuaishouPersonalConfig.DefaultConfigPath(account.Id, platform);
        var config = KuaishouPersonalConfig.Load(new PublishJob
        {
            Platform = platform,
            AccountId = account.Id,
            ConfigPath = File.Exists(path) ? path : string.Empty,
        });
        if (profile.ValueKind == JsonValueKind.Object)
            KuaishouLegacyConfigImporter.ImportElement(profile, config, platform, account.LegacySessionSourceRoot);
        if (!string.IsNullOrWhiteSpace(authStatePath)) config.AuthStatePath = authStatePath;
        await config.SaveAsync(path, cancellationToken);
        return path;
    }

    private static void ApplyAccountFields(ChannelsAccount account, JsonElement profile)
    {
        account.CostReportCompanyName = Text(profile, "cost_report_company_name") ?? account.CostReportCompanyName;
        account.CostReportSealPath = Text(profile, "cost_report_seal_path") ?? account.CostReportSealPath;
        account.CostReportSignPath = Text(profile, "cost_report_sign_path") ?? account.CostReportSignPath;
        account.CostReportTemplatePath = Text(profile, "cost_report_template_docx_path") ?? account.CostReportTemplatePath;
        account.CostReportActorPayRatio = Text(profile, "cost_report_actor_pay_ratio") ?? account.CostReportActorPayRatio;
        account.WorkRootDirectory = FirstExistingDirectory(
            Text(profile, "last_task_queue_workspace"),
            Text(profile, "kuaishou_personal_last_workspace"),
            Text(profile, "kuaishou_last_workspace")) ?? account.WorkRootDirectory;
        account.DownloadDirectory = FirstExistingDirectory(Text(profile, "last_download_workspace")) ?? account.DownloadDirectory;
        account.ArchiveRootDirectory = FirstExistingDirectory(
            Text(profile, "kuaishou_personal_archive_root_dir"),
            Text(profile, "kuaishou_archive_root_dir")) ?? account.ArchiveRootDirectory;
        account.Nickname = FirstText(profile, "kuaishou_personal_nickname") ?? account.Nickname;
    }

    private async Task<string?> TryCopySessionAsync(ChannelsAccount account, string source, string destination,
        string requiredDomain, CancellationToken cancellationToken)
    {
        if (!File.Exists(source)) return null;
        try
        {
            EnsureLegacySource(new LegacyAccountSessionCandidate(
                account.LegacyProfileId, account.Name, account.LegacySessionSourceRoot,
                State(source), State(source), State(source)), source);
            await CopyValidatedStateAsync(source, destination, requiredDomain, cancellationToken);
            return destination;
        }
        catch { return null; }
    }

    private static async Task FillContentPolicyAsync(ChannelsAccount account, JsonElement profile, CancellationToken cancellationToken)
    {
        await FillContentPolicyAsync(account, profile, PublishPlatform.KuaishouPersonalRevenue, account.KuaishouPersonalConfigPath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(account.KuaishouEnterpriseConfigPath))
            await FillContentPolicyAsync(account, profile, PublishPlatform.KuaishouEnterpriseRevenue, account.KuaishouEnterpriseConfigPath, cancellationToken);
    }

    private static async Task FillContentPolicyAsync(ChannelsAccount account, JsonElement profile, PublishPlatform platform,
        string? configPath, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(configPath) ? KuaishouPersonalConfig.DefaultConfigPath(account.Id, platform) : configPath;
        if (!File.Exists(path)) return;
        var config = KuaishouPersonalConfig.Load(new PublishJob
        {
            Platform = platform,
            AccountId = account.Id,
            ConfigPath = path,
        });
        if (config.ContentPolicyMigrated) return;
        var authStatePath = config.AuthStatePath;
        KuaishouLegacyConfigImporter.ImportElement(profile, config, platform, account.LegacySessionSourceRoot);
        config.AuthStatePath = authStatePath;
        config.ContentPolicyMigrated = true;
        await config.SaveAsync(path, cancellationToken);
    }

    private async Task FillMissingWeixinSessionAsync(ChannelsAccount account, string directory, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(account.WeixinAuthStatePath) && File.Exists(account.WeixinAuthStatePath)) return;
        var copied = await TryCopySessionAsync(account, ResolveNewestState(directory, "wx_auth_state*.json"),
            Path.Combine(_dataRoot, "sessions", Safe(account.Id), "weixin-storage-state.json"),
            "weixin.qq.com", cancellationToken);
        if (copied is null) return;
        account.WeixinAuthStatePath = copied;
        await MirrorAsync(copied, Path.Combine(account.ProfileDir, "weixin-series-auth.json"), cancellationToken);
        await MirrorAsync(copied, Path.Combine(account.ProfileDir, "weixin-auth.json"), cancellationToken);
        _accounts.Update(account);
    }

    private static string ResolveNewestState(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return Path.Combine(directory, "wx_auth_state.json");
        var preferred = Path.Combine(directory, "wx_auth_state.json");
        if (File.Exists(preferred)) return preferred;
        var newest = Directory.EnumerateFiles(directory, pattern)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest?.FullName ?? preferred;
    }

    private static string ResolveKuaishouState(string directory, bool personal)
    {
        var specific = Path.Combine(directory, personal
            ? "kuaishou_personal_kdj_auth_state.json"
            : "kuaishou_enterprise_kdj_auth_state.json");
        if (File.Exists(specific)) return specific;
        return Path.Combine(directory, "kuaishou_kdj_auth_state.json");
    }

    private static string? FirstText(JsonElement profile, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Text(profile, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string? FirstExistingDirectory(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) return value;
        }
        return null;
    }

    private static async Task CopyValidatedStateAsync(string source, string destination, string requiredDomain,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = new FileInfo(source);
            byte[] bytes;
            await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous))
            {
                bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
            }
            ValidateStorageState(bytes, requiredDomain);
            var after = new FileInfo(source);
            if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
            {
                if (attempt < 3) { await Task.Delay(attempt * 150, cancellationToken); continue; }
                throw new IOException("旧工具正在更新登录状态，请稍后重试。");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                File.Move(temporary, destination, true);
                return;
            }
            finally { try { File.Delete(temporary); } catch { } }
        }
    }

    private static void EnsureLegacySource(LegacyAccountSessionCandidate candidate, string source)
    {
        var profilesRoot = Path.GetFullPath(Path.Combine(candidate.SourceRoot, "profiles")) + Path.DirectorySeparatorChar;
        var fullSource = Path.GetFullPath(source);
        if (!fullSource.StartsWith(profilesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("登录态来源不在旧工具账号目录中，已拒绝导入。");
    }

    internal static void ValidateStorageState(byte[] bytes, string requiredDomain)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cookies", out var cookies) ||
            cookies.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("登录态文件不是有效的 Playwright storage_state JSON。");
        var matched = cookies.EnumerateArray().Any(cookie =>
            (Text(cookie, "domain") ?? string.Empty).Contains(requiredDomain, StringComparison.OrdinalIgnoreCase));
        if (!matched) throw new InvalidOperationException($"登录态中未找到 {requiredDomain} 的 Cookie。");
    }

    private static async Task MirrorAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var bytes = await File.ReadAllBytesAsync(source, cancellationToken);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try { await File.WriteAllBytesAsync(temporary, bytes, cancellationToken); File.Move(temporary, destination, true); }
        finally { try { File.Delete(temporary); } catch { } }
    }

    private static LegacySessionFile State(string path) => File.Exists(path)
        ? new LegacySessionFile(path, true, File.GetLastWriteTimeUtc(path), new FileInfo(path).Length)
        : new LegacySessionFile(path, false, null, 0);
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string Safe(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
}

public sealed record LegacySessionFile(string Path, bool Exists, DateTime? UpdatedAt, long Length);
public sealed record LegacyAccountSessionCandidate(string Id, string Name, string SourceRoot,
    LegacySessionFile Weixin, LegacySessionFile KuaishouPersonal, LegacySessionFile KuaishouEnterprise)
{
    public bool HasAnySession => Weixin.Exists || KuaishouPersonal.Exists || KuaishouEnterprise.Exists;
    public string Summary => $"视频号：{Label(Weixin)} · 快手个人：{Label(KuaishouPersonal)} · 快手企业：{Label(KuaishouEnterprise)}";
    private static string Label(LegacySessionFile file) => file.Exists ? "可导入" : "缺失";
    public override string ToString() => $"{Name} ({Id})";
}
public sealed record LegacySessionImportSelection(bool Weixin, bool KuaishouPersonal, bool KuaishouEnterprise);
public sealed record LegacySessionImportResult(ChannelsAccount Account, IReadOnlyList<string> Platforms, DateTimeOffset ImportedAt);
