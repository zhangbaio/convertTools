using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Desktop.Services;

public sealed class MaterialSystemHighlightScheduleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string RulesPath => Path.Combine(UserDataRoot(), "material-system-highlight-schedule-rules.json");
    public string StatePath => Path.Combine(UserDataRoot(), "material-system-highlight-schedule-state.json");

    public IReadOnlyList<MaterialSystemHighlightScheduleRule> LoadRules()
    {
        if (!File.Exists(RulesPath))
        {
            return [];
        }

        try
        {
            var payload = JsonSerializer.Deserialize<List<MaterialSystemHighlightScheduleRule>>(File.ReadAllText(RulesPath, Encoding.UTF8), JsonOptions);
            return NormalizeRules(payload ?? []);
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<MaterialSystemHighlightScheduleRule> SaveRules(IEnumerable<MaterialSystemHighlightScheduleRule> rules)
    {
        var normalized = NormalizeRules(rules);
        Directory.CreateDirectory(Path.GetDirectoryName(RulesPath)!);
        File.WriteAllText(RulesPath, JsonSerializer.Serialize(normalized, JsonOptions), Encoding.UTF8);
        return normalized;
    }

    public IReadOnlyDictionary<string, MaterialSystemHighlightScheduleState> LoadStateMap()
    {
        if (!File.Exists(StatePath))
        {
            return new Dictionary<string, MaterialSystemHighlightScheduleState>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, MaterialSystemHighlightScheduleState>>(File.ReadAllText(StatePath, Encoding.UTF8), JsonOptions)
                   ?? new Dictionary<string, MaterialSystemHighlightScheduleState>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, MaterialSystemHighlightScheduleState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void UpdateState(MaterialSystemHighlightScheduleRule rule, string summary, DateTimeOffset now)
    {
        var map = LoadStateMap().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var nextRun = CalculateNextRun(rule, now.AddMinutes(1));
        map[rule.Id] = new MaterialSystemHighlightScheduleState(
            Enabled: rule.Enabled,
            TriggerMode: rule.TriggerMode,
            NextRunAt: nextRun?.ToString("O") ?? string.Empty,
            LastRunKey: BuildRunKey(rule, now),
            LastRunAt: now.ToString("O"),
            LastSummary: summary);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(map, JsonOptions), Encoding.UTF8);
    }

    public IReadOnlyList<MaterialSystemHighlightScheduleRule> NormalizeRules(IEnumerable<MaterialSystemHighlightScheduleRule> rules)
    {
        var result = new List<MaterialSystemHighlightScheduleRule>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rules)
        {
            var rule = NormalizeRule(raw);
            var id = rule.Id;
            while (!seen.Add(id))
            {
                id = NewRuleId();
            }

            result.Add(rule with { Id = id });
        }

        return result;
    }

    public MaterialSystemHighlightScheduleRule NormalizeRule(MaterialSystemHighlightScheduleRule rule)
    {
        var id = SanitizeId(rule.Id);
        var dramas = NormalizeDramaItems(rule.Dramas).ToArray();
        var publishCount = Math.Clamp(rule.PublishCount <= 0 ? MaterialSystemHighlightScheduleRule.DefaultPublishCount : rule.PublishCount, 1, 999);
        return rule with
        {
            Id = string.IsNullOrWhiteSpace(id) ? NewRuleId() : id,
            Name = string.IsNullOrWhiteSpace(rule.Name)
                ? dramas.Length > 0 ? string.Join(" / ", dramas.Take(2).Select(item => item.Title)) : "系统高光自动发布"
                : rule.Name.Trim(),
            ProfileId = SanitizeId(rule.ProfileId),
            WorkspacePath = NormalizeOptionalPath(rule.WorkspacePath),
            TriggerMode = string.Equals(rule.TriggerMode, "interval", StringComparison.OrdinalIgnoreCase) ? "interval" : "fixed_time",
            IntervalMinutes = Math.Clamp(rule.IntervalMinutes <= 0 ? 30 : rule.IntervalMinutes, 1, 1440),
            ScheduleMode = string.Equals(rule.ScheduleMode, "weekly", StringComparison.OrdinalIgnoreCase) ? "weekly" : "daily",
            Time = NormalizeClockText(rule.Time),
            Weekdays = NormalizeWeekdays(rule.Weekdays),
            DefaultDescription = string.IsNullOrWhiteSpace(rule.DefaultDescription)
                ? MaterialSystemHighlightBatchPublishService.DefaultDescription
                : rule.DefaultDescription.Trim(),
            PublishCount = publishCount,
            PublishTargetMode = string.Equals(rule.PublishTargetMode, "type", StringComparison.OrdinalIgnoreCase) ? "type" : "count",
            PublishVideoTypes = NormalizeVideoTypes(rule.PublishVideoTypes),
            RegenerateVideoTypes = NormalizeVideoTypes(rule.RegenerateVideoTypes),
            Dramas = dramas.Select(item => item with { PublishCount = publishCount }).ToArray()
        };
    }

    public DateTimeOffset? CalculateNextRun(MaterialSystemHighlightScheduleRule rule, DateTimeOffset now)
    {
        rule = NormalizeRule(rule);
        if (!rule.Enabled)
        {
            return null;
        }

        if (rule.TriggerMode == "interval")
        {
            return now.AddMinutes(Math.Max(1, rule.IntervalMinutes));
        }

        var parts = rule.Time.Split(':');
        var hour = int.Parse(parts[0]);
        var minute = int.Parse(parts[1]);
        for (var offset = 0; offset < 14; offset++)
        {
            var day = now.Date.AddDays(offset);
            var candidate = new DateTimeOffset(day.AddHours(hour).AddMinutes(minute), now.Offset);
            if (candidate <= now)
            {
                continue;
            }

            if (rule.ScheduleMode == "weekly")
            {
                var weekday = ((int)candidate.DayOfWeek + 6) % 7 + 1;
                var weekdays = rule.Weekdays.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
                if (!weekdays.Contains(weekday))
                {
                    continue;
                }
            }

            return candidate;
        }

        return null;
    }

    public string BuildRunKey(MaterialSystemHighlightScheduleRule rule, DateTimeOffset now)
    {
        rule = NormalizeRule(rule);
        return rule.TriggerMode == "interval"
            ? $"{now:yyyyMMddHHmm}"
            : $"{now:yyyyMMdd}-{rule.Time}";
    }

    private static IReadOnlyList<MaterialSystemHighlightScheduleDrama> NormalizeDramaItems(IEnumerable<MaterialSystemHighlightScheduleDrama>? dramas)
    {
        var result = new List<MaterialSystemHighlightScheduleDrama>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in dramas ?? [])
        {
            var title = (raw.Title ?? string.Empty).Trim();
            if (title.Length == 0 || !seen.Add(Regex.Replace(title, @"\s+", string.Empty)))
            {
                continue;
            }

            result.Add(new MaterialSystemHighlightScheduleDrama(title, Math.Max(1, raw.PublishCount), raw.Enabled));
        }

        return result;
    }

    public static IReadOnlyList<MaterialSystemHighlightScheduleDrama> ParseDramaLines(string text, int publishCount)
    {
        return (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(line => new MaterialSystemHighlightScheduleDrama(line, Math.Max(1, publishCount), true))
            .ToArray();
    }

    private static string NormalizeClockText(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "09:00" : value.Trim();
        var parts = text.Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var hour) ||
            !int.TryParse(parts[1], out var minute) ||
            hour is < 0 or > 23 ||
            minute is < 0 or > 59)
        {
            return "09:00";
        }

        return $"{hour:00}:{minute:00}";
    }

    private static string NormalizeWeekdays(string value)
    {
        var days = Regex.Split(value ?? string.Empty, @"[\s,，;；]+")
            .Where(item => int.TryParse(item, out var day) && day is >= 1 and <= 7)
            .Select(int.Parse)
            .Distinct()
            .OrderBy(day => day)
            .ToArray();
        return string.Join(",", days.Length == 0 ? [1, 2, 3, 4, 5, 6, 7] : days);
    }

    private static IReadOnlyList<string> NormalizeVideoTypes(IEnumerable<string>? values)
    {
        var requested = (values ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToHashSet(StringComparer.Ordinal);
        return MaterialSystemHighlightBatchPublishService.VideoTypeOptions
            .Where(item => requested.Count == 0 || requested.Contains(item))
            .DefaultIfEmpty(MaterialSystemHighlightBatchPublishService.VideoTypeOptions[0])
            .ToArray();
    }

    private static string NormalizeOptionalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string SanitizeId(string value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"[^a-zA-Z0-9_-]+", "-").Trim('-', '_');

    private static string NewRuleId() => Guid.NewGuid().ToString("N")[..12];

    private static string UserDataRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".shortdrama-desktop")
            : Path.Combine(appData, "ShortDramaDesktop");
    }
}

public sealed record MaterialSystemHighlightScheduleRule(
    string Id,
    string Name,
    bool Enabled,
    string ProfileId,
    string WorkspacePath,
    string TriggerMode,
    int IntervalMinutes,
    string ScheduleMode,
    string Time,
    string Weekdays,
    bool CatchUpOnStartup,
    bool OnlyWhenIdle,
    string DefaultDescription,
    int PublishCount,
    string PublishTargetMode,
    IReadOnlyList<string> PublishVideoTypes,
    bool RegenerateAfterPublish,
    IReadOnlyList<string> RegenerateVideoTypes,
    IReadOnlyList<MaterialSystemHighlightScheduleDrama> Dramas)
{
    public const int DefaultPublishCount = 10;

    public static MaterialSystemHighlightScheduleRule CreateDefault(string workspacePath, string profileId = "") =>
        new(
            Id: string.Empty,
            Name: string.Empty,
            Enabled: true,
            ProfileId: profileId,
            WorkspacePath: workspacePath,
            TriggerMode: "fixed_time",
            IntervalMinutes: 30,
            ScheduleMode: "daily",
            Time: "09:00",
            Weekdays: "1,2,3,4,5,6,7",
            CatchUpOnStartup: false,
            OnlyWhenIdle: true,
            DefaultDescription: MaterialSystemHighlightBatchPublishService.DefaultDescription,
            PublishCount: DefaultPublishCount,
            PublishTargetMode: "count",
            PublishVideoTypes: MaterialSystemHighlightBatchPublishService.VideoTypeOptions,
            RegenerateAfterPublish: false,
            RegenerateVideoTypes: MaterialSystemHighlightBatchPublishService.VideoTypeOptions,
            Dramas: []);
}

public sealed record MaterialSystemHighlightScheduleDrama(
    string Title,
    int PublishCount,
    bool Enabled);

public sealed record MaterialSystemHighlightScheduleState(
    bool Enabled,
    string TriggerMode,
    string NextRunAt,
    string LastRunKey,
    string LastRunAt,
    string LastSummary);
