using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public static void ExportTo(string zipPath, bool includeRunData)
    {
        var accounts = ReadAccounts(AppPaths.AccountsFile);
        var activeId = ReadActiveAccountId(AppPaths.ActiveAccountFile);
        var manifest = new ConfigManifest
        {
            Version = Version,
            ExportedAt = DateTimeOffset.Now.ToString("O"),
            ActiveAccountId = activeId,
            Scope = includeRunData ? "data" : "config",
        };

        var tempRoot = Path.Combine(Path.GetTempPath(), "tiktok-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            if (File.Exists(AppPaths.AccountsFile))
                File.Copy(AppPaths.AccountsFile, Path.Combine(tempRoot, "accounts.json"), overwrite: true);
            if (File.Exists(AppPaths.ActiveAccountFile))
                File.Copy(AppPaths.ActiveAccountFile, Path.Combine(tempRoot, "active-account.json"), overwrite: true);
            if (includeRunData)
            {
                if (File.Exists(AppPaths.AppDatabaseFile))
                {
                    BackupSqlite(AppPaths.AppDatabaseFile, Path.Combine(tempRoot, "app.db"));
                    manifest.HasAppDatabase = true;
                }
            }
            else
            {
                var settings = ReadAppSettings(AppPaths.AppDatabaseFile);
                if (settings.Count > 0)
                {
                    File.WriteAllText(
                        Path.Combine(tempRoot, "app-settings.json"),
                        JsonSerializer.Serialize(settings, JsonOptions));
                    manifest.HasAppSettings = true;
                }
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
                if (includeRunData)
                    CopyProfile(account, tempRoot, entry);
                else
                    CopyLoginState(account, tempRoot, entry);
                CopyFile(entry.Seal, Path.Combine(tempRoot, "seals", account.Id, Path.GetFileName(entry.Seal)), out var hasSeal);
                entry.HasSeal = hasSeal;
                if (includeRunData)
                {
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
                }

                manifest.Accounts.Add(entry);
            }

            File.WriteAllText(Path.Combine(tempRoot, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
            CreateZip(tempRoot, zipPath);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public static ConfigPackageInfo Inspect(string zipPath)
    {
        var manifest = ReadManifest(zipPath);
        var ending = manifest.IncludesRunData
            ? "可以选择只导入配置，或连运行数据一起导入。连数据导入会先把本机主库和队列库备份为 .bak。软件授权不会改变。"
            : "这份包只有配置。导入后按账号 ID 或名称覆盖已有账号，并合并系统设置。本机队列、Excel 和上传记录不会改变。软件授权不会改变。";
        return new ConfigPackageInfo(BuildSummary(manifest, ending, packageContents: true), manifest.IncludesRunData);
    }

    public static string ImportFrom(string zipPath, bool includeRunData)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "tiktok-config-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            ExtractZip(zipPath, tempRoot);
            var manifest = ReadManifestFile(Path.Combine(tempRoot, "manifest.json"));
            var applyData = includeRunData && manifest.IncludesRunData;
            var notes = new List<string>();
            if (applyData)
            {
                if (manifest.HasAppDatabase)
                    RestoreAppDatabase(Path.Combine(tempRoot, "app.db"));
            }
            else
            {
                RestoreSettingsOnly(tempRoot);
            }

            var importedAccounts = ReadAccounts(Path.Combine(tempRoot, "accounts.json"));
            var current = ReadAccounts(AppPaths.AccountsFile);
            foreach (var account in importedAccounts)
            {
                var entry = manifest.Accounts.FirstOrDefault(item => item.Id == account.Id);
                RestoreProfile(tempRoot, account);
                RestoreSeal(tempRoot, account, entry, notes);
                if (applyData)
                {
                    RestoreExcel(tempRoot, account, entry, notes);
                    RestoreQueue(tempRoot, account, entry, notes);
                }
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

            var ending = applyData
                ? "导入完成。运行数据已覆盖，原主库和队列库已备份为 .bak。软件授权未改变。"
                : "导入完成。只更新了配置、登录态和系统设置。本机队列、Excel 和上传记录未改变。软件授权未改变。";
            var summary = BuildSummary(manifest, ending, packageContents: applyData);
            if (notes.Count > 0)
                summary += Environment.NewLine + string.Join(Environment.NewLine, notes);
            return summary;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void RestoreSettingsOnly(string tempRoot)
    {
        var settingsFile = Path.Combine(tempRoot, "app-settings.json");
        if (File.Exists(settingsFile))
        {
            var rows = JsonSerializer.Deserialize<List<AppSettingRow>>(File.ReadAllText(settingsFile), JsonOptions) ?? [];
            UpsertAppSettings(rows);
            return;
        }

        var database = Path.Combine(tempRoot, "app.db");
        if (File.Exists(database))
            UpsertAppSettings(ReadAppSettings(database));
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

    private static void CopyLoginState(TikTokAccountProfile account, string tempRoot, ConfigAccountEntry entry)
    {
        var profileDir = string.IsNullOrWhiteSpace(account.ProfileDir)
            ? AppPaths.ProfileDirFor(account.Id)
            : account.ProfileDir;
        var dest = Path.Combine(tempRoot, "profiles", account.Id, "tiktok_auth_state.json");
        foreach (var source in new[] { Path.Combine(profileDir, "tiktok_auth_state.json"), account.TiktokStorageStatePath })
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                continue;
            if (!TryCopyShared(source, dest))
                continue;
            entry.HasLoginState = true;
            return;
        }
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
            !IsUnder(account.TiktokStorageStatePath, profileDir) &&
            TryCopyShared(
                account.TiktokStorageStatePath,
                Path.Combine(tempRoot, "profiles", account.Id, "tiktok_auth_state.json")))
        {
            entry.HasLoginState = true;
        }
    }

    private static string BuildSummary(ConfigManifest manifest, string ending, bool packageContents)
    {
        var loginCount = manifest.Accounts.Count(item => item.HasLoginState);
        var sealCount = manifest.Accounts.Count(item => item.HasSeal);
        var lines = new List<string>
        {
            $"范围：{(manifest.IncludesRunData ? "配置和运行数据" : "仅配置")}",
            $"账号 {manifest.Accounts.Count} 个",
            $"系统设置：{(manifest.HasAppSettings || manifest.HasAppDatabase ? "有" : "无")}",
            $"TikTok 登录态 {loginCount} 个",
            $"印章 {sealCount} 个",
        };
        if (packageContents && manifest.IncludesRunData)
        {
            lines.Add($"队列项目 {manifest.QueueProjectCount} 条");
            lines.Add($"归档记录 {manifest.ArchiveRecordCount} 条");
            lines.Add($"Excel 文件 {manifest.Accounts.Count(item => item.HasExcel)} 个");
            lines.Add($"主数据库：{(manifest.HasAppDatabase ? "有" : "无")}");
        }

        lines.Add(ending);
        return string.Join(Environment.NewLine, lines);
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

    private static List<AppSettingRow> ReadAppSettings(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return [];
        var rows = new List<AppSettingRow>();
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False;Default Timeout=30");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_json, updated_at FROM app_settings";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AppSettingRow
            {
                Key = reader.GetString(0),
                ValueJson = reader.GetString(1),
                UpdatedAt = reader.GetString(2),
            });
        }

        return rows;
    }

    private static void UpsertAppSettings(IReadOnlyList<AppSettingRow> rows)
    {
        if (rows.Count == 0)
            return;
        AppDatabaseInitializer.EnsureInitialized(AppPaths.AppDatabaseFile);
        using var connection = AppDatabaseInitializer.OpenConnection(AppPaths.AppDatabaseFile);
        using var tx = connection.BeginTransaction();
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText =
                """
                INSERT INTO app_settings (key, value_json, updated_at)
                VALUES ($key, $value, $updated)
                ON CONFLICT(key) DO UPDATE SET
                    value_json = excluded.value_json,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$key", row.Key);
            command.Parameters.AddWithValue("$value", row.ValueJson);
            command.Parameters.AddWithValue("$updated", row.UpdatedAt);
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void BackupSqlite(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        source.Open();
        using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        destination.Open();
        source.BackupDatabase(destination);
        try
        {
            using var checkpoint = destination.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }

        destination.Close();
        source.Close();
        SqliteConnection.ClearPool(destination);
        SqliteConnection.ClearPool(source);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = destinationPath + suffix;
            if (!File.Exists(sidecar))
                continue;
            try
            {
                File.Delete(sidecar);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void CreateZip(string sourceDir, string zipPath)
    {
        if (File.Exists(zipPath))
            File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var entryName = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var input = OpenForZip(file);
            using var output = entry.Open();
            input.CopyTo(output);
        }
    }

    private static FileStream OpenForZip(string path)
    {
        IOException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(200);
            }
        }

        throw last!;
    }

    private static int CountRows(string databasePath, string sql)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
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
        copied = TryCopyShared(source, destination);
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (IsCachePath(relative))
                continue;
            TryCopyShared(file, Path.Combine(destination, relative));
        }
    }

    private static bool TryCopyShared(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
            input.CopyTo(output);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsCachePath(string relative)
    {
        var names = new[]
        {
            "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "Service Worker",
            "BrowserMetrics", "Crashpad", "DawnGraphiteCache", "DawnWebGPUCache",
        };
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
        public string Scope { get; set; } = "";
        public bool HasAppDatabase { get; set; }
        public bool HasAppSettings { get; set; }

        [JsonIgnore]
        public bool IncludesRunData =>
            string.Equals(Scope, "data", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(Scope) &&
                (HasAppDatabase || Accounts.Any(item => item.HasExcel || item.HasQueueDatabase)));
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

    private sealed class AppSettingRow
    {
        public string Key { get; set; } = "";
        public string ValueJson { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }
}

public sealed record ConfigPackageInfo(string Summary, bool IncludesRunData);
