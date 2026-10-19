using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouContentPolicyDefaults
{
    public static string TitlePolicyJson { get; } = Read("kuaishou-title-policy.json");
    public static string SynopsisPolicyJson { get; } = Read("kuaishou-synopsis-policy.json");

    private static string Read(string fileName)
    {
        var name = typeof(KuaishouContentPolicyDefaults).Assembly.GetManifestResourceNames()
            .First(item => item.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = typeof(KuaishouContentPolicyDefaults).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("缺少快手内容合规默认词库：" + fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public static class KuaishouContentPolicyText
{
    public static string BaselineSummary(string defaultJson, string categoriesName)
    {
        var categories = ReadCategories(Parse(defaultJson)?[categoriesName]);
        var words = categories.Sum(item => item.Value.Count);
        return $"系统默认基线：{categories.Count} 个分类，共 {words} 个词（当前列表可编辑）";
    }

    public static string FormatCategories(string? savedJson, string defaultJson, string categoriesName, string disabledName)
    {
        var categories = EffectiveCategories(savedJson, defaultJson, categoriesName, disabledName);
        return string.Join("\n", categories.Select(item => $"{item.Key}：{string.Join('、', item.Value)}"));
    }

    public static string FormatRules(string? savedJson, string defaultJson)
    {
        var rules = ReadRules(Parse(savedJson)?["combination_rules"] ?? Parse(defaultJson)?["combination_rules"]);
        return string.Join("\n", rules.Select(rule => $"{rule.Category}：{string.Join('+', rule.Groups)}"));
    }

    public static string BuildTitlePolicy(string editorText, string defaultJson)
    {
        var edited = ParseCategoryText(editorText);
        var defaults = ReadCategories(Parse(defaultJson)?["categories"]);
        return new JsonObject
        {
            ["version"] = 1,
            ["categories"] = ToJson(edited),
            ["disabled"] = ToJson(Removed(defaults, edited)),
        }.ToJsonString();
    }

    public static string BuildSynopsisPolicy(string termsText, string conceptsText, string rulesText, string defaultJson)
    {
        var defaultRoot = Parse(defaultJson);
        var terms = ParseCategoryText(termsText);
        var concepts = ParseCategoryText(conceptsText);
        var defaultTerms = ReadCategories(defaultRoot?["categories"]);
        var defaultConcepts = ReadCategories(defaultRoot?["concept_groups"]);
        return new JsonObject
        {
            ["version"] = 2,
            ["categories"] = ToJson(terms),
            ["disabled"] = ToJson(Removed(defaultTerms, terms)),
            ["concept_groups"] = ToJson(concepts),
            ["disabled_concepts"] = ToJson(Removed(defaultConcepts, concepts)),
            ["combination_rules"] = ToRules(ParseRulesText(rulesText)),
        }.ToJsonString();
    }

    public static IReadOnlyList<string> EffectiveTerms(string? savedJson, string defaultJson, string categoriesName, string disabledName) =>
        EffectiveCategories(savedJson, defaultJson, categoriesName, disabledName)
            .SelectMany(item => item.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> Terms(string? policyJson, string categoriesName)
    {
        var categories = ReadCategories(Parse(string.IsNullOrWhiteSpace(policyJson) ? null : policyJson)?[categoriesName]);
        return categories.SelectMany(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<string, List<string>> EffectiveCategories(
        string? savedJson, string defaultJson, string categoriesName, string disabledName)
    {
        var result = ReadCategories(Parse(defaultJson)?[categoriesName]);
        var saved = Parse(savedJson);
        foreach (var (category, terms) in ReadCategories(saved?[categoriesName]))
        {
            if (!result.TryGetValue(category, out var target))
            {
                result[category] = terms;
                continue;
            }
            foreach (var term in terms)
                if (!target.Contains(term, StringComparer.Ordinal)) target.Add(term);
        }
        foreach (var (category, terms) in ReadCategories(saved?[disabledName]))
        {
            if (!result.TryGetValue(category, out var target)) continue;
            result[category] = target.Where(term => !terms.Contains(term, StringComparer.Ordinal)).ToList();
            if (result[category].Count == 0) result.Remove(category);
        }
        return result;
    }

    private static Dictionary<string, List<string>> Removed(
        Dictionary<string, List<string>> defaults, Dictionary<string, List<string>> edited)
    {
        var removed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (category, terms) in defaults)
        {
            var kept = edited.TryGetValue(category, out var editedTerms) ? editedTerms : [];
            var missing = terms.Where(term => !kept.Contains(term, StringComparer.Ordinal)).ToList();
            if (missing.Count > 0) removed[category] = missing;
        }
        return removed;
    }

    private static Dictionary<string, List<string>> ParseCategoryText(string? value)
    {
        var categories = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var raw in (value ?? string.Empty).Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = Regex.Split(line, "[:：]", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (split.Length < 2 || string.IsNullOrWhiteSpace(split[0]))
                throw new InvalidOperationException($"第 {lineNumber} 行格式错误，应为“分类：词1、词2”。");
            var terms = Regex.Split(string.Join('：', split.Skip(1)), "[、,，;；]")
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (terms.Count == 0) throw new InvalidOperationException($"第 {lineNumber} 行没有词语。");
            if (!categories.TryGetValue(split[0].Trim(), out var target))
                categories[split[0].Trim()] = terms;
            else
                target.AddRange(terms.Where(term => !target.Contains(term, StringComparer.Ordinal)));
        }
        return categories;
    }

    private static IReadOnlyList<(string Category, IReadOnlyList<string> Groups)> ParseRulesText(string? value)
    {
        var rules = new List<(string, IReadOnlyList<string>)>();
        var lineNumber = 0;
        foreach (var raw in (value ?? string.Empty).Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = Regex.Split(line, "[:：]", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (split.Length < 2)
                throw new InvalidOperationException($"组合规则第 {lineNumber} 行格式错误，应为“分类：概念组1+概念组2”。");
            var groups = split[1].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (groups.Length == 0) throw new InvalidOperationException($"组合规则第 {lineNumber} 行至少需要一个概念组。");
            rules.Add((split[0].Trim(), groups));
        }
        return rules;
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json)?.AsObject(); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static Dictionary<string, List<string>> ReadCategories(JsonNode? node)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (node is not JsonObject source) return result;
        foreach (var (name, value) in source)
        {
            if (value is not JsonArray array) continue;
            var terms = array.Select(item => item?.ToString().Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (terms.Count > 0) result[name] = terms;
        }
        return result;
    }

    private static IReadOnlyList<(string Category, IReadOnlyList<string> Groups)> ReadRules(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        var rules = new List<(string, IReadOnlyList<string>)>();
        foreach (var item in array)
        {
            if (item is not JsonObject rule) continue;
            var category = rule["category"]?.ToString().Trim() ?? string.Empty;
            var groups = rule["required_groups"] is JsonArray required
                ? required.Select(group => group?.ToString().Trim()).Where(group => !string.IsNullOrWhiteSpace(group)).Select(group => group!).ToArray()
                : [];
            if (category.Length > 0 && groups.Length > 0) rules.Add((category, groups));
        }
        return rules;
    }

    private static JsonObject ToJson(Dictionary<string, List<string>> categories)
    {
        var result = new JsonObject();
        foreach (var (category, terms) in categories)
            result[category] = new JsonArray(terms.Select(term => JsonValue.Create(term)).ToArray());
        return result;
    }

    private static JsonArray ToRules(IReadOnlyList<(string Category, IReadOnlyList<string> Groups)> rules)
    {
        var result = new JsonArray();
        foreach (var rule in rules)
        {
            result.Add(new JsonObject
            {
                ["category"] = rule.Category,
                ["required_groups"] = new JsonArray(rule.Groups.Select(group => JsonValue.Create(group)).ToArray()),
            });
        }
        return result;
    }
}
