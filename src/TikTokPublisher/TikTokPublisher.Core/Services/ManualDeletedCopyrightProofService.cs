using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public enum ManualDeletedCopyrightProofInputMode
{
    KnownOriginalTitle,
    UnknownOriginalTitle,
}

public sealed record ManualDeletedCopyrightProofEntry(
    string NewTitle,
    string OriginalTitle);

/// <summary>
/// Builds recoverable deleted-project snapshots from an exact new title and an optional original title.
/// Existing queue/archive projects still take precedence so the manual fallback cannot create duplicates.
/// </summary>
public static class ManualDeletedCopyrightProofService
{
    public static IReadOnlyList<ManualDeletedCopyrightProofEntry> ParseUnknownOriginalTitles(
        string? input)
    {
        return (input ?? string.Empty)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(title => title.Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .Select(title => new ManualDeletedCopyrightProofEntry(title, string.Empty))
            .ToArray();
    }

    /// <summary>
    /// Exact-match queue → archive → deleted history, then promote remaining Missing titles to
    /// published-video recovery snapshots (same as manual "unknown original title" rebuild).
    /// </summary>
    public static IReadOnlyList<CopyrightProofProjectMatch> MatchByNewTitleExactOrRecover(
        IEnumerable<string> newTitles,
        string workspaceRoot,
        TikTokAccountProfile account,
        IEnumerable<QueueProjectItem> queueProjects,
        IEnumerable<ArchivedProjectItem> archivedProjects,
        IEnumerable<TikTokExecutionProjectSnapshot>? deletedHistoryProjects = null)
    {
        var matches = CopyrightProofProjectMatcher.MatchByNewTitleExact(
            newTitles,
            queueProjects,
            archivedProjects,
            deletedHistoryProjects);
        return PromoteMissingToPublishedRecovery(matches, workspaceRoot, account);
    }

    /// <summary>
    /// Turns Missing matches into DeletedHistory recovery snapshots with empty original title.
    /// Non-Missing matches (including real history and Conflict) are left unchanged.
    /// </summary>
    public static IReadOnlyList<CopyrightProofProjectMatch> PromoteMissingToPublishedRecovery(
        IReadOnlyList<CopyrightProofProjectMatch> matches,
        string workspaceRoot,
        TikTokAccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(account);

        if (matches.Count == 0)
            return matches;

        var workspace = Path.GetFullPath(workspaceRoot);
        var timestamp = DateTimeOffset.Now.ToString("o");
        var results = new List<CopyrightProofProjectMatch>(matches.Count);
        foreach (var match in matches)
        {
            if (match.Location != CopyrightProofProjectLocation.Missing)
            {
                results.Add(match);
                continue;
            }

            results.Add(CreateRecoveryMatch(
                match.NewTitle,
                originalTitle: string.Empty,
                workspace,
                account,
                timestamp));
        }

        return results;
    }

    public static IReadOnlyList<CopyrightProofProjectMatch> BuildMatches(
        IEnumerable<ManualDeletedCopyrightProofEntry> entries,
        string workspaceRoot,
        TikTokAccountProfile account,
        IEnumerable<QueueProjectItem>? queueProjects = null,
        IEnumerable<ArchivedProjectItem>? archivedProjects = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(account);

        var workspace = Path.GetFullPath(workspaceRoot);
        var normalized = entries
            .Select(entry => new ManualDeletedCopyrightProofEntry(
                (entry.NewTitle ?? string.Empty).Trim(),
                (entry.OriginalTitle ?? string.Empty).Trim()))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.NewTitle))
            .Distinct()
            .ToArray();
        if (normalized.Length == 0)
            return [];

        var existingMatches = CopyrightProofProjectMatcher.MatchByNewTitleExact(
                normalized.Select(entry => entry.NewTitle),
                queueProjects ?? [],
                archivedProjects ?? [])
            .ToDictionary(match => match.NewTitle, StringComparer.Ordinal);
        var timestamp = DateTimeOffset.Now.ToString("o");
        var results = new List<CopyrightProofProjectMatch>();

        foreach (var group in normalized.GroupBy(entry => entry.NewTitle, StringComparer.Ordinal))
        {
            var originalTitles = group
                .Select(entry => entry.OriginalTitle)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (originalTitles.Length > 1)
            {
                results.Add(new CopyrightProofProjectMatch(
                    group.Key,
                    CopyrightProofProjectLocation.Conflict,
                    ConflictCandidates: originalTitles));
                continue;
            }

            if (existingMatches.TryGetValue(group.Key, out var existing) &&
                existing.Location != CopyrightProofProjectLocation.Missing)
            {
                results.Add(existing);
                continue;
            }

            results.Add(CreateRecoveryMatch(
                group.Key,
                originalTitles[0],
                workspace,
                account,
                timestamp));
        }

        return results;
    }

    /// <summary>
    /// True when the match will recover via TikTok published video (empty original title).
    /// </summary>
    public static bool IsPublishedRecoveryFallback(CopyrightProofProjectMatch match) =>
        match.Location == CopyrightProofProjectLocation.DeletedHistory &&
        string.IsNullOrWhiteSpace(match.HistorySnapshot?.Item.OriginalTitle);

    private static CopyrightProofProjectMatch CreateRecoveryMatch(
        string newTitle,
        string originalTitle,
        string workspace,
        TikTokAccountProfile account,
        string timestamp)
    {
        var projectDirectoryName = string.IsNullOrWhiteSpace(originalTitle)
            ? SanitizeFileName(newTitle) + "_版权恢复"
            : originalTitle;
        var item = new QueueProjectItem
        {
            ProjectDir = Path.Combine(workspace, projectDirectoryName),
            DisplayName = string.IsNullOrWhiteSpace(originalTitle) ? newTitle : originalTitle,
            OriginalTitle = originalTitle,
            NewTitle = newTitle,
            EpisodeCount = 0,
            AccountProfileId = account.Id,
            AccountProfileName = account.DisplayName,
            QueuedAt = timestamp,
            // The TikTok series already exists, but its real upload time is unknown.
            // Leaving this empty prevents a proof-only recovery from counting as today's upload.
            UploadCompletedAt = string.Empty,
            Enabled = true,
            StatusText = QueueStepStatus.Completed,
            Remark = string.IsNullOrWhiteSpace(originalTitle)
                ? "原剧名未知，将从 TikTok 原创管理项目恢复视频并补全版权证明"
                : "用户手动指定原剧名，用于重建已删除项目并补全版权证明",
            StepStates = new Dictionary<string, string>
            {
                [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
            },
        };
        item.NormalizeStepStates();
        return new CopyrightProofProjectMatch(
            newTitle,
            CopyrightProofProjectLocation.DeletedHistory,
            HistorySnapshot: new TikTokExecutionProjectSnapshot(
                workspace,
                timestamp,
                item));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((value ?? string.Empty)
                .Trim()
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray())
            .Trim()
            .Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "已发布剧集" : sanitized;
    }
}
