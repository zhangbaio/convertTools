using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 记录快手准备步骤是否完成。第一次读取时把旧准备状态导入，避免已完成步骤被重新执行。
/// </summary>
public static class KuaishouPrepareStateStore
{
    public const string FileName = ".kuaishou-step-state.json";

    public static void ImportLegacy(string workflowDirectory, bool enterprise)
    {
        if (string.IsNullOrWhiteSpace(workflowDirectory) || !Directory.Exists(workflowDirectory))
            return;
        var state = Load(workflowDirectory);
        var importedName = enterprise ? "enterpriseImported" : "personalImported";
        if (state[importedName]?.GetValue<bool>() == true)
            return;

        var legacyName = enterprise
            ? ".kuaishou-enterprise-prepare-state.json"
            : ".kuaishou-personal-prepare-state.json";
        var key = enterprise ? "enterprise" : "personal";
        if (state[key] is not JsonArray steps)
        {
            steps = new JsonArray();
            state[key] = steps;
        }
        foreach (var step in ReadLegacySteps(Path.Combine(workflowDirectory, legacyName)))
        {
            if (!steps.Select(StepText).Any(item => string.Equals(item, step, StringComparison.OrdinalIgnoreCase)))
                steps.Add(step);
        }
        state[importedName] = true;
        Save(workflowDirectory, state);
    }

    public static IReadOnlyCollection<string> ReadSteps(string workflowDirectory, bool enterprise)
    {
        if (string.IsNullOrWhiteSpace(workflowDirectory) || !File.Exists(Path.Combine(workflowDirectory, FileName)))
            return [];
        var state = Load(workflowDirectory);
        var steps = state[enterprise ? "enterprise" : "personal"] as JsonArray;
        return steps?.Select(StepText)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray() ?? [];
    }

    public static void Mark(string workflowDirectory, bool enterprise, string step)
    {
        if (string.IsNullOrWhiteSpace(workflowDirectory) || string.IsNullOrWhiteSpace(step))
            return;
        Directory.CreateDirectory(workflowDirectory);
        ImportLegacy(workflowDirectory, enterprise);
        var state = Load(workflowDirectory);
        var key = enterprise ? "enterprise" : "personal";
        if (state[key] is not JsonArray steps)
        {
            steps = new JsonArray();
            state[key] = steps;
        }
        if (!steps.Select(StepText).Any(item => string.Equals(item, step.Trim(), StringComparison.OrdinalIgnoreCase)))
            steps.Add(step.Trim());
        state[enterprise ? "enterpriseImported" : "personalImported"] = true;
        Save(workflowDirectory, state);
    }

    private static IEnumerable<string> ReadLegacySteps(string path)
    {
        if (!File.Exists(path))
            yield break;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            yield break;
        }

        var completed = root?["completed_prepare_steps"];
        if (completed is JsonArray array)
        {
            foreach (var item in array)
            {
                var text = StepText(item);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
            }
        }
        else if (completed is JsonObject map)
        {
            foreach (var property in map)
            {
                if (property.Value is JsonValue value &&
                    value.TryGetValue<bool>(out var enabled) &&
                    enabled &&
                    !string.IsNullOrWhiteSpace(property.Key))
                    yield return property.Key.Trim();
            }
        }
    }

    private static string? StepText(JsonNode? item) =>
        item is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static JsonObject Load(string workflowDirectory)
    {
        var path = Path.Combine(workflowDirectory, FileName);
        if (!File.Exists(path))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void Save(string workflowDirectory, JsonObject state)
    {
        var path = Path.Combine(workflowDirectory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, state.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }
}
