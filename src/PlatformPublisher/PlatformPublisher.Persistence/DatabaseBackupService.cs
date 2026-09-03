namespace PlatformPublisher.Persistence;

public sealed class DatabaseBackupService
{
    public string Backup(PlatformDatabase source, string backupDirectory)
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(source);
        Directory.CreateDirectory(backupDirectory);
        var destination = Path.Combine(Path.GetFullPath(backupDirectory),
            $"{Path.GetFileNameWithoutExtension(source.Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        using var sourceConnection = source.Open();
        using var destinationConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destination};Pooling=False");
        destinationConnection.Open();
        sourceConnection.BackupDatabase(destinationConnection);
        return destination;
    }

    public string IntegrityCheck(PlatformDatabase database)
    {
        using var connection = database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return command.ExecuteScalar()?.ToString() ?? "unknown";
    }
}
