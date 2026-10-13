using Microsoft.Data.Sqlite;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Analytics.Storage;

public sealed class AnalyticsRepository
{
    private readonly string _connectionString;
    private readonly object _writeGate = new();

    public AnalyticsRepository(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS analytics_account_snapshots(
              platform INTEGER NOT NULL, account_id TEXT NOT NULL, collected_at TEXT NOT NULL,
              video_total INTEGER, follower_total INTEGER, yesterday_net_followers INTEGER,
              yesterday_views INTEGER, yesterday_likes INTEGER, yesterday_comments INTEGER,
              listed_series_total INTEGER, mounted_video_total INTEGER, series_views_total INTEGER,
              ad_income_fen INTEGER, yesterday_ad_income_fen INTEGER, heating_income_fen INTEGER,
              mounted_income_fen INTEGER, violation_deduction_fen INTEGER, range_start TEXT, range_end TEXT,
              PRIMARY KEY(platform, account_id));
            CREATE TABLE IF NOT EXISTS analytics_daily_metrics(
              platform INTEGER NOT NULL, account_id TEXT NOT NULL, metric_date TEXT NOT NULL,
              collected_at TEXT NOT NULL, status INTEGER NOT NULL, listed_series_total INTEGER,
              mounted_video_total INTEGER, series_views_total INTEGER, ad_income_fen INTEGER,
              heating_income_fen INTEGER, mounted_income_fen INTEGER, violation_deduction_fen INTEGER,
              message TEXT NOT NULL DEFAULT '', PRIMARY KEY(platform,account_id,metric_date));
            CREATE TABLE IF NOT EXISTS analytics_subject_daily_metrics(
              platform INTEGER NOT NULL, account_id TEXT NOT NULL, subject_type TEXT NOT NULL,
              subject_id TEXT NOT NULL, subject_name TEXT NOT NULL, metric_date TEXT NOT NULL,
              collected_at TEXT NOT NULL, status INTEGER NOT NULL, views INTEGER, likes INTEGER,
              comments INTEGER, favorites INTEGER, ad_income_fen INTEGER, message TEXT NOT NULL DEFAULT '',
              PRIMARY KEY(platform,account_id,subject_type,subject_id,metric_date));
            CREATE TABLE IF NOT EXISTS analytics_publish_activities(
              activity_id TEXT PRIMARY KEY, platform INTEGER NOT NULL, account_id TEXT NOT NULL,
              account_name TEXT NOT NULL, project_name TEXT NOT NULL, occurred_at TEXT NOT NULL,
              status TEXT NOT NULL, item_count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS analytics_collection_runs(
              id TEXT PRIMARY KEY, platform INTEGER NOT NULL, account_id TEXT NOT NULL,
              started_at TEXT NOT NULL, finished_at TEXT, status TEXT NOT NULL, message TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS analytics_account_mappings(
              source_platform TEXT NOT NULL, source_account_id TEXT NOT NULL, target_account_id TEXT NOT NULL,
              matched_by TEXT NOT NULL, PRIMARY KEY(source_platform,source_account_id));
            CREATE TABLE IF NOT EXISTS analytics_runtime_state(
              key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_analytics_daily_account_date ON analytics_daily_metrics(account_id,metric_date);
            CREATE INDEX IF NOT EXISTS idx_analytics_subject_account_date ON analytics_subject_daily_metrics(account_id,metric_date);
            CREATE INDEX IF NOT EXISTS idx_analytics_activity_date ON analytics_publish_activities(occurred_at);
            """;
        command.ExecuteNonQuery();
    }

    public void UpsertSnapshot(AccountAnalyticsSnapshot value)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO analytics_account_snapshots VALUES(
                $p,$a,$at,$v,$f,$nf,$yv,$yl,$yc,$ls,$mv,$sv,$ai,$yai,$hi,$mi,$vd,$rs,$re)
                ON CONFLICT(platform,account_id) DO UPDATE SET
                collected_at=excluded.collected_at,video_total=excluded.video_total,follower_total=excluded.follower_total,
                yesterday_net_followers=excluded.yesterday_net_followers,yesterday_views=excluded.yesterday_views,
                yesterday_likes=excluded.yesterday_likes,yesterday_comments=excluded.yesterday_comments,
                listed_series_total=excluded.listed_series_total,mounted_video_total=excluded.mounted_video_total,
                series_views_total=excluded.series_views_total,ad_income_fen=excluded.ad_income_fen,
                yesterday_ad_income_fen=excluded.yesterday_ad_income_fen,heating_income_fen=excluded.heating_income_fen,
                mounted_income_fen=excluded.mounted_income_fen,violation_deduction_fen=excluded.violation_deduction_fen,
                range_start=excluded.range_start,range_end=excluded.range_end;
                """;
            BindSnapshot(command, value);
            command.ExecuteNonQuery();
        }
    }

    public void UpsertDaily(DailyAnalyticsRecord value)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO analytics_daily_metrics VALUES($p,$a,$d,$at,$s,$ls,$mv,$sv,$ai,$hi,$mi,$vd,$msg)
                ON CONFLICT(platform,account_id,metric_date) DO UPDATE SET
                collected_at=excluded.collected_at,status=excluded.status,listed_series_total=excluded.listed_series_total,
                mounted_video_total=excluded.mounted_video_total,series_views_total=excluded.series_views_total,
                ad_income_fen=excluded.ad_income_fen,heating_income_fen=excluded.heating_income_fen,
                mounted_income_fen=excluded.mounted_income_fen,violation_deduction_fen=excluded.violation_deduction_fen,
                message=excluded.message
                WHERE analytics_daily_metrics.status != 0 OR excluded.status = 0;
                """;
            Add(command, "$p", (int)value.Platform); Add(command, "$a", value.AccountId); Add(command, "$d", Date(value.MetricDate));
            Add(command, "$at", value.CollectedAt.ToString("O")); Add(command, "$s", (int)value.Status);
            Add(command, "$ls", value.ListedSeriesTotal); Add(command, "$mv", value.MountedVideoTotal); Add(command, "$sv", value.SeriesViewsTotal);
            Add(command, "$ai", value.AdMonetizationIncomeFen); Add(command, "$hi", value.HeatingIncomeFen);
            Add(command, "$mi", value.MountedIncomeFen); Add(command, "$vd", value.EstimatedViolationDeductionFen); Add(command, "$msg", value.Message);
            command.ExecuteNonQuery();
        }
    }

    public void UpsertSubjects(IEnumerable<SubjectDailyAnalyticsRecord> records)
    {
        lock (_writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var value in records)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO analytics_subject_daily_metrics VALUES($p,$a,$t,$id,$n,$d,$at,$s,$v,$l,$c,$f,$i,$m)
                    ON CONFLICT(platform,account_id,subject_type,subject_id,metric_date) DO UPDATE SET
                    subject_name=excluded.subject_name,collected_at=excluded.collected_at,status=excluded.status,
                    views=excluded.views,likes=excluded.likes,comments=excluded.comments,favorites=excluded.favorites,
                    ad_income_fen=excluded.ad_income_fen,message=excluded.message
                    WHERE analytics_subject_daily_metrics.status != 0 OR excluded.status = 0;
                    """;
                Add(command, "$p", (int)value.Platform); Add(command, "$a", value.AccountId); Add(command, "$t", value.SubjectType);
                Add(command, "$id", value.SubjectId); Add(command, "$n", value.SubjectName); Add(command, "$d", Date(value.MetricDate));
                Add(command, "$at", value.CollectedAt.ToString("O")); Add(command, "$s", (int)value.Status); Add(command, "$v", value.Views);
                Add(command, "$l", value.Likes); Add(command, "$c", value.Comments); Add(command, "$f", value.Favorites);
                Add(command, "$i", value.AdIncomeFen); Add(command, "$m", value.Message); command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public void UpsertActivity(PublishActivityRecord value)
    {
        lock (_writeGate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO analytics_publish_activities VALUES($id,$p,$a,$an,$pn,$at,$s,$c)
                ON CONFLICT(activity_id) DO UPDATE SET occurred_at=excluded.occurred_at,status=excluded.status,item_count=excluded.item_count;
                """;
            Add(command, "$id", value.ActivityId); Add(command, "$p", (int)value.Platform); Add(command, "$a", value.AccountId);
            Add(command, "$an", value.AccountName); Add(command, "$pn", value.ProjectName); Add(command, "$at", value.OccurredAt.ToString("O"));
            Add(command, "$s", value.Status); Add(command, "$c", value.ItemCount); command.ExecuteNonQuery();
        }
    }

    public bool HasActivityPrefix(string prefix) { using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT 1 FROM analytics_publish_activities WHERE activity_id LIKE $p LIMIT 1";Add(q,"$p",prefix+"%");return q.ExecuteScalar()!=null; }
    public void DeleteActivity(string id) { lock(_writeGate){using var c=Open();using var q=c.CreateCommand();q.CommandText="DELETE FROM analytics_publish_activities WHERE activity_id=$id";Add(q,"$id",id);q.ExecuteNonQuery();} }

    public IReadOnlyList<AccountAnalyticsSnapshot> ListSnapshots() { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM analytics_account_snapshots"; using var r = q.ExecuteReader(); var list = new List<AccountAnalyticsSnapshot>(); while (r.Read()) list.Add(ReadSnapshot(r)); return list; }
    public IReadOnlyList<DailyAnalyticsRecord> ListDaily(DateOnly from, DateOnly to) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM analytics_daily_metrics WHERE metric_date >= $f AND metric_date <= $t ORDER BY metric_date"; Add(q, "$f", Date(from)); Add(q, "$t", Date(to)); using var r = q.ExecuteReader(); var list = new List<DailyAnalyticsRecord>(); while (r.Read()) list.Add(ReadDaily(r)); return list; }
    public IReadOnlyList<SubjectDailyAnalyticsRecord> ListSubjects(DateOnly from, DateOnly to) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM analytics_subject_daily_metrics WHERE metric_date >= $f AND metric_date <= $t ORDER BY metric_date,subject_name"; Add(q, "$f", Date(from)); Add(q, "$t", Date(to)); using var r = q.ExecuteReader(); var list = new List<SubjectDailyAnalyticsRecord>(); while (r.Read()) list.Add(ReadSubject(r)); return list; }
    public IReadOnlyList<PublishActivityRecord> ListActivities(DateOnly from, DateOnly to) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT * FROM analytics_publish_activities WHERE substr(occurred_at,1,10) >= $f AND substr(occurred_at,1,10) <= $t"; Add(q, "$f", Date(from)); Add(q, "$t", Date(to)); using var r = q.ExecuteReader(); var list = new List<PublishActivityRecord>(); while (r.Read()) list.Add(new() { ActivityId=r.GetString(0),Platform=(PublishPlatform)r.GetInt32(1),AccountId=r.GetString(2),AccountName=r.GetString(3),ProjectName=r.GetString(4),OccurredAt=DateTimeOffset.Parse(r.GetString(5)),Status=r.GetString(6),ItemCount=r.GetInt32(7)}); return list; }

    public string? GetState(string key) { using var c = Open(); using var q = c.CreateCommand(); q.CommandText = "SELECT value FROM analytics_runtime_state WHERE key=$k"; Add(q,"$k",key); return q.ExecuteScalar() as string; }
    public void SetState(string key, string value) { lock(_writeGate) { using var c=Open(); using var q=c.CreateCommand(); q.CommandText="INSERT INTO analytics_runtime_state VALUES($k,$v,$at) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at"; Add(q,"$k",key);Add(q,"$v",value);Add(q,"$at",DateTimeOffset.UtcNow.ToString("O"));q.ExecuteNonQuery(); } }

    private static void BindSnapshot(SqliteCommand q, AccountAnalyticsSnapshot v) { Add(q,"$p",(int)v.Platform);Add(q,"$a",v.AccountId);Add(q,"$at",v.CollectedAt.ToString("O"));Add(q,"$v",v.VideoTotal);Add(q,"$f",v.FollowerTotal);Add(q,"$nf",v.YesterdayNetFollowers);Add(q,"$yv",v.YesterdayViews);Add(q,"$yl",v.YesterdayLikes);Add(q,"$yc",v.YesterdayComments);Add(q,"$ls",v.ListedSeriesTotal);Add(q,"$mv",v.MountedVideoTotal);Add(q,"$sv",v.SeriesViewsTotal);Add(q,"$ai",v.AdMonetizationIncomeFen);Add(q,"$yai",v.YesterdayAdMonetizationIncomeFen);Add(q,"$hi",v.HeatingIncomeFen);Add(q,"$mi",v.MountedIncomeFen);Add(q,"$vd",v.EstimatedViolationDeductionFen);Add(q,"$rs",v.RangeStart);Add(q,"$re",v.RangeEnd); }
    private static AccountAnalyticsSnapshot ReadSnapshot(SqliteDataReader r) => new() { Platform=(PublishPlatform)r.GetInt32(0),AccountId=r.GetString(1),CollectedAt=DateTimeOffset.Parse(r.GetString(2)),VideoTotal=Long(r,3),FollowerTotal=Long(r,4),YesterdayNetFollowers=Long(r,5),YesterdayViews=Long(r,6),YesterdayLikes=Long(r,7),YesterdayComments=Long(r,8),ListedSeriesTotal=Int(r,9),MountedVideoTotal=Long(r,10),SeriesViewsTotal=Long(r,11),AdMonetizationIncomeFen=Long(r,12),YesterdayAdMonetizationIncomeFen=Long(r,13),HeatingIncomeFen=Long(r,14),MountedIncomeFen=Long(r,15),EstimatedViolationDeductionFen=Long(r,16),RangeStart=Text(r,17),RangeEnd=Text(r,18) };
    private static DailyAnalyticsRecord ReadDaily(SqliteDataReader r) => new() { Platform=(PublishPlatform)r.GetInt32(0),AccountId=r.GetString(1),MetricDate=DateOnly.Parse(r.GetString(2)),CollectedAt=DateTimeOffset.Parse(r.GetString(3)),Status=(AnalyticsRecordStatus)r.GetInt32(4),ListedSeriesTotal=Int(r,5),MountedVideoTotal=Long(r,6),SeriesViewsTotal=Long(r,7),AdMonetizationIncomeFen=Long(r,8),HeatingIncomeFen=Long(r,9),MountedIncomeFen=Long(r,10),EstimatedViolationDeductionFen=Long(r,11),Message=Text(r,12) };
    private static SubjectDailyAnalyticsRecord ReadSubject(SqliteDataReader r) => new() { Platform=(PublishPlatform)r.GetInt32(0),AccountId=r.GetString(1),SubjectType=r.GetString(2),SubjectId=r.GetString(3),SubjectName=r.GetString(4),MetricDate=DateOnly.Parse(r.GetString(5)),CollectedAt=DateTimeOffset.Parse(r.GetString(6)),Status=(AnalyticsRecordStatus)r.GetInt32(7),Views=Long(r,8),Likes=Long(r,9),Comments=Long(r,10),Favorites=Long(r,11),AdIncomeFen=Long(r,12),Message=Text(r,13) };
    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd");
    private static long? Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static string Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? string.Empty : r.GetString(i);
}
