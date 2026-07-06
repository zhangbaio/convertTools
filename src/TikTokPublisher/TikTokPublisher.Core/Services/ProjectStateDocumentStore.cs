using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Services;

/// <summary>同步 <c>project_state_documents</c>（与 Python <c>database.py</c> 对齐）。</summary>
public static class ProjectStateDocumentStore
{
    public const string UploadStateDocumentType = "tiktok_upload_state";

    public static void SaveUploadState(
        string workspaceRoot,
        string projectDir,
        Dictionary<string, object?> payload,
        string? workflowProjectDir = null)
    {
        SaveDocument(workspaceRoot, projectDir, UploadStateDocumentType, payload, workflowProjectDir);
    }

    public static Dictionary<string, JsonElement> LoadUploadState(string workspaceRoot, string projectDir) =>
        LoadDocument(workspaceRoot, projectDir, UploadStateDocumentType);

    public static Dictionary<string, JsonElement> LoadDocument(
        string workspaceRoot,
        string projectDir,
        string documentType)
    {
        var databasePath = ClientSettingsStore.WorkspaceDatabasePath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return new Dictionary<string, JsonElement>();

        var docType = (documentType ?? "").Trim();
        if (string.IsNullOrWhiteSpace(docType))
            return new Dictionary<string, JsonElement>();

        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_json
                FROM project_state_documents
                WHERE workspace_path = $workspace_path
                  AND project_dir = $project_dir
                  AND document_type = $document_type
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$workspace_path", NormalizePath(workspaceRoot));
            command.Parameters.AddWithValue("$project_dir", NormalizePath(projectDir));
            command.Parameters.AddWithValue("$document_type", docType);

            var json = command.ExecuteScalar()?.ToString();
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, JsonElement>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, JsonElement>();

            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, JsonElement>();
        }
    }

    public static Dictionary<string, Dictionary<string, object?>> LoadProjectDocuments(
        string workspaceRoot,
        string projectDir)
    {
        var databasePath = ClientSettingsStore.WorkspaceDatabasePath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        var workspaceAliases = WorkspaceAliases(workspaceRoot);
        var projectKey = NormalizePath(projectDir);
        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            var placeholders = string.Join(", ", workspaceAliases.Select((_, index) => $"$workspace{index}"));
            command.CommandText = $"""
                SELECT document_type, payload_json
                FROM project_state_documents
                WHERE workspace_path IN ({placeholders})
                  AND project_dir = $project_dir
                ORDER BY updated_at ASC, created_at ASC
                """;
            for (var i = 0; i < workspaceAliases.Count; i++)
                command.Parameters.AddWithValue($"$workspace{i}", workspaceAliases[i]);
            command.Parameters.AddWithValue("$project_dir", projectKey);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var docType = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
                if (string.IsNullOrWhiteSpace(docType))
                    continue;

                var payloadJson = reader.IsDBNull(1) ? "{}" : reader.GetString(1);
                result[docType] = DeserializePayload(payloadJson);
            }
        }
        catch
        {
            return new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        }

        return result;
    }

    public static void SaveDocument(
        string workspaceRoot,
        string projectDir,
        string documentType,
        Dictionary<string, object?> payload,
        string? workflowProjectDir = null)
    {
        var databasePath = ClientSettingsStore.WorkspaceDatabasePath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(databasePath))
            return;

        var docType = (documentType ?? "").Trim();
        if (string.IsNullOrWhiteSpace(docType))
            return;

        WorkspaceQueueDatabase.EnsureDatabase(databasePath);
        var workspaceKey = NormalizePath(workspaceRoot);
        var projectKey = NormalizePath(projectDir);
        var workflowKey = string.IsNullOrWhiteSpace(workflowProjectDir) ? "" : NormalizePath(workflowProjectDir);
        var documentId = StableDocumentId(workspaceKey, projectKey, docType);
        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss");
        var payloadJson = JsonSerializer.Serialize(payload ?? new Dictionary<string, object?>());

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        var createdAt = now;
        using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT created_at FROM project_state_documents WHERE document_id = $id";
            existing.Parameters.AddWithValue("$id", documentId);
            var value = existing.ExecuteScalar()?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                createdAt = value;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO project_state_documents (
                document_id, project_id, workspace_path, project_dir, workflow_project_dir,
                document_type, payload_json, created_at, updated_at
            ) VALUES (
                $document_id, $project_id, $workspace_path, $project_dir, $workflow_project_dir,
                $document_type, $payload_json, $created_at, $updated_at
            )
            ON CONFLICT(document_id) DO UPDATE SET
                project_id = excluded.project_id,
                workspace_path = excluded.workspace_path,
                project_dir = excluded.project_dir,
                workflow_project_dir = excluded.workflow_project_dir,
                document_type = excluded.document_type,
                payload_json = excluded.payload_json,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$document_id", documentId);
        command.Parameters.AddWithValue("$project_id", StableProjectId(workspaceKey, projectKey));
        command.Parameters.AddWithValue("$workspace_path", workspaceKey);
        command.Parameters.AddWithValue("$project_dir", projectKey);
        command.Parameters.AddWithValue("$workflow_project_dir", workflowKey);
        command.Parameters.AddWithValue("$document_type", docType);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$created_at", createdAt);
        command.Parameters.AddWithValue("$updated_at", now);
        command.ExecuteNonQuery();
    }

    public static void DeleteProjectDocuments(string workspaceRoot, string projectDir)
    {
        var databasePath = ClientSettingsStore.WorkspaceDatabasePath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return;

        var workspaceAliases = WorkspaceAliases(workspaceRoot);
        try
        {
            WorkspaceQueueDatabase.EnsureDatabase(databasePath);
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            var placeholders = string.Join(", ", workspaceAliases.Select((_, index) => $"$workspace{index}"));
            command.CommandText = $"""
                DELETE FROM project_state_documents
                WHERE workspace_path IN ({placeholders})
                  AND project_dir = $project_dir
                """;
            for (var i = 0; i < workspaceAliases.Count; i++)
                command.Parameters.AddWithValue($"$workspace{i}", workspaceAliases[i]);
            command.Parameters.AddWithValue("$project_dir", NormalizePath(projectDir));
            command.ExecuteNonQuery();
        }
        catch
        {
            // State documents are an optimization. A stale row must not block moving the project files.
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path.Trim());

    private static IReadOnlyList<string> WorkspaceAliases(string workspaceRoot)
    {
        var aliases = new List<string>();
        AddAlias(NormalizePath(workspaceRoot));

        try
        {
            var full = Path.GetFullPath(workspaceRoot.Trim());
            AddAlias(full);
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            AddAlias(trimmed);
            if (!string.Equals(full, trimmed, StringComparison.Ordinal))
                AddAlias(trimmed + Path.DirectorySeparatorChar);
        }
        catch
        {
            // NormalizePath above already added the best-effort canonical alias.
        }

        return aliases;

        void AddAlias(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !aliases.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(value);
            }
        }
    }

    private static Dictionary<string, object?> DeserializePayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, object?>(StringComparer.Ordinal);

            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static string StableDocumentId(string workspaceKey, string projectKey, string documentType)
    {
        var payload = string.Join('\n', workspaceKey, projectKey, documentType);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"document\n{payload}"))).ToLowerInvariant();
    }

    private static string StableProjectId(string workspaceKey, string projectKey)
    {
        var payload = string.Join('\n', workspaceKey, projectKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
