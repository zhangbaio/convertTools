using Microsoft.Data.Sqlite;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouUploadRecord(
    string Edition,
    string AccountId,
    string Nickname,
    DateTimeOffset RecordedAt,
    string Status,
    string ProjectName,
    string OriginalTitle,
    string NewTitle,
    string Author,
    int EpisodeCount,
    string ProjectDirectory,
    string Error);

/// <summary>
/// 快手分账上传记录。一部剧一行，写在应用数据库里，导出时才生成 Excel。
/// </summary>
public static class KuaishouUploadRecordStore
{
    private static readonly object Gate = new();

    public static string DatabasePath(string? storeRoot) =>
        string.IsNullOrWhiteSpace(storeRoot)
            ? PlatformPublisherPaths.MainDatabasePath
            : Path.Combine(storeRoot, "app.db");

    public static void Upsert(KuaishouUploadRecord record, string? storeRoot = null)
    {
        if (string.IsNullOrWhiteSpace(record.Edition) || string.IsNullOrWhiteSpace(record.AccountId))
            return;
        var projectKey = ProjectKey(record.ProjectDirectory);
        if (string.IsNullOrWhiteSpace(projectKey))
            projectKey = record.ProjectName.Trim();
        if (string.IsNullOrWhiteSpace(projectKey))
            return;

        lock (Gate)
        {
            using var connection = Open(storeRoot);
            EnsureSchema(connection);
            Write(connection, record, projectKey);
        }
    }

    public static void FillEmptyNicknames(string edition, string accountId, string nickname, string? storeRoot = null)
    {
        if (string.IsNullOrWhiteSpace(edition) || string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(nickname))
            return;
        lock (Gate)
        {
            using var connection = Open(storeRoot);
            EnsureSchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE kuaishou_upload_records
                SET nickname=$nickname
                WHERE edition=$edition AND account_id=$account AND nickname=''
                """;
            command.Parameters.AddWithValue("$nickname", nickname.Trim());
            command.Parameters.AddWithValue("$edition", edition);
            command.Parameters.AddWithValue("$account", accountId.Trim());
            command.ExecuteNonQuery();
        }
    }

    public static IReadOnlyList<KuaishouUploadRecord> List(string edition, string? storeRoot = null)
    {
        lock (Gate)
        {
            using var connection = Open(storeRoot);
            EnsureSchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT account_id,nickname,recorded_at,status,project_name,original_title,new_title,
                       author,episode_count,project_directory,error
                FROM kuaishou_upload_records
                WHERE edition=$edition
                ORDER BY recorded_at, project_name
                """;
            command.Parameters.AddWithValue("$edition", edition);
            return ReadAll(command, edition);
        }
    }

    public static IReadOnlyList<KuaishouUploadRecord> ListSuccess(
        string edition,
        string accountId,
        DateTime day,
        string? storeRoot = null)
    {
        lock (Gate)
        {
            using var connection = Open(storeRoot);
            EnsureSchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT account_id,nickname,recorded_at,status,project_name,original_title,new_title,
                       author,episode_count,project_directory,error
                FROM kuaishou_upload_records
                WHERE edition=$edition AND account_id=$account AND status='成功' AND record_date=$day
                ORDER BY recorded_at, project_name
                """;
            command.Parameters.AddWithValue("$edition", edition);
            command.Parameters.AddWithValue("$account", accountId.Trim());
            command.Parameters.AddWithValue("$day", day.ToString("yyyy-MM-dd"));
            return ReadAll(command, edition);
        }
    }

    public static bool IsMigrated(string? storeRoot)
    {
        lock (Gate)
        {
            using var connection = Open(storeRoot);
            EnsureSchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM kuaishou_record_migrations WHERE migration_key='json-ledger-v1'";
            return command.ExecuteScalar() is not null;
        }
    }

    public static void MarkMigrated(string? storeRoot)
    {
        lock (Gate)
        {
            using var connection = Open(storeRoot);
            EnsureSchema(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO kuaishou_record_migrations(migration_key,completed_at)
                VALUES('json-ledger-v1',$at)
                """;
            command.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public static string ProjectKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        var full = path.Trim();
        try
        {
            full = Path.GetFullPath(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var timeDash = name.LastIndexOf('-');
        if (timeDash <= 0 || name.Length - timeDash != 7)
            return name;
        var time = name[(timeDash + 1)..];
        if (time.Length != 6 || !time.All(char.IsDigit))
            return name;
        var dateDash = name.LastIndexOf('-', timeDash - 1);
        if (dateDash < 0 || timeDash - dateDash != 9)
            return name;
        var date = name[(dateDash + 1)..timeDash];
        return date.Length == 8 && date.All(char.IsDigit) ? name[..dateDash] : name;
    }

    private static void Write(SqliteConnection connection, KuaishouUploadRecord record, string projectKey)
    {
        var local = record.RecordedAt.ToLocalTime();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO kuaishou_upload_records(
              edition,account_id,project_key,nickname,recorded_at,record_date,status,
              project_name,original_title,new_title,author,episode_count,project_directory,error)
            VALUES($edition,$account,$key,$nickname,$recorded,$day,$status,
              $project,$original,$newTitle,$author,$episodes,$directory,$error)
            ON CONFLICT(edition,account_id,project_key) DO UPDATE SET
              nickname=CASE WHEN excluded.nickname<>'' THEN excluded.nickname ELSE nickname END,
              recorded_at=excluded.recorded_at,
              record_date=excluded.record_date,
              status=excluded.status,
              project_name=CASE WHEN excluded.project_name<>'' THEN excluded.project_name ELSE project_name END,
              original_title=CASE WHEN excluded.original_title<>'' THEN excluded.original_title ELSE original_title END,
              new_title=CASE WHEN excluded.new_title<>'' THEN excluded.new_title ELSE new_title END,
              author=CASE WHEN excluded.author<>'' THEN excluded.author ELSE author END,
              episode_count=CASE WHEN excluded.episode_count>0 THEN excluded.episode_count ELSE episode_count END,
              project_directory=CASE WHEN excluded.project_directory<>'' THEN excluded.project_directory ELSE project_directory END,
              error=excluded.error
            """;
        command.Parameters.AddWithValue("$edition", record.Edition);
        command.Parameters.AddWithValue("$account", record.AccountId.Trim());
        command.Parameters.AddWithValue("$key", projectKey);
        command.Parameters.AddWithValue("$nickname", record.Nickname.Trim());
        command.Parameters.AddWithValue("$recorded", local.ToString("yyyy-MM-dd HH:mm:ss"));
        command.Parameters.AddWithValue("$day", local.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(record.Status) ? "失败" : record.Status.Trim());
        command.Parameters.AddWithValue("$project", record.ProjectName.Trim());
        command.Parameters.AddWithValue("$original", record.OriginalTitle.Trim());
        command.Parameters.AddWithValue("$newTitle", record.NewTitle.Trim());
        command.Parameters.AddWithValue("$author", record.Author.Trim());
        command.Parameters.AddWithValue("$episodes", Math.Max(0, record.EpisodeCount));
        command.Parameters.AddWithValue("$directory", record.ProjectDirectory.Trim());
        command.Parameters.AddWithValue("$error", record.Error.Trim());
        command.ExecuteNonQuery();
    }

    private static List<KuaishouUploadRecord> ReadAll(SqliteCommand command, string edition)
    {
        using var reader = command.ExecuteReader();
        var records = new List<KuaishouUploadRecord>();
        while (reader.Read())
        {
            var recorded = reader.GetString(2);
            var at = DateTime.TryParseExact(
                recorded,
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var wall)
                ? new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Local))
                : DateTimeOffset.Now;
            records.Add(new KuaishouUploadRecord(
                edition,
                reader.GetString(0),
                reader.GetString(1),
                at,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10)));
        }
        return records;
    }

    private static SqliteConnection Open(string? storeRoot)
    {
        var path = DatabasePath(storeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS kuaishou_upload_records(
              edition TEXT NOT NULL,
              account_id TEXT NOT NULL,
              project_key TEXT NOT NULL,
              nickname TEXT NOT NULL DEFAULT '',
              recorded_at TEXT NOT NULL,
              record_date TEXT NOT NULL,
              status TEXT NOT NULL,
              project_name TEXT NOT NULL DEFAULT '',
              original_title TEXT NOT NULL DEFAULT '',
              new_title TEXT NOT NULL DEFAULT '',
              author TEXT NOT NULL DEFAULT '',
              episode_count INTEGER NOT NULL DEFAULT 0,
              project_directory TEXT NOT NULL DEFAULT '',
              error TEXT NOT NULL DEFAULT '',
              PRIMARY KEY(edition,account_id,project_key));
            CREATE INDEX IF NOT EXISTS idx_kuaishou_upload_records_day
              ON kuaishou_upload_records(edition,account_id,record_date,status);
            CREATE TABLE IF NOT EXISTS kuaishou_record_migrations(
              migration_key TEXT PRIMARY KEY,
              completed_at TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }
}
