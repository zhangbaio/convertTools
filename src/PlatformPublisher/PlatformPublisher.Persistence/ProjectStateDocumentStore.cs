using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PlatformPublisher.Persistence;

public sealed class ProjectStateDocumentStore
{
    public const string DatabaseFileName = ".yunfan-platform.db";

    public T? Load<T>(string projectDirectory, string documentType)
    {
        var database = ForProject(projectDirectory);
        if (!File.Exists(database.Path)) return default;
        PlatformDatabaseInitializer.EnsureWorkspaceDatabase(database);
        using var connection = database.Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM project_state_documents WHERE project_directory=$dir AND document_type=$type LIMIT 1";
        command.Parameters.AddWithValue("$dir", Normalize(projectDirectory));
        command.Parameters.AddWithValue("$type", documentType);
        var json = command.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json); } catch { return default; }
    }

    public void Save<T>(string projectDirectory, string documentType, T value)
    {
        var database = ForProject(projectDirectory);
        PlatformDatabaseInitializer.EnsureWorkspaceDatabase(database);
        database.WriteGate.Wait();
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            var directory = Normalize(projectDirectory);
            var id = StableId(directory + "\n" + documentType);
            var now = DateTimeOffset.UtcNow.ToString("O");
            command.CommandText = """
                INSERT INTO project_state_documents(document_id,project_id,project_directory,document_type,payload_json,created_at,updated_at)
                VALUES($id,'',$dir,$type,$json,$at,$at)
                ON CONFLICT(document_id) DO UPDATE SET payload_json=excluded.payload_json,updated_at=excluded.updated_at
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$dir", directory);
            command.Parameters.AddWithValue("$type", documentType);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value));
            command.Parameters.AddWithValue("$at", now);
            command.ExecuteNonQuery();
        }
        finally { database.WriteGate.Release(); }
    }

    public static PlatformDatabase ForProject(string projectDirectory) =>
        new(Path.Combine(Normalize(projectDirectory), DatabaseFileName));

    private static string Normalize(string path) => Path.GetFullPath(path.Trim());
    private static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
