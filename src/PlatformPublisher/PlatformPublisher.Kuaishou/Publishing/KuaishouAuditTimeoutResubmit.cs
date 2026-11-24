using System.Globalization;
using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public enum KuaishouAuditTimeoutEligibility
{
    TimeoutOnly,
    MixedSummary,
    NeedsChanges,
}

public sealed record KuaishouAuditTimeoutTarget(
    string SeriesId,
    string Title,
    KuaishouAuditTimeoutEligibility Eligibility,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> EpisodeIds,
    IReadOnlyList<int> EpisodeNumbers,
    bool Pending = false)
{
    public bool CanSelect => !Pending && Eligibility != KuaishouAuditTimeoutEligibility.NeedsChanges;

    public string EligibilityLabel => Pending ? "待确认" : Eligibility switch
    {
        KuaishouAuditTimeoutEligibility.TimeoutOnly => "仅审核超时",
        KuaishouAuditTimeoutEligibility.MixedSummary => "摘要含封面或标题提示，分集仅报超时",
        _ => "需修改或核验审核原因",
    };

    public string EpisodeLabel => EpisodeNumbers.Count > 0
        ? $"第 {string.Join("、", EpisodeNumbers)} 集"
        : EpisodeIds.Count > 0 ? $"分集 ID {string.Join("、", EpisodeIds)}" : "剧集审核";
}

/// <summary>仅解析平台明确报告的超时和分集驳回证据。</summary>
internal static class KuaishouAuditTimeoutResubmit
{
    private const string MixedSummaryReason = "单集封面/标题审核不通过，请修改";
    private static readonly string[] TimeoutReasons =
    [
        "您提交的内容审核超时，请您重新提交",
        "您提交的内容审核超时，请重新提交",
        "内容审核超时，请重新提交",
        "审核超时，请重新提交",
        "审核超时",
    ];

    internal static bool TryReadTarget(JsonElement row, out KuaishouAuditTimeoutTarget? target)
    {
        target = null;
        if (row.ValueKind != JsonValueKind.Object ||
            ReadNumber(row, "sellingStatus") != KuaishouShelfApi.OfflineSellingStatus ||
            ReadNumber(row, "auditStatus") != KuaishouDraftReplace.RejectedAuditStatus)
            return false;

        var display = Property(row, "checkReasonDisplayInfo");
        var summary = Property(row, "failReasonSummary");
        var checks = Property(row, "checkReasons");
        var courseReasons = SplitReasons(Text(summary, "reviewReason"))
            .Concat(SplitReasons(Text(summary, "uploadReason")))
            .Concat(SplitReasons(Text(display, "rejectReason")))
            .Concat(ReadGroupReasons(Property(checks, "courseReasonGroups")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var episodes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var numbers = new HashSet<int>();
        foreach (var episode in Items(Property(display, "episodeDisplayInfos")))
        {
            var id = Text(episode, "episodeId");
            if (id.Length == 0) continue;
            if (!episodes.TryGetValue(id, out var reasons))
                episodes[id] = reasons = [];
            reasons.AddRange(SplitReasons(Text(episode, "rejectReason")));
            if (ReadNumber(episode, "orderNo") is >= 0 and < int.MaxValue and var order)
                numbers.Add(order + 1);
        }

        var groupsByEpisode = Property(checks, "lidReasonGroupsMap");
        if (groupsByEpisode.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in groupsByEpisode.EnumerateObject())
            {
                if (!episodes.TryGetValue(entry.Name, out var reasons))
                    episodes[entry.Name] = reasons = [];
                reasons.AddRange(ReadGroupReasons(entry.Value));
            }
        }

        var reasonsForDisplay = courseReasons.Concat(episodes.Values.SelectMany(reasons => reasons))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (!reasonsForDisplay.Any(reason => reason.Contains("审核超时", StringComparison.Ordinal)))
            return false;

        var seriesId = Text(row, "miniSeriesId");
        if (seriesId.Length == 0) return false;
        var title = Text(row, "courseName");
        if (title.Length == 0)
            title = Text(row, "miniSeriesTitle");
        if (title.Length == 0)
            title = seriesId;

        var mixed = courseReasons.Contains(MixedSummaryReason, StringComparer.Ordinal);
        var allEpisodeReasonsAreTimeouts = episodes.Values.All(reasons => reasons.Count > 0 && reasons.All(IsTimeout));
        var rejectedOrderCount = Items(Property(display, "rejectedEpisodeNoList")).Count();
        var courseHasOtherReasons = courseReasons.Any(reason => !IsTimeout(reason) && reason != MixedSummaryReason);
        var incompleteEvidence = rejectedOrderCount > episodes.Count || (mixed && episodes.Count == 0);
        var eligibility = courseHasOtherReasons || incompleteEvidence || !allEpisodeReasonsAreTimeouts
            ? KuaishouAuditTimeoutEligibility.NeedsChanges
            : mixed ? KuaishouAuditTimeoutEligibility.MixedSummary : KuaishouAuditTimeoutEligibility.TimeoutOnly;

        target = new KuaishouAuditTimeoutTarget(seriesId, title, eligibility, reasonsForDisplay,
            episodes.Keys.ToArray(), numbers.Order().ToArray());
        return true;
    }

    internal static bool CanSaveOriginal(
        JsonElement baseInfo, JsonElement episodeList, KuaishouAuditTimeoutTarget target)
    {
        var info = Property(baseInfo, "data");
        var returnedId = Text(info, "miniSeriesId");
        if ((returnedId.Length > 0 && returnedId != target.SeriesId) ||
            Property(info, "canEdit").ValueKind != JsonValueKind.True ||
            ReadNumber(info, "auditStatus") != KuaishouDraftReplace.RejectedAuditStatus ||
            ReadNumber(info, "sellingStatus") != KuaishouShelfApi.OfflineSellingStatus ||
            Text(info, "coverImgKey").Length == 0 ||
            (Text(info, "courseName").Length == 0 && Text(info, "miniSeriesTitle").Length == 0))
            return false;

        var data = Property(episodeList, "data");
        var list = Property(data, "seriesList");
        if (list.ValueKind != JsonValueKind.Array)
            list = Property(data, "episodeList");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items(list))
        {
            var id = Text(item, "episodeId");
            if (id.Length == 0 || !ids.Add(id) || Text(item, "episodeTitle").Length == 0 ||
                Property(item, "coverImgKey").ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return false;

            var comments = Property(item, "videoReviewComments");
            if (target.EpisodeIds.Contains(id, StringComparer.Ordinal) && HasOtherReviewComment(comments))
                return false;
        }

        return ids.Count > 0 && target.EpisodeIds.All(ids.Contains);
    }

    private static bool HasOtherReviewComment(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => false,
            JsonValueKind.String => SplitReasons(value.GetString() ?? "").Any(reason => !IsTimeout(reason)),
            JsonValueKind.Array => value.EnumerateArray().Any(HasOtherReviewComment),
            JsonValueKind.Object => value.EnumerateObject().Any(property => HasOtherReviewComment(property.Value)),
            _ => true,
        };

    private static IEnumerable<string> ReadGroupReasons(JsonElement groups) => Items(groups)
        .SelectMany(group => Items(Property(group, "reasons")))
        .Where(reason => reason.ValueKind == JsonValueKind.String)
        .SelectMany(reason => SplitReasons(reason.GetString() ?? ""));

    private static bool IsTimeout(string reason) => TimeoutReasons.Contains(reason, StringComparer.Ordinal);

    private static IEnumerable<string> SplitReasons(string text) => text
        .Replace('，', '，')
        .Split([';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(reason => reason.TrimEnd('。', '.').Replace(',', '，').Trim())
        .Where(reason => reason.Length > 0);

    internal static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var node) ? node : default;

    internal static IEnumerable<JsonElement> Items(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    internal static string Text(JsonElement value, string name)
    {
        var node = Property(value, name);
        return node.ValueKind switch
        {
            JsonValueKind.String => node.GetString()?.Trim() ?? "",
            JsonValueKind.Number when node.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => "",
        };
    }

    internal static int ReadNumber(JsonElement value, string name, int fallback = -1) =>
        int.TryParse(Text(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;
}
