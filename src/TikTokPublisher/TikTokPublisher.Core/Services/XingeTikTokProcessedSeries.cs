using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Remote;

namespace TikTokPublisher.Core.Services;

public sealed record XingeTikTokProcessedFact(
    [property: JsonPropertyName("account_profile_id")] string AccountProfileId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("series_id")] string SeriesId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("episode_cnt")] int EpisodeCount,
    [property: JsonPropertyName("source")] string Source = "");

public static class XingeTikTokProcessedSeries
{
    public static IReadOnlyList<XingeTikTokProcessedFact> Collect(IReadOnlyList<XingeRemoteAccountSeed> accounts)
    {
        var facts = new HashSet<XingeTikTokProcessedFact>();
        var accountIds = accounts.Select(account => account.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workspaces = accounts.GroupBy(account => account.ResolveWorkspacePath(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0).ToArray();
        void Add(string accountId, string kind, string id, string title, int episodes)
        {
            title = title.Trim();
            var key = SeriesKey("", id);
            if ((!string.IsNullOrEmpty(accountId) && !accountIds.Contains(accountId)) || (key.Length == 0 && (title.Length == 0 || episodes <= 0)))
                return;
            facts.Add(new(accountId, kind, key, title, Math.Max(0, episodes)));
        }

        foreach (var stamp in TikTokExecutionHistoryService.ReadUploadCompletionStamps())
        {
            if (stamp.AccountProfileId.Length == 0 && !workspaces.Any(group => string.Equals(group.Key, stamp.Workspace, StringComparison.OrdinalIgnoreCase)))
                continue;
            Add(stamp.AccountProfileId, "uploaded", FirstNonEmpty(stamp.SourceSeriesId, TikTokTodayUploadCountService.ReadProjectBookId(stamp.ProjectDir)),
                stamp.OriginalTitle, stamp.EpisodeCount);
        }

        foreach (var group in workspaces)
        {
            foreach (var item in WorkspaceQueueService.LoadCompletedUploadItems(group.Key))
            {
                Add(item.AccountProfileId, "uploaded", TikTokTodayUploadCountService.ReadProjectBookId(item.ProjectDir), item.OriginalTitle,
                    item.SourceEpisodeCount > 0 ? item.SourceEpisodeCount : item.EpisodeCount);
            }

            foreach (var archiveRoot in group.Select(account => account.ResolveArchiveRoot(group.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var item in TikTokArchivedProjectService.ListStored(group.Key, archiveRoot))
                {
                    var id = FirstNonEmpty(TikTokTodayUploadCountService.ReadProjectBookId(item.ArchivedSourceDir),
                        TikTokTodayUploadCountService.ReadProjectBookId(item.ArchiveProjectDir),
                        TikTokTodayUploadCountService.ReadProjectBookId(item.ArchivedWorkflowDir));
                    _ = int.TryParse(TikTokSeriesIdentityIndex.ReadArchiveEpisodeCount(item), out var episodes);
                    Add(item.AccountProfileId, "archived", id, item.OriginalTitle, episodes);
                    if (!string.IsNullOrWhiteSpace(item.UploadCompletedAt))
                        Add(item.AccountProfileId, "uploaded", id, item.OriginalTitle, episodes);
                }
            }
        }
        return facts.ToArray();
    }

    public static IReadOnlyList<XingeTikTokProcessedFact> FindMatches(TikTokUploadSeriesSpec item, string source, IReadOnlyList<XingeTikTokProcessedFact> facts)
    {
        var id = SeriesKey(source, item.SeriesId);
        var title = TitleKey(item.Title);
        return facts.Where(fact =>
        {
            var knownId = SeriesKey(fact.Source, fact.SeriesId);
            if (id.Length > 0 && knownId.Length > 0)
                return id == knownId;
            return title.Length > 0 && item.EpisodeCnt > 0 && item.EpisodeCnt == fact.EpisodeCount && title == TitleKey(fact.Title);
        }).ToArray();
    }

    private static string SeriesKey(string source, string value)
    {
        var id = value.Trim();
        var separator = id.IndexOf(':');
        if (separator >= 0)
        {
            source = id[..separator];
            id = id[(separator + 1)..].Trim();
        }
        var canon = source.Trim().ToLowerInvariant() switch
        {
            "hongguo" or "hongguoapp" or "hglocal" or "hgoffline" or "hgnew" or "hghigh" or "downloader" => "hongguo",
            "dramabox" => "dramabox",
            "playlet" => "playlet",
            "pikachu" => "pikachu",
            "foreign" => "foreign",
            _ => "",
        };
        return canon.Length > 0 && id.Length > 0 ? canon + ":" + id : "";
    }

    private static string TitleKey(string title) => Regex.Replace(title.Replace('’', '\'').Replace('‘', '\''), @"\s+", " ").Trim().ToLowerInvariant();

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
