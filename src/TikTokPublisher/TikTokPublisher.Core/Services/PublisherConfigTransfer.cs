using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class PublisherConfigTransfer
{
    private const int Version = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static void ExportTo(string zipPath)
    {
        var accounts = ReadAccounts(AppPaths.AccountsFile);
        var activeId = ReadActiveAccountId(AppPaths.ActiveAccountFile);
        var manifest = new ConfigManifest
        {
            Version = Version,
            ExportedAt = DateTimeOffset.Now.ToString("O"),
            ActiveAccountId = activeId,
        };

        var tempRoot = Path.Combine(Path.GetTempPath(), "tiktok-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            if (File.Exists(AppPaths.AccountsFile))
                File.Copy(AppPaths.AccountsFile, Path.Combine(tempRoot, "accounts.json"), overwrite: true);
            if (File.Exists(AppPaths.ActiveAccountFile))
                File.Copy(AppPaths.ActiveAccountFile, Path.Combine(tempRoot, "active-account.json"), overwrite: true);
            if (File.Exists(AppPaths.AppDatabaseFile))
            {
                BackupSqlite(AppPaths.AppDatabaseFile, Path.Combine(tempRoot, "app.db"));
                manifest.HasAppDatabase = true;
            }

            foreach (var account in accounts)
            {
                var entry = new ConfigAccountEntry
                {
                    Id = account.Id,
                    Name = account.DisplayName,
                    Workspace = ConfiguredWorkspace(account),
                    Excel = SafeFullPath(TikTokExcelExportService.ResolveReportPath(account)),
                    Seal = SafeFullPath(account.TiktokProofSealPath),
                    Archive = SafeFullPath(account.TiktokArchiveRootDir),
                };
                CopyProfile(account, tempRoot, entry);
                CopyFile(entry.Seal, Path.Combine(tempRoot, "seals", account.Id, Path.GetFileName(entry.Seal)), out var hasSeal);
                entry.HasSeal = hasSeal;
                CopyFile(entry.Excel, Path.Combine(tempRoot, "excel", account.Id, Path.GetFileName(entry.Excel)), out var hasExcel);
                entry.HasExcel = hasExcel;
                var queueDb = string.IsNullOrWhiteSpace(entry.Workspace)
                    ? ""
                    : WorkspaceQueuePaths.QueueDatabasePath(entry.Workspace);
                if (!string.IsNullOrWhiteSpace(queueDb) && File.Exists(queueDb))
                {
                    var exported = Path.Combine(tempRoot, "queues", account.Id, "queue.db");
                    BackupSqlite(queueDb, exported);
                    entry.HasQueueDatabase = true;
                    manifest.QueueProjectCount += CountRows(exported, "SELECT COUNT(*) FROM upload_projects");
                    manifest.ArchiveRecordCount += CountRows(exported, "SELECT COUNT(*) FROM archive_projects");
                }

                manifest.Accounts.Add(entry);
            }

            File.WriteAllText(Path.Combine(tempRoot, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tempRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public static string Describe(string zipPath)
    {
        var manifest = ReadManifest(zipPath);
        return BuildSummary(manifest, "导入后将覆盖本机主数据库（先备份为 app.db.bak），并按账号 ID 或名称覆盖已有账号。软件授权不会改变。");
    }

    public static string ImportFrom(string zipPath)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "tiktok-config-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            ExtractZip(zipPath, tempRoot);
            var manifest = ReadManifestFile(Path.Combine(tempRoot, "manifest.json"));
            var notes = new List<string>();
            if (manifest.HasAppDatabase)
                RestoreAppDatabase(Path.Combine(tempRoot, "app.db"));

            var importedAccounts = ReadAccounts(Path.Combine(tempRoot, "accounts.json"));
            var current = ReadAccounts(AppPaths.AccountsFile);
            foreach (var account in importedAccounts)
            {
                var entry = manifest.Accounts.FirstOrDefault(item => item.Id == account.Id);
                RestoreProfile(tempRoot, account);
                RestoreSeal(tempRoot, account, entry, notes);
                RestoreExcel(tempRoot, account, entry, notes);
                RestoreQueue(tempRoot, account, entry, notes);
                var index = current.FindIndex(item =>
                    item.Id == account.Id ||
                    string.Equals(item.Name, account.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    current[index] = account;
                else
                    current.Add(account);
            }

            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(AppPaths.AccountsFile, JsonSerializer.Serialize(current, JsonOptions));
            var activeId = manifest.ActiveAccountId;
            if (current.All(item => item.Id != activeId))
                activeId = current.FirstOrDefault()?.Id ?? "";
            File.WriteAllText(
                AppPaths.ActiveAccountFile,
                JsonSerializer.Serialize(new ActiveAccountPointer { ActiveAccountId = activeId }, JsonOptions));

            var summary = BuildSummary(manifest, "导入完成。软件授权未改变。");
            if (notes.Count > 0)
                summary += Environment.NewLine + string.Join(Environment.NewLine, notes);
            return summary;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void RestoreAppDatabase(string extractedDb)
    {
        if (!File.Exists(extractedDb))
            return;
        Directory.CreateDirectory(AppPaths.DataRoot);
        if (File.Exists(AppPaths.AppDatabaseFile))
            BackupSqlite(AppPaths.AppDatabaseFile, AppPaths.AppDatabaseFile + ".bak");
        BackupSqlite(extractedDb, AppPaths.AppDatabaseFile);
    }

    private static void RestoreProfile(string tempRoot, TikTokAccountProfile account)
    {
        var source = Path.Combine(tempRoot, "profiles", account.Id);
        account.ProfileDir = AppPaths.ProfileDirFor(account.Id);
        account.TiktokStorageStatePath = AppPaths.DefaultStorageStatePath(account.Id);
        if (!Directory.Exists(source))
            return;
        CopyDirectory(source, account.ProfileDir);
    }

    private static void RestoreSeal(string tempRoot, TikTokAccountProfile account, ConfigAccountEntry? entry, List<string> notes)
    {
        var source = Directory.Exists(Path.Combine(tempRoot, "seals", account.Id))
            ? Directory.EnumerateFiles(Path.Combine(tempRoot, "seals", account.Id)).FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(source))
            return;
        var original = entry?.Seal ?? "";
        var parent = string.IsNullOrWhiteSpace(original) ? "" : Path.GetDirectoryName(original);
        string dest;
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            dest = original;
        else
        {
            dest = Path.Combine(account.ProfileDir, Path.GetFileName(source));
            if (!string.IsNullOrWhiteSpace(original))
                notes.Add($"「{account.DisplayName}」印章原目录不存在，已改存到账号目录。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
        account.TiktokProofSealPath = dest;
    }

    private static void RestoreExcel(string tempRoot, TikTokAccountProfile account, ConfigAccountEntry? entry, List<string> notes)
    {
        var source = Directory.Exists(Path.Combine(tempRoot, "excel", account.Id))
            ? Directory.EnumerateFiles(Path.Combine(tempRoot, "excel", account.Id)).FirstOrDefault()
            : null;
        if (string.IsNullOrWhiteSpace(source))
            return;
        var original = entry?.Excel ?? "";
        var parent = string.IsNullOrWhiteSpace(original) ? "" : Path.GetDirectoryName(original);
        var workspace = ConfiguredWorkspace(account);
        string dest;
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
            dest = original;
        else if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            dest = Path.Combine(workspace, Path.GetFileName(source));
            notes.Add($"「{account.DisplayName}」Excel 原目录不存在，已改存到工作目录。");
        }
        else
        {
            dest = Path.Combine(AppPaths.DataRoot, "reports", account.Id, Path.GetFileName(source));
            notes.Add($"「{account.DisplayName}」Excel 原目录不存在，已改存到报告目录。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
        account.TiktokExcelReportPath = dest;
    }

    private static void RestoreQueue(string tempRoot, TikTokAccountProfile account, ConfigAccountEntry? entry, List<string> notes)
    {
        var source = Path.Combine(tempRoot, "queues", account.Id, "queue.db");
        if (!File.Exists(source))
            return;
        var workspace = ConfiguredWorkspace(account);
        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            var dest = WorkspaceQueuePaths.QueueDatabasePath(workspace);
            if (File.Exists(dest))
                BackupSqlite(dest, dest + ".bak");
            BackupSqlite(source, dest);
            return;
        }

        var holding = Path.Combine(AppPaths.DataRoot, "import-holding", account.Id, WorkspaceQueuePaths.QueueDatabaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(holding)!);
        BackupSqlite(source, holding);
        notes.Add($"「{account.DisplayName}」工作目录不存在：{workspace}。队列库已暂存到 {holding}");
    }

    private static void CopyProfile(TikTokAccountProfile account, string tempRoot, ConfigAccountEntry entry)
    {
        var profileDir = string.IsNullOrWhiteSpace(account.ProfileDir)
            ? AppPaths.ProfileDirFor(account.Id)
            : account.ProfileDir;
        if (Directory.Exists(profileDir))
        {
            CopyDirectory(profileDir, Path.Combine(tempRoot, "profiles", account.Id));
            entry.HasLoginState = File.Exists(Path.Combine(profileDir, "tiktok_auth_state.json"))
                                  || File.Exists(account.TiktokStorageStatePath);
        }

        if (!string.IsNullOrWhiteSpace(account.TiktokStorageStatePath) &&
            File.Exists(account.TiktokStorageStatePath) &&
            !IsUnder(account.TiktokStorageStatePath, profileDir))
        {
            File.Copy(
                account.TiktokStorageStatePath,
                Path.Combine(tempRoot, "profiles", account.Id, "tiktok_auth_state.json"),
                overwrite: true);
            entry.HasLoginState = true;
        }
    }

    private static string BuildSummary(ConfigManifest manifest, string ending)
    {
        var loginCount = manifest.Accounts.Count(item => item.HasLoginState);
        var excelCount = manifest.Accounts.Count(item => item.HasExcel);
        return string.Join(Environment.NewLine,
        [
            $"账号 {manifest.Accounts.Count} 个",
            $"队列项目 {manifest.QueueProjectCount} 条",
            $"归档记录 {manifest.ArchiveRecordCount} 条",
            $"主数据库：{(manifest.HasAppDatabase ? "有" : "无")}",
            $"Excel 文件 {excelCount} 个",
            $"TikTok 登录态 {loginCount} 个",
            ending,
        ]);
    }

    private static ConfigManifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidOperationException("配置包缺少 manifest.json");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<ConfigManifest>(stream, JsonOptions)
               ?? throw new InvalidOperationException("配置包无法读取");
    }

    private static ConfigManifest ReadManifestFile(string path)
    {
        return JsonSerializer.Deserialize<ConfigManifest>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidOperationException("配置包无法读取");
    }

    private static void ExtractZip(string zipPath, string tempRoot)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            var dest = SafeCombine(tempRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static List<TikTokAccountProfile> ReadAccounts(string path)
    {
        if (!File.Exists(path))
            return [];
        return JsonSerializer.Deserialize<List<TikTokAccountProfile>>(File.ReadAllText(path), JsonOptions) ?? [];
    }

    private static string ReadActiveAccountId(string path)
    {
        if (!File.Exists(path))
            return "";
        return JsonSerializer.Deserialize<ActiveAccountPointer>(File.ReadAllText(path), JsonOptions)?.ActiveAccountId ?? "";
    }

    private static string ConfiguredWorkspace(TikTokAccountProfile account)
    {
        foreach (var candidate in new[] { account.TiktokUploadProfilePath, account.LastWorkspace })
        {
            var path = SafeFullPath(candidate);
            if (path.Length > 0)
                return path;
        }

        return "";
    }

    private static void BackupSqlite(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
        source.Open();
        using var destination = new SqliteConnection($"Data Source={destinationPath}");
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static int CountRows(string databasePath, string sql)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch
        {
            return 0;
        }
    }

    private static void CopyFile(string source, string destination, out bool copied)
    {
        copied = false;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        copied = true;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (IsCachePath(relative))
                continue;
            var dest = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static bool IsCachePath(string relative)
    {
        var names = new[] { "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "Service Worker" };
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => names.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsUnder(string path, string root)
    {
        var full = SafeFullPath(path);
        var rootFull = SafeFullPath(root);
        return full.Length > 0 && rootFull.Length > 0 &&
               full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFullPath(string? path)
    {
        var text = (path ?? "").Trim();
        if (text.Length == 0)
            return "";
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(text));
        }
        catch
        {
            return "";
        }
    }

    private static string SafeCombine(string root, string entryName)
    {
        var dest = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置包包含无效路径");
        return dest;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响导入导出结果。
        }
    }

    private sealed class ConfigManifest
    {
        public int Version { get; set; }
        public string ExportedAt { get; set; } = "";
        public string ActiveAccountId { get; set; } = "";
        public bool HasAppDatabase { get; set; }
        public int QueueProjectCount { get; set; }
        public int ArchiveRecordCount { get; set; }
        public List<ConfigAccountEntry> Accounts { get; set; } = [];
    }

    private sealed class ConfigAccountEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Workspace { get; set; } = "";
        public string Excel { get; set; } = "";
        public string Seal { get; set; } = "";
        public string Archive { get; set; } = "";
        public bool HasQueueDatabase { get; set; }
        public bool HasExcel { get; set; }
        public bool HasSeal { get; set; }
        public bool HasLoginState { get; set; }
    }

    private sealed class ActiveAccountPointer
    {
        public string ActiveAccountId { get; set; } = "";
    }
}
