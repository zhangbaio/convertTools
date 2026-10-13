using System.Text.Json;

namespace PlatformPublisher.Persistence;

public interface IJsonSettingStore
{
    bool TryLoad<T>(string key, out T? value);
    T Load<T>(string key, Func<T> defaultFactory);
    void Save<T>(string key, T value, int schemaVersion = 1);
}

public sealed class JsonSettingStore : IJsonSettingStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
    private readonly PlatformDatabase _database;

    public JsonSettingStore(PlatformDatabase database) => _database = database;

    public T Load<T>(string key, Func<T> defaultFactory)
    {
        return TryLoad<T>(key, out var value) && value is not null ? value : defaultFactory();
    }

    public bool TryLoad<T>(string key, out T? value)
    {
        value = default;
        PlatformDatabaseInitializer.EnsureMainDatabase(_database);
        using var connection = _database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM app_settings WHERE key=$key LIMIT 1";
        command.Parameters.AddWithValue("$key", key);
        var json = command.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json)) return false;
        try { value = JsonSerializer.Deserialize<T>(json, Options); return value is not null; }
        catch { return false; }
    }

    public void Save<T>(string key, T value, int schemaVersion = 1)
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(_database);
        _database.WriteGate.Wait();
        try
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_settings(key,value_json,schema_version,updated_at)
                VALUES($key,$json,$version,$at)
                ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json,
                  schema_version=excluded.schema_version,updated_at=excluded.updated_at
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Options));
            command.Parameters.AddWithValue("$version", schemaVersion);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally { _database.WriteGate.Release(); }
    }
}
