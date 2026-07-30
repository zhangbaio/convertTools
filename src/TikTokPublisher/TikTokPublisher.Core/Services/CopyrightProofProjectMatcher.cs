using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public enum CopyrightProofProjectLocation
{
    Missing,
    CurrentQueue,
    Archived,
    DeletedHistory,
    Conflict,
}

public sealed record CopyrightProofProjectMatch(
    string NewTitle,
    CopyrightProofProjectLocation Location,
    QueueProjectItem? QueueProject = null,
    ArchivedProjectItem? ArchivedProject = null,
    TikTokExecutionProjectSnapshot? HistorySnapshot = null,
    IReadOnlyList<string>? ConflictCandidates = null)
{
    public bool CanExecute =>
        Location is CopyrightProofProjectLocation.CurrentQueue
            or CopyrightProofProjectLocation.Archived
            or CopyrightProofProjectLocation.DeletedHistory;
}

/// <summary>Only exact rewritten/new-title matches are allowed.</summary>
public static class CopyrightProofProjectMatcher
{
    public static IReadOnlyList<string> ParseNewTitles(string? input) =>
        (input ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(title => !string.IsNullOrWhiteSpace(title))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<CopyrightProofProjectMatch> MatchByNewTitleExact(
        IEnumerable<string> newTitles,
        IEnumerable<QueueProjectItem> queueProjects,
        IEnumerable<ArchivedProjectItem> archivedProjects,
        IEnumerable<TikTokExecutionProjectSnapshot>? deletedHistoryProjects = null)
    {
        var queueByTitle = queueProjects
            .Where(item => !item.Archived && !string.IsNullOrWhiteSpace(item.NewTitle))
            .GroupBy(item => item.NewTitle.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var archiveByTitle = archivedProjects
            .Where(item => !string.IsNullOrWhiteSpace(item.NewTitle))
            .GroupBy(item => item.NewTitle.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var historyByTitle = (deletedHistoryProjects ?? [])
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Item.NewTitle))
            .GroupBy(snapshot => snapshot.Item.NewTitle.Trim(), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(snapshot => ParseTimestamp(snapshot.Timestamp))
                    .ToArray(),
                StringComparer.Ordinal);

        var results = new List<CopyrightProofProjectMatch>();
        foreach (var title in newTitles
                     .Select(value => (value ?? string.Empty).Trim())
                     .Where(value => value.Length > 0)
                     .Distinct(StringComparer.Ordinal))
        {
            if (queueByTitle.TryGetValue(title, out var queueMatches))
            {
                results.Add(queueMatches.Length == 1
                    ? new CopyrightProofProjectMatch(
                        title,
                        CopyrightProofProjectLocation.CurrentQueue,
                        QueueProject: queueMatches[0])
                    : Conflict(title, queueMatches.Select(item => item.ProjectDir)));
                continue;
            }

            if (archiveByTitle.TryGetValue(title, out var archiveMatches))
            {
                results.Add(archiveMatches.Length == 1
                    ? new CopyrightProofProjectMatch(
                        title,
                        CopyrightProofProjectLocation.Archived,
                        ArchivedProject: archiveMatches[0])
                    : Conflict(title, archiveMatches.Select(item => item.ArchiveProjectDir)));
                continue;
            }

            if (historyByTitle.TryGetValue(title, out var historyMatches))
            {
                var originalTitles = historyMatches
                    .Select(snapshot => (snapshot.Item.OriginalTitle ?? string.Empty).Trim())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (originalTitles.Length > 1)
                {
                    results.Add(new CopyrightProofProjectMatch(
                        title,
                        CopyrightProofProjectLocation.Conflict,
                        ConflictCandidates: originalTitles));
                    continue;
                }

                // A project can have multiple execution snapshots over its lifetime. Use the
                // newest recoverable snapshot rather than forcing the user to disambiguate
                // records that all refer to the same exact rewritten title.
                results.Add(new CopyrightProofProjectMatch(
                    title,
                    CopyrightProofProjectLocation.DeletedHistory,
                    HistorySnapshot: historyMatches[0]));
                continue;
            }

            results.Add(new CopyrightProofProjectMatch(title, CopyrightProofProjectLocation.Missing));
        }

        return results;
    }

    private static CopyrightProofProjectMatch Conflict(string title, IEnumerable<string> candidates) =>
        new(
            title,
            CopyrightProofProjectLocation.Conflict,
            ConflictCandidates: candidates
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
