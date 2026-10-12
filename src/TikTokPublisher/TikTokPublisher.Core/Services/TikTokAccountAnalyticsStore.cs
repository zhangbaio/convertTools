using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed class TikTokAccountAnalyticsStore
{
    private readonly string _databasePath;

    public TikTokAccountAnalyticsStore(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath) ? AppPaths.AnalyticsDatabaseFile : databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var conn = Open();
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS account_daily_stats (
                account_id TEXT NOT NULL,
                event_date TEXT NOT NULL,
                total_views INTEGER NOT NULL,
                valid_views INTEGER NOT NULL,
                fetched_at TEXT NOT NULL,
                PRIMARY KEY (account_id, event_date)
            );
            CREATE TABLE IF NOT EXISTS series_snapshots (
                account_id TEXT NOT NULL,
                series_id TEXT NOT NULL,
                snapshot_date TEXT NOT NULL,
                title TEXT NOT NULL,
                published_on TEXT NOT NULL,
                valid_views INTEGER NOT NULL,
                avg_watch_seconds INTEGER NOT NULL,
                favorites INTEGER NOT NULL,
                comments INTEGER NOT NULL,
                likes INTEGER NOT NULL,
                fetched_at TEXT NOT NULL,
                PRIMARY KEY (account_id, series_id, snapshot_date)
            );
            CREATE TABLE IF NOT EXISTS analytics_meta (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS account_revenue (
                account_id TEXT NOT NULL PRIMARY KEY,
                total_amount TEXT NOT NULL,
                pending_amount TEXT NOT NULL,
                paying_amount TEXT NOT NULL,
                completed_amount TEXT NOT NULL,
                currency TEXT NOT NULL,
                company_name TEXT NOT NULL,
                fetched_at TEXT NOT NULL
            );
            """);
    }

    public bool HasSeriesSnapshot(string accountId, DateOnly date)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM series_snapshots
            WHERE account_id = $id AND snapshot_date = $date
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
        return cmd.ExecuteScalar() is not null;
    }

    public DateOnly? GetMarkerDate(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM analytics_meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var value = cmd.ExecuteScalar() as string;
        return DateOnly.TryParse(value, out var date) ? date : null;
    }

    public void SetMarkerDate(string key, DateOnly date)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO analytics_meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", date.ToString("yyyy-MM-dd"));
        cmd.ExecuteNonQuery();
    }

    public DateOnly? LatestDailyDate(string accountId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(event_date) FROM account_daily_stats WHERE account_id = $id";
        cmd.Parameters.AddWithValue("$id", accountId);
        var value = cmd.ExecuteScalar() as string;
        return DateOnly.TryParse(value, out var date) ? date : null;
    }

    public void UpsertDaily(string accountId, IEnumerable<TikTokAccountDailyStat> rows)
    {
        var now = DateTimeOffset.Now.ToString("O");
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var row in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO account_daily_stats (account_id, event_date, total_views, valid_views, fetched_at)
                VALUES ($id, $date, $total, $valid, $at)
                ON CONFLICT(account_id, event_date) DO UPDATE SET
                    total_views = excluded.total_views,
                    valid_views = excluded.valid_views,
                    fetched_at = excluded.fetched_at
                """;
            cmd.Parameters.AddWithValue("$id", accountId);
            cmd.Parameters.AddWithValue("$date", row.Date.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$total", row.TotalViews);
            cmd.Parameters.AddWithValue("$valid", row.ValidViews);
            cmd.Parameters.AddWithValue("$at", now);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<TikTokAccountDailyStat> ListDaily(string accountId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_date, total_views, valid_views
            FROM account_daily_stats
            WHERE account_id = $id
            ORDER BY event_date
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<TikTokAccountDailyStat>();
        while (reader.Read())
        {
            rows.Add(new TikTokAccountDailyStat(
                DateOnly.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }

        return rows;
    }

    public void UpsertSeries(string accountId, IEnumerable<TikTokSeriesSnapshot> rows)
    {
        var now = DateTimeOffset.Now.ToString("O");
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var row in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO series_snapshots (
                    account_id, series_id, snapshot_date, title, published_on,
                    valid_views, avg_watch_seconds, favorites, comments, likes, fetched_at)
                VALUES ($id, $series, $date, $title, $published, $views, $watch, $fav, $comments, $likes, $at)
                ON CONFLICT(account_id, series_id, snapshot_date) DO UPDATE SET
                    title = excluded.title,
                    published_on = excluded.published_on,
                    valid_views = excluded.valid_views,
                    avg_watch_seconds = excluded.avg_watch_seconds,
                    favorites = excluded.favorites,
                    comments = excluded.comments,
                    likes = excluded.likes,
                    fetched_at = excluded.fetched_at
                """;
            cmd.Parameters.AddWithValue("$id", accountId);
            cmd.Parameters.AddWithValue("$series", row.SeriesId);
            cmd.Parameters.AddWithValue("$date", row.SnapshotDate.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$title", row.Title);
            cmd.Parameters.AddWithValue("$published", row.PublishedOn?.ToString("yyyy-MM-dd") ?? "");
            cmd.Parameters.AddWithValue("$views", row.ValidViews);
            cmd.Parameters.AddWithValue("$watch", row.AverageWatchSeconds);
            cmd.Parameters.AddWithValue("$fav", row.Favorites);
            cmd.Parameters.AddWithValue("$comments", row.Comments);
            cmd.Parameters.AddWithValue("$likes", row.Likes);
            cmd.Parameters.AddWithValue("$at", now);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<(TikTokSeriesSnapshot Latest, DateOnly? PreviousDate, long? PreviousViews)> ListLatestSeriesWithPrevious(string accountId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.series_id, s.title, s.snapshot_date, s.published_on, s.valid_views, s.avg_watch_seconds,
                   s.favorites, s.comments, s.likes, p.snapshot_date, p.valid_views
            FROM series_snapshots s
            LEFT JOIN series_snapshots p
              ON p.account_id = s.account_id
             AND p.series_id = s.series_id
             AND p.snapshot_date = (
                SELECT MAX(snapshot_date) FROM series_snapshots
                WHERE account_id = s.account_id AND series_id = s.series_id AND snapshot_date < s.snapshot_date)
            WHERE s.account_id = $id
              AND s.snapshot_date = (SELECT MAX(snapshot_date) FROM series_snapshots WHERE account_id = $id)
            ORDER BY s.valid_views DESC
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(TikTokSeriesSnapshot, DateOnly?, long?)>();
        while (reader.Read())
        {
            var publishedText = reader.GetString(3);
            var latest = new TikTokSeriesSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                DateOnly.Parse(reader.GetString(2)),
                DateOnly.TryParse(publishedText, out var published) ? published : null,
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8));
            DateOnly? previousDate = reader.IsDBNull(9) ? null : DateOnly.Parse(reader.GetString(9));
            long? previousViews = reader.IsDBNull(10) ? null : reader.GetInt64(10);
            rows.Add((latest, previousDate, previousViews));
        }

        return rows;
    }

    public IReadOnlyList<(TikTokSeriesSnapshot Latest, DateOnly? ListedOn)> ListLatestSeriesListing(string accountId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.series_id, s.title, s.snapshot_date, s.published_on, s.valid_views, s.avg_watch_seconds,
                   s.favorites, s.comments, s.likes,
                   (SELECT MIN(h.snapshot_date) FROM series_snapshots h
                     WHERE h.account_id = s.account_id AND h.series_id = s.series_id),
                   (SELECT MIN(snapshot_date) FROM series_snapshots WHERE account_id = $id)
            FROM series_snapshots s
            WHERE s.account_id = $id
              AND s.snapshot_date = (SELECT MAX(snapshot_date) FROM series_snapshots WHERE account_id = $id)
            ORDER BY s.valid_views DESC
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(TikTokSeriesSnapshot, DateOnly?)>();
        while (reader.Read())
        {
            var publishedText = reader.GetString(3);
            var latest = new TikTokSeriesSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                DateOnly.Parse(reader.GetString(2)),
                DateOnly.TryParse(publishedText, out var published) ? published : null,
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8));
            var firstSeen = DateOnly.Parse(reader.GetString(9));
            var catalogStart = DateOnly.Parse(reader.GetString(10));
            rows.Add((latest, firstSeen == catalogStart ? null : firstSeen));
        }

        return rows;
    }

    public (int Listed, int AddedToday) GetListingCounts(string accountId, DateOnly today)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH bounds AS (
                SELECT MIN(snapshot_date) AS first_day, MAX(snapshot_date) AS latest_day
                FROM series_snapshots WHERE account_id = $id
            ),
            seen AS (
                SELECT series_id, MIN(snapshot_date) AS first_seen
                FROM series_snapshots WHERE account_id = $id
                GROUP BY series_id
            )
            SELECT
                (SELECT COUNT(*) FROM series_snapshots s, bounds b
                  WHERE s.account_id = $id AND b.latest_day IS NOT NULL AND s.snapshot_date = b.latest_day),
                (SELECT COUNT(*) FROM seen, bounds b
                  WHERE seen.first_seen = $today
                    AND b.first_day IS NOT NULL
                    AND b.first_day <> $today
                    AND EXISTS (
                        SELECT 1 FROM series_snapshots s
                        WHERE s.account_id = $id AND s.series_id = seen.series_id AND s.snapshot_date = b.latest_day))
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd"));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, 0);
        return (Convert.ToInt32(reader.GetInt64(0)), Convert.ToInt32(reader.GetInt64(1)));
    }

    public IReadOnlyList<TikTokSeriesSnapshot> ListLatestSeries(string accountId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT series_id, title, snapshot_date, published_on, valid_views, avg_watch_seconds, favorites, comments, likes
            FROM series_snapshots
            WHERE account_id = $id
              AND snapshot_date = (SELECT MAX(snapshot_date) FROM series_snapshots WHERE account_id = $id)
            ORDER BY valid_views DESC
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        return ReadSeries(cmd);
    }

    public IReadOnlyList<TikTokSeriesSnapshot> ListSeriesHistory(string accountId, string seriesId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT series_id, title, snapshot_date, published_on, valid_views, avg_watch_seconds, favorites, comments, likes
            FROM series_snapshots
            WHERE account_id = $id AND series_id = $series
            ORDER BY snapshot_date
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.Parameters.AddWithValue("$series", seriesId);
        return ReadSeries(cmd);
    }

    public void UpsertRevenue(string accountId, TikTokAccountRevenue revenue)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO account_revenue (
                account_id, total_amount, pending_amount, paying_amount, completed_amount, currency, company_name, fetched_at)
            VALUES ($id, $total, $pending, $paying, $completed, $currency, $company, $at)
            ON CONFLICT(account_id) DO UPDATE SET
                total_amount = excluded.total_amount,
                pending_amount = excluded.pending_amount,
                paying_amount = excluded.paying_amount,
                completed_amount = excluded.completed_amount,
                currency = excluded.currency,
                company_name = excluded.company_name,
                fetched_at = excluded.fetched_at
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        cmd.Parameters.AddWithValue("$total", revenue.TotalAmount);
        cmd.Parameters.AddWithValue("$pending", revenue.PendingAmount);
        cmd.Parameters.AddWithValue("$paying", revenue.PayingAmount);
        cmd.Parameters.AddWithValue("$completed", revenue.CompletedAmount);
        cmd.Parameters.AddWithValue("$currency", revenue.Currency);
        cmd.Parameters.AddWithValue("$company", revenue.CompanyName);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public TikTokAccountRevenue? GetRevenue(string accountId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT total_amount, pending_amount, paying_amount, completed_amount, currency, company_name
            FROM account_revenue WHERE account_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", accountId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new TikTokAccountRevenue(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    private static List<TikTokSeriesSnapshot> ReadSeries(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var rows = new List<TikTokSeriesSnapshot>();
        while (reader.Read())
        {
            var publishedText = reader.GetString(3);
            rows.Add(new TikTokSeriesSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                DateOnly.Parse(reader.GetString(2)),
                DateOnly.TryParse(publishedText, out var published) ? published : null,
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8)));
        }

        return rows;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_databasePath};Default Timeout=30");
        conn.Open();
        return conn;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
