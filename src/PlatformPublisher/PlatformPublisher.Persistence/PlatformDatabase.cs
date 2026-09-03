using Microsoft.Data.Sqlite;

namespace PlatformPublisher.Persistence;

public sealed class PlatformDatabase
{
    private readonly string _connectionString;
    public SemaphoreSlim WriteGate { get; } = new(1, 1);

    public PlatformDatabase(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            DefaultTimeout = 30,
            Pooling = false,
        }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open(bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString)
        {
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = readOnly
            ? "PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;"
            : "PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
        return connection;
    }
}
