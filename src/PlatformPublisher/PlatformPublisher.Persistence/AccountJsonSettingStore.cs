using System.Text.Json;

namespace PlatformPublisher.Persistence;

public sealed class AccountJsonSettingStore
{
    private static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
    private readonly PlatformDatabase _database;
    public AccountJsonSettingStore(PlatformDatabase database)=>_database=database;

    public bool TryLoad<T>(string accountId,string key,out T? value)
    {
        value=default;PlatformDatabaseInitializer.EnsureMainDatabase(_database);using var connection=_database.Open(readOnly:true);using var command=connection.CreateCommand();
        command.CommandText="SELECT value_json FROM account_settings WHERE account_id=$account AND key=$key LIMIT 1";command.Parameters.AddWithValue("$account",accountId);command.Parameters.AddWithValue("$key",key);
        var json=command.ExecuteScalar()?.ToString();if(string.IsNullOrWhiteSpace(json))return false;try{value=JsonSerializer.Deserialize<T>(json,Options);return value is not null;}catch{return false;}
    }

    public void Save<T>(string accountId,string key,T value,int schemaVersion=1)
    {
        PlatformDatabaseInitializer.EnsureMainDatabase(_database);_database.WriteGate.Wait();try{using var connection=_database.Open();using var command=connection.CreateCommand();
            command.CommandText="""INSERT INTO account_settings(account_id,key,value_json,schema_version,updated_at) VALUES($account,$key,$json,$version,$at) ON CONFLICT(account_id,key) DO UPDATE SET value_json=excluded.value_json,schema_version=excluded.schema_version,updated_at=excluded.updated_at""";
            command.Parameters.AddWithValue("$account",accountId);command.Parameters.AddWithValue("$key",key);command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(value,Options));command.Parameters.AddWithValue("$version",schemaVersion);command.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));command.ExecuteNonQuery();
        }finally{_database.WriteGate.Release();}
    }
}
