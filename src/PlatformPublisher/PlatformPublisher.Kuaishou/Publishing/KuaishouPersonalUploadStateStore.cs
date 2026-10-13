using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalUploadState
{
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "pending";
    public string CurrentStage { get; set; } = "pending";
    public string LastError { get; set; } = string.Empty;
    public string MiniSeriesId { get; set; } = string.Empty;
    public bool FirstPageCompleted { get; set; }
    public bool EpisodeInfoCompleted { get; set; }
    public bool VideosUploaded { get; set; }
    public bool ReviewSubmitted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class KuaishouPersonalUploadStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private readonly ProjectStateDocumentStore? _databaseStore;

    public KuaishouPersonalUploadStateStore() { }
    public KuaishouPersonalUploadStateStore(ProjectStateDocumentStore databaseStore) => _databaseStore = databaseStore;

    public KuaishouPersonalUploadState Load(
        string workflowDirectory,
        PublishPlatform platform = PublishPlatform.KuaishouPersonalRevenue)
    {
        var documentType = DocumentType(platform);
        var stored = _databaseStore?.Load<KuaishouPersonalUploadState>(workflowDirectory, documentType);
        if (stored is not null) return stored;
        var path = GetPath(workflowDirectory, platform);
        if (!File.Exists(path)) return new KuaishouPersonalUploadState();
        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<KuaishouPersonalUploadState>(json, JsonOptions)
                        ?? new KuaishouPersonalUploadState();
            using var document = JsonDocument.Parse(json);
            ApplyLegacyState(document.RootElement, state);
            _databaseStore?.Save(workflowDirectory, documentType, state);
            return state;
        }
        catch (JsonException)
        {
            return new KuaishouPersonalUploadState
            {
                Status = "failed",
                CurrentStage = "state_load",
                LastError = "状态文件格式损坏，已忽略并重新执行。",
            };
        }
    }

    public async Task SaveAsync(
        string workflowDirectory,
        KuaishouPersonalUploadState state,
        CancellationToken cancellationToken,
        PublishPlatform platform = PublishPlatform.KuaishouPersonalRevenue)
    {
        Directory.CreateDirectory(workflowDirectory);
        state.UpdatedAt = DateTimeOffset.Now;
        _databaseStore?.Save(workflowDirectory, DocumentType(platform), state);
        var path = GetPath(workflowDirectory, platform);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    public static string GetPath(
        string workflowDirectory,
        PublishPlatform platform = PublishPlatform.KuaishouPersonalRevenue) =>
        Path.Combine(workflowDirectory, platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? ".kuaishou-enterprise-upload-state.json"
            : ".kuaishou-personal-upload-state.json");

    private static string DocumentType(PublishPlatform platform) =>
        platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? "kuaishou_enterprise_upload_state"
            : "kuaishou_personal_upload_state";

    private static void ApplyLegacyState(JsonElement root, KuaishouPersonalUploadState state)
    {
        state.Status = First(state.Status == "pending" ? string.Empty : state.Status, ReadString(root, "status"), "pending");
        state.CurrentStage = First(state.CurrentStage == "pending" ? string.Empty : state.CurrentStage, ReadString(root, "current_stage"), "pending");
        state.LastError = First(state.LastError, ReadString(root, "last_error"));
        state.MiniSeriesId = First(
            state.MiniSeriesId,
            ReadString(root, "mini_series_id"),
            ReadString(root, "series_id"),
            ReadNestedString(root, "response_summary", "mini_series_id"),
            ReadNestedString(root, "response", "create", "data", "mini_series_id"));
        state.FirstPageCompleted |= ReadBool(root, "first_page_completed") || StageAtLeastVideo(state.CurrentStage);
        state.EpisodeInfoCompleted |= ReadBool(root, "episode_info_completed") || StageAtLeastVideo(state.CurrentStage);
        var uploaded = Math.Max(ReadInt(root, "uploaded_episode_count"), ReadNestedInt(root, "response_summary", "uploaded_episode_count"));
        var total = Math.Max(ReadInt(root, "episode_count"), ReadNestedInt(root, "response_summary", "episode_count"));
        state.VideosUploaded |= ReadBool(root, "videos_uploaded") || total > 0 && uploaded >= total;
        state.ReviewSubmitted |= ReadBool(root, "review_submitted") ||
                                 state.CurrentStage.Contains("review", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StageAtLeastVideo(string stage) =>
        new[] { "video", "episode_upload", "videos_uploaded", "final", "review" }
            .Any(value => stage.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ToString().Trim()
            : string.Empty;

    private static bool ReadBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);

    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        (value.TryGetInt32(out var number) || value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            ? number
            : 0;

    private static string ReadNestedString(JsonElement element, params string[] path)
    {
        foreach (var name in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return string.Empty;
        return element.ToString().Trim();
    }

    private static int ReadNestedInt(JsonElement element, params string[] path)
    {
        foreach (var name in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return 0;
        return element.TryGetInt32(out var number) || element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out number)
            ? number
            : 0;
    }

    private static string First(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
