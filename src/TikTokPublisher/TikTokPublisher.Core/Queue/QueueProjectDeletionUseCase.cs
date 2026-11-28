using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Queue;

public sealed record QueueProjectDeletionRequest(
    string WorkspaceRoot,
    TikTokAccountProfile? Account,
    IReadOnlyList<string> SelectedPaths,
    IReadOnlyList<QueueProjectItem> QueueSnapshot);

public sealed record QueueProjectDeletionResult(
    IReadOnlyList<string> RemovedProjects,
    int DeletedDirectoryCount,
    IReadOnlyList<string> Errors,
    bool Cancelled);

/// <summary>Removes confirmed projects from an idle work queue and clears their directories.</summary>
public sealed class QueueProjectDeletionUseCase
{
    private readonly Func<string, IDisposable?> _reserveWorkspace;
    private readonly Action<string, string> _moveDirectory;
    private readonly Action<string, string> _commitDeletion;
    private readonly Action<string> _purgeDirectory;
    private readonly string? _historyDatabasePath;

    public QueueProjectDeletionUseCase(Func<string, IDisposable?> reserveWorkspace)
        : this(reserveWorkspace, Directory.Move, WorkspaceQueueService.CommitProjectDeletion, ResilientFileSystem.DeleteDirectory)
    {
    }

    internal QueueProjectDeletionUseCase(Func<string, IDisposable?> reserveWorkspace,
        Action<string, string> moveDirectory, Action<string, string> commitDeletion,
        Action<string> purgeDirectory, string? historyDatabasePath = null)
    {
        _reserveWorkspace = reserveWorkspace;
        _moveDirectory = moveDirectory;
        _commitDeletion = commitDeletion;
        _purgeDirectory = purgeDirectory;
        _historyDatabasePath = historyDatabasePath;
    }

    public Task<QueueProjectDeletionResult> ExecuteAsync(QueueProjectDeletionRequest request, CancellationToken ct = default) =>
        Task.Run(() => Execute(request, ct));

    private QueueProjectDeletionResult Execute(QueueProjectDeletionRequest request, CancellationToken ct)
    {
        var removed = new List<string>();
        var errors = new List<string>();
        var deletedCount = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (request.SelectedPaths.Count == 0)
                return new([], 0, ["请先选中要删除的项目"], false);
            if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
                return new([], 0, ["请先设置上传工作目录"], false);

            var root = QueueProjectDeletionPaths.Normalize(request.WorkspaceRoot);
            if (!QueueProjectDeletionPaths.ExistsAsSafeDirectory(root))
                throw new DirectoryNotFoundException($"工作目录不存在：{root}");
            QueueProjectDeletionPaths.ValidateDirectoryChain(root);
            ValidateAccountWorkspace(root, request.Account);

            using var lease = _reserveWorkspace(root);
            if (lease is null)
                return new([], 0, ["该工作目录正在执行队列或删除项目，请结束后重试"], false);

            QueueProjectDeletionPaths.ValidateQueueFiles(root);
            var visible = IndexByPath(request.QueueSnapshot);
            var stored = IndexByPath(WorkspaceQueueDatabase.Load(root).Items);
            var otherProjects = visible.Keys.Concat(stored.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in request.SelectedPaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (string.IsNullOrWhiteSpace(path))
                        throw new InvalidDataException("项目目录为空");
                    var projectDir = QueueProjectDeletionPaths.Normalize(path);
                    if (!seen.Add(projectDir))
                        continue;
                    if (!visible.TryGetValue(projectDir, out var item) || item.Archived ||
                        WorkspaceQueueService.IsDeletedProjectDir(root, projectDir))
                        throw new InvalidOperationException("项目已不在当前待处理队列");
                    ValidateAccountBinding(item, request.Account);
                    if (item.StatusText is QueueStepStatus.Running or QueueStepStatus.WaitingUploadSlot ||
                        item.StepStates.Values.Contains(QueueStepStatus.Running))
                        throw new InvalidOperationException("项目仍在执行，请先停止队列");

                    var targets = ResolveTargets(root, projectDir, request.Account, otherProjects);
                    deletedCount += DeleteProject(root, projectDir, item, request.Account, targets, removed, errors, ct);
                }
                catch (Exception ex) when (IsDeletionError(ex))
                {
                    errors.Add($"{path}：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(removed, deletedCount, errors, true);
        }
        catch (Exception ex) when (IsDeletionError(ex))
        {
            errors.Add(ex.Message);
        }

        return new(removed, deletedCount, errors, false);
    }

    private int DeleteProject(string root, string projectDir, QueueProjectItem item, TikTokAccountProfile? account,
        IReadOnlyList<string> targets, List<string> removed, List<string> errors, CancellationToken ct)
    {
        var snapshot = TikTokExecutionHistoryService.CaptureDeletionSnapshot(root, item, account, _historyDatabasePath);
        var stageRoot = Path.Combine(root, QueueProjectDeletionPaths.StagingFolderName);
        QueueProjectDeletionPaths.ValidateDirectoryChain(stageRoot);
        var stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var moves = targets.Select((source, index) => (Source: source, Staged: Path.Combine(stage, index.ToString()))).ToArray();
        var moved = new List<(string Source, string Staged)>();
        try
        {
            File.WriteAllText(Path.Combine(stage, "restore.json"), JsonSerializer.Serialize(new
            {
                Project = projectDir,
                Directories = moves.ToDictionary(move => Path.GetFileName(move.Staged), move => move.Source),
            }));

            foreach (var move in moves)
            {
                ct.ThrowIfCancellationRequested();
                QueueProjectDeletionPaths.ValidateDirectoryChain(move.Source);
                if (!QueueProjectDeletionPaths.ExistsAsSafeDirectory(move.Source))
                    continue;
                _moveDirectory(move.Source, move.Staged);
                moved.Add(move);
            }

            ct.ThrowIfCancellationRequested();
            _commitDeletion(root, projectDir);
        }
        catch
        {
            var restored = true;
            for (var index = moved.Count - 1; index >= 0; index--)
            {
                var (source, backup) = moved[index];
                try
                {
                    QueueProjectDeletionPaths.ValidateDirectoryChain(source);
                    QueueProjectDeletionPaths.ValidateDirectoryChain(backup);
                    _moveDirectory(backup, source);
                }
                catch (Exception ex) when (IsDeletionError(ex))
                {
                    restored = false;
                    errors.Add($"无法还原 {source}。可从暂存目录恢复：{backup}。{ex.Message}");
                }
            }
            if (restored)
                Purge(stage, errors);
            throw;
        }

        removed.Add(projectDir);
        try { snapshot(); }
        catch (Exception ex) when (IsDeletionError(ex))
        {
            errors.Add($"恢复快照写入失败，项目资料已保留在暂存目录：{stage}。{ex.Message}");
            return 0;
        }

        return Purge(stage, errors) ? moved.Count : 0;
    }

    private bool Purge(string stage, List<string> errors)
    {
        try
        {
            QueueProjectDeletionPaths.ValidateDirectoryChain(stage);
            _purgeDirectory(stage);
            return true;
        }
        catch (Exception ex) when (IsDeletionError(ex))
        {
            errors.Add($"暂存目录清理失败：{stage}。{ex.Message}");
            return false;
        }
    }

    private static string[] ResolveTargets(string root, string projectDir, TikTokAccountProfile? account,
        IReadOnlySet<string> otherProjects)
    {
        QueueProjectDeletionPaths.ValidateProjectDirectory(root, projectDir);
        var context = ProjectWorkspaceService.LoadContext(projectDir);
        if (!string.Equals(QueueProjectDeletionPaths.Normalize(context.WorkspaceRoot), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("项目元数据中的工作目录与当前工作区不一致");

        var targets = new[] { context.SourceProjectDir, context.WorkflowProjectDir }
            .Select(QueueProjectDeletionPaths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var archive = account?.ResolveArchiveRootPath(root);
        foreach (var target in targets)
        {
            QueueProjectDeletionPaths.ValidateProjectDirectory(root, target);
            if (!string.IsNullOrWhiteSpace(archive) && Overlap(target, QueueProjectDeletionPaths.Normalize(archive)))
                throw new InvalidDataException($"项目路径与归档目录重叠：{target}");
            if (otherProjects.Any(other => !other.Equals(projectDir, StringComparison.OrdinalIgnoreCase) && Overlap(other, target)))
                throw new InvalidDataException($"项目路径与队列中的其他项目重叠：{target}");
        }
        ProjectWorkspaceService.ValidateContextOwnership(context, repairMovedMetadata: false);
        return targets;
    }

    private static bool Overlap(string first, string second) => QueueProjectDeletionPaths.EqualOrNested(first, second) ||
        QueueProjectDeletionPaths.EqualOrNested(second, first);

    private static Dictionary<string, QueueProjectItem> IndexByPath(IEnumerable<QueueProjectItem> items)
    {
        var indexed = new Dictionary<string, QueueProjectItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectDir))
                continue;
            try { indexed.TryAdd(QueueProjectDeletionPaths.Normalize(item.ProjectDir), item); }
            catch (Exception ex) when (IsDeletionError(ex)) { }
        }
        return indexed;
    }

    private static void ValidateAccountWorkspace(string root, TikTokAccountProfile? account)
    {
        var accountRoot = account?.ResolveWorkspacePath();
        if (!string.IsNullOrWhiteSpace(accountRoot) &&
            !root.Equals(QueueProjectDeletionPaths.Normalize(accountRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("所选账号的工作目录与当前队列不一致");
    }

    private static void ValidateAccountBinding(QueueProjectItem item, TikTokAccountProfile? account)
    {
        if (!string.IsNullOrWhiteSpace(item.AccountProfileId) &&
            !string.Equals(item.AccountProfileId.Trim(), account?.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("项目不属于当前账号");
    }

    private static bool IsDeletionError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or InvalidDataException or InvalidOperationException or
        JsonException or Microsoft.Data.Sqlite.SqliteException;
}
