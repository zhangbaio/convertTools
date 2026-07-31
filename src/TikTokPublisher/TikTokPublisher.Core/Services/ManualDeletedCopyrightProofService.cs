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

            var originalTitle = originalTitles[0];
            var projectDirectoryName = string.IsNullOrWhiteSpace(originalTitle)
                ? SanitizeFileName(group.Key) + "_版权恢复"
                : originalTitle;
            var item = new QueueProjectItem
            {
                ProjectDir = Path.Combine(workspace, projectDirectoryName),
                DisplayName = string.IsNullOrWhiteSpace(originalTitle) ? group.Key : originalTitle,
                OriginalTitle = originalTitle,
                NewTitle = group.Key,
                EpisodeCount = 0,
                AccountProfileId = account.Id,
                AccountProfileName = account.DisplayName,
                QueuedAt = timestamp,
                UploadCompletedAt = timestamp,
                Enabled = true,
                StatusText = QueueStepStatus.Completed,
                Remark = string.IsNullOrWhiteSpace(originalTitle)
                    ? "原剧名未知，将从 TikTok 已发布项目恢复视频并补全版权证明"
                    : "用户手动指定原剧名，用于重建已删除项目并补全版权证明",
                StepStates = new Dictionary<string, string>
                {
                    [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
                },
            };
            item.NormalizeStepStates();
            results.Add(new CopyrightProofProjectMatch(
                group.Key,
                CopyrightProofProjectLocation.DeletedHistory,
                HistorySnapshot: new TikTokExecutionProjectSnapshot(
                    workspace,
                    timestamp,
                    item)));
        }

        return results;
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
