using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// Reconstructs deleted-project title mappings from local durable data without asking the
/// user to enter an original title. Only exact new-title mappings are returned.
/// </summary>
public static class CopyrightProofLocalHistoryDiscoveryService
{
    private const int MaxHistoryFileBytes = 4 * 1024 * 1024;
    private const int MaxHistoryFilesPerRoot = 20_000;

    public static IReadOnlyList<TikTokExecutionProjectSnapshot> Discover(
        string currentWorkspace,
        TikTokAccountProfile account,
        string? archiveRootDir = null,
        string? mainDatabasePath = null,
        IEnumerable<string>? additionalExcelPaths = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        var workspace = Path.GetFullPath(currentWorkspace);
        var workspaceRoots = BuildWorkspaceRoots(workspace, account);
        var accountKeys = BuildAccountKeys(account);
        var candidates = new List<HistoryCandidate>();

        ReadMainDatabase(
            string.IsNullOrWhiteSpace(mainDatabasePath)
                ? ClientSettingsStore.MainDatabasePath
                : Path.GetFullPath(mainDatabasePath),
            candidates);

        foreach (var root in workspaceRoots)
        {
            ReadWorkspaceDatabase(Path.Combine(root, WorkspaceQueuePaths.QueueDatabaseFileName), candidates);
            ReadJsonFile(Path.Combine(root, WorkspaceQueuePaths.LegacyQueueJsonFileName), "旧队列 JSON", candidates);
            ReadArchiveMetadata(Path.Combine(root, "archive", "meta"), candidates);
            ReadProjectInfoFiles(Path.Combine(root, "workflow"), candidates);
            ReadBackupFiles(Path.Combine(root, "_codex-backups"), candidates);
        }

        if (!string.IsNullOrWhiteSpace(archiveRootDir))
        {
            var archiveRoot = SafeFullPath(archiveRootDir);
            if (!string.IsNullOrWhiteSpace(archiveRoot))
            {
                ReadArchiveMetadata(Path.Combine(archiveRoot, "meta"), candidates);
                ReadBackupFiles(Path.Combine(archiveRoot, "_codex-backups"), candidates);
            }
        }

        var excelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TikTokExcelExportService.ResolveReportPath(account),
        };
        foreach (var path in additionalExcelPaths ?? [])
        {
            var normalized = SafeFullPath(path);
            if (!string.IsNullOrWhiteSpace(normalized))
                excelPaths.Add(normalized);
        }
        foreach (var path in excelPaths)
            ReadExcel(path, candidates);

        return BuildSnapshots(
            candidates,
            workspace,
            workspaceRoots,
            account,
            accountKeys);
    }

    private static IReadOnlyList<TikTokExecutionProjectSnapshot> BuildSnapshots(
        IEnumerable<HistoryCandidate> candidates,
        string currentWorkspace,
        IReadOnlySet<string> workspaceRoots,
        TikTokAccountProfile account,
        IReadOnlySet<string> accountKeys)
    {
        var usable = candidates
            .Select(Normalize)
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.NewTitle) &&
                !string.IsNullOrWhiteSpace(candidate.OriginalTitle) &&
                MatchesAccountOrWorkspace(candidate, workspaceRoots, accountKeys))
            .GroupBy(
                candidate => $"{candidate.NewTitle}\n{candidate.OriginalTitle}",
                StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.EpisodeCount > 0)
                .ThenByDescending(candidate => ParseTimestamp(candidate.Timestamp))
                .First())
            .ToArray();

        return usable
            .Select(candidate =>
            {
                var timestamp = FirstNonEmpty(
                    candidate.Timestamp,
                    DateTimeOffset.Now.ToString("o"));
                var item = new QueueProjectItem
                {
                    ProjectDir = Path.Combine(currentWorkspace, candidate.OriginalTitle),
                    DisplayName = candidate.OriginalTitle,
                    OriginalTitle = candidate.OriginalTitle,
                    NewTitle = candidate.NewTitle,
                    EpisodeCount = Math.Max(0, candidate.EpisodeCount),
                    AccountProfileId = account.Id,
                    AccountProfileName = account.DisplayName,
                    QueuedAt = timestamp,
                    UploadCompletedAt = timestamp,
                    Remark = $"从本地历史自动追溯：{candidate.Source}",
                    StatusText = QueueStepStatus.Completed,
                    StepStates = new Dictionary<string, string>
                    {
                        [QueueStepKeys.UploadSeries] = QueueStepStatus.Completed,
                    },
                };
                item.NormalizeStepStates();
                return new TikTokExecutionProjectSnapshot(
                    currentWorkspace,
                    timestamp,
                    item);
            })
            .OrderBy(snapshot => snapshot.Item.NewTitle, StringComparer.Ordinal)
            .ThenBy(snapshot => snapshot.Item.OriginalTitle, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MatchesAccountOrWorkspace(
        HistoryCandidate candidate,
        IReadOnlySet<string> workspaceRoots,
        IReadOnlySet<string> accountKeys)
    {
        var candidateAccountKeys = new[] { candidate.AccountId, candidate.AccountName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        if (candidateAccountKeys.Length > 0)
            return candidateAccountKeys.Any(accountKeys.Contains);

        if (string.IsNullOrWhiteSpace(candidate.Workspace))
            return candidate.IsWorkspaceScoped;
        var candidateWorkspace = SafeFullPath(candidate.Workspace);
        return !string.IsNullOrWhiteSpace(candidateWorkspace) &&
               workspaceRoots.Contains(candidateWorkspace);
    }

    private static HashSet<string> BuildWorkspaceRoots(
        string currentWorkspace,
        TikTokAccountProfile account)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentWorkspace,
        };
        foreach (var raw in new[]
                 {
                     account.LastWorkspace,
                     account.TiktokUploadProfilePath,
                 })
        {
            var path = SafeFullPath(raw);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(path);
        }
        return roots;
    }

    private static HashSet<string> BuildAccountKeys(TikTokAccountProfile account) =>
        new[]
            {
                account.Id,
                account.Name,
                account.DisplayName,
                account.ResolveTikTokAccountName(),
                account.TiktokLoginEmail,
                account.TiktokLastLoginEmail,
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void ReadMainDatabase(string path, ICollection<HistoryCandidate> output)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            conn.Open();

            if (TableExists(conn, "upload_project_snapshots"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT workspace, payload_json, updated_at
                    FROM upload_project_snapshots
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ReadJson(
                        reader.IsDBNull(1) ? "{}" : reader.GetString(1),
                        "主数据库项目快照",
                        output,
                        priority: 10,
                        workspace: reader.IsDBNull(0) ? "" : reader.GetString(0),
                        timestamp: reader.IsDBNull(2) ? "" : reader.GetString(2));
                }
            }

            if (TableExists(conn, "upload_task_events"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT payload_json, created_at FROM upload_task_events";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ReadJson(
                        reader.IsDBNull(0) ? "{}" : reader.GetString(0),
                        "主数据库上传历史",
                        output,
                        priority: 20,
                        timestamp: reader.IsDBNull(1) ? "" : reader.GetString(1));
                }
            }

            if (TableExists(conn, "ai_rewrite_history"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT original_title, new_title, account_profile_id, created_at, payload_json
                    FROM ai_rewrite_history
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var payload = reader.IsDBNull(4) ? "{}" : reader.GetString(4);
                    ReadJson(
                        payload,
                        "AI 改写历史",
                        output,
                        priority: 30,
                        timestamp: reader.IsDBNull(3) ? "" : reader.GetString(3));
                    AddCandidate(
                        output,
                        new HistoryCandidate(
                            ReadString(reader, 0),
                            ReadString(reader, 1),
                            0,
                            ReadString(reader, 2),
                            "",
                            "",
                            "",
                            ReadString(reader, 3),
                            "AI 改写历史",
                            30,
                            IsWorkspaceScoped: false));
                }
            }
        }
        catch
        {
            // A damaged/locked history database is a skipped source, never a batch blocker.
        }
    }

    private static void ReadWorkspaceDatabase(string path, ICollection<HistoryCandidate> output)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            conn.Open();
            if (TableExists(conn, "upload_projects"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT payload_json, workspace_path, project_dir, updated_at
                    FROM upload_projects
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ReadJson(
                        ReadString(reader, 0),
                        "工作目录队列数据库",
                        output,
                        priority: 15,
                        workspace: ReadString(reader, 1),
                        projectDir: ReadString(reader, 2),
                        timestamp: ReadString(reader, 3),
                        workspaceScoped: true);
                }
            }

            if (TableExists(conn, "archive_projects"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT payload_json, archived_workflow_dir, updated_at
                    FROM archive_projects
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    ReadJson(
                        ReadString(reader, 0),
                        "工作目录归档数据库",
                        output,
                        priority: 12,
                        projectDir: ReadString(reader, 1),
                        timestamp: ReadString(reader, 2),
                        workspaceScoped: true);
                }
            }
        }
        catch
        {
            // Keep searching other local sources.
        }
    }

    private static void ReadArchiveMetadata(
        string metadataRoot,
        ICollection<HistoryCandidate> output)
    {
        if (!Directory.Exists(metadataRoot)) return;
        foreach (var path in SafeEnumerateFiles(metadataRoot, "*.json", SearchOption.TopDirectoryOnly))
            ReadJsonFile(path, "归档元数据", output, priority: 8, workspaceScoped: true);
    }

    private static void ReadProjectInfoFiles(
        string workflowRoot,
        ICollection<HistoryCandidate> output)
    {
        if (!Directory.Exists(workflowRoot)) return;
        foreach (var path in SafeEnumerateFiles(workflowRoot, "短剧信息.txt", SearchOption.AllDirectories))
            ReadProjectInfoFile(path, "本地短剧信息", output, priority: 18, workspaceScoped: true);
    }

    private static void ReadBackupFiles(
        string backupRoot,
        ICollection<HistoryCandidate> output)
    {
        if (!Directory.Exists(backupRoot)) return;
        var count = 0;
        foreach (var path in SafeEnumerateFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            if (++count > MaxHistoryFilesPerRoot) break;
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxHistoryFileBytes) continue;
            if (path.EndsWith("短剧信息.txt", StringComparison.OrdinalIgnoreCase))
            {
                ReadProjectInfoFile(path, "本地备份短剧信息", output, priority: 5, workspaceScoped: true);
            }
            else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                ReadJsonFile(path, "本地备份记录", output, priority: 4, workspaceScoped: true);
            }
        }
    }

    private static void ReadJsonFile(
        string path,
        string source,
        ICollection<HistoryCandidate> output,
        int priority = 25,
        bool workspaceScoped = false)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxHistoryFileBytes) return;
            ReadJson(
                File.ReadAllText(path),
                source,
                output,
                priority,
                timestamp: info.LastWriteTimeUtc.ToString("o"),
                workspaceScoped: workspaceScoped);
        }
        catch
        {
            // Ignore malformed legacy files.
        }
    }

    private static void ReadJson(
        string json,
        string source,
        ICollection<HistoryCandidate> output,
        int priority,
        string workspace = "",
        string projectDir = "",
        string timestamp = "",
        bool workspaceScoped = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            ReadJsonElement(
                document.RootElement,
                source,
                output,
                priority,
                workspace,
                projectDir,
                timestamp,
                workspaceScoped);
        }
        catch
        {
            // Ignore malformed payloads and continue the remaining sources.
        }
    }

    private static void ReadJsonElement(
        JsonElement element,
        string source,
        ICollection<HistoryCandidate> output,
        int priority,
        string inheritedWorkspace,
        string inheritedProjectDir,
        string inheritedTimestamp,
        bool workspaceScoped)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                ReadJsonElement(
                    child,
                    source,
                    output,
                    priority,
                    inheritedWorkspace,
                    inheritedProjectDir,
                    inheritedTimestamp,
                    workspaceScoped);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        var workspace = FirstNonEmpty(
            GetJsonString(element, "workspace", "workspace_path", "workspacePath", "工作目录"),
            inheritedWorkspace);
        var projectDir = FirstNonEmpty(
            GetJsonString(
                element,
                "project_dir",
                "projectDir",
                "workflowProjectDir",
                "archivedWorkflowDir",
                "archived_workflow_dir",
                "项目目录"),
            inheritedProjectDir);
        var timestamp = FirstNonEmpty(
            GetJsonString(
                element,
                "updated_at",
                "updatedAt",
                "created_at",
                "createdAt",
                "archived_at",
                "archivedAt",
                "timestamp",
                "上传完成时间"),
            inheritedTimestamp);
        AddCandidate(
            output,
            new HistoryCandidate(
                GetJsonString(
                    element,
                    "original_title",
                    "originalTitle",
                    "OriginalTitle",
                    "原剧名",
                    "project_key",
                    "projectKey",
                    "ProjectKey"),
                GetJsonString(
                    element,
                    "new_title",
                    "newTitle",
                    "NewTitle",
                    "新剧名",
                    "last_upload_title"),
                GetJsonInt(element, "episode_count", "episodeCount", "EpisodeCount", "集数"),
                GetJsonString(element, "account_profile_id", "accountProfileId", "账号ID"),
                GetJsonString(element, "account_profile_name", "accountProfileName", "账号"),
                workspace,
                projectDir,
                timestamp,
                source,
                priority,
                workspaceScoped));

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                ReadJsonElement(
                    property.Value,
                    source,
                    output,
                    priority,
                    workspace,
                    projectDir,
                    timestamp,
                    workspaceScoped);
            }
        }
    }

    private static void ReadProjectInfoFile(
        string path,
        string source,
        ICollection<HistoryCandidate> output,
        int priority,
        bool workspaceScoped)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(path))
            {
                var index = line.IndexOfAny([':', '：']);
                if (index <= 0) continue;
                var key = line[..index].Trim();
                var value = line[(index + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    values[key] = value;
            }
            AddCandidate(
                output,
                new HistoryCandidate(
                    ReadDictionary(values, "原剧名", "原剧名称", "原始剧名"),
                    ReadDictionary(values, "新剧名", "新剧名称"),
                    ParseInt(ReadDictionary(values, "集数", "总集数")),
                    "",
                    ReadDictionary(values, "账号"),
                    "",
                    Path.GetDirectoryName(path) ?? "",
                    File.GetLastWriteTimeUtc(path).ToString("o"),
                    source,
                    priority,
                    workspaceScoped));
        }
        catch
        {
            // Continue other local history sources.
        }
    }

    private static void ReadExcel(string path, ICollection<HistoryCandidate> output)
    {
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0) return;
            using var document = SpreadsheetDocument.Open(path, false);
            var workbookPart = document.WorkbookPart;
            var sheet = workbookPart?.Workbook.Sheets?
                .Elements<Sheet>()
                .FirstOrDefault(item => string.Equals(item.Name?.Value, "汇总", StringComparison.Ordinal))
                ?? workbookPart?.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault();
            if (workbookPart is null || sheet?.Id?.Value is null) return;
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
            var rows = worksheetPart.Worksheet.Descendants<Row>().ToArray();
            if (rows.Length == 0) return;

            var sharedStrings = workbookPart.SharedStringTablePart?
                .SharedStringTable
                .Elements<SharedStringItem>()
                .Select(item => item.InnerText)
                .ToArray() ?? [];
            var headers = ReadExcelRow(rows[0], sharedStrings)
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Value.Trim(), pair => pair.Key, StringComparer.OrdinalIgnoreCase);
            var originalColumn = FindHeader(headers, "原剧名", "原剧名称", "原始剧名");
            var newColumn = FindHeader(headers, "新剧名", "新剧名称");
            if (originalColumn < 0 || newColumn < 0) return;

            foreach (var row in rows.Skip(1))
            {
                var values = ReadExcelRow(row, sharedStrings);
                AddCandidate(
                    output,
                    new HistoryCandidate(
                        values.GetValueOrDefault(originalColumn, ""),
                        values.GetValueOrDefault(newColumn, ""),
                        ParseInt(ReadExcelValue(values, headers, "集数", "总集数")),
                        "",
                        ReadExcelValue(values, headers, "账号", "账号名称"),
                        ReadExcelValue(values, headers, "工作目录"),
                        ReadExcelValue(values, headers, "项目目录"),
                        FirstNonEmpty(
                            ReadExcelValue(values, headers, "上传完成时间", "加入时间"),
                            info.LastWriteTimeUtc.ToString("o")),
                        "本地 Excel",
                        25,
                        IsWorkspaceScoped: false));
            }
        }
        catch
        {
            // Invalid/locked/legacy XLS files are skipped.
        }
    }

    private static Dictionary<int, string> ReadExcelRow(
        Row row,
        IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<int, string>();
        var fallbackIndex = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            var index = ColumnIndex(cell.CellReference?.Value);
            if (index < 0)
                index = fallbackIndex;
            values[index] = ReadCellValue(cell, sharedStrings);
            fallbackIndex = index + 1;
        }
        return values;
    }

    private static string ReadCellValue(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? "";
        var raw = cell.CellValue?.Text ?? cell.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 &&
            index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }
        return raw;
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return -1;
        var value = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            value = checked(value * 26 + (char.ToUpperInvariant(ch) - 'A' + 1));
        }
        return value - 1;
    }

    private static int FindHeader(
        IReadOnlyDictionary<string, int> headers,
        params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (headers.TryGetValue(candidate, out var index))
                return index;
        }
        return -1;
    }

    private static string ReadExcelValue(
        IReadOnlyDictionary<int, string> values,
        IReadOnlyDictionary<string, int> headers,
        params string[] candidates)
    {
        var index = FindHeader(headers, candidates);
        return index >= 0 ? values.GetValueOrDefault(index, "") : "";
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static IEnumerable<string> SafeEnumerateFiles(
        string root,
        string pattern,
        SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, searchOption);
        }
        catch
        {
            return [];
        }
    }

    private static void AddCandidate(
        ICollection<HistoryCandidate> output,
        HistoryCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.OriginalTitle) &&
            !string.IsNullOrWhiteSpace(candidate.NewTitle))
        {
            output.Add(candidate);
        }
    }

    private static HistoryCandidate Normalize(HistoryCandidate candidate) =>
        candidate with
        {
            OriginalTitle = candidate.OriginalTitle.Trim(),
            NewTitle = candidate.NewTitle.Trim(),
            AccountId = candidate.AccountId.Trim(),
            AccountName = candidate.AccountName.Trim(),
            Workspace = candidate.Workspace.Trim(),
            ProjectDir = candidate.ProjectDir.Trim(),
            Timestamp = candidate.Timestamp.Trim(),
        };

    private static string GetJsonString(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString()?.Trim() ?? "";
            if (property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.Value.ToString().Trim();
        }
        return "";
    }

    private static int GetJsonInt(JsonElement element, params string[] names) =>
        ParseInt(GetJsonString(element, names));

    private static int ParseInt(string? value)
    {
        var text = (value ?? "").Trim();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return Math.Max(0, parsed);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Math.Max(0, (int)number);
        return 0;
    }

    private static string ReadDictionary(
        IReadOnlyDictionary<string, string> values,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    private static string ReadString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? "" : reader.GetValue(index)?.ToString()?.Trim() ?? "";

    private static string SafeFullPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        }
        catch
        {
            return "";
        }
    }

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var text = (value ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return "";
    }

    private sealed record HistoryCandidate(
        string OriginalTitle,
        string NewTitle,
        int EpisodeCount,
        string AccountId,
        string AccountName,
        string Workspace,
        string ProjectDir,
        string Timestamp,
        string Source,
        int Priority,
        bool IsWorkspaceScoped);
}
