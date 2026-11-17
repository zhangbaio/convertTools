using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 用本地新剧替换平台上已有的短剧草稿。只走更新，不调用新建。
/// </summary>
public static partial class KuaishouDraftReplace
{
    public const int PendingAuditStatus = 1;
    public const int ReviewingAuditStatus = 2;
    public const int PublishedAuditStatus = 3;
    public const int RejectedAuditStatus = 4;

    public readonly record struct Target(string SeriesId, string Title, int AuditStatus, int SellingStatus);

    public static bool TryParseSeriesId(string? text, out string seriesId)
    {
        seriesId = "";
        var raw = text?.Trim() ?? "";
        if (raw.Length == 0)
            return false;
        var matches = IdPattern().Matches(raw);
        if (matches.Count != 1 || matches[0].Value.Length < 5)
            return false;
        seriesId = matches[0].Value;
        return true;
    }

    public static string EpisodeDetailBody(string seriesId) =>
        "{\"miniSeriesId\":" + seriesId.Trim() + "}";

    public static bool TryRead(JsonElement root, string seriesId, out Target target)
    {
        target = default;
        var wanted = seriesId.Trim();
        if (!TryList(root, out var list))
            return false;
        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            var id = ReadId(row);
            if (!string.Equals(id, wanted, StringComparison.Ordinal))
                continue;
            var title = ReadString(row, "courseName");
            if (title.Length == 0)
                title = ReadString(row, "miniSeriesTitle");
            if (title.Length == 0)
                title = id;
            target = new Target(id, title, ReadInt(row, "auditStatus"), ReadInt(row, "sellingStatus"));
            return true;
        }

        return false;
    }

    public static string Refusal(Target target)
    {
        var name = $"《{target.Title}》（{target.SeriesId}）";
        if (target.SellingStatus == KuaishouShelfApi.OnlineSellingStatus)
            return name + "已上架，不能替换。";
        if (target.SellingStatus == KuaishouShelfApi.RiskSellingStatus)
            return name + "被平台风控下架，不能替换。";
        if (target.AuditStatus == ReviewingAuditStatus)
            return name + "正在审核，不能替换。";
        if (target.AuditStatus == PublishedAuditStatus)
            return name + "已发布成功，不能替换。";
        if (target.AuditStatus is PendingAuditStatus or RejectedAuditStatus)
            return "";
        return name + "当前不能替换。";
    }

    public static string BindingMessage(string? currentSeriesId, string targetSeriesId)
    {
        var current = currentSeriesId?.Trim() ?? "";
        var target = targetSeriesId.Trim();
        if (current.Length == 0 || string.Equals(current, target, StringComparison.Ordinal))
            return "";
        return $"这部本地剧已经绑定短剧 {current}，不能再换到 {target}。";
    }

    public static string EpisodeCountMessage(int platformCount, int localCount, string seriesId)
    {
        if (platformCount <= 0)
            return $"短剧 {seriesId} 没有单集，已停止替换。";
        if (platformCount != localCount)
            return $"平台短剧有 {platformCount} 集，本地有 {localCount} 集，已停止替换。";
        return "";
    }

    public static bool ContinueDespiteDailyCap(KuaishouPersonalUploadState? state)
    {
        if (state is null || !state.ReplaceExistingVideos || string.IsNullOrWhiteSpace(state.MiniSeriesId))
            return false;
        var status = state.Status?.Trim() ?? "";
        return !status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            && !status.Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task EnsureEpisodeCountAsync(
        IKuaishouKdjGateway gateway,
        string seriesId,
        int localCount,
        CancellationToken cancellationToken)
    {
        var detail = await gateway.PostJsonAsync(
            KuaishouApiVideoUploader.DetailUrl,
            EpisodeDetailBody(seriesId),
            cancellationToken);
        using var document = JsonDocument.Parse(detail);
        var count = KuaishouSeriesDraft.ReadEpisodeIds(document.RootElement).Count;
        var message = EpisodeCountMessage(count, localCount, seriesId);
        if (message.Length > 0)
            throw new InvalidOperationException(message);
    }

    private static bool TryList(JsonElement root, out JsonElement list)
    {
        list = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("data", out list) ||
            list.ValueKind != JsonValueKind.Array)
            return false;
        return true;
    }

    private static string ReadId(JsonElement row)
    {
        if (!row.TryGetProperty("miniSeriesId", out var id))
            return "";
        if (id.ValueKind == JsonValueKind.String)
            return id.GetString()?.Trim() ?? "";
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number))
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "";
    }

    private static string ReadString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static int ReadInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    [GeneratedRegex(@"\d+")]
    private static partial Regex IdPattern();
}

public sealed record KuaishouDraftReplacePreview(
    string SeriesId,
    string PlatformTitle,
    string LocalTitle,
    int EpisodeCount);
