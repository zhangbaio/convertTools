using System.Diagnostics;
using System.Text.Json;

namespace TikTokPublisher.Core.Queue;

internal static class QueueProjectDeletionRecovery
{
    private const string JournalName = "restore.json";

    private sealed record Journal(string Project, Dictionary<string, string> Directories);

    internal static FileStream? TryAcquire(string workspace, bool create)
    {
        var root = Path.Combine(workspace, QueueProjectDeletionPaths.StagingFolderName);
        if (!create && !Directory.Exists(root))
            return null;

        QueueProjectDeletionPaths.ValidateDirectoryChain(root);
        if (create)
            Directory.CreateDirectory(root);

        var lockPath = Path.Combine(root, "operation.lock");
        _ = QueueProjectDeletionPaths.ReadSafeAttributes(lockPath);
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static void WriteJournal(string stage, string project, Dictionary<string, string> directories)
    {
        var path = Path.Combine(stage, JournalName);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
        {
            JsonSerializer.Serialize(stream, new Journal(project, directories));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path);
    }

    internal static void Recover(string workspace)
    {
        try
        {
            using var lease = TryAcquire(workspace, create: false);
            if (lease is null)
                return;

            var root = Path.Combine(workspace, QueueProjectDeletionPaths.StagingFolderName);
            var stages = Directory.EnumerateDirectories(root)
                .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
                .ToArray();
            if (stages.Length == 0)
                return;

            QueueProjectDeletionPaths.ValidateQueueFiles(workspace);
            var removed = WorkspaceQueueDatabase.LoadDeletedProjectDirs(workspace);
            var queued = WorkspaceQueueDatabase.Load(workspace).Items
                .Where(item => !item.Archived && !string.IsNullOrWhiteSpace(item.ProjectDir))
                .Select(item => item.ProjectDir)
                .Where(path => WorkspaceQueueService.IsProjectInsideWorkspace(workspace, path))
                .Select(QueueProjectDeletionPaths.Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stage in stages)
            {
                try { RestoreStage(workspace, stage, queued, removed); }
                catch (Exception ex) when (IsRecoveryError(ex))
                {
                    Trace.TraceWarning($"恢复中断的队列删除失败，暂存资料保留在 {stage}。{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (IsRecoveryError(ex))
        {
            Trace.TraceWarning($"无法检查工作目录 {workspace} 的删除恢复记录：{ex.Message}");
        }
    }

    private static void RestoreStage(string workspace, string stage, IReadOnlySet<string> queued, IReadOnlySet<string> removed)
    {
        QueueProjectDeletionPaths.ValidateDirectoryChain(stage);
        var path = Path.Combine(stage, JournalName);
        if (QueueProjectDeletionPaths.ReadSafeAttributes(path) is null)
            return;

        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))
            ?? throw new InvalidDataException("删除恢复记录为空");
        if (string.IsNullOrWhiteSpace(journal.Project) || journal.Directories is not { Count: >= 1 and <= 2 })
            throw new InvalidDataException("删除恢复记录不完整");

        var project = QueueProjectDeletionPaths.Normalize(journal.Project);
        // A committed removal may still have a staging backup if writing history or cleanup failed.
        if (removed.Contains(project) || !queued.Contains(project))
            return;

        var moves = new List<(string Source, string Destination)>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, raw) in journal.Directories)
        {
            if (slot is not ("0" or "1") || string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
                throw new InvalidDataException("删除恢复记录包含无效路径");
            var destination = QueueProjectDeletionPaths.Normalize(raw);
            QueueProjectDeletionPaths.ValidateProjectDirectory(workspace, destination);
            if (!destinations.Add(destination) || queued.Any(other =>
                    !other.Equals(project, StringComparison.OrdinalIgnoreCase) &&
                    (QueueProjectDeletionPaths.EqualOrNested(other, destination) ||
                     QueueProjectDeletionPaths.EqualOrNested(destination, other))))
                throw new InvalidDataException("恢复路径与其他队列项目重叠");
            var source = Path.Combine(stage, slot);
            if (QueueProjectDeletionPaths.ExistsAsSafeDirectory(source))
            {
                if (QueueProjectDeletionPaths.ExistsAsSafeDirectory(destination))
                    throw new IOException($"恢复目标已存在：{destination}");
                moves.Add((source, destination));
            }
        }

        if (!destinations.Contains(project))
            throw new InvalidDataException("恢复路径与队列项目不匹配");
        foreach (var (source, destination) in moves)
            Directory.Move(source, destination);

        // Only remove the journal when it is the last entry. Unexpected data remains available.
        if (Directory.EnumerateFileSystemEntries(stage).All(entry => entry == path))
        {
            File.Delete(path);
            Directory.Delete(stage);
        }
    }

    private static bool IsRecoveryError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or InvalidDataException or InvalidOperationException or
        JsonException or Microsoft.Data.Sqlite.SqliteException;
}
