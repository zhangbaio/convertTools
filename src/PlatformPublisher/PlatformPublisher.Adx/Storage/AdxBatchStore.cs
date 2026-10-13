using System.Text.Json;
using PlatformPublisher.Adx.Models;
using PlatformPublisher.Persistence;

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
    private readonly ProjectStateDocumentStore? _projectStateStore;

    public AdxBatchStore() { }
    public AdxBatchStore(ProjectStateDocumentStore projectStateStore) => _projectStateStore = projectStateStore;

    public IReadOnlyList<AdxBatchManifest> List(string workflowDirectory)
        => ListCore(workflowDirectory, includeMissingFiles: false);

    public IReadOnlyList<AdxBatchManifest> ListInventory(string workflowDirectory)
        => ListCore(workflowDirectory, includeMissingFiles: true);

    private IReadOnlyList<AdxBatchManifest> ListCore(string workflowDirectory, bool includeMissingFiles)
    {
        var baseDirectory = Path.Combine(Path.GetFullPath(workflowDirectory), "materials", "adx");
        if (!Directory.Exists(baseDirectory)) return [];
        var directories = new[] { baseDirectory }.Concat(Directory.EnumerateDirectories(baseDirectory));
        var result = new List<AdxBatchManifest>();
        foreach (var directory in directories)
        {
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath)) manifestPath = Recover(workflowDirectory, directory) ?? string.Empty;
            var manifest = ReadCore(manifestPath, includeMissingFiles);
            if (manifest is not null && manifest.Items.Count > 0) result.Add(manifest);
        }
        if (result.Count == 0 && _projectStateStore?.Load<List<AdxBatchManifest>>(workflowDirectory, "adx_batches") is { } stored)
            result.AddRange(stored.Where(batch => includeMissingFiles
                ? batch.Items.Count > 0
                : batch.Items.Any(item => File.Exists(item.VideoPath))));
        return result.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.BatchId).ToArray();
    }

    public AdxBatchManifest? Read(string manifestPath)
        => ReadCore(manifestPath, includeMissingFiles: false);

    public AdxBatchManifest? ReadInventory(string manifestPath)
        => ReadCore(manifestPath, includeMissingFiles: true);

    private AdxBatchManifest? ReadCore(string manifestPath, bool includeMissingFiles)
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
                .Where(item => !string.IsNullOrWhiteSpace(item.MaterialId) &&
                               (includeMissingFiles || File.Exists(item.VideoPath)))
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
            if (_projectStateStore is not null)
            {
                var batches = _projectStateStore.Load<List<AdxBatchManifest>>(manifest.WorkflowDir, "adx_batches") ?? [];
                var next = batches.Where(item => !item.BatchId.Equals(manifest.BatchId, StringComparison.OrdinalIgnoreCase)).ToList();
                next.Add(manifest);
                _projectStateStore.Save(manifest.WorkflowDir, "adx_batches", next);
                MirrorRelational(manifest);
            }
        }
    }

    public void RecordItem(string manifestPath, string accountId, string materialId, string status, string message)
    {
        lock (_gate)
        {
            // Preserve missing inventory entries while updating one item's status. A normal
            // publish read filters missing files, but rewriting that filtered view would
            // permanently erase the missing rows from the manifest.
            var manifest = ReadInventory(manifestPath) ?? throw new InvalidOperationException("ADX 批次清单不存在或格式错误。");
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

    private static void MirrorRelational(AdxBatchManifest manifest)
    {
        var database=ProjectStateDocumentStore.ForProject(manifest.WorkflowDir);PlatformDatabaseInitializer.EnsureWorkspaceDatabase(database);
        database.WriteGate.Wait();try
        {
            using var connection=database.Open();using var transaction=connection.BeginTransaction();
            using(var batch=connection.CreateCommand()){batch.Transaction=transaction;batch.CommandText="""
                INSERT INTO adx_batches(batch_id,project_directory,manifest_path,original_title,new_title,created_at,updated_at,payload_json)
                VALUES($id,$project,$manifest,$original,$new,$created,$updated,$json)
                ON CONFLICT(batch_id) DO UPDATE SET manifest_path=excluded.manifest_path,original_title=excluded.original_title,
                new_title=excluded.new_title,updated_at=excluded.updated_at,payload_json=excluded.payload_json
                """;batch.Parameters.AddWithValue("$id",manifest.BatchId);batch.Parameters.AddWithValue("$project",manifest.WorkflowDir);batch.Parameters.AddWithValue("$manifest",manifest.ManifestPath);batch.Parameters.AddWithValue("$original",manifest.OriginalTitle);batch.Parameters.AddWithValue("$new",manifest.NewTitle);batch.Parameters.AddWithValue("$created",manifest.CreatedAt.ToString("O"));batch.Parameters.AddWithValue("$updated",manifest.UpdatedAt.ToString("O"));batch.Parameters.AddWithValue("$json",JsonSerializer.Serialize(manifest,JsonOptions));batch.ExecuteNonQuery();}
            using(var clear=connection.CreateCommand()){clear.Transaction=transaction;clear.CommandText="DELETE FROM adx_batch_items WHERE batch_id=$id";clear.Parameters.AddWithValue("$id",manifest.BatchId);clear.ExecuteNonQuery();}
            foreach(var item in manifest.Items){using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO adx_batch_items(batch_id,material_id,rank,video_path,cover_path,status,payload_json) VALUES($batch,$material,$rank,$video,$cover,$status,$json)";command.Parameters.AddWithValue("$batch",manifest.BatchId);command.Parameters.AddWithValue("$material",item.MaterialId);command.Parameters.AddWithValue("$rank",item.Rank);command.Parameters.AddWithValue("$video",item.VideoPath);command.Parameters.AddWithValue("$cover",item.CoverPath??(object)DBNull.Value);command.Parameters.AddWithValue("$status",item.Status);command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(item,JsonOptions));command.ExecuteNonQuery();}
            foreach(var account in manifest.PublishByAccount)foreach(var result in account.Value.Items){using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="INSERT INTO adx_publish_results VALUES($batch,$account,$material,$status,$message,$updated) ON CONFLICT(batch_id,account_id,material_id) DO UPDATE SET status=excluded.status,message=excluded.message,updated_at=excluded.updated_at";command.Parameters.AddWithValue("$batch",manifest.BatchId);command.Parameters.AddWithValue("$account",account.Key);command.Parameters.AddWithValue("$material",result.Key);command.Parameters.AddWithValue("$status",result.Value.Status);command.Parameters.AddWithValue("$message",result.Value.Message);command.Parameters.AddWithValue("$updated",result.Value.UpdatedAt.ToString("O"));command.ExecuteNonQuery();}
            transaction.Commit();
        }finally{database.WriteGate.Release();}
    }
}
