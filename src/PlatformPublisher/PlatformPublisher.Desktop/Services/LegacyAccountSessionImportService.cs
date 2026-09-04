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
        string authStatePath, CancellationToken cancellationToken)
    {
        var path = KuaishouPersonalConfig.DefaultConfigPath(account.Id, platform);
        var config = KuaishouPersonalConfig.Load(new PublishJob
        {
            Platform = platform,
            AccountId = account.Id,
            ConfigPath = File.Exists(path) ? path : string.Empty,
        });
        config.AuthStatePath = authStatePath;
        await config.SaveAsync(path, cancellationToken);
        return path;
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
