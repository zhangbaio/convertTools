using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public enum CopyrightProofProjectLocation
{
    Missing,
    CurrentQueue,
    Archived,
    Conflict,
}

public sealed record CopyrightProofProjectMatch(
    string NewTitle,
    CopyrightProofProjectLocation Location,
    QueueProjectItem? QueueProject = null,
    ArchivedProjectItem? ArchivedProject = null,
    IReadOnlyList<string>? ConflictCandidates = null)
{
    public bool CanExecute =>
        Location is CopyrightProofProjectLocation.CurrentQueue or CopyrightProofProjectLocation.Archived;
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
        IEnumerable<ArchivedProjectItem> archivedProjects)
    {
        var queueByTitle = queueProjects
            .Where(item => !item.Archived && !string.IsNullOrWhiteSpace(item.NewTitle))
            .GroupBy(item => item.NewTitle.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var archiveByTitle = archivedProjects
            .Where(item => !string.IsNullOrWhiteSpace(item.NewTitle))
            .GroupBy(item => item.NewTitle.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

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
}
