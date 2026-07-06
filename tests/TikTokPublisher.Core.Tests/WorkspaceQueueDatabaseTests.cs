using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Queue;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class WorkspaceQueueDatabaseTests
{
    [Fact]
    public void Load_Should_Read_Legacy_Trailing_Workspace_Key()
    {
        var workspace = CreateWorkspace();
        try
        {
            var projectDir = CreateProject(workspace, "demo");
            var legacyWorkspace = workspace + Path.DirectorySeparatorChar;
            var item = CreateQueueItem(projectDir, QueueStepStatus.Failed);

            InsertQueueRow(workspace, legacyWorkspace, item, "2026-07-06T10:00:00.0000000+08:00");

            var state = WorkspaceQueueDatabase.Load(workspace);

            state.Items.Should().ContainSingle();
            state.Items[0].ProjectDir.Should().Be(Path.GetFullPath(projectDir));
            state.Items[0].StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Failed);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void Load_Should_Prefer_Newer_Duplicate_Workspace_Key_State()
    {
        var workspace = CreateWorkspace();
        try
        {
            var projectDir = CreateProject(workspace, "demo");
            var canonical = WorkspaceQueueDatabase.WorkspaceKey(workspace);
            var legacyWorkspace = canonical + Path.DirectorySeparatorChar;

            InsertQueueRow(
                workspace,
                canonical,
                CreateQueueItem(projectDir, QueueStepStatus.Completed),
                "2026-07-06T10:00:00.0000000+08:00");
            InsertQueueRow(
                workspace,
                legacyWorkspace,
                CreateQueueItem(projectDir, QueueStepStatus.Failed),
                "2026-07-06T10:05:00.0000000+08:00");

            var state = WorkspaceQueueDatabase.Load(legacyWorkspace);

            state.Items.Should().ContainSingle();
            state.Items[0].StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Failed);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void Save_Should_Migrate_Legacy_Trailing_Workspace_Key_To_Canonical()
    {
        var workspace = CreateWorkspace();
        try
        {
            var projectDir = CreateProject(workspace, "demo");
            var canonical = WorkspaceQueueDatabase.WorkspaceKey(workspace);
            var legacyWorkspace = canonical + Path.DirectorySeparatorChar;
            var item = CreateQueueItem(projectDir, QueueStepStatus.Failed);

            InsertQueueRow(workspace, legacyWorkspace, item, "2026-07-06T10:00:00.0000000+08:00");

            item.StepStates[QueueStepKeys.UploadSeries] = QueueStepStatus.Completed;
            item.StatusText = QueueStepStatus.Completed;
            WorkspaceQueueDatabase.Save(legacyWorkspace, [item]);

            ReadWorkspaceKeys(workspace).Should().Equal(canonical);
            WorkspaceQueueDatabase.Load(workspace).Items.Should().ContainSingle()
                .Which.StepStates[QueueStepKeys.UploadSeries].Should().Be(QueueStepStatus.Completed);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"queue-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static string CreateProject(string workspace, string name)
    {
        var projectDir = Path.Combine(workspace, name);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "shortdrama-project.json"), "{}");
        return projectDir;
    }

    private static QueueProjectItem CreateQueueItem(string projectDir, string uploadStatus)
    {
        var item = new QueueProjectItem
        {
            ProjectDir = Path.GetFullPath(projectDir),
            DisplayName = Path.GetFileName(projectDir),
            OriginalTitle = Path.GetFileName(projectDir),
            NewTitle = Path.GetFileName(projectDir),
            Enabled = true,
            StatusText = uploadStatus,
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = uploadStatus,
            },
        };
        item.NormalizeStepStates();
        return item;
    }

    private static void InsertQueueRow(
        string workspace,
        string workspacePath,
        QueueProjectItem item,
        string updatedAt)
    {
        var dbPath = WorkspaceQueuePaths.QueueDatabasePath(workspace);
        WorkspaceQueueDatabase.EnsureDatabase(dbPath);
        var payload = item.ToPayload();
        payload["project_dir"] = Path.GetFullPath(item.ProjectDir);
        var payloadJson = JsonSerializer.Serialize(payload);
        var createdAt = string.IsNullOrWhiteSpace(item.QueuedAt) ? updatedAt : item.QueuedAt;

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO upload_projects (
                project_id, account_profile_id, workspace_path, project_dir,
                original_title, new_title, synopsis, status, payload_json, created_at, updated_at
            ) VALUES (
                $project_id, '', $workspace_path, $project_dir,
                $original_title, $new_title, '', $status, $payload_json, $created_at, $updated_at
            )
            """;
        cmd.Parameters.AddWithValue("$project_id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$workspace_path", workspacePath);
        cmd.Parameters.AddWithValue("$project_dir", Path.GetFullPath(item.ProjectDir));
        cmd.Parameters.AddWithValue("$original_title", item.OriginalTitle);
        cmd.Parameters.AddWithValue("$new_title", item.NewTitle);
        cmd.Parameters.AddWithValue("$status", item.StatusText);
        cmd.Parameters.AddWithValue("$payload_json", payloadJson);
        cmd.Parameters.AddWithValue("$created_at", createdAt);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> ReadWorkspaceKeys(string workspace)
    {
        var dbPath = WorkspaceQueuePaths.QueueDatabasePath(workspace);
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT workspace_path FROM upload_projects ORDER BY workspace_path";
        using var reader = cmd.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys;
    }

    private static void TryDelete(string workspace)
    {
        try
        {
            Directory.Delete(workspace, recursive: true);
        }
        catch (IOException)
        {
            // SQLite on Windows can keep handles alive for a moment after closing.
        }
    }
}
