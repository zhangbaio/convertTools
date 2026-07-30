using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Archive;

public enum AccountArchiveMigrationMatchKind
{
    AccountId,
    AccountAlias,
    WorkspacePath,
}

public sealed record AccountArchiveMigrationCandidate(
    ArchivedProjectItem Item,
    AccountArchiveMigrationMatchKind MatchKind,
    string MatchReason,
    string TargetMetadataPath,
    string TargetSourceDir,
    string TargetWorkflowDir);

public sealed record AccountArchiveMigrationPreview(
    string SourceArchiveRoot,
    string TargetArchiveRoot,
    int SourceProjectCount,
    IReadOnlyList<AccountArchiveMigrationCandidate> Candidates,
    int SkippedOwnershipCount,
    int ConflictCount,
    IReadOnlyList<string> Notes)
{
    public int MigratableCount => Candidates.Count;
}

public sealed record AccountArchiveMigrationResult(
    string SourceArchiveRoot,
    string TargetArchiveRoot,
    int MigratedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<string> Messages);

public static partial class TikTokArchivedProjectService
{
    /// <summary>
    /// Scans the historical global archive root and selects only projects that can be
    /// unambiguously attributed to <paramref name="account"/>.
    /// </summary>
    public static AccountArchiveMigrationPreview BuildAccountArchiveMigrationPreview(
        string workspaceRoot,
        string sourceArchiveRoot,
        string targetArchiveRoot,
        TikTokAccountProfile account,
        IReadOnlyCollection<TikTokAccountProfile>? knownAccounts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchiveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetArchiveRoot);
        ArgumentNullException.ThrowIfNull(account);

        var workspace = Path.GetFullPath(workspaceRoot);
        var sourceRoot = Path.GetFullPath(sourceArchiveRoot);
        var targetRoot = Path.GetFullPath(targetArchiveRoot);
        if (PathEquals(sourceRoot, targetRoot))
            throw new InvalidOperationException("旧归档目录与当前账号归档目录相同，无需迁移。");
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"旧归档目录不存在：{sourceRoot}");

        var accounts = NormalizeKnownAccounts(account, knownAccounts);
        var notes = new List<string>();
        var candidates = new List<AccountArchiveMigrationCandidate>();
        var skippedOwnership = 0;
        var conflicts = 0;
        var sourceItems = List(workspace, sourceRoot)
            .Where(item => ItemBelongsToArchiveRoot(item, sourceRoot))
            .ToArray();

        foreach (var item in sourceItems)
        {
            var ownership = ResolveMigrationOwnership(item, account, accounts, workspace);
            if (ownership is null)
            {
                skippedOwnership++;
                continue;
            }

            var targetSourceDir = BuildMigrationComponentTarget(
                targetRoot,
                "source",
                item.ArchivedSourceDir,
                item.ProjectKey,
                isWorkflow: false);
            var targetWorkflowDir = BuildMigrationComponentTarget(
                targetRoot,
                "workflow",
                item.ArchivedWorkflowDir,
                item.NewTitle,
                isWorkflow: true);
            var metadataName = SanitizeName(
                FirstNonEmpty(
                    Path.GetFileNameWithoutExtension(item.MetadataPath),
                    item.ProjectKey,
                    item.NewTitle));
            var targetMetadataPath = Path.Combine(targetRoot, "meta", metadataName + ".json");

            var sourceExists = Directory.Exists(item.ArchivedSourceDir);
            var workflowExists = Directory.Exists(item.ArchivedWorkflowDir);
            if (!sourceExists && !workflowExists)
            {
                conflicts++;
                notes.Add($"{item.NewTitle}：归档 Source 和 Workflow 均不存在，已跳过。");
                continue;
            }

            var targetConflict =
                (sourceExists && Directory.Exists(targetSourceDir)) ||
                (workflowExists && Directory.Exists(targetWorkflowDir)) ||
                File.Exists(targetMetadataPath);
            if (targetConflict)
            {
                conflicts++;
                notes.Add($"{item.NewTitle}：当前账号归档目录已有同名项目，已跳过且不会覆盖。");
                continue;
            }

            candidates.Add(new AccountArchiveMigrationCandidate(
                item,
                ownership.Value.Kind,
                ownership.Value.Reason,
                targetMetadataPath,
                targetSourceDir,
                targetWorkflowDir));
        }

        return new AccountArchiveMigrationPreview(
            sourceRoot,
            targetRoot,
            sourceItems.Length,
            candidates,
            skippedOwnership,
            conflicts,
            notes);
    }

    public static async Task<AccountArchiveMigrationResult> MigrateAccountArchivesAsync(
        string workspaceRoot,
        AccountArchiveMigrationPreview preview,
        TikTokAccountProfile account,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(account);

        var workspace = Path.GetFullPath(workspaceRoot);
        var sourceRoot = Path.GetFullPath(preview.SourceArchiveRoot);
        var targetRoot = Path.GetFullPath(preview.TargetArchiveRoot);
        if (PathEquals(sourceRoot, targetRoot))
            throw new InvalidOperationException("旧归档目录与当前账号归档目录相同，无需迁移。");

        Directory.CreateDirectory(Path.Combine(targetRoot, "source"));
        Directory.CreateDirectory(Path.Combine(targetRoot, "workflow"));
        Directory.CreateDirectory(Path.Combine(targetRoot, "meta"));

        var migrated = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var candidate in preview.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在迁移：{candidate.Item.NewTitle}");
            try
            {
                await MigrateArchiveCandidateAsync(
                    workspace,
                    sourceRoot,
                    targetRoot,
                    candidate,
                    account,
                    ct).ConfigureAwait(false);
                migrated++;
                progress?.Report($"迁移完成：{candidate.Item.NewTitle}");
            }
            catch (Exception ex)
            {
                failed++;
                var message = $"{candidate.Item.NewTitle}：{ex.Message}";
                messages.Add(message);
                progress?.Report($"迁移失败：{message}");
            }
        }

        // Rebuild the active workspace archive index from the target files. Source files
        // that could not be migrated remain untouched and can still be scanned later.
        if (migrated > 0)
            SaveArchiveProjectsToDatabase(workspace, List(workspace, targetRoot));

        return new AccountArchiveMigrationResult(
            sourceRoot,
            targetRoot,
            migrated,
            preview.SkippedOwnershipCount + preview.ConflictCount,
            failed,
            messages);
    }

    private static async Task MigrateArchiveCandidateAsync(
        string workspaceRoot,
        string sourceRoot,
        string targetRoot,
        AccountArchiveMigrationCandidate candidate,
        TikTokAccountProfile account,
        CancellationToken ct)
    {
        var item = candidate.Item;
        if (!ItemBelongsToArchiveRoot(item, sourceRoot))
            throw new InvalidOperationException("归档记录已不在旧归档目录中。");

        var sourceExists = Directory.Exists(item.ArchivedSourceDir);
        var workflowExists = Directory.Exists(item.ArchivedWorkflowDir);
        if (!sourceExists && !workflowExists)
            throw new DirectoryNotFoundException("归档 Source 和 Workflow 均不存在。");
        if ((sourceExists && Directory.Exists(candidate.TargetSourceDir)) ||
            (workflowExists && Directory.Exists(candidate.TargetWorkflowDir)) ||
            File.Exists(candidate.TargetMetadataPath))
        {
            throw new InvalidOperationException("目标归档中已存在同名项目，未覆盖。");
        }

        var stagingRoot = Path.Combine(
            targetRoot,
            ".migration-staging",
            Guid.NewGuid().ToString("N"));
        var stagedSource = Path.Combine(stagingRoot, "source");
        var stagedWorkflow = Path.Combine(stagingRoot, "workflow");
        var finalSourceCreated = false;
        var finalWorkflowCreated = false;
        try
        {
            if (sourceExists)
                await CopyDirectoryVerifiedAsync(item.ArchivedSourceDir, stagedSource, ct).ConfigureAwait(false);
            if (workflowExists)
                await CopyDirectoryVerifiedAsync(item.ArchivedWorkflowDir, stagedWorkflow, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (sourceExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(candidate.TargetSourceDir)!);
                Directory.Move(stagedSource, candidate.TargetSourceDir);
                finalSourceCreated = true;
            }
            if (workflowExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(candidate.TargetWorkflowDir)!);
                Directory.Move(stagedWorkflow, candidate.TargetWorkflowDir);
                finalWorkflowCreated = true;
            }

            var payload = ReadJsonObject(item.MetadataPath);
            if (payload.Count == 0)
                payload = ToPayload(item);
            RewriteMigrationPayload(
                payload,
                item,
                account,
                workspaceRoot,
                sourceExists ? candidate.TargetSourceDir : "",
                workflowExists ? candidate.TargetWorkflowDir : "",
                candidate.TargetMetadataPath);
            await WriteJsonAtomicallyAsync(candidate.TargetMetadataPath, payload, ct).ConfigureAwait(false);

            // Only remove the historical copy after both copied directories and the new
            // metadata have been verified and committed.
            if (sourceExists && Directory.Exists(item.ArchivedSourceDir))
                Directory.Delete(item.ArchivedSourceDir, recursive: true);
            if (workflowExists &&
                !PathEquals(item.ArchivedWorkflowDir, item.ArchivedSourceDir) &&
                Directory.Exists(item.ArchivedWorkflowDir))
            {
                Directory.Delete(item.ArchivedWorkflowDir, recursive: true);
            }
            if (File.Exists(item.MetadataPath))
                File.Delete(item.MetadataPath);
            RemoveArchiveFromDatabase(workspaceRoot, item.MetadataPath);
            PruneEmptyParent(item.MetadataPath, sourceRoot);
        }
        catch
        {
            // A committed target is useful only together with its metadata. If metadata
            // was not committed, remove copied targets and leave the original untouched.
            if (!File.Exists(candidate.TargetMetadataPath))
            {
                if (finalSourceCreated && Directory.Exists(candidate.TargetSourceDir))
                    Directory.Delete(candidate.TargetSourceDir, recursive: true);
                if (finalWorkflowCreated && Directory.Exists(candidate.TargetWorkflowDir))
                    Directory.Delete(candidate.TargetWorkflowDir, recursive: true);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
            var stagingParent = Path.GetDirectoryName(stagingRoot);
            if (!string.IsNullOrWhiteSpace(stagingParent) &&
                Directory.Exists(stagingParent) &&
                !Directory.EnumerateFileSystemEntries(stagingParent).Any())
            {
                Directory.Delete(stagingParent);
            }
        }
    }

    private static void RewriteMigrationPayload(
        IDictionary<string, object?> payload,
        ArchivedProjectItem item,
        TikTokAccountProfile account,
        string workspaceRoot,
        string archivedSourceDir,
        string archivedWorkflowDir,
        string metadataPath)
    {
        var sourceLeaf = FirstNonEmpty(
            PathLeaf(item.SourceProjectDir),
            PathLeaf(item.ArchivedSourceDir),
            item.ProjectKey);
        var workflowLeaf = FirstNonEmpty(
            PathLeaf(item.WorkflowProjectDir),
            PathLeaf(item.ArchivedWorkflowDir),
            "_" + item.NewTitle.TrimStart('_'));
        var sourceProjectDir = Path.Combine(workspaceRoot, SanitizeName(sourceLeaf));
        var workflowProjectDir = Path.Combine(workspaceRoot, "workflow", SanitizeName(workflowLeaf));
        var accountName = FirstNonEmpty(account.DisplayName, account.Name, account.Id);

        SetBoth(payload, "projectKey", "project_key", item.ProjectKey);
        SetBoth(payload, "displayName", "display_name", item.DisplayName);
        SetBoth(payload, "originalTitle", "original_title", item.OriginalTitle);
        SetBoth(payload, "newTitle", "new_title", item.NewTitle);
        SetBoth(payload, "accountProfileId", "account_profile_id", account.Id.Trim());
        SetBoth(payload, "accountProfileName", "account_profile_name", accountName);
        SetBoth(payload, "metadataPath", "metadata_path", metadataPath);
        SetBoth(payload, "sourceProjectDir", "source_project_dir", sourceProjectDir);
        SetBoth(payload, "workflowProjectDir", "workflow_project_dir", workflowProjectDir);
        SetBoth(payload, "archivedSourceDir", "archived_source_dir", archivedSourceDir);
        SetBoth(payload, "archivedWorkflowDir", "archived_workflow_dir", archivedWorkflowDir);
    }

    private static void SetBoth(
        IDictionary<string, object?> payload,
        string camelKey,
        string snakeKey,
        object? value)
    {
        payload[camelKey] = value;
        payload[snakeKey] = value;
    }

    private static async Task WriteJsonAtomicallyAsync(
        string targetPath,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                ct).ConfigureAwait(false);
            File.Move(tempPath, targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static async Task CopyDirectoryVerifiedAsync(
        string sourceDir,
        string destinationDir,
        CancellationToken ct)
    {
        var source = Path.GetFullPath(sourceDir);
        var destination = Path.GetFullPath(destinationDir);
        var sourceFiles = Directory
            .EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                Source = path,
                Relative = Path.GetRelativePath(source, path),
                Length = new FileInfo(path).Length,
            })
            .ToArray();

        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, file.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(
                file.Source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 1024 * 1024, ct).ConfigureAwait(false);
        }

        var targetFiles = Directory
            .EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(destination, path),
                path => new FileInfo(path).Length,
                StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Length != targetFiles.Count ||
            sourceFiles.Any(file =>
                !targetFiles.TryGetValue(file.Relative, out var length) ||
                length != file.Length))
        {
            throw new IOException($"复制校验失败：{sourceDir}");
        }
    }

    private static (AccountArchiveMigrationMatchKind Kind, string Reason)? ResolveMigrationOwnership(
        ArchivedProjectItem item,
        TikTokAccountProfile account,
        IReadOnlyCollection<TikTokAccountProfile> knownAccounts,
        string currentWorkspace)
    {
        var itemId = item.AccountProfileId.Trim();
        if (itemId.Length > 0 &&
            string.Equals(itemId, account.Id.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return (AccountArchiveMigrationMatchKind.AccountId, "账号 ID 精确匹配");
        }

        var explicitIdentities = new[] { item.AccountProfileId, item.AccountProfileName }
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (explicitIdentities.Length > 0)
        {
            var matchingAccounts = knownAccounts
                .Where(candidate => explicitIdentities.Any(identity => AccountAliases(candidate).Contains(identity)))
                .ToArray();
            if (matchingAccounts.Length == 1 &&
                string.Equals(matchingAccounts[0].Id, account.Id, StringComparison.OrdinalIgnoreCase))
            {
                return (AccountArchiveMigrationMatchKind.AccountAlias, "历史账号名称唯一匹配");
            }

            return null;
        }

        var itemPaths = new[] { item.SourceProjectDir, item.WorkflowProjectDir }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (!itemPaths.Any(path => TryIsWithin(path, currentWorkspace)))
            return null;

        var matchingByWorkspace = knownAccounts
            .Select(candidate => (Account: candidate, Workspace: candidate.ResolveWorkspacePath()))
            .Where(pair =>
                !string.IsNullOrWhiteSpace(pair.Workspace) &&
                itemPaths.Any(path => TryIsWithin(path, pair.Workspace)))
            .Select(pair => pair.Account.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matchingByWorkspace.Length == 1 &&
            string.Equals(matchingByWorkspace[0], account.Id, StringComparison.OrdinalIgnoreCase))
        {
            return (AccountArchiveMigrationMatchKind.WorkspacePath, "原工作目录唯一匹配");
        }

        // Some legacy accounts no longer have an existing workspace, so ResolveWorkspacePath
        // returns empty. The current workspace supplied by the caller is still authoritative
        // when no other configured account resolves to the same path.
        var otherClaimsCurrentWorkspace = knownAccounts.Any(candidate =>
            !string.Equals(candidate.Id, account.Id, StringComparison.OrdinalIgnoreCase) &&
            TryPathEquals(candidate.ResolveWorkspacePath(), currentWorkspace));
        return otherClaimsCurrentWorkspace
            ? null
            : (AccountArchiveMigrationMatchKind.WorkspacePath, "原路径属于当前工作目录");
    }

    private static IReadOnlyCollection<TikTokAccountProfile> NormalizeKnownAccounts(
        TikTokAccountProfile account,
        IReadOnlyCollection<TikTokAccountProfile>? knownAccounts)
    {
        var result = (knownAccounts ?? Array.Empty<TikTokAccountProfile>())
            .Where(candidate => candidate is not null)
            .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (!result.Any(candidate =>
                string.Equals(candidate.Id, account.Id, StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(account);
        }
        return result;
    }

    private static HashSet<string> AccountAliases(TikTokAccountProfile account) =>
        new(new[]
        {
            account.Id,
            account.Name,
            account.DisplayName,
            account.TiktokAccountNickname,
            account.TiktokLoginEmail,
            account.TiktokLastLoginEmail,
            account.ResolveTikTokAccountName(),
        }.Select(value => (value ?? "").Trim()).Where(value => value.Length > 0),
            StringComparer.OrdinalIgnoreCase);

    private static bool ItemBelongsToArchiveRoot(ArchivedProjectItem item, string archiveRoot) =>
        TryIsWithin(item.MetadataPath, archiveRoot) ||
        TryIsWithin(item.ArchiveProjectDir, archiveRoot) ||
        TryIsWithin(item.ArchivedSourceDir, archiveRoot) ||
        TryIsWithin(item.ArchivedWorkflowDir, archiveRoot);

    private static string BuildMigrationComponentTarget(
        string targetArchiveRoot,
        string componentName,
        string archivedPath,
        string fallbackName,
        bool isWorkflow)
    {
        var leaf = FirstNonEmpty(
            PathLeaf(archivedPath),
            isWorkflow ? "_" + fallbackName.TrimStart('_') : fallbackName);
        return Path.Combine(targetArchiveRoot, componentName, SanitizeName(leaf));
    }

    private static string PathLeaf(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(Path.GetFullPath(path));
        }
        catch
        {
            return "";
        }
    }

    private static bool TryIsWithin(string? path, string? parent)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   !string.IsNullOrWhiteSpace(parent) &&
                   IsWithin(path, parent);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPathEquals(string? left, string? right)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   PathEquals(Path.GetFullPath(left), Path.GetFullPath(right));
        }
        catch
        {
            return false;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
