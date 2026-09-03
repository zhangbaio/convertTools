namespace PlatformPublisher.Persistence;

public interface ISecureBlobStore
{
    bool Contains(string key);
    byte[]? Load(string key, string identity);
    void Save(string key, string identity, byte[] encryptedBlob, string encryptionKind = "dpapi-current-user");
    void Delete(string key);
}

public sealed class SecureBlobStore : ISecureBlobStore
{
    private readonly PlatformDatabase _database;
    public SecureBlobStore(PlatformDatabase database) => _database = database;

    public bool Contains(string key)
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(_database);
        using var connection = _database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM secure_settings WHERE key=$key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is not null;
    }

    public byte[]? Load(string key, string identity)
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(_database);
        using var connection = _database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT encrypted_blob FROM secure_settings WHERE key=$key AND identity=$identity LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$identity", identity);
        return command.ExecuteScalar() as byte[];
    }

    public void Save(string key, string identity, byte[] encryptedBlob, string encryptionKind = "dpapi-current-user")
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(_database);
        _database.WriteGate.Wait();
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO secure_settings(key,identity,encrypted_blob,encryption_kind,updated_at)
                VALUES($key,$identity,$blob,$kind,$at)
                ON CONFLICT(key) DO UPDATE SET identity=excluded.identity,encrypted_blob=excluded.encrypted_blob,
                  encryption_kind=excluded.encryption_kind,updated_at=excluded.updated_at
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$identity", identity);
            command.Parameters.AddWithValue("$blob", encryptedBlob);
            command.Parameters.AddWithValue("$kind", encryptionKind);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally { _database.WriteGate.Release(); }
    }

    public void Delete(string key)
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(_database);
        _database.WriteGate.Wait();
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM secure_settings WHERE key=$key";
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
        finally { _database.WriteGate.Release(); }
    }
}
