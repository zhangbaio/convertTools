using System.Text.Json;
using PlatformPublisher.Adx.Models;

namespace PlatformPublisher.Adx.Storage;

public sealed class AdxBatchStore
{
    public const string ManifestFileName = ".weixin-channels-adx-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly object _gate = new();

    public IReadOnlyList<AdxBatchManifest> List(string workflowDirectory)
    {
        var baseDirectory = Path.Combine(Path.GetFullPath(workflowDirectory), "materials", "adx");
        if (!Directory.Exists(baseDirectory)) return [];
        var directories = new[] { baseDirectory }.Concat(Directory.EnumerateDirectories(baseDirectory));
        var result = new List<AdxBatchManifest>();
        foreach (var directory in directories)
        {
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath)) manifestPath = Recover(workflowDirectory, directory) ?? string.Empty;
            var manifest = Read(manifestPath);
            if (manifest is not null && manifest.Items.Count > 0) result.Add(manifest);
        }
        return result.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.BatchId).ToArray();
    }

    public AdxBatchManifest? Read(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<AdxBatchManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest is null) return null;
            manifest.ManifestPath = Path.GetFullPath(manifestPath);
            manifest.BatchId = string.IsNullOrWhiteSpace(manifest.BatchId)
                ? Path.GetFileName(Path.GetDirectoryName(manifestPath)) ?? "legacy-adx"
                : manifest.BatchId;
            manifest.NewTitle = string.IsNullOrWhiteSpace(manifest.NewTitle) ? manifest.SeriesName : manifest.NewTitle;
            manifest.Items = manifest.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.MaterialId) && File.Exists(item.VideoPath))
                .OrderBy(item => item.Rank)
                .ToList();
            return manifest;
        }
        catch { return null; }
    }

    public void Write(AdxBatchManifest manifest)
    {
        if (Path.GetFileName(manifest.ManifestPath) != ManifestFileName)
            throw new InvalidOperationException("无效的 ADX 批次清单路径。");
        lock (_gate)
        {
            manifest.Version = 2;
            manifest.UpdatedAt = DateTimeOffset.UtcNow;
            AdxSettingsStore.AtomicWrite(manifest.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        }
    }

    public void RecordItem(string manifestPath, string accountId, string materialId, string status, string message)
    {
        lock (_gate)
        {
            var manifest = Read(manifestPath) ?? throw new InvalidOperationException("ADX 批次清单不存在或格式错误。");
            if (!manifest.PublishByAccount.TryGetValue(accountId, out var account))
                manifest.PublishByAccount[accountId] = account = new AdxAccountPublishStatus();
            var previousSucceeded = account.Items.TryGetValue(materialId, out var previous) && IsSucceeded(previous.Status);
            if (!previousSucceeded || IsSucceeded(status))
                account.Items[materialId] = new AdxItemPublishStatus { Status = status, Message = message, UpdatedAt = DateTimeOffset.UtcNow };
            account.UpdatedAt = DateTimeOffset.UtcNow;
            var statuses = account.Items.Values.Select(item => item.Status).ToArray();
            account.Status = statuses.Any(value => value == "failed")
                ? statuses.Any(IsSucceeded) ? "partial_failed" : "failed"
                : statuses.Any(value => value == "cancelled")
                    ? statuses.Any(IsSucceeded) ? "partial_failed" : "cancelled"
                    : statuses.All(value => value == "draft_saved") ? "draft_saved" : "success";
            Write(manifest);
        }
    }

    private string? Recover(string workflowDirectory, string directory)
    {
        var videos = Directory.EnumerateFiles(directory, "*.mp4", SearchOption.TopDirectoryOnly).ToArray();
        if (videos.Length == 0) return null;
        var items = new List<AdxBatchItem>();
        string originalTitle = string.Empty, newTitle = string.Empty;
        for (var index = 0; index < videos.Length; index++)
        {
            var video = videos[index];
            var stem = Path.GetFileNameWithoutExtension(video);
            var sidecar = ReadSidecar(Path.Combine(directory, stem + ".publish.json"));
            originalTitle = GetString(sidecar, "originalTitle") ?? originalTitle;
            newTitle = GetString(sidecar, "newTitle") ?? newTitle;
            var cover = GetString(sidecar, "coverPath") ?? Path.Combine(directory, stem + ".cover.jpg");
            items.Add(new AdxBatchItem
            {
                MaterialId = GetString(sidecar, "materialId") ?? LastDigits(stem) ?? $"recovered-{index + 1}",
                Rank = GetInt(sidecar, "rank") ?? ParseRank(stem) ?? index + 1,
                VideoPath = video,
                CoverPath = File.Exists(cover) ? cover : null,
            });
        }
        var manifest = new AdxBatchManifest
        {
            BatchId = Path.GetFileName(directory).Equals("adx", StringComparison.OrdinalIgnoreCase) ? "legacy-adx" : Path.GetFileName(directory),
            WorkflowDir = Path.GetFullPath(workflowDirectory), SeriesName = newTitle, NewTitle = newTitle,
            OriginalTitle = originalTitle, CreatedAt = Directory.GetLastWriteTimeUtc(directory), Items = items,
            ManifestPath = Path.Combine(directory, ManifestFileName),
        };
        Write(manifest);
        return manifest.ManifestPath;
    }

    private static JsonElement? ReadSidecar(string path)
    {
        try { return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone(); }
        catch { return null; }
    }
    private static string? GetString(JsonElement? root, string name) => root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int? GetInt(JsonElement? root, string name) => root is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : null;
    private static string? LastDigits(string value) => System.Text.RegularExpressions.Regex.Match(value, @"(\d+)$").Groups[1].Value is { Length: > 0 } result ? result : null;
    private static int? ParseRank(string value) => int.TryParse(System.Text.RegularExpressions.Regex.Match(value, @"TOP(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value, out var rank) ? rank : null;
    private static bool IsSucceeded(string status) => status is "success" or "draft_saved";
}
