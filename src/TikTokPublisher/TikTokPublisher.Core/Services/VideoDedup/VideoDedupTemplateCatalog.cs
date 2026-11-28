using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

public sealed record VideoDedupTemplateEntry(string Id, string Name, VideoDedupPreset Preset);

public enum VideoDedupTemplateTarget { TikTokLocal, TikTokOverseas, Kuaishou }

public readonly record struct VideoDedupTemplateAccount(string Id, VideoDedupTemplateTarget Target)
{
    internal string BindingKey => (Target == VideoDedupTemplateTarget.Kuaishou ? "kuaishou:" : "tiktok:") + Id;

    public string DefaultTemplateId => Target == VideoDedupTemplateTarget.TikTokOverseas
        ? VideoDedupTemplateCatalog.EnglishId : VideoDedupTemplateCatalog.ChineseId;

    public static VideoDedupTemplateAccount ForTikTok(TikTokAccountProfile account) =>
        new(account.Id, TikTokAccountProfile.NormalizeDramaMarket(account.TiktokDramaMarket) == TikTokAccountProfile.DramaMarketOverseas
            ? VideoDedupTemplateTarget.TikTokOverseas : VideoDedupTemplateTarget.TikTokLocal);

    public static VideoDedupTemplateAccount ForKuaishou(string accountId) =>
        new(accountId, VideoDedupTemplateTarget.Kuaishou);
}

public sealed record VideoDedupTemplateSnapshot(
    IReadOnlyList<VideoDedupTemplateEntry> Templates,
    IReadOnlyDictionary<string, string> AccountDefaults)
{
    public string? SelectedTemplateId(VideoDedupTemplateAccount account) =>
        AccountDefaults.GetValueOrDefault(account.BindingKey);

    public VideoDedupTemplateEntry DefaultFor(VideoDedupTemplateAccount account) =>
        Templates.FirstOrDefault(template => template.Id == SelectedTemplateId(account)) ??
        Templates.First(template => template.Id == account.DefaultTemplateId);
}

/// <summary>快手和 TikTok 共用模板库。账号仅保存显式选择，默认模板由平台和剧集类型决定。</summary>
public sealed class VideoDedupTemplateCatalog(string dataRoot)
{
    public const string ChineseId = "builtin-zh";
    public const string EnglishId = "builtin-en";
    public const string FollowDefaultId = "follow-default";

    private const int CurrentVersion = 2;
    private const string FileName = "video-dedup-template-library.json";
    private const string SeedResource = "TikTokPublisher.Core.Resources.VideoDedupBuiltInTemplates.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Lazy<VideoDedupTemplateEntry[]> BuiltInTemplates = new(ReadBuiltInTemplates);
    private readonly string _root = Path.GetFullPath(dataRoot);

    public static VideoDedupTemplateCatalog OpenDefault() => new(AppPaths.DataRoot);

    public static bool IsBuiltIn(string? id) => id is ChineseId or EnglishId;

    public VideoDedupTemplateSnapshot Load() => Edit(state =>
        (new VideoDedupTemplateSnapshot(state.Templates.ToArray(), state.AccountDefaults), false));

    public VideoDedupTemplateEntry Save(string? id, string name, VideoDedupPreset preset) =>
        Edit(state => (SaveTemplate(state, id, name, preset), true));

    public VideoDedupTemplateEntry SaveAccountConfiguration(
        VideoDedupTemplateAccount account, string? selectionId, string? name = null, VideoDedupPreset? preset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Id);
        return Edit(state =>
        {
            var followDefault = selectionId == FollowDefaultId;
            var templateId = followDefault ? account.DefaultTemplateId : selectionId;
            var template = preset is null
                ? RequireTemplate(state, templateId)
                : SaveTemplate(state, templateId, name ?? "", preset);
            var changed = followDefault
                ? state.AccountDefaults.Remove(account.BindingKey)
                : state.AccountDefaults.GetValueOrDefault(account.BindingKey) != template.Id;
            if (!followDefault)
                state.AccountDefaults[account.BindingKey] = template.Id;
            return (template, changed || preset is not null);
        });
    }

    private static VideoDedupTemplateEntry SaveTemplate(
        CatalogState state, string? id, string name, VideoDedupPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var index = id is null ? -1 : state.Templates.FindIndex(item => item.Id == id);
        if (id is not null && index < 0)
            throw new InvalidOperationException("该模板已不存在，请重新选择。");

        name = IsBuiltIn(id) ? state.Templates[index].Name : name.Trim();
        if (name.Length is 0 or > 80 || name == "跟随默认")
            throw new InvalidOperationException("请输入 1–80 字的自定义模板名称。");
        if (state.Templates.Any(item => item.Id != id && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("已存在同名视频去重模板。");

        var entry = CreateEntry(id ?? Guid.NewGuid().ToString("N"), name, preset);
        if (index < 0) state.Templates.Add(entry);
        else state.Templates[index] = entry;
        return entry;
    }

    internal void BindImportedAccounts(string templateId, IReadOnlyList<string> accountIds)
    {
        if (templateId == FollowDefaultId || accountIds.Count == 0)
            return;
        Edit(state =>
        {
            RequireTemplate(state, templateId);
            foreach (var id in accountIds)
                state.AccountDefaults[new VideoDedupTemplateAccount(id, VideoDedupTemplateTarget.TikTokLocal).BindingKey] = templateId;
            return (0, true);
        });
    }

    internal void RemoveImportedAccountBindings(IReadOnlyList<string> accountIds) => Edit(state =>
    {
        var changed = false;
        foreach (var id in accountIds)
            changed |= state.AccountDefaults.Remove(new VideoDedupTemplateAccount(id, VideoDedupTemplateTarget.TikTokLocal).BindingKey);
        return (0, changed);
    });

    public void Delete(string id, string? replacementId = null)
    {
        if (IsBuiltIn(id)) throw new InvalidOperationException("内置模板不能删除。");
        Edit(state =>
        {
            RequireTemplate(state, id);
            var bound = state.AccountDefaults.Where(item => item.Value == id).Select(item => item.Key).ToArray();
            if (bound.Length > 0)
            {
                if (replacementId is null || replacementId == id)
                    throw new InvalidOperationException("仍有账号使用此模板，请先选择替代模板。");
                if (replacementId == FollowDefaultId)
                {
                    foreach (var account in bound) state.AccountDefaults.Remove(account);
                }
                else
                {
                    RequireTemplate(state, replacementId);
                    foreach (var account in bound) state.AccountDefaults[account] = replacementId;
                }
            }
            state.Templates.RemoveAll(item => item.Id == id);
            return (0, true);
        });
    }

    internal IReadOnlyDictionary<string, VideoDedupPreset?> CaptureRunPresets(
        IReadOnlyList<TikTokAccountProfile> accounts, Action<string>? log)
    {
        var snapshot = LoadForRun(log);
        var captured = new Dictionary<string, VideoDedupPreset?>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
            captured[account.Id] = snapshot.DefaultFor(VideoDedupTemplateAccount.ForTikTok(account)).Preset.Clone();
        return captured;
    }

    public VideoDedupPreset CaptureRunPreset(VideoDedupTemplateAccount account, Action<string>? log) =>
        LoadForRun(log).DefaultFor(account).Preset.Clone();

    private VideoDedupTemplateSnapshot LoadForRun(Action<string>? log)
    {
        try
        {
            return Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"无法读取视频去重模板，使用内置默认模板：{ex.Message}");
            return new VideoDedupTemplateSnapshot(BuiltInTemplates.Value, new Dictionary<string, string>());
        }
    }

    internal static VideoDedupPreset BuiltInFor(VideoDedupTemplateAccount account) =>
        BuiltInTemplates.Value.First(item => item.Id == account.DefaultTemplateId).Preset.Clone();

    internal Dictionary<string, VideoDedupTemplateEntry> CaptureAccountSelections(IEnumerable<TikTokAccountProfile> accounts)
    {
        var snapshot = Load();
        var selections = new Dictionary<string, VideoDedupTemplateEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in accounts)
        {
            var account = VideoDedupTemplateAccount.ForTikTok(profile);
            if (snapshot.SelectedTemplateId(account) is not null)
                selections[account.Id] = snapshot.DefaultFor(account);
        }
        return selections;
    }

    internal void ImportAccountSelections(
        IReadOnlyDictionary<string, VideoDedupTemplateEntry> selections, IEnumerable<TikTokAccountProfile> accounts) =>
        Edit(state =>
        {
            var imported = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var profile in accounts)
            {
                var account = VideoDedupTemplateAccount.ForTikTok(profile);
                if (!selections.TryGetValue(account.Id, out var entry))
                {
                    state.AccountDefaults.Remove(account.BindingKey);
                    continue;
                }

                if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Preset is null)
                    throw new JsonException("导入的去重模板缺少参数或标识。");
                if (!imported.TryGetValue(entry.Id, out var id))
                {
                    var fingerprint = entry.Preset.Fingerprint();
                    var existing = state.Templates.FirstOrDefault(template =>
                        (template.Id == entry.Id || template.Name == entry.Name) &&
                        template.Preset.Fingerprint() == fingerprint);
                    var name = string.IsNullOrWhiteSpace(entry.Name) ? "导入模板" : entry.Name.Trim();
                    id = existing?.Id ?? SaveTemplate(state, null, UniqueName(state, name), entry.Preset).Id;
                    imported[entry.Id] = id;
                }
                state.AccountDefaults[account.BindingKey] = id;
            }
            return (0, true);
        });

    private static string UniqueName(CatalogState state, string name)
    {
        var root = name[..Math.Min(70, name.Length)];
        var candidate = root;
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
                ? JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(path), JsonOptions)
                    ?? throw new JsonException("视频去重模板库为空。")
                : new CatalogState { Version = 1 };
            var upgraded = state.Version == 1;
            if (upgraded)
                state = Upgrade(state);
            if (state.Version != CurrentVersion) throw new JsonException("视频去重模板库版本不受支持。");
            if (state.AccountDefaults is null || state.Templates is null ||
                state.Templates.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) ||
                    string.IsNullOrWhiteSpace(item.Name) || item.Preset is null || item.Id == FollowDefaultId) ||
                state.Templates.Select(item => item.Id).Distinct().Count() != state.Templates.Count ||
                !state.Templates.Any(item => item.Id == ChineseId) || !state.Templates.Any(item => item.Id == EnglishId))
                throw new JsonException("视频去重模板库包含无效或重复的模板。");

            state.AccountDefaults = new Dictionary<string, string>(state.AccountDefaults, StringComparer.OrdinalIgnoreCase);
            var (result, changed) = action(state);
            if (changed || upgraded) Write(path, state);
            return result;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static CatalogState Upgrade(CatalogState old)
    {
        if (old.Templates is null)
            throw new JsonException("旧视频去重模板库缺少模板列表。");

        var state = new CatalogState { Version = CurrentVersion };
        foreach (var builtIn in BuiltInTemplates.Value)
        {
            var previous = old.Templates.FirstOrDefault(item => item?.Name == builtIn.Name);
            try
            {
                if (previous is not null && previous.Preset is null)
                    throw new InvalidOperationException("缺少原模板参数。");
                state.Templates.Add(CreateEntry(builtIn.Id, builtIn.Name, previous?.Preset ?? builtIn.Preset));
            }
            catch (InvalidOperationException ex)
            {
                throw new JsonException($"无法升级去重模板「{builtIn.Name}」：{ex.Message}", ex);
            }
        }
        return state;
    }

    private static VideoDedupTemplateEntry[] ReadBuiltInTemplates()
    {
        using var stream = typeof(VideoDedupTemplateCatalog).Assembly.GetManifestResourceStream(SeedResource)
            ?? throw new InvalidOperationException("缺少内置视频去重模板。");
        var entries = JsonSerializer.Deserialize<VideoDedupTemplateEntry[]>(stream, JsonOptions)
            ?? throw new InvalidOperationException("内置视频去重模板为空。");
        if (entries.Length != 2 || entries[0].Id != ChineseId || entries[1].Id != EnglishId)
            throw new InvalidOperationException("内置视频去重模板不完整。");
        return entries.Select(entry => CreateEntry(entry.Id, entry.Name, entry.Preset)).ToArray();
    }

    private static VideoDedupTemplateEntry CreateEntry(string id, string name, VideoDedupPreset preset)
    {
        var parameters = preset.NormalizedCopy();
        if (parameters.DurationRangeError() is { } durationError)
            throw new InvalidOperationException(durationError);
        VideoDedupEffectFilters.Validate(parameters);
        parameters.Name = name;
        return new VideoDedupTemplateEntry(id, name, parameters);
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

    private static VideoDedupTemplateEntry RequireTemplate(CatalogState state, string? id) =>
        state.Templates.FirstOrDefault(template => template.Id == id)
        ?? throw new InvalidOperationException("该视频去重模板已不存在，请重新选择。");

    private sealed class CatalogState
    {
        public int Version { get; set; }
        public List<VideoDedupTemplateEntry> Templates { get; set; } = [];
        public Dictionary<string, string> AccountDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
