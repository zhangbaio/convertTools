using Microsoft.Data.Sqlite;
using PlatformPublisher.Persistence;
using Xunit;

namespace PlatformPublisher.Persistence.Tests;

public sealed class PlatformDatabaseTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"platform-db-tests-"+Guid.NewGuid().ToString("N"));

    [Fact]
    public void MainDatabaseInitializesAndSettingsRoundTrip()
    {
        var database=Database();PlatformDatabaseInitializer.EnsureMainDatabase(database);
        var store=new JsonSettingStore(database);store.Save("test",new TestSettings("value",3));
        Assert.Equal(new TestSettings("value",3),store.Load("test",()=>new TestSettings("",0)));
        using var connection=database.Open(readOnly:true);using var command=connection.CreateCommand();command.CommandText="PRAGMA foreign_keys";Assert.Equal(1L,command.ExecuteScalar());
    }

    [Fact]
    public void SecureBlobRequiresMatchingIdentity()
    {
        var store=new SecureBlobStore(Database());store.Save("secret","user-a",[1,2,3]);
        Assert.Equal(new byte[]{1,2,3},store.Load("secret","user-a"));Assert.Null(store.Load("secret","user-b"));
    }

    [Fact]
    public void WorkspaceStateRoundTripsAndBackupIsValid()
    {
        var project=Path.Combine(_root,"project");Directory.CreateDirectory(project);
        var stateStore=new ProjectStateDocumentStore();stateStore.Save(project,"upload",new TestSettings("running",4));
        Assert.Equal("running",stateStore.Load<TestSettings>(project,"upload")!.Name);
        var main=Database();PlatformDatabaseInitializer.EnsureMainDatabase(main);
        var backup=new DatabaseBackupService().Backup(main,Path.Combine(_root,"backups"));
        Assert.True(File.Exists(backup));Assert.Equal("ok",new DatabaseBackupService().IntegrityCheck(new PlatformDatabase(backup)));
    }

    [Fact]
    public async Task ConcurrentSettingWritesDoNotLockDatabase()
    {
        var database=Database();var store=new JsonSettingStore(database);
        await Task.WhenAll(Enumerable.Range(0,20).Select(index=>Task.Run(()=>store.Save("key-"+index,new TestSettings("v",index)))));
        Assert.Equal(19,store.Load("key-19",()=>new TestSettings("",0)).Count);
    }

    [Fact]
    public void LegacySettingsImportIsIdempotent()
    {
        var legacy=Path.Combine(_root,"legacy.db");Directory.CreateDirectory(_root);
        using(var connection=new SqliteConnection($"Data Source={legacy};Pooling=False")){connection.Open();using var command=connection.CreateCommand();command.CommandText="CREATE TABLE app_settings(key TEXT PRIMARY KEY,value_json TEXT NOT NULL,updated_at TEXT NOT NULL);INSERT INTO app_settings VALUES('legacy','{\"name\":\"old\",\"count\":1}','2026-01-01');";command.ExecuteNonQuery();}
        var target=Database();var importer=new LegacyDatabaseImporter(target);
        Assert.Equal(1,importer.Import(legacy,Path.Combine(_root,"none.db")).Settings);
        Assert.Equal(0,importer.Import(legacy,Path.Combine(_root,"none.db")).Settings);
        Assert.Equal("old",new JsonSettingStore(target).Load("legacy",()=>new TestSettings("",0)).Name);
    }

    [Fact]
    public void MainDatabaseUpgradesLegacySettingsInPlace()
    {
        var database = Database();
        Directory.CreateDirectory(_root);
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE app_settings(key TEXT PRIMARY KEY, value_json TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO app_settings VALUES('legacy', '{"name":"原有设置","count":7}', '2026-01-01');
                """;
            command.ExecuteNonQuery();
        }

        var store = new JsonSettingStore(database);
        var newSetting = new TestSettings("新增设置", 9);
        store.Save("new", newSetting, schemaVersion: 5);
        PlatformDatabaseInitializer.EnsureMainDatabase(database);
        Assert.Equal(newSetting, store.Load("new", () => new TestSettings("", 0)));
        Assert.Equal(new TestSettings("原有设置", 7), store.Load("legacy", () => new TestSettings("", 0)));

        using var readerConnection = database.Open(readOnly: true);
        using var query = readerConnection.CreateCommand();
        query.CommandText = "SELECT value_json, schema_version, updated_at FROM app_settings WHERE key = 'legacy'";
        using (var reader = query.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal("{\"name\":\"原有设置\",\"count\":7}", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal("2026-01-01", reader.GetString(2));
        }

        query.CommandText = "SELECT schema_version FROM app_settings WHERE key = 'new'";
        Assert.Equal(5L, query.ExecuteScalar());
    }

    private PlatformDatabase Database()=>new(Path.Combine(_root,"app.db"));
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
    private sealed record TestSettings(string Name,int Count);
}
