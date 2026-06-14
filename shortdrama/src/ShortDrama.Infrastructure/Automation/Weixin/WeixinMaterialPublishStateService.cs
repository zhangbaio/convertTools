using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShortDrama.Infrastructure.Automation.Weixin;

internal static class WeixinMaterialPublishStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string ResolveStatePath(string projectDir, string stateFile)
    {
        var fileName = string.IsNullOrWhiteSpace(stateFile) ? ".weixin-channel-publish-state.json" : stateFile.Trim();
        return Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(projectDir, fileName);
    }

    public static MaterialPublishState Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty();
        }

        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MaterialPublishState>(text, JsonOptions) ?? Empty();
        }
        catch
        {
            return Empty();
        }
    }

    public static void Save(string path, MaterialPublishState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    public static string ResolveEffectiveRunStrategy(WeixinVideoPublishOptions options)
    {
        var normalized = NormalizeRunStrategy(options.RunStrategy);
        if (options.AllowDuplicatePublish)
        {
            return "resume";
        }

        return normalized == "all" ? "resume" : normalized;
    }

    public static string NormalizeRunStrategy(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "resume" or "resume_unfinished" or "断点续跑" => "resume",
            "retry_failed" or "retry_failed_only" or "failed" or "只重试失败集" => "retry_failed",
            _ => "all"
        };
    }

    public static string PrepareDuplicatePublishSession(
        MaterialPublishState state,
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> requested,
        bool enabled)
    {
        if (!enabled || requested.Count == 0)
        {
            return string.Empty;
        }

        var signature = requested
            .Select(item => new MaterialDuplicatePublishTarget(item.EpisodeIndex, item.VideoPath))
            .ToArray();

        var session = state.MaterialDuplicateSession;
        if (session is not null &&
            session.Active &&
            session.Targets.SequenceEqual(signature))
        {
            return "resume";
        }

        var entries = new Dictionary<string, MaterialPublishStateEntry>(state.Entries, StringComparer.Ordinal);
        foreach (var item in signature)
        {
            entries.Remove(item.EpisodeIndex.ToString());
        }

        state.Entries = entries;
        state.MaterialDuplicateSession = new MaterialDuplicatePublishSession(
            Active: true,
            Targets: signature,
            StartedAt: DateTimeOffset.Now,
            UpdatedAt: DateTimeOffset.Now,
            CompletedAt: null);
        return "started";
    }

    public static bool CompleteDuplicatePublishSessionIfDone(
        MaterialPublishState state,
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> requested)
    {
        var session = state.MaterialDuplicateSession;
        if (session is null || !session.Active)
        {
            return false;
        }

        var signature = requested
            .Select(item => new MaterialDuplicatePublishTarget(item.EpisodeIndex, item.VideoPath))
            .ToArray();
        if (!session.Targets.SequenceEqual(signature))
        {
            return false;
        }

        foreach (var item in signature)
        {
            if (!state.Entries.TryGetValue(item.EpisodeIndex.ToString(), out var entry))
            {
                return false;
            }

            var status = (entry.Status ?? string.Empty).Trim().ToLowerInvariant();
            if (status is not ("success" or "manual_done" or "skipped_video"))
            {
                return false;
            }
        }

        state.MaterialDuplicateSession = session with
        {
            Active = false,
            UpdatedAt = DateTimeOffset.Now,
            CompletedAt = DateTimeOffset.Now
        };
        return true;
    }

    public static IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> SelectPublishItemsByStrategy(
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> items,
        string runStrategy,
        MaterialPublishState state)
    {
        return runStrategy switch
        {
            "resume" => items.Where(item =>
            {
                if (!state.Entries.TryGetValue(item.EpisodeIndex.ToString(), out var entry))
                {
                    return true;
                }

                return !string.Equals(entry.Status, "success", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(entry.Status, "manual_done", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(entry.Status, "skipped_video", StringComparison.OrdinalIgnoreCase);
            }).ToArray(),
            "retry_failed" => items.Where(item =>
            {
                return state.Entries.TryGetValue(item.EpisodeIndex.ToString(), out var entry) &&
                       string.Equals(entry.Status, "failed", StringComparison.OrdinalIgnoreCase);
            }).ToArray(),
            _ => items
        };
    }

    public static IReadOnlyDictionary<string, MaterialPublishStateEntry> UpsertEntry(
        IReadOnlyDictionary<string, MaterialPublishStateEntry> source,
        string key,
        MaterialPublishStateEntry value)
    {
        var dictionary = new Dictionary<string, MaterialPublishStateEntry>(source, StringComparer.Ordinal)
        {
            [key] = value
        };
        return dictionary;
    }

    public static MaterialPublishState Empty()
    {
        return new MaterialPublishState(
            new Dictionary<string, MaterialPublishStateEntry>(StringComparer.Ordinal),
            null);
    }
}

internal sealed record MaterialPublishState
{
    [JsonPropertyName("entries")]
    public IReadOnlyDictionary<string, MaterialPublishStateEntry> Entries { get; set; }

    [JsonPropertyName("material_duplicate_session")]
    public MaterialDuplicatePublishSession? MaterialDuplicateSession { get; set; }

    public MaterialPublishState(
        IReadOnlyDictionary<string, MaterialPublishStateEntry>? entries,
        MaterialDuplicatePublishSession? materialDuplicateSession)
    {
        Entries = entries ?? new Dictionary<string, MaterialPublishStateEntry>(StringComparer.Ordinal);
        MaterialDuplicateSession = materialDuplicateSession;
    }
}

internal sealed record MaterialPublishStateEntry(
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("video_path")]
    string VideoPath,
    [property: JsonPropertyName("updated_at")]
    DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("error")]
    string? Error);

internal sealed record MaterialDuplicatePublishSession(
    [property: JsonPropertyName("active")]
    bool Active,
    [property: JsonPropertyName("targets")]
    IReadOnlyList<MaterialDuplicatePublishTarget> Targets,
    [property: JsonPropertyName("started_at")]
    DateTimeOffset StartedAt,
    [property: JsonPropertyName("updated_at")]
    DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("completed_at")]
    DateTimeOffset? CompletedAt);

internal sealed record MaterialDuplicatePublishTarget(
    [property: JsonPropertyName("episode_index")]
    int EpisodeIndex,
    [property: JsonPropertyName("video_path")]
    string VideoPath);
