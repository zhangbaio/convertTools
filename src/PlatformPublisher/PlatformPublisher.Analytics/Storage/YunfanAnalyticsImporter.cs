using System.Text.Json;
using Microsoft.Data.Sqlite;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Analytics.Storage;

public sealed record YunfanAnalyticsImportReport(int DailyRecords, int SubjectRecords, int Snapshots,
    IReadOnlyList<string> UnmappedAccountIds, string Message);

public sealed class YunfanAnalyticsImporter
{
    private readonly AnalyticsRepository _target;
    public YunfanAnalyticsImporter(AnalyticsRepository target) => _target = target;

    public YunfanAnalyticsImportReport Import(string yunfanRoot, IReadOnlyList<AnalyticsAccount> targetAccounts)
    {
        var root = Path.GetFullPath(yunfanRoot);
        var sourceAccounts = LoadSourceAccounts(Path.Combine(root, "accounts.json"));
        var map = BuildAccountMap(sourceAccounts, targetAccounts);
        var unmapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var daily = 0; var subjects = 0; var snapshots = 0;
        var database = Path.Combine(root, "data", "yunfan.sqlite3");
        if (File.Exists(database))
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            if (TableExists(connection, "analytics_daily_metrics"))
                daily = ImportDaily(connection, map, unmapped);
            if (TableExists(connection, "analytics_subject_daily_metrics"))
                subjects = ImportSubjects(connection, map, unmapped);
        }
        var snapshotDirectory = Path.Combine(root, "analytics", "weixin-channels");
        if (Directory.Exists(snapshotDirectory))
            foreach (var path in Directory.EnumerateFiles(snapshotDirectory, "*.json"))
                if (TryImportSnapshot(path, map, unmapped)) snapshots++;
        return new(daily, subjects, snapshots, unmapped.Order().ToArray(),
            $"历史数据导入完成：视频号日记录 {daily}，快手短剧记录 {subjects}，快照 {snapshots}，未映射账号 {unmapped.Count}。");
    }

    private int ImportDaily(SqliteConnection connection, IReadOnlyDictionary<string, string> map, HashSet<string> unmapped)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM analytics_daily_metrics";
        using var reader = command.ExecuteReader(); var count = 0;
        while (reader.Read())
        {
            var sourceId = Text(reader, "account_id");
            if (!map.TryGetValue("weixin:" + sourceId, out var accountId)) { unmapped.Add("weixin:" + sourceId); continue; }
            _target.UpsertDaily(new DailyAnalyticsRecord
            {
                Platform = PublishPlatform.WeixinChannel, AccountId = accountId,
                MetricDate = DateOnly.Parse(Text(reader, "metric_date")),
                CollectedAt = ParseTime(Text(reader, "collected_at")), Status = Status(Text(reader, "status")),
                ListedSeriesTotal = Int(reader, "listed_series_total"), MountedVideoTotal = Long(reader, "mounted_video_total"),
                SeriesViewsTotal = Long(reader, "series_views_total"),
                AdMonetizationIncomeFen = Fen(reader, "ad_monetization_income_yuan"),
                HeatingIncomeFen = Fen(reader, "heating_income_yuan"), MountedIncomeFen = Fen(reader, "mounted_income_yuan"),
                EstimatedViolationDeductionFen = Fen(reader, "estimated_violation_deduction_yuan"), Message = Text(reader, "message"),
            }); count++;
        }
        return count;
    }

    private int ImportSubjects(SqliteConnection connection, IReadOnlyDictionary<string, string> map, HashSet<string> unmapped)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT * FROM analytics_subject_daily_metrics";
        using var reader = command.ExecuteReader(); var records = new List<SubjectDailyAnalyticsRecord>();
        while (reader.Read())
        {
            var platformText = Text(reader, "platform_id");
            var sourceId = Text(reader, "account_id");
            if (!map.TryGetValue(platformText + ":" + sourceId, out var accountId)) { unmapped.Add(platformText + ":" + sourceId); continue; }
            records.Add(new SubjectDailyAnalyticsRecord
            {
                Platform = Platform(platformText), AccountId = accountId, SubjectType = Text(reader,"subject_type"),
                SubjectId = Text(reader,"subject_id"), SubjectName = Text(reader,"subject_name"),
                MetricDate = DateOnly.Parse(Text(reader,"metric_date")), CollectedAt = ParseTime(Text(reader,"collected_at")),
                Status = Status(Text(reader,"status")), Views=Long(reader,"views"), Likes=Long(reader,"likes"),
                Comments=Long(reader,"comments"), Favorites=Long(reader,"favorites"), AdIncomeFen=Fen(reader,"ad_income_yuan"),
                Message=Text(reader,"message"),
            });
        }
        _target.UpsertSubjects(records); return records.Count;
    }

    private bool TryImportSnapshot(string path, IReadOnlyDictionary<string, string> map, HashSet<string> unmapped)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path)); var root = document.RootElement;
            var sourceId = root.GetProperty("accountId").GetString() ?? string.Empty;
            if (!map.TryGetValue("weixin:" + sourceId, out var accountId)) { unmapped.Add("weixin:" + sourceId); return false; }
            var home = root.GetProperty("home"); var income = root.GetProperty("income");
            _target.UpsertSnapshot(new AccountAnalyticsSnapshot
            {
                Platform=PublishPlatform.WeixinChannel,AccountId=accountId,CollectedAt=ParseTime(root.GetProperty("collectedAt").GetString()),
                VideoTotal=JsonLong(home,"videoTotal"),FollowerTotal=JsonLong(home,"followerTotal"),YesterdayNetFollowers=JsonLong(home,"yesterdayNetFollowers"),
                YesterdayViews=JsonLong(home,"yesterdayViews"),YesterdayLikes=JsonLong(home,"yesterdayLikes"),YesterdayComments=JsonLong(home,"yesterdayComments"),
                ListedSeriesTotal=(int?)JsonLong(income,"listedSeriesTotal"),MountedVideoTotal=JsonLong(income,"mountedVideoTotal"),SeriesViewsTotal=JsonLong(income,"seriesViewsTotal"),
                AdMonetizationIncomeFen=JsonFen(income,"adMonetizationIncomeYuan"),YesterdayAdMonetizationIncomeFen=JsonFen(income,"yesterdayAdMonetizationIncomeYuan"),
                HeatingIncomeFen=JsonFen(income,"heatingIncomeYuan"),MountedIncomeFen=JsonFen(income,"mountedIncomeYuan"),EstimatedViolationDeductionFen=JsonFen(income,"estimatedViolationDeductionYuan"),
                RangeStart=JsonText(income,"rangeStart"),RangeEnd=JsonText(income,"rangeEnd"),
            }); return true;
        }
        catch { return false; }
    }

    private static Dictionary<string,string> BuildAccountMap(IReadOnlyList<SourceAccount> source, IReadOnlyList<AnalyticsAccount> targets)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in source)
        {
            var platform = SourcePlatform(item.Platform); var key = item.Platform + ":" + item.Id;
            var exact = targets.FirstOrDefault(target => target.Platform == platform && target.Id.Equals(item.Id,StringComparison.OrdinalIgnoreCase));
            var names = targets.Where(target => target.Platform == platform && Normalize(target.Name)==Normalize(item.Name)).ToArray();
            var target = exact ?? (names.Length == 1 ? names[0] : null);
            if (target is not null) result[key] = target.Id;
        }
        return result;
    }

    private static IReadOnlyList<SourceAccount> LoadSourceAccounts(string path)
    {
        try { using var doc=JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.GetProperty("accounts").EnumerateArray().Select(item=>new SourceAccount(JsonText(item,"id"),JsonText(item,"platform"),JsonText(item,"name"))).ToArray(); }
        catch { return []; }
    }
    private static bool TableExists(SqliteConnection c,string name){using var q=c.CreateCommand();q.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";q.Parameters.AddWithValue("$n",name);return q.ExecuteScalar()!=null;}
    private static string Text(SqliteDataReader r,string n)=>r[n] is DBNull?string.Empty:Convert.ToString(r[n])??string.Empty;
    private static long? Long(SqliteDataReader r,string n)=>r[n] is DBNull?null:Convert.ToInt64(Math.Round(Convert.ToDouble(r[n])));
    private static int? Int(SqliteDataReader r,string n)=>(int?)Long(r,n);
    private static long? Fen(SqliteDataReader r,string n)=>r[n] is DBNull?null:checked((long)Math.Round(Convert.ToDecimal(r[n])*100m));
    private static DateTimeOffset ParseTime(string? v)=>DateTimeOffset.TryParse(v,out var x)?x:DateTimeOffset.UtcNow;
    private static AnalyticsRecordStatus Status(string value)=>value switch{"success"=>AnalyticsRecordStatus.Success,"not-ready"=>AnalyticsRecordStatus.NotReady,"failed"=>AnalyticsRecordStatus.Failed,_=>AnalyticsRecordStatus.Missing};
    private static PublishPlatform Platform(string value)=>value=="kuaishou-personal"?PublishPlatform.KuaishouPersonalRevenue:PublishPlatform.WeixinChannel;
    private static PublishPlatform SourcePlatform(string value)=>Platform(value);
    private static string Normalize(string value)=>new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static long? JsonLong(JsonElement e,string n)=>e.TryGetProperty(n,out var v)&&v.TryGetDouble(out var x)?checked((long)Math.Round(x)):null;
    private static long? JsonFen(JsonElement e,string n)=>e.TryGetProperty(n,out var v)&&v.TryGetDecimal(out var x)?checked((long)Math.Round(x*100m)):null;
    private static string JsonText(JsonElement e,string n)=>e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??string.Empty:string.Empty;
    private sealed record SourceAccount(string Id,string Platform,string Name);
}
