using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Archive;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

if (args.Length != 3)
{
    Console.Error.WriteLine("参数依次为原工作区路径、备份目录和试迁移输出目录。");
    Console.Error.WriteLine("备份目录应包含 app-before.db 和 workspace-before.db 两个 SQLite 一致性备份。");
    return 2;
}

var workspace = Path.GetFullPath(args[0]);
var input = Path.GetFullPath(args[1]);
var mainBackup = Path.Combine(input, "app-before.db");
var workspaceBackup = Path.Combine(input, "workspace-before.db");
using var legacy = OpenReadOnly(workspaceBackup);
Require(Scalar(legacy, "PRAGMA quick_check") as string == "ok", "工作区备份完整性检查失败。");
var output = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(args[2]), $"tiktok-db-pilot-{Guid.NewGuid():N}")).FullName;
AppPaths.DataRootOverride = output;
WorkspaceDatabaseMigration.BackupDatabase(mainBackup, AppPaths.AppDatabaseFile);

var first = WorkspaceDatabaseMigration.MigrateBackup(workspace, workspaceBackup, AppPaths.AppDatabaseFile);
var second = WorkspaceDatabaseMigration.MigrateBackup(workspace, workspaceBackup, AppPaths.AppDatabaseFile);
Require(!first.AlreadyMigrated && second.AlreadyMigrated && first.WorkspaceId == second.WorkspaceId,
    "重复迁移校验失败。");

var queueCount = (long)Scalar(legacy, "SELECT count(*) FROM upload_projects")!;
var archiveCount = (long)Scalar(legacy, "SELECT count(*) FROM archive_projects")!;
var documentCount = (long)Scalar(legacy, "SELECT count(*) FROM project_state_documents")!;
Require(queueCount == first.QueueProjects && archiveCount == first.Archives && documentCount == first.StateDocuments,
    "迁移条目数与备份不一致。");
Require(WorkspaceQueueDatabase.Load(workspace).Items.Count == queueCount, "队列读取校验失败。");

var archiveRoots = ReadPaths(legacy, "SELECT metadata_path FROM archive_projects")
    .Where(Path.IsPathFullyQualified)
    .Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path)))
    .Where(path => path is not null)
    .Distinct(StringComparer.OrdinalIgnoreCase);
var visibleArchiveCount = archiveRoots.SelectMany(path => TikTokArchivedProjectService.ListStored(workspace, path))
    .DistinctBy(item => item.MetadataPath, StringComparer.OrdinalIgnoreCase).Count();
Require(visibleArchiveCount == archiveCount, "归档列表读取校验失败。");

var uploadStateCount = 0;
var draftCount = 0;
foreach (var project in ReadPaths(legacy,
             "SELECT project_dir FROM project_state_documents WHERE document_type = 'tiktok_upload_state'"))
{
    var state = ProjectStateDocumentStore.LoadUploadState(workspace, project);
    Require(state.Count > 0, "上传断点读取校验失败。");
    uploadStateCount++;
    if (state.TryGetValue("platform_series_lookup", out var lookup) && lookup.ValueKind == JsonValueKind.Object &&
        lookup.TryGetProperty("status", out var status) && status.GetString() == "found" &&
        lookup.TryGetProperty("detail_url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
        draftCount++;
}

using var migrated = OpenReadOnly(AppPaths.AppDatabaseFile);
Require(Scalar(migrated, "PRAGMA integrity_check") as string == "ok", "试迁移主库完整性检查失败。");
Require((long)Scalar(migrated, "SELECT count(*) FROM tiktok_workspaces")! == 1, "试迁移结果包含多个工作区。");
WorkspaceDatabaseMigration.BackupWorkspace(workspace, Path.Combine(output, "workspace-export.db"));
Console.WriteLine($"队列 {queueCount}，归档 {archiveCount}，项目状态 {documentCount}，上传断点 {uploadStateCount}，草稿链接 {draftCount}");
Console.WriteLine(output);
return 0;

static SqliteConnection OpenReadOnly(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
    }.ToString());
    connection.Open();
    return connection;
}

static object? Scalar(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}

static IEnumerable<string> ReadPaths(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    using var reader = command.ExecuteReader();
    while (reader.Read())
        yield return reader.GetString(0);
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidDataException(message);
}
