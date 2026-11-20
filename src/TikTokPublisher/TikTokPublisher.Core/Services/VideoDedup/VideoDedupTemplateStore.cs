using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.VideoDedup;

public sealed record VideoDedupTemplate(string Id, VideoDedupPreset Preset)
{
    [JsonIgnore]
    public string Name => Preset.Name;
}

public static class VideoDedupTemplateStore
{
    public const string SystemId = "system";
    internal const string FileName = "video-dedup-templates.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static string LibraryPath => Path.Combine(AppPaths.DataRoot, FileName);

    public static IReadOnlyList<VideoDedupTemplate> Load()
    {
        lock (Gate)
            return Read(LibraryPath);
    }

    public static VideoDedupTemplate Save(string? id, VideoDedupPreset preset)
    {
        var normalized = preset.NormalizedCopy();
        var durationError = normalized.DurationRangeError();
        if (durationError is not null)
            throw new InvalidOperationException(durationError);
        VideoDedupEffectFilters.Validate(normalized);
        lock (Gate)
            return SaveTemplate(Read(LibraryPath), id, normalized, preset.Name);
    }

    public static void Rename(string id, string name)
    {
        lock (Gate)
        {
            var templates = Read(LibraryPath);
            var current = templates.FirstOrDefault(template => template.Id == id)
                ?? throw new InvalidOperationException("该模板已不存在。");
            SaveTemplate(templates, id, current.Preset.Clone(), name);
        }
    }

    private static VideoDedupTemplate SaveTemplate(
        List<VideoDedupTemplate> templates, string? id, VideoDedupPreset preset, string? requestedName)
    {
        var name = requestedName?.Trim() ?? "";
        if (name.Length == 0 || string.Equals(name, "系统默认", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请输入自定义模板名称。");
        var existing = templates.FindIndex(template => template.Id == id);
        if (id is not null && existing < 0)
            throw new InvalidOperationException("该模板已不存在，请重新选择或新建模板。");
        if (templates.Any(template => template.Id != id &&
                string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("已存在同名视频去重模板。");
        preset.Name = name;
        var saved = new VideoDedupTemplate(id ?? Guid.NewGuid().ToString("N"), preset);
        if (existing >= 0)
            templates[existing] = saved;
        else
            templates.Add(saved);
        Write(LibraryPath, templates);
        return saved;
    }

    public static void Delete(string id, IEnumerable<TikTokAccountProfile> accounts)
    {
        lock (Gate)
        {
            if (accounts.Any(account => account.VideoDedupTemplateId == id))
                throw new InvalidOperationException("仍有账号使用此模板，请先为这些账号更换模板。");
            var templates = Read(LibraryPath);
            if (templates.RemoveAll(template => template.Id == id) > 0)
                Write(LibraryPath, templates);
        }
    }

    public static VideoDedupPreset? Resolve(TikTokAccountProfile account, IReadOnlyList<VideoDedupTemplate> templates)
    {
        if (account.VideoDedupTemplateId == SystemId)
            return null;
        return (templates.FirstOrDefault(template => template.Id == account.VideoDedupTemplateId)?.Preset
            ?? account.VideoDedupPreset)?.Clone();
    }

    public static IReadOnlyDictionary<string, VideoDedupPreset?> CaptureRunPresets(
        IEnumerable<TikTokAccountProfile> accounts, Action<string>? log)
    {
        IReadOnlyList<VideoDedupTemplate> templates;
        try { templates = Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"无法读取视频去重模板库，将使用账号保存的参数：{ex.Message}");
            templates = [];
        }

        var ids = templates.Select(template => template.Id).ToHashSet(StringComparer.Ordinal);
        var snapshot = new Dictionary<string, VideoDedupPreset?>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            var id = account.VideoDedupTemplateId;
            if (!string.IsNullOrWhiteSpace(id) && id != SystemId && !ids.Contains(id))
                log?.Invoke(account.VideoDedupPreset is null
                    ? $"账号「{account.DisplayName}」的视频去重模板缺失，使用系统默认。"
                    : $"账号「{account.DisplayName}」的视频去重模板缺失，使用账号保存的参数。");
            snapshot[account.Id] = Resolve(account, templates);
        }
        return snapshot;
    }

    internal static bool MigrateAccounts(IEnumerable<TikTokAccountProfile> accounts)
    {
        var legacy = accounts.Where(account => string.IsNullOrWhiteSpace(account.VideoDedupTemplateId) &&
            account.VideoDedupPreset is not null).ToArray();
        if (legacy.Length == 0)
            return false;

        lock (Gate)
        {
            try
            {
                var templates = Read(LibraryPath);
                var assignments = new List<(TikTokAccountProfile Account, VideoDedupTemplate Template)>();
                var before = templates.Count;
                foreach (var account in legacy)
                {
                    var preset = account.VideoDedupPreset!;
                    var fingerprint = preset.Fingerprint();
                    var template = templates.FirstOrDefault(candidate => candidate.Preset.Fingerprint() == fingerprint);
                    if (template is null)
                    {
                        var name = UniqueName(templates, $"{account.DisplayName} · 原去重方案");
                        template = Create(Guid.NewGuid().ToString("N"), preset, name);
                        templates.Add(template);
                    }
                    assignments.Add((account, template));
                }
                if (templates.Count != before)
                    Write(LibraryPath, templates);
                foreach (var (account, template) in assignments)
                    account.VideoDedupTemplateId = template.Id;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceWarning($"迁移视频去重模板失败，将继续使用原账号配置：{ex.Message}");
                return false;
            }
        }
    }

    internal static void Export(string path, IReadOnlyList<TikTokAccountProfile> accounts)
    {
        var ids = accounts.Select(account => account.VideoDedupTemplateId)
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != SystemId)
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            return;
        var templates = Load().Where(template => ids.Contains(template.Id)).ToArray();
        if (templates.Length > 0)
            Write(path, templates);
    }

    internal static void Import(string path, IReadOnlyList<TikTokAccountProfile> accounts)
    {
        var packaged = Read(path);
        if (packaged.Count == 0)
            return;
        lock (Gate)
        {
            var templates = Read(LibraryPath);
            var before = templates.Count;
            var remapped = new Dictionary<string, VideoDedupTemplate>(StringComparer.Ordinal);
            foreach (var template in packaged)
            {
                var local = templates.FirstOrDefault(candidate => candidate.Id == template.Id);
                if (local is null || local.Preset.Fingerprint() != template.Preset.Fingerprint())
                {
                    local = Create(local is null ? template.Id : Guid.NewGuid().ToString("N"),
                        template.Preset, UniqueName(templates, template.Name));
                    templates.Add(local);
                }
                remapped[template.Id] = local;
            }

            if (templates.Count != before)
                Write(LibraryPath, templates);
            foreach (var account in accounts)
            {
                if (!remapped.TryGetValue(account.VideoDedupTemplateId ?? "", out var template))
                    continue;
                account.VideoDedupTemplateId = template.Id;
                account.VideoDedupPreset = template.Preset.Clone();
            }
        }
    }

    private static VideoDedupTemplate Create(string id, VideoDedupPreset preset, string name)
    {
        var copy = preset.NormalizedCopy();
        copy.Name = name;
        return new VideoDedupTemplate(id, copy);
    }

    private static string UniqueName(IEnumerable<VideoDedupTemplate> templates, string baseName)
    {
        var names = templates.Select(template => template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        names.Add("系统默认");
        var name = baseName;
        for (var index = 2; names.Contains(name); index++)
            name = $"{baseName} ({index})";
        return name;
    }

    private static List<VideoDedupTemplate> Read(string path)
    {
        if (!File.Exists(path))
            return [];
        var templates = JsonSerializer.Deserialize<List<VideoDedupTemplate>>(File.ReadAllText(path), JsonOptions) ?? [];
        if (templates.Any(template => template is null || template.Preset is null ||
                string.IsNullOrWhiteSpace(template.Preset.Name) || string.IsNullOrWhiteSpace(template.Id) || template.Id == SystemId) ||
            templates.Select(template => template.Id).Distinct(StringComparer.Ordinal).Count() != templates.Count)
            throw new JsonException("视频去重模板库包含无效或重复的模板。");
        return templates;
    }

    private static void Write(string path, IReadOnlyList<VideoDedupTemplate> templates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(templates, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
