using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouScheduledShelfSchedule
{
    public const string Interval = "interval";
    public const string Daily = "daily";

    public static bool IsDue(
        bool enabled,
        string? mode,
        int intervalHours,
        string? dailyTime,
        DateTimeOffset? lastRun,
        DateTimeOffset now)
    {
        if (!enabled)
            return false;
        return string.Equals(mode, Daily, StringComparison.OrdinalIgnoreCase)
            ? DailyDue(dailyTime, lastRun, now)
            : IntervalDue(intervalHours, lastRun, now);
    }

    public static bool IntervalDue(int intervalHours, DateTimeOffset? lastRun, DateTimeOffset now)
    {
        var hours = Math.Clamp(intervalHours, 1, 24);
        return lastRun is null || now - lastRun.Value >= TimeSpan.FromHours(hours);
    }

    public static bool DailyDue(string? dailyTime, DateTimeOffset? lastRun, DateTimeOffset now)
    {
        if (!TryParseClock(dailyTime, out var clock))
            return false;
        var local = now.ToLocalTime();
        if (local.TimeOfDay < clock)
            return false;
        if (lastRun is { } ran)
        {
            var ranLocal = ran.ToLocalTime();
            if (ranLocal.Date == local.Date && ranLocal.TimeOfDay >= clock)
                return false;
        }

        return true;
    }

    public static bool TryParseClock(string? raw, out TimeSpan clock)
    {
        clock = default;
        var text = (raw ?? string.Empty).Trim();
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3)
            return false;
        if (!int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute))
            return false;
        if (hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;
        clock = new TimeSpan(hour, minute, 0);
        return true;
    }

    public static string NormalizeClock(string? raw) =>
        TryParseClock(raw, out var clock) ? $"{clock.Hours:00}:{clock.Minutes:00}" : "10:00";
}

public static class KuaishouScheduledShelfRow
{
    public static bool CanShelf(string? rowText)
    {
        if (string.IsNullOrWhiteSpace(rowText))
            return false;
        if (rowText.Contains("平台风控", StringComparison.Ordinal) ||
            rowText.Contains("不可上架", StringComparison.Ordinal))
            return false;
        return rowText.Contains("已下架", StringComparison.Ordinal);
    }

    public static string Key(string rowText)
    {
        var match = Regex.Match(rowText, @"短剧ID[:：\s]*(\d+)");
        return match.Success ? match.Groups[1].Value : string.Concat(rowText.Where(ch => !char.IsWhiteSpace(ch)));
    }

    public static string Title(string rowText)
    {
        foreach (var line in rowText.Split('\n', '\r'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.Contains("短剧ID", StringComparison.Ordinal))
                continue;
            if (text is "查看" or "编辑" or "上架" or "预约上架")
                continue;
            if (text.Contains("已下架", StringComparison.Ordinal) ||
                text.Contains("发布成功", StringComparison.Ordinal) ||
                text.Contains("平台风控", StringComparison.Ordinal))
                continue;
            if (DateTime.TryParse(text, out _))
                continue;
            return text.Length > 40 ? text[..40] : text;
        }

        return "未命名短剧";
    }
}

public static class KuaishouScheduledShelfStateStore
{
    public static string PathFor(KuaishouPersonalConfig config)
    {
        var directory = Path.GetDirectoryName(config.AuthStatePath);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.GetDirectoryName(KuaishouPersonalConfig.DefaultConfigPath(config.StorageAccountId, config.StoragePlatform));
        return Path.Combine(directory ?? string.Empty, "scheduled-shelf-state.json");
    }

    public static DateTimeOffset? ReadLastRun(KuaishouPersonalConfig config)
    {
        var path = PathFor(config);
        if (!File.Exists(path))
            return null;
        try
        {
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            return DateTimeOffset.TryParse(state?.LastRunAt, out var at) ? at : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void WriteLastRun(KuaishouPersonalConfig config, DateTimeOffset at)
    {
        var path = PathFor(config);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(new State { LastRunAt = at.ToString("O") }));
    }

    private sealed class State
    {
        public string LastRunAt { get; set; } = string.Empty;
    }
}
