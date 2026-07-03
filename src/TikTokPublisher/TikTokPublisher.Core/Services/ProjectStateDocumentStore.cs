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
        var databasePath = ClientSettingsStore.WorkspaceDatabasePath(workspaceRoot);
        if (string.IsNullOrWhiteSpace(databasePath))
            return;

        WorkspaceQueueDatabase.EnsureDatabase(databasePath);
        var workspaceKey = NormalizePath(workspaceRoot);
        var projectKey = NormalizePath(projectDir);
        var workflowKey = string.IsNullOrWhiteSpace(workflowProjectDir) ? "" : NormalizePath(workflowProjectDir);
        var documentId = StableDocumentId(workspaceKey, projectKey, UploadStateDocumentType);
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
        command.Parameters.AddWithValue("$document_type", UploadStateDocumentType);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$created_at", createdAt);
        command.Parameters.AddWithValue("$updated_at", now);
        command.ExecuteNonQuery();
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path.Trim());

    private static string StableDocumentId(string workspaceKey, string projectKey, string documentType)
    {
        var payload = string.Join('\n', workspaceKey, projectKey, documentType);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"document\n{payload}"))).ToLowerInvariant();
    }

    private static string StableProjectId(string workspaceKey, string projectKey)
    {
        var payload = string.Join('\n', workspaceKey, projectKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"project\n{payload}"))).ToLowerInvariant();
    }
}
