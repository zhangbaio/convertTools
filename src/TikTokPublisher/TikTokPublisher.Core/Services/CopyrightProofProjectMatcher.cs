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

public enum CopyrightProofConflictKind
{
    None,
    QueueDirectory,
    ArchiveDirectory,
    OriginalTitle,
}

public sealed record CopyrightProofConflictOption(
    string Key,
    string Label,
    CopyrightProofProjectMatch Resolution);

public sealed record CopyrightProofProjectMatch(
    string NewTitle,
    CopyrightProofProjectLocation Location,
    QueueProjectItem? QueueProject = null,
    ArchivedProjectItem? ArchivedProject = null,
    TikTokExecutionProjectSnapshot? HistorySnapshot = null,
    IReadOnlyList<string>? ConflictCandidates = null,
    IReadOnlyList<CopyrightProofConflictOption>? ConflictOptions = null,
    CopyrightProofConflictKind ConflictKind = CopyrightProofConflictKind.None)
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
                results.Add(MatchQueue(title, queueMatches));
                continue;
            }

            if (archiveByTitle.TryGetValue(title, out var archiveMatches))
            {
                results.Add(MatchArchive(title, archiveMatches));
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
                    var options = originalTitles
                        .Select(original =>
                        {
                            var snapshot = historyMatches.First(item =>
                                string.Equals(
                                    (item.Item.OriginalTitle ?? string.Empty).Trim(),
                                    original,
                                    StringComparison.Ordinal));
                            return new CopyrightProofConflictOption(
                                original,
                                $"原剧名：{original}",
                                new CopyrightProofProjectMatch(
                                    title,
                                    CopyrightProofProjectLocation.DeletedHistory,
                                    HistorySnapshot: snapshot));
                        })
                        .ToArray();
                    results.Add(CreateConflict(
                        title,
                        CopyrightProofConflictKind.OriginalTitle,
                        options));
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

    public static string ConflictHeadline(CopyrightProofProjectMatch match) =>
        match.ConflictKind switch
        {
            CopyrightProofConflictKind.QueueDirectory => "当前队列有多个目录，不能自动执行",
            CopyrightProofConflictKind.ArchiveDirectory => "已归档有多个目录，不能自动执行",
            CopyrightProofConflictKind.OriginalTitle => "同一新剧名对应多个原剧名，不能自动执行",
            _ => "同名冲突，不能自动执行",
        };

    public static string DescribeConflict(CopyrightProofProjectMatch match)
    {
        var details = match.ConflictOptions?.Select(option => option.Label).ToArray()
            ?? match.ConflictCandidates
            ?? [];
        if (details.Count == 0)
            return ConflictHeadline(match);
        return ConflictHeadline(match) + Environment.NewLine +
               string.Join(Environment.NewLine, details.Select(line => "    " + line));
    }

    private static CopyrightProofProjectMatch MatchQueue(string title, IReadOnlyList<QueueProjectItem> matches)
    {
        var unique = DedupeByDirectory(
            matches,
            item => item.ProjectDir,
            item => !string.IsNullOrWhiteSpace(item.OriginalTitle));
        if (unique.Count == 1)
        {
            return new CopyrightProofProjectMatch(
                title,
                CopyrightProofProjectLocation.CurrentQueue,
                QueueProject: unique[0]);
        }

        var options = unique
            .Select(item =>
            {
                var path = DisplayPath(item.ProjectDir);
                var original = (item.OriginalTitle ?? string.Empty).Trim();
                var label = string.IsNullOrWhiteSpace(original)
                    ? path
                    : $"{path}（原剧名：{original}）";
                return new CopyrightProofConflictOption(
                    path,
                    label,
                    new CopyrightProofProjectMatch(
                        title,
                        CopyrightProofProjectLocation.CurrentQueue,
                        QueueProject: item));
            })
            .ToArray();
        return CreateConflict(title, CopyrightProofConflictKind.QueueDirectory, options);
    }

    private static CopyrightProofProjectMatch MatchArchive(string title, IReadOnlyList<ArchivedProjectItem> matches)
    {
        var unique = DedupeByDirectory(
            matches,
            item => item.ArchiveProjectDir,
            item => !string.IsNullOrWhiteSpace(item.OriginalTitle));
        if (unique.Count == 1)
        {
            return new CopyrightProofProjectMatch(
                title,
                CopyrightProofProjectLocation.Archived,
                ArchivedProject: unique[0]);
        }

        var options = unique
            .Select(item =>
            {
                var path = DisplayPath(item.ArchiveProjectDir);
                var original = (item.OriginalTitle ?? string.Empty).Trim();
                var label = string.IsNullOrWhiteSpace(original)
                    ? path
                    : $"{path}（原剧名：{original}）";
                return new CopyrightProofConflictOption(
                    path,
                    label,
                    new CopyrightProofProjectMatch(
                        title,
                        CopyrightProofProjectLocation.Archived,
                        ArchivedProject: item));
            })
            .ToArray();
        return CreateConflict(title, CopyrightProofConflictKind.ArchiveDirectory, options);
    }

    private static CopyrightProofProjectMatch CreateConflict(
        string title,
        CopyrightProofConflictKind kind,
        IReadOnlyList<CopyrightProofConflictOption> options) =>
        new(
            title,
            CopyrightProofProjectLocation.Conflict,
            ConflictCandidates: options.Select(option => option.Key).ToArray(),
            ConflictOptions: options,
            ConflictKind: kind);

    private static List<T> DedupeByDirectory<T>(
        IReadOnlyList<T> items,
        Func<T, string?> pathSelector,
        Func<T, bool> prefer)
    {
        var result = new List<T>();
        var indexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = NormalizeDirectory(pathSelector(item));
            if (key is null)
            {
                result.Add(item);
                continue;
            }

            if (!indexByPath.TryGetValue(key, out var existing))
            {
                indexByPath[key] = result.Count;
                result.Add(item);
                continue;
            }

            if (prefer(item) && !prefer(result[existing]))
                result[existing] = item;
        }

        return result;
    }

    private static string? NormalizeDirectory(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return text;
        }
    }

    private static string DisplayPath(string? path) =>
        NormalizeDirectory(path) ?? (path ?? string.Empty).Trim();

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
