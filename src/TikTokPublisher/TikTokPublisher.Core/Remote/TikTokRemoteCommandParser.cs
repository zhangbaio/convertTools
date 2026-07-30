using System.Text.Json;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Remote;

public static class TikTokRemoteCommandParser
{
    private static readonly HashSet<string> TextHelpAliases = new(StringComparer.Ordinal)
    {
        "教程", "帮助", "help", "tutorial",
    };

    private static readonly HashSet<string> CardHelpAliases = new(StringComparer.Ordinal)
    {
        "菜单", "menu", "卡片", "命令卡片", "卡片教程", "教程卡片",
        "help_card", "card_tutorial", "tutorial_card",
    };

    private static readonly HashSet<string> StatusAliases = new(StringComparer.Ordinal)
    {
        "tiktok状态", "状态", "查询状态", "tiktokstatus", "status",
    };

    private static readonly HashSet<string> StartQueueAliases = new(StringComparer.Ordinal)
    {
        "执行tiktok队列", "启动tiktok队列", "开始tiktok队列", "执行队列",
    };

    private static readonly HashSet<string> StopQueueAliases = new(StringComparer.Ordinal)
    {
        "停止tiktok队列", "结束tiktok队列", "停止队列", "停止",
    };

    private static readonly HashSet<string> UploadAliases = new(StringComparer.Ordinal)
    {
        "tiktok上传", "上传tiktok", "上传tiktok剧集", "上传tiktok短剧", "上传剧集",
    };

    private static readonly HashSet<string> WorkspaceKeys = new(StringComparer.Ordinal)
    {
        "工作目录", "workspace", "workspace_path",
    };

    private static readonly HashSet<string> AccountKeys = new(StringComparer.Ordinal)
    {
        "账号", "account", "profile", "account_profile",
    };

    private static readonly HashSet<string> AccountListKeys = new(StringComparer.Ordinal)
    {
        "账号列表", "多账号", "accounts", "profiles", "account_profiles", "account_profile_ids",
    };

    private static readonly HashSet<string> AllAccountAliases = new(StringComparer.Ordinal)
    {
        "全部", "全部账号", "所有", "所有账号", "all", "*",
    };

    private static readonly HashSet<string> AutoRunTrue = new(StringComparer.Ordinal)
    {
        "是", "true", "1", "yes", "y", "自动执行",
    };

    private static readonly HashSet<string> AutoRunFalse = new(StringComparer.Ordinal)
    {
        "否", "false", "0", "no", "n", "仅导入",
    };

    public static TikTokRemoteCommand? Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var json = ParseJsonCommand(raw);
        return json ?? ParseTextCommand(raw);
    }

    public static IReadOnlyList<string> NormalizeEnabledSteps(object? value)
    {
        var known = QueueStepRegistry.All
            .Where(step => QueueStepRegistry.IsAvailable(step.Key))
            .Select(step => step.Key)
            .ToHashSet(StringComparer.Ordinal);
        IEnumerable<object?> rawItems = value switch
        {
            null => Array.Empty<object?>(),
            JsonElement element => StepsFromJsonElement(element),
            IEnumerable<string> strings => strings.Cast<object?>().ToArray(),
            IEnumerable<object?> objects => objects.ToArray(),
            _ => NormalizeListText(value).Cast<object?>().ToArray(),
        };

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rawItems)
        {
            var step = (item?.ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(step) || !known.Contains(step) || !seen.Add(step))
                continue;
            result.Add(step);
        }

        return result;
    }

    public static IReadOnlyList<string> NormalizeTitles(IEnumerable<object?> values)
    {
        var titles = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var title = NormalizeTitle(value);
            if (string.IsNullOrWhiteSpace(title) || !seen.Add(title))
                continue;
            titles.Add(title);
        }

        return titles;
    }

    public static string NormalizeTitle(object? value)
    {
        var text = (value?.ToString() ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "";

        text = text.Replace("\u200b", "", StringComparison.Ordinal)
            .Replace("\ufeff", "", StringComparison.Ordinal)
            .Trim();
        text = Regex.Replace(text, "^[\\\"'“”‘’《》<>【】\\[\\]\\(\\)\\s]+", "");
        text = Regex.Replace(text, "[\\\"'“”‘’《》<>【】\\[\\]\\(\\)\\s]+$", "");
        text = Regex.Replace(text, "^\\d+[\\.\\)） 、-]+", "");
        return text.Trim();
    }

    public static string NormalizeCommandAlias(object? value) =>
        Regex.Replace((value?.ToString() ?? "").Trim(), "\\s+", "").ToLowerInvariant();

    public static IReadOnlyList<string> NormalizeAccountSelectors(object? value)
    {
        IEnumerable<object?> rawItems = value switch
        {
            null => Array.Empty<object?>(),
            JsonElement element => AccountSelectorsFromJsonElement(element),
            IEnumerable<string> strings => strings.Cast<object?>().ToArray(),
            IEnumerable<object?> objects => objects.ToArray(),
            _ => NormalizeListText(value).Cast<object?>().ToArray(),
        };

        var selectors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rawItems)
        {
            var selector = (item?.ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(selector) || !seen.Add(selector))
                continue;
            selectors.Add(selector);
        }

        return selectors;
    }

    public static bool IsAllAccountsSelector(object? value) =>
        AllAccountAliases.Contains(NormalizeCommandAlias(value));

    private static TikTokRemoteCommand? ParseJsonCommand(string text)
    {
        if (!text.StartsWith('{'))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var root = document.RootElement;
            var commandName = GetString(root, "command").Trim().ToLowerInvariant();
            var workspacePath = FirstString(root, "workspace_path", "workspace");
            var accountProfileId = FirstString(root, "account_profile_id", "profile_id");
            var accountProfileName = FirstString(root, "account_profile_name", "account", "profile");
            var accountSelectors = ReadJsonAccountSelectors(root, accountProfileId, accountProfileName);
            var allAccounts = IsAllAccountsSelector(accountProfileId) ||
                              IsAllAccountsSelector(accountProfileName) ||
                              accountSelectors.Any(IsAllAccountsSelector) ||
                              (TryGetProperty(root, "all_accounts", out var allAccountsElement) &&
                               GetBool(allAccountsElement, false));
            var enabledSteps = TryGetProperty(root, "enabled_steps", out var enabledElement)
                ? NormalizeEnabledSteps(enabledElement)
                : null;
            var autoRun = TryGetProperty(root, "auto_run", out var autoRunElement)
                ? GetBool(autoRunElement, true)
                : true;
            var queueOptions = TryGetProperty(root, "queue_options", out var queueOptionsElement)
                ? JsonObjectToDictionary(queueOptionsElement)
                : null;
            var matchMode = FirstString(root, "match_mode").ToLowerInvariant();
            var series = TryGetProperty(root, "series", out var seriesElement)
                ? SeriesFromJsonElement(seriesElement)
                : Array.Empty<TikTokUploadSeriesSpec>();

            if (commandName is "tiktok_upload_series" or "upload_tiktok_series")
            {
                var titles = TryGetProperty(root, "titles", out var titlesElement)
                    ? TitlesFromJsonElement(titlesElement)
                    : [];
                // 兼容仅下发 series（无 titles）的情况：从 series 补齐剧名列表。
                if (titles.Count == 0 && series.Count > 0)
                    titles = NormalizeTitles(series.Select(spec => (object?)spec.Title));
                return new TikTokRemoteCommand(
                    TikTokRemoteCommandNames.UploadSeries,
                    Titles: titles,
                    WorkspacePath: workspacePath,
                    AccountProfileId: accountProfileId,
                    AccountProfileName: accountProfileName,
                    AccountSelectors: EmptyToNull(accountSelectors),
                    AllAccounts: allAccounts,
                    EnabledSteps: EmptyToNull(enabledSteps),
                    AutoRun: autoRun,
                    QueueOptions: queueOptions,
                    MatchMode: matchMode,
                    Series: series.Count > 0 ? series : null);
            }

            if (commandName is "tiktok_start_queue" or "start_tiktok_queue")
            {
                return new TikTokRemoteCommand(
                    TikTokRemoteCommandNames.StartQueue,
                    WorkspacePath: workspacePath,
                    AccountProfileId: accountProfileId,
                    AccountProfileName: accountProfileName,
                    AccountSelectors: EmptyToNull(accountSelectors),
                    AllAccounts: allAccounts,
                    EnabledSteps: EmptyToNull(enabledSteps),
                    AutoRun: true,
                    QueueOptions: queueOptions);
            }

            return commandName switch
            {
                "tiktok_stop_queue" or "stop_tiktok_queue" => new TikTokRemoteCommand(TikTokRemoteCommandNames.StopQueue),
                "tiktok_query_status" or "query_tiktok_status" => new TikTokRemoteCommand(TikTokRemoteCommandNames.QueryStatus),
                "help" or "tutorial" or "show_help_text" => new TikTokRemoteCommand(TikTokRemoteCommandNames.ShowHelpText),
                "menu" or "show_help_card" or "help_card" or "card_tutorial" or "tutorial_card" => new TikTokRemoteCommand(TikTokRemoteCommandNames.ShowHelpCard),
                "switch_account_profile" => new TikTokRemoteCommand(
                    TikTokRemoteCommandNames.SwitchAccountProfile,
                    AccountProfileId: accountProfileId,
                    AccountProfileName: accountProfileName,
                    AccountSelectors: EmptyToNull(accountSelectors),
                    AllAccounts: allAccounts),
                _ => null,
            };
        }
    }

    private static TikTokRemoteCommand? ParseTextCommand(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (lines.Length == 0)
            return null;

        var firstLine = NormalizeCommandAlias(lines[0]);
        if (TextHelpAliases.Contains(firstLine))
            return new TikTokRemoteCommand(TikTokRemoteCommandNames.ShowHelpText);
        if (CardHelpAliases.Contains(firstLine))
            return new TikTokRemoteCommand(TikTokRemoteCommandNames.ShowHelpCard);
        if (StatusAliases.Contains(firstLine))
            return new TikTokRemoteCommand(TikTokRemoteCommandNames.QueryStatus);
        if (StartQueueAliases.Contains(firstLine))
        {
            var common = ParseCommonCommandLines(lines.Skip(1));
            return new TikTokRemoteCommand(
                TikTokRemoteCommandNames.StartQueue,
                WorkspacePath: common.WorkspacePath,
                AccountProfileId: common.AccountProfileId,
                AccountProfileName: common.AccountProfileName,
                AccountSelectors: common.AccountSelectors,
                AllAccounts: common.AllAccounts,
                EnabledSteps: common.EnabledSteps);
        }
        if (StopQueueAliases.Contains(firstLine))
            return new TikTokRemoteCommand(TikTokRemoteCommandNames.StopQueue);

        var matchedTitles = MatchUploadCommand(lines[0]);
        if (matchedTitles is null)
            return null;

        var parsed = ParseCommonCommandLines(lines.Skip(1));
        var titles = NormalizeTitles(matchedTitles.Cast<object?>().Concat(parsed.TitleLines.Cast<object?>()));
        return new TikTokRemoteCommand(
            TikTokRemoteCommandNames.UploadSeries,
            Titles: titles,
            WorkspacePath: parsed.WorkspacePath,
            AccountProfileId: parsed.AccountProfileId,
            AccountProfileName: parsed.AccountProfileName,
            AccountSelectors: parsed.AccountSelectors,
            AllAccounts: parsed.AllAccounts,
            EnabledSteps: parsed.EnabledSteps,
            AutoRun: parsed.AutoRun);
    }

    private static List<string>? MatchUploadCommand(string firstLine)
    {
        var text = (firstLine ?? "").Trim();
        var normalized = NormalizeCommandAlias(text);
        if (UploadAliases.Contains(normalized))
            return [];

        foreach (var alias in UploadAliases.OrderByDescending(item => item.Length))
        {
            if (!normalized.StartsWith(alias, StringComparison.Ordinal))
                continue;

            var remainder = text[Math.Min(text.Length, alias.Length)..].Trim(' ', '：', ':', '，', ',');
            return string.IsNullOrWhiteSpace(remainder)
                ? []
                : NormalizeTitles([remainder]).ToList();
        }

        return null;
    }

    private static ParsedCommonCommandLines ParseCommonCommandLines(IEnumerable<string> lines)
    {
        var workspacePath = "";
        var accountProfileId = "";
        var accountProfileName = "";
        IReadOnlyList<string>? accountSelectors = null;
        var allAccounts = false;
        IReadOnlyList<string>? enabledSteps = null;
        var autoRun = true;
        var titleLines = new List<string>();

        foreach (var line in lines)
        {
            var (key, value) = SplitKeyValue(line);
            if (!string.IsNullOrEmpty(key) && WorkspaceKeys.Contains(key))
            {
                workspacePath = value;
                continue;
            }

            if (!string.IsNullOrEmpty(key) && (AccountKeys.Contains(key) || AccountListKeys.Contains(key)))
            {
                var selectors = NormalizeAccountSelectors(value);
                allAccounts = selectors.Any(IsAllAccountsSelector);
                if (!allAccounts)
                    accountSelectors = EmptyToNull(selectors);
                if (selectors.Count == 1)
                {
                    var selector = selectors[0];
                    if (Regex.IsMatch(selector, "^[a-zA-Z0-9_-]+$"))
                        accountProfileId = selector;
                    accountProfileName = selector;
                }
                else
                {
                    accountProfileId = "";
                    accountProfileName = value.Trim();
                }
                continue;
            }

            if (!string.IsNullOrEmpty(key) && key is "步骤" or "steps" or "enabled_steps")
            {
                enabledSteps = EmptyToNull(NormalizeEnabledSteps(value));
                continue;
            }

            if (!string.IsNullOrEmpty(key) && key is "自动执行" or "auto_run")
            {
                autoRun = ParseAutoRun(value);
                continue;
            }

            titleLines.Add(line);
        }

        return new ParsedCommonCommandLines(
            workspacePath,
            accountProfileId,
            accountProfileName,
            accountSelectors,
            allAccounts,
            enabledSteps,
            autoRun,
            titleLines);
    }

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        var match = Regex.Match(line ?? "", "^\\s*([^:：]+)\\s*[:：]\\s*(.+?)\\s*$");
        return match.Success
            ? (NormalizeCommandAlias(match.Groups[1].Value), match.Groups[2].Value.Trim())
            : ("", "");
    }

    private static bool ParseAutoRun(string value)
    {
        var normalized = NormalizeCommandAlias(value);
        if (AutoRunFalse.Contains(normalized))
            return false;
        if (AutoRunTrue.Contains(normalized))
            return true;
        return true;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static string FirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetString(element, propertyName).Trim();
            if (value.Length > 0)
                return value;
        }

        return "";
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool GetBool(JsonElement element, bool fallback)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => TryParseBool(element.GetString(), fallback),
            JsonValueKind.Number => element.TryGetInt32(out var number) ? number != 0 : fallback,
            _ => fallback,
        };
    }

    private static bool TryParseBool(string? value, bool fallback)
    {
        var normalized = NormalizeCommandAlias(value);
        if (AutoRunFalse.Contains(normalized))
            return false;
        if (AutoRunTrue.Contains(normalized))
            return true;
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static IReadOnlyDictionary<string, object?>? JsonObjectToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            result[property.Name] = JsonValueToObject(property.Value);
        return result;
    }

    private static object? JsonValueToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(JsonValueToObject).ToList(),
            JsonValueKind.Object => JsonObjectToDictionary(element),
            _ => null,
        };

    private static object?[] StepsFromJsonElement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(JsonValueToObject).ToArray()
            : NormalizeListText(element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()).Cast<object?>().ToArray();

    private static IReadOnlyList<string> TitlesFromJsonElement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? NormalizeTitles(element.EnumerateArray().Select(JsonValueToObject))
            : NormalizeTitles([element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()]);

    // 解析 series:[{title, episode_cnt, series_id}]（也兼容字符串数组，退化为仅剧名）。
    private static IReadOnlyList<TikTokUploadSeriesSpec> SeriesFromJsonElement(JsonElement element)
    {
        var list = new List<TikTokUploadSeriesSpec>();
        if (element.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                var plainTitle = NormalizeTitle(entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.ToString());
                if (plainTitle.Length > 0)
                    list.Add(new TikTokUploadSeriesSpec(plainTitle));
                continue;
            }

            var title = NormalizeTitle(GetString(entry, "title"));
            if (title.Length == 0)
                continue;

            var episodeCnt = 0;
            if (TryGetProperty(entry, "episode_cnt", out var episodeElement))
            {
                if (episodeElement.ValueKind == JsonValueKind.Number && episodeElement.TryGetInt32(out var number))
                    episodeCnt = number;
                else if (episodeElement.ValueKind == JsonValueKind.String && int.TryParse(episodeElement.GetString(), out var parsed))
                    episodeCnt = parsed;
            }

            list.Add(new TikTokUploadSeriesSpec(title, episodeCnt < 0 ? 0 : episodeCnt, GetString(entry, "series_id").Trim()));
        }

        return list;
    }

    private static IReadOnlyList<string> ReadJsonAccountSelectors(
        JsonElement root,
        params string[] fallbackValues)
    {
        foreach (var propertyName in new[] { "accounts", "account_profiles", "profiles", "account_profile_ids" })
        {
            if (TryGetProperty(root, propertyName, out var element))
                return NormalizeAccountSelectors(element);
        }

        return NormalizeAccountSelectors(fallbackValues.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static object?[] AccountSelectorsFromJsonElement(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(JsonValueToObject).ToArray()
            : NormalizeListText(element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()).Cast<object?>().ToArray();

    private static IReadOnlyList<string> NormalizeListText(object? value)
    {
        var items = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in Regex.Split(value?.ToString() ?? "", "[\\r\\n,，;；]+"))
        {
            var cleaned = chunk.Trim();
            if (cleaned.Length == 0 || !seen.Add(cleaned))
                continue;
            items.Add(cleaned);
        }

        return items;
    }

    private static IReadOnlyList<string>? EmptyToNull(IReadOnlyList<string>? value) =>
        value is { Count: > 0 } ? value : null;

    private sealed record ParsedCommonCommandLines(
        string WorkspacePath,
        string AccountProfileId,
        string AccountProfileName,
        IReadOnlyList<string>? AccountSelectors,
        bool AllAccounts,
        IReadOnlyList<string>? EnabledSteps,
        bool AutoRun,
        IReadOnlyList<string> TitleLines);
}
