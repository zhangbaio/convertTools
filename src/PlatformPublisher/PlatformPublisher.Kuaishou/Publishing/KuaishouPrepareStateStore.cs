using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 记录快手准备步骤是否完成。第一次读取时把旧准备状态导入，避免已完成步骤被重新执行。
/// </summary>
public static class KuaishouPrepareStateStore
{
    public const string FileName = ".kuaishou-step-state.json";
    private static readonly ConcurrentDictionary<string, object> DirectoryGates = new(StringComparer.OrdinalIgnoreCase);

    public static void ImportLegacy(string workflowDirectory, bool enterprise)
    {
        if (string.IsNullOrWhiteSpace(workflowDirectory) || !Directory.Exists(workflowDirectory))
            return;
        lock (Gate(workflowDirectory))
            ImportLegacyCore(workflowDirectory, enterprise);
    }

    private static void ImportLegacyCore(string workflowDirectory, bool enterprise)
    {
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
        lock (Gate(workflowDirectory))
            return ReadStepsCore(workflowDirectory, enterprise);
    }

    private static IReadOnlyCollection<string> ReadStepsCore(string workflowDirectory, bool enterprise)
    {
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
        lock (Gate(workflowDirectory))
        {
            Directory.CreateDirectory(workflowDirectory);
            ImportLegacyCore(workflowDirectory, enterprise);
            MarkCore(workflowDirectory, enterprise, step);
        }
    }

    private static void MarkCore(string workflowDirectory, bool enterprise, string step)
    {
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

    private static object Gate(string workflowDirectory)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workflowDirectory));
        return DirectoryGates.GetOrAdd(key, _ => new object());
    }

    private static JsonObject Load(string workflowDirectory)
    {
        var path = Path.Combine(workflowDirectory, FileName);
        if (!File.Exists(path))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(ReadTextRetry(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void Save(string workflowDirectory, JsonObject state)
    {
        var path = Path.Combine(workflowDirectory, FileName);
        var json = state.ToJsonString();
        const int attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < attempts)
            {
                TryDelete(temporary);
                Thread.Sleep(150 * attempt);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                TryDelete(temporary);
                throw new IOException($"文件被其他程序占用：{Path.GetFileName(path)}", ex);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
    }

    private static string ReadTextRetry(string path)
    {
        const int attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < attempts)
            {
                Thread.Sleep(150 * attempt);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                throw new IOException($"文件被其他程序占用：{Path.GetFileName(path)}", ex);
            }
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
