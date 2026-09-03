using System.Text.Json;
using ChannelsPublisher.Core.Models;
using PlatformPublisher.Persistence;

namespace ChannelsPublisher.Core.Services;

public sealed class AccountStore
{
    private const int WeixinPlatformValue = 0;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };
    private readonly List<PublishAccount> _accounts = new();
    private readonly PlatformDatabase? _database;
    private readonly string _legacyPath;

    public AccountStore() : this(null, AppPaths.AccountsFile) { }
    public AccountStore(PlatformDatabase? database, string? legacyPath = null)
    {
        _database = database;
        _legacyPath = legacyPath ?? AppPaths.AccountsFile;
    }

    public IReadOnlyList<PublishAccount> Accounts => _accounts;

    public void Load()
    {
        _accounts.Clear();
        if (_database is not null)
        {
            PlatformDatabaseInitializer.EnsureMainDatabase(_database);
            _accounts.AddRange(LoadDatabase());
            if (_accounts.Count == 0 && File.Exists(_legacyPath))
            {
                _accounts.AddRange(LoadLegacy());
                if (_accounts.Count > 0) Save();
            }
            return;
        }
        _accounts.AddRange(LoadLegacy());
    }

    public void Save()
    {
        if (_database is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_legacyPath)!);
            File.WriteAllText(_legacyPath, JsonSerializer.Serialize(_accounts, JsonOptions));
            return;
        }
        _database.WriteGate.Wait();
        try
        {
            using var connection = _database.Open(); using var transaction = connection.BeginTransaction();
            using (var mark = connection.CreateCommand())
            {
                mark.Transaction=transaction;mark.CommandText="UPDATE platform_accounts SET deleted_at=$at WHERE platform=$platform AND owner='channels'";
                mark.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));mark.Parameters.AddWithValue("$platform",WeixinPlatformValue);mark.ExecuteNonQuery();
            }
            foreach (var account in _accounts)
            {
                using var command=connection.CreateCommand();command.Transaction=transaction;
                command.CommandText="""
                    INSERT INTO platform_accounts(account_id,platform,display_name,owner,platform_user_id,session_directory,config_json,status,last_login_at,created_at,updated_at,deleted_at)
                    VALUES($id,$platform,$name,'channels',$user,$session,$json,'offline',$login,$created,$updated,NULL)
                    ON CONFLICT(account_id) DO UPDATE SET display_name=excluded.display_name,platform_user_id=excluded.platform_user_id,
                    session_directory=excluded.session_directory,config_json=excluded.config_json,last_login_at=excluded.last_login_at,
                    owner='channels',updated_at=excluded.updated_at,deleted_at=NULL
                    """;
                var now=DateTimeOffset.UtcNow.ToString("O");
                command.Parameters.AddWithValue("$id",account.Id);command.Parameters.AddWithValue("$platform",WeixinPlatformValue);
                command.Parameters.AddWithValue("$name",account.Name);command.Parameters.AddWithValue("$user",account.Nickname);
                command.Parameters.AddWithValue("$session",account.ProfileDir);command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(account,JsonOptions));
                command.Parameters.AddWithValue("$login",account.LastLoginAt?.ToString("O")??(object)DBNull.Value);
                command.Parameters.AddWithValue("$created",now);command.Parameters.AddWithValue("$updated",now);command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally{_database.WriteGate.Release();}
    }

    public PublishAccount Add(string name)
    {
        var id="acct-"+Guid.NewGuid().ToString("N")[..8];var account=new PublishAccount{Id=id,Name=string.IsNullOrWhiteSpace(name)?id:name.Trim(),ProfileDir=AppPaths.ProfileDirFor(id)};
        Directory.CreateDirectory(account.ProfileDir);_accounts.Add(account);Save();return account;
    }

    public void Remove(PublishAccount account)
    {
        _accounts.Remove(account);Save();
        try{if(Directory.Exists(account.ProfileDir))Directory.Delete(account.ProfileDir,true);}catch{ }
    }

    public void Update(PublishAccount account)=>Save();

    private List<PublishAccount> LoadDatabase()
    {
        using var connection=_database!.Open(readOnly:true);using var command=connection.CreateCommand();
        command.CommandText="SELECT account_id,display_name,platform_user_id,session_directory,config_json,last_login_at FROM platform_accounts WHERE platform=$platform AND owner='channels' AND deleted_at IS NULL ORDER BY created_at";
        command.Parameters.AddWithValue("$platform",WeixinPlatformValue);using var reader=command.ExecuteReader();var result=new List<PublishAccount>();
        while(reader.Read()){PublishAccount account;try{account=JsonSerializer.Deserialize<PublishAccount>(reader.GetString(4),JsonOptions)??new();}catch{account=new();}account.Id=reader.GetString(0);account.Name=reader.GetString(1);account.Nickname=reader.GetString(2);account.ProfileDir=reader.GetString(3);account.LastLoginAt=reader.IsDBNull(5)?null:DateTimeOffset.Parse(reader.GetString(5));result.Add(account);}return result;
    }

    private List<PublishAccount> LoadLegacy()
    {
        try{return File.Exists(_legacyPath)?JsonSerializer.Deserialize<List<PublishAccount>>(File.ReadAllText(_legacyPath),JsonOptions)??[]:[];}catch{return[];}
    }
}
