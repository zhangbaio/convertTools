using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

internal sealed record WorkspaceDatabaseLocation(string Path, string WorkspaceId = "")
{
    internal const string SettingsPrefix = "tiktok_workspace:";

    internal bool IsInMainDatabase => WorkspaceId.Length != 0;

    internal string SettingKey(string key) =>
        IsInMainDatabase ? $"{SettingsPrefix}{WorkspaceId}:{key}" : key;

    internal string ArchiveKey(string archiveId) =>
        IsInMainDatabase ? $"{WorkspaceId}:{archiveId}" : archiveId;

    internal static WorkspaceDatabaseLocation Resolve(string workspaceRoot)
    {
        var main = AppPaths.AppDatabaseFile;
        if (File.Exists(main))
        {
            using var connection = WorkspaceQueueDatabase.OpenConnection(main, readOnly: true);
            if (HasWorkspaceRegistry(connection))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT workspace_id FROM tiktok_workspaces WHERE path_key = $path";
                command.Parameters.AddWithValue("$path", PathKey(workspaceRoot));
                if (command.ExecuteScalar() is string workspaceId)
                    return new WorkspaceDatabaseLocation(main, workspaceId);
            }
        }

        return new WorkspaceDatabaseLocation(WorkspaceQueuePaths.QueueDatabasePath(workspaceRoot));
    }

    internal static string PathKey(string workspaceRoot)
    {
        var path = WorkspaceQueueDatabase.WorkspaceKey(workspaceRoot);
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    private static bool HasWorkspaceRegistry(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'tiktok_workspaces'";
        return command.ExecuteScalar() is not null;
    }
}
