using System.Globalization;
using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 经营者平台的上架接口。已下架且发布成功、并且页面上有可点的上架按钮时才上架。
/// </summary>
public static class KuaishouShelfApi
{
    public const string ListUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/queryList";
    public const string OnlineOfflineUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/onlineOffline";
    public const int OfflineSellingStatus = 2;
    public const int OnlineSellingStatus = 1;
    public const int RiskSellingStatus = 9;
    public const int PublishedAuditStatus = 3;
    public const int SchedulingStatus = 2;
    public const int PageSize = 20;

    public readonly record struct Candidate(
        string SeriesId,
        string Title,
        int AuditStatus,
        int SellingStatus,
        bool CanShelf,
        string SkipReason);

    public static string OfflineListBody(int pageNum, int pageSize) =>
        ListBody(pageNum, pageSize, OfflineSellingStatus, PublishedAuditStatus, []);

    public static string StatusBody(string seriesId) =>
        ListBody(1, 5, 0, 0, [seriesId.Trim()]);

    public static string OnlineBody(string seriesId)
    {
        var id = seriesId.Trim();
        return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? JsonSerializer.Serialize(new { miniSeriesId = number, online = true })
            : JsonSerializer.Serialize(new { miniSeriesId = id, online = true });
    }

    public static int ReadOfflinePage(JsonElement root, ICollection<Candidate> rows, out int totalCount)
    {
        totalCount = 0;
        if (!TryData(root, out var data))
            return 0;
        if (data.TryGetProperty("totalCount", out var total) && total.TryGetInt32(out var count))
            totalCount = count;
        if (!data.TryGetProperty("data", out var list) || list.ValueKind != JsonValueKind.Array)
            return 0;

        var raw = 0;
        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            raw++;
            if (TryReadCandidate(row, out var candidate))
                rows.Add(candidate);
        }

        return raw;
    }

    public static bool TryReadStatus(JsonElement root, string seriesId, out int auditStatus, out int sellingStatus)
    {
        auditStatus = 0;
        sellingStatus = 0;
        if (!TryData(root, out var data) ||
            !data.TryGetProperty("data", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var row in list.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || ReadId(row) != seriesId.Trim())
                continue;
            auditStatus = ReadInt(row, "auditStatus");
            sellingStatus = ReadInt(row, "sellingStatus");
            return true;
        }

        return false;
    }

    public static bool TryReadCandidate(JsonElement row, out Candidate candidate)
    {
        candidate = default;
        var id = ReadId(row);
        if (id.Length == 0)
            return false;
        var title = ReadString(row, "courseName");
        if (title.Length == 0)
            title = ReadString(row, "miniSeriesTitle");
        if (title.Length == 0)
            title = id;
        var audit = ReadInt(row, "auditStatus");
        var selling = ReadInt(row, "sellingStatus");
        var reason = ReadString(row, "onlineDisableReason");
        if (reason.Length == 0)
            reason = ReadString(row, "distributionOnlineDisableReason");
        var canShelf = audit == PublishedAuditStatus &&
            selling == OfflineSellingStatus &&
            selling != RiskSellingStatus &&
            ReadBool(row, "canOnlineOffline") == true &&
            ReadBool(row, "onlineDisableFlag") != true &&
            ReadBool(row, "distributionOnlineDisableFlag") != true &&
            ReadBool(row, "copyedSeriesAuthStatus") != false &&
            ReadInt(row, "onlineScheduleStatus") != SchedulingStatus &&
            !reason.Contains("平台风控", StringComparison.Ordinal) &&
            !reason.Contains("不可上架", StringComparison.Ordinal);
        var skip = "";
        if (!canShelf)
        {
            skip = reason.Contains("平台风控", StringComparison.Ordinal) ||
                reason.Contains("不可上架", StringComparison.Ordinal) ||
                selling == RiskSellingStatus
                ? "平台风控"
                : "没有上架按钮";
        }

        candidate = new Candidate(id, title, audit, selling, canShelf, skip);
        return true;
    }

    private static string ListBody(int pageNum, int pageSize, int sellingStatus, int auditStatus, string[] seriesIds) =>
        JsonSerializer.Serialize(new
        {
            pageNum,
            pageSize,
            episodeTitle = "",
            sellingStatus,
            queryType = 0,
            sortParam = new { },
            miniSeriesTitle = "",
            auditStatus,
            saleType = (int?)null,
            seriesIdList = seriesIds,
            createDateFrom = "",
            createDateTo = "",
            sourceType4Filter = (int?)null,
        });

    private static bool TryData(JsonElement root, out JsonElement data)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out data) &&
            data.ValueKind == JsonValueKind.Object)
            return true;
        data = default;
        return false;
    }

    private static string ReadId(JsonElement row)
    {
        if (!row.TryGetProperty("miniSeriesId", out var id))
            return "";
        if (id.ValueKind == JsonValueKind.String)
            return id.GetString()?.Trim() ?? "";
        if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number))
            return number.ToString(CultureInfo.InvariantCulture);
        return "";
    }

    private static string ReadString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static int ReadInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static bool? ReadBool(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
