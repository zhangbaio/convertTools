using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

public sealed record VideoDedupTemplateEntry(string Id, string Name, VideoDedupPreset? Preset);

public sealed record VideoDedupTemplateSnapshot(
    IReadOnlyList<VideoDedupTemplateEntry> Templates,
    IReadOnlyDictionary<string, string> AccountDefaults)
{
    public VideoDedupTemplateEntry DefaultFor(string? accountId)
    {
        var id = accountId is null ? null : AccountDefaults.GetValueOrDefault(accountId);
        return Templates.FirstOrDefault(template => template.Id == id) ?? Templates[0];
    }
}

/// <summary>共享模板和账号选择保存在同一个文件，所有修改以一份目录快照提交。</summary>
public sealed class VideoDedupTemplateCatalog(string dataRoot)
{
    public const string SystemId = "system";
    private const string FileName = "video-dedup-template-library.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root = Path.GetFullPath(dataRoot);
    private static VideoDedupTemplateEntry SystemDefault => new(SystemId, "系统默认", null);

    public static VideoDedupTemplateCatalog OpenDefault() => new(AppPaths.DataRoot);

    public VideoDedupTemplateSnapshot Load(IEnumerable<TikTokAccountProfile> accounts) => Edit(state =>
    {
        var migrated = ImportLegacy(state);
        var changed = ApplyAccountProfiles(state, accounts, replace: false) | migrated;
        return (ToSnapshot(state), changed);
    });

    public VideoDedupTemplateEntry Save(string? id, string name, VideoDedupPreset preset) =>
        SaveTemplate(id, name, preset, accountId: null);

    public VideoDedupTemplateEntry SaveAccountConfiguration(
        string accountId, string? templateId, string? name = null, VideoDedupPreset? preset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (preset is not null)
            return SaveTemplate(templateId, name ?? "", preset, accountId);

        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        return Edit(state =>
        {
            var entry = RequireTemplate(state, templateId);
            var changed = state.AccountDefaults.GetValueOrDefault(accountId) != entry.Id;
            state.AccountDefaults[accountId] = entry.Id;
            return (entry, changed);
        });
    }

    private VideoDedupTemplateEntry SaveTemplate(string? id, string name, VideoDedupPreset preset, string? accountId)
    {
        ArgumentNullException.ThrowIfNull(preset);
        name = name.Trim();
        if (name.Length is 0 or > 80 || string.Equals(name, SystemDefault.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请输入 1–80 字的自定义模板名称。");
        if (id == SystemId)
            throw new InvalidOperationException("系统默认模板可复制后编辑。");
        var parameters = preset.NormalizedCopy();
        if (parameters.DurationRangeError() is { } durationError)
            throw new InvalidOperationException(durationError);
        VideoDedupEffectFilters.Validate(parameters);
        parameters.Name = name;
        return Edit(state =>
        {
            var index = id is null ? -1 : state.Templates.FindIndex(item => item.Id == id);
            if (id is not null && index < 0)
                throw new InvalidOperationException("该模板已不存在，请重新选择。");
            if (state.Templates.Any(item => item.Id != id && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名视频去重模板。");
            var entry = new VideoDedupTemplateEntry(id ?? Guid.NewGuid().ToString("N"), name, parameters);
            if (index < 0) state.Templates.Add(entry);
            else state.Templates[index] = entry;
            if (accountId is not null)
                state.AccountDefaults[accountId] = entry.Id;
            return (entry, true);
        });
    }

    public void SetDefault(string accountId, string templateId) =>
        SaveAccountConfiguration(accountId, templateId);

    public void Delete(string id, string? replacementId = null)
    {
        if (id == SystemId) throw new InvalidOperationException("系统默认模板不能删除。");
        Edit(state =>
        {
            RequireTemplate(state, id);
            var bound = state.AccountDefaults.Where(item => item.Value == id).Select(item => item.Key).ToArray();
            if (bound.Length > 0)
            {
                if (replacementId is null || replacementId == id)
                    throw new InvalidOperationException("仍有账号使用此模板，请先选择替代模板。");
                RequireTemplate(state, replacementId);
                foreach (var account in bound) state.AccountDefaults[account] = replacementId;
            }
            state.Templates.RemoveAll(item => item.Id == id);
            return (0, true);
        });
    }

    internal void ApplyImportedAccountPresets(IEnumerable<TikTokAccountProfile> accounts) =>
        Edit(state =>
        {
            var migrated = ImportLegacy(state);
            return (0, ApplyAccountProfiles(state, accounts, replace: true) | migrated);
        });

    internal IReadOnlyDictionary<string, VideoDedupPreset?> CaptureRunPresets(
        IReadOnlyList<TikTokAccountProfile> accounts, Action<string>? log)
    {
        try
        {
            var snapshot = Edit(state => (ToSnapshot(state), false));
            var entries = snapshot.Templates.ToDictionary(item => item.Id);
            var legacy = accounts.Any(account => !snapshot.AccountDefaults.ContainsKey(account.Id) &&
                         !string.IsNullOrWhiteSpace(account.VideoDedupTemplateId) && account.VideoDedupTemplateId != SystemId)
                ? ReadLegacy() : [];
            var captured = new Dictionary<string, VideoDedupPreset?>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in accounts)
                captured[account.Id] = snapshot.AccountDefaults.TryGetValue(account.Id, out var id) &&
                                       entries.TryGetValue(id, out var entry)
                    ? entry.Preset?.Clone() : ResolveLegacy(account, legacy);
            return captured;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"无法读取视频去重模板，使用账号原有配置：{ex.Message}");
            var captured = new Dictionary<string, VideoDedupPreset?>(StringComparer.OrdinalIgnoreCase);
            var legacy = ReadLegacy();
            foreach (var account in accounts)
                captured[account.Id] = ResolveLegacy(account, legacy);
            return captured;
        }
    }

    private static bool ApplyAccountProfiles(CatalogState state, IEnumerable<TikTokAccountProfile> accounts, bool replace)
    {
        var changed = false;
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id) || (!replace && state.AccountDefaults.ContainsKey(account.Id))) continue;
            var previous = account.VideoDedupTemplateId == SystemId ? null : account.VideoDedupPreset;
            var fingerprint = previous?.Fingerprint();
            if (replace && state.AccountDefaults.TryGetValue(account.Id, out var assignedId) &&
                (assignedId == SystemId ? previous is null :
                    fingerprint is not null && state.Templates.FirstOrDefault(item => item.Id == assignedId)?.Preset?.Fingerprint() == fingerprint))
                continue;
            var matchingLegacy = state.Templates.FirstOrDefault(template =>
                template.Id.StartsWith("legacy-", StringComparison.Ordinal) &&
                (template.Id == "legacy-" + account.VideoDedupTemplateId ||
                 (fingerprint is not null && template.Preset?.Fingerprint() == fingerprint)));
            if (!replace && previous is null && matchingLegacy is null) continue;
            var entry = matchingLegacy ?? (replace && previous is not null
                ? state.Templates.FirstOrDefault(template => template.Name == previous.Name && template.Preset?.Fingerprint() == fingerprint)
                : null);
            if (entry is null && previous is not null)
            {
                var copy = previous.Clone();
                var display = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Id : account.DisplayName;
                copy.Name = UniqueName(state, display + (replace ? " · 导入配置" : " · 原配置"));
                entry = new VideoDedupTemplateEntry(Guid.NewGuid().ToString("N"), copy.Name, copy);
                state.Templates.Add(entry);
            }
            state.AccountDefaults[account.Id] = entry?.Id ?? SystemId;
            changed = true;
        }
        return changed;
    }

    private bool ImportLegacy(CatalogState state)
    {
        if (state.LegacyImported) return false;
        var legacy = ReadLegacy();
        if (legacy is null) return false;
        foreach (var item in legacy)
        {
            if (item?.Preset is null || string.IsNullOrWhiteSpace(item.Id) || item.Id == SystemId) continue;
            var id = "legacy-" + item.Id;
            if (state.Templates.Any(template => template.Id == id)) continue;
            var preset = item.Preset.Clone();
            preset.Name = UniqueName(state, string.IsNullOrWhiteSpace(preset.Name) ? "旧模板" : preset.Name);
            state.Templates.Add(new(id, preset.Name, preset));
        }
        state.LegacyImported = true;
        return true;
    }

    private IReadOnlyList<LegacyEntry>? ReadLegacy()
    {
        var path = Path.Combine(_root, VideoDedupTemplateStore.FileName);
        if (!File.Exists(path)) return null;
        List<LegacyEntry>? legacy;
        try { legacy = JsonSerializer.Deserialize<List<LegacyEntry>>(File.ReadAllText(path), JsonOptions); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        return legacy ?? [];
    }

    private static VideoDedupPreset? ResolveLegacy(TikTokAccountProfile account, IReadOnlyList<LegacyEntry>? legacy)
    {
        if (account.VideoDedupTemplateId == SystemId) return null;
        return (legacy?.FirstOrDefault(item => item?.Id == account.VideoDedupTemplateId)?.Preset
                ?? account.VideoDedupPreset)?.Clone();
    }

    private static string UniqueName(CatalogState state, string name)
    {
        var candidate = name.Trim();
        var length = Math.Min(70, candidate.Length);
        var root = candidate[..length];
        candidate = root;
        for (var number = 2; state.Templates.Any(template => template.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); number++)
            candidate = $"{root} ({number})";
        return candidate;
    }

    private T Edit<T>(Func<CatalogState, (T Result, bool Changed)> action)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? _root.ToUpperInvariant() : _root)));
        using var mutex = new Mutex(false, "ConvertTools.VideoDedup." + key);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(4)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("其他客户端正在保存视频去重模板，请稍后重试。");
        try
        {
            var path = Path.Combine(_root, FileName);
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(path), JsonOptions) ?? throw new JsonException("视频去重模板库为空。")
                : new CatalogState();
            if (state.Version != 1) throw new JsonException("视频去重模板库版本不受支持。");
            if (state.AccountDefaults is null || state.Templates is null ||
                state.Templates.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Preset is null || item.Id == SystemId) ||
                state.Templates.Select(item => item.Id).Distinct().Count() != state.Templates.Count)
                throw new JsonException("视频去重模板库包含无效或重复的模板。");
            state.AccountDefaults = new Dictionary<string, string>(state.AccountDefaults, StringComparer.OrdinalIgnoreCase);
            var (result, changed) = action(state);
            if (changed) Write(path, state);
            return result;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void Write(string path, CatalogState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static VideoDedupTemplateEntry RequireTemplate(CatalogState state, string id) =>
        id == SystemId ? SystemDefault :
        state.Templates.FirstOrDefault(template => template.Id == id)
            ?? throw new InvalidOperationException("该视频去重模板已不存在，请重新选择。");

    private static VideoDedupTemplateSnapshot ToSnapshot(CatalogState state) =>
        new(new[] { SystemDefault }.Concat(state.Templates).ToArray(), state.AccountDefaults);

    private sealed class CatalogState
    {
        public int Version { get; set; } = 1;
        public bool LegacyImported { get; set; }
        public List<VideoDedupTemplateEntry> Templates { get; set; } = [];
        public Dictionary<string, string> AccountDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LegacyEntry(string Id, VideoDedupPreset Preset);
}
