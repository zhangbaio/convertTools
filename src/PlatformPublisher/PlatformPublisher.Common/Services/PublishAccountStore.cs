using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Persistence;

namespace PlatformPublisher.Common.Services;

public sealed class PublishAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly PlatformDatabase _database;
    private readonly string _legacyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PublishAccountStore(string? storePath = null)
    {
        _legacyPath = string.IsNullOrWhiteSpace(storePath) ? PlatformPublisherPaths.AccountStorePath : Path.GetFullPath(storePath);
        var databasePath = string.IsNullOrWhiteSpace(storePath)
            ? PlatformPublisherPaths.MainDatabasePath
            : Path.GetExtension(_legacyPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(_legacyPath, ".db")
                : _legacyPath;
        _database = new PlatformDatabase(databasePath);
    }

    public PublishAccountStore(PlatformDatabase database, string? legacyPath = null)
    {
        _database = database;
        _legacyPath = legacyPath ?? PlatformPublisherPaths.AccountStorePath;
    }

    public string StorePath => _database.Path;

    public async Task<IReadOnlyList<PublishAccount>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PlatformDatabaseInitializer.EnsureMainDatabase(_database);
            var values = LoadDatabase();
            if (values.Count > 0 || !File.Exists(_legacyPath)) return values;
            var legacy = await LoadLegacyAsync(cancellationToken);
            if (legacy.Count > 0) SaveDatabase(legacy);
            return legacy;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(IEnumerable<PublishAccount> accounts, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { PlatformDatabaseInitializer.EnsureMainDatabase(_database); SaveDatabase(accounts.ToArray()); }
        finally { _gate.Release(); }
    }

    private List<PublishAccount> LoadDatabase()
    {
        using var connection = _database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id,platform,display_name,config_json,created_at,updated_at FROM platform_accounts WHERE owner='platform' AND deleted_at IS NULL ORDER BY created_at";
        using var reader = command.ExecuteReader();
        var result = new List<PublishAccount>();
        while (reader.Read())
        {
            var account = DeserializeConfig(reader.GetString(3));
            account.Id = reader.GetString(0);
            account.Platform = (PublishPlatform)reader.GetInt32(1);
            account.Name = reader.GetString(2);
            account.CreatedAt = DateTimeOffset.Parse(reader.GetString(4));
            account.UpdatedAt = DateTimeOffset.Parse(reader.GetString(5));
            result.Add(account);
        }
        return result;
    }

    private void SaveDatabase(IReadOnlyList<PublishAccount> accounts)
    {
        _database.WriteGate.Wait();
        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            using (var mark = connection.CreateCommand())
            {
                mark.Transaction = transaction;
                mark.CommandText = "UPDATE platform_accounts SET deleted_at=$at WHERE owner='platform'";
                mark.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                mark.ExecuteNonQuery();
            }
            foreach (var account in accounts)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO platform_accounts(account_id,platform,display_name,owner,config_json,created_at,updated_at,deleted_at)
                    VALUES($id,$platform,$name,'platform',$json,$created,$updated,NULL)
                    ON CONFLICT(account_id) DO UPDATE SET platform=excluded.platform,display_name=excluded.display_name,
                    owner='platform',config_json=excluded.config_json,updated_at=excluded.updated_at,deleted_at=NULL
                    """;
                command.Parameters.AddWithValue("$id", account.Id);
                command.Parameters.AddWithValue("$platform", (int)account.Platform);
                command.Parameters.AddWithValue("$name", account.Name);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(account, JsonOptions));
                command.Parameters.AddWithValue("$created", account.CreatedAt.ToString("O"));
                command.Parameters.AddWithValue("$updated", account.UpdatedAt.ToString("O"));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _database.WriteGate.Release(); }
    }

    private async Task<List<PublishAccount>> LoadLegacyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(_legacyPath);
            return await JsonSerializer.DeserializeAsync<List<PublishAccount>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch { return []; }
    }

    private static PublishAccount DeserializeConfig(string json)
    {
        try { return JsonSerializer.Deserialize<PublishAccount>(json, JsonOptions) ?? new PublishAccount(); }
        catch { return new PublishAccount(); }
    }
}
