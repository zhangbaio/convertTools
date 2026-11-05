using System.Globalization;
using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 快手分账今日成功上传剧集数。个人版和企业版分开记，只统计当前账号，同一项目当天只计一次。
/// </summary>
public static class KuaishouTodayUploadCounter
{
    private static readonly object Gate = new();

    public static void RecordSuccess(
        PublishPlatform platform,
        string accountId,
        string projectDirectory,
        DateTimeOffset? recordedAt = null,
        string? storeRoot = null,
        string? nickname = null)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            return;
        var edition = Edition(platform);
        if (edition is null || string.IsNullOrWhiteSpace(accountId))
            return;
        EnsureMigrated(storeRoot);
        var at = recordedAt ?? DateTimeOffset.Now;
        var title = Path.GetFileName(projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        KuaishouUploadRecordStore.Upsert(new KuaishouUploadRecord(
            edition,
            accountId.Trim(),
            nickname ?? "",
            at,
            "成功",
            title,
            title,
            title,
            ReadInfoValue(projectDirectory, "作者"),
            0,
            Path.GetFullPath(projectDirectory),
            ""), storeRoot);
    }

    public static void AppendRun(
        PublishPlatform platform,
        string accountId,
        string accountName,
        string projectDirectory,
        bool succeeded,
        string? error,
        string? storeRoot = null,
        string? nickname = null)
    {
        var edition = Edition(platform);
        var account = FirstText(accountId, accountName);
        if (string.IsNullOrWhiteSpace(projectDirectory) || edition is null || string.IsNullOrWhiteSpace(account))
            return;
        EnsureMigrated(storeRoot);
        var enterprise = platform == PublishPlatform.KuaishouEnterpriseRevenue;
        KuaishouArtifactColumnStatus? artifacts = null;
        try
        {
            if (Directory.Exists(projectDirectory))
                artifacts = KuaishouArtifactColumnStatusReader.Read(projectDirectory, enterprise);
        }
        catch
        {
            artifacts = null;
        }

        var stateName = enterprise ? ".kuaishou-enterprise-upload-state.json" : ".kuaishou-personal-upload-state.json";
        var state = ReadObject(FindStateFile(projectDirectory, stateName));
        var rawStatus = succeeded
            ? FirstText(ReadString(state, "Status"), ReadString(state, "status"), "completed")
            : "failed";
        if (succeeded && IsInProgressStatus(rawStatus))
            rawStatus = "completed";
        var originalTitle = artifacts?.OriginalTitle ?? Path.GetFileName(projectDirectory);
        var newTitle = string.IsNullOrWhiteSpace(artifacts?.NewTitle) ? originalTitle : artifacts!.NewTitle;
        KuaishouUploadRecordStore.Upsert(new KuaishouUploadRecord(
            edition,
            account,
            nickname ?? "",
            DateTimeOffset.Now,
            StatusLabel(rawStatus),
            string.IsNullOrWhiteSpace(newTitle) ? originalTitle : newTitle,
            originalTitle,
            newTitle,
            ReadInfoValue(projectDirectory, "作者"),
            artifacts?.EpisodeCount ?? 0,
            Path.GetFullPath(projectDirectory),
            succeeded ? "" : error?.Trim() ?? ""), storeRoot);
    }

    public static string ExportExcel(PublishPlatform platform, string? storeRoot = null)
    {
        var edition = Edition(platform) ?? throw new InvalidOperationException("只有快手分账可以导出上传记录。");
        EnsureMigrated(storeRoot);
        var records = KuaishouUploadRecordStore.List(edition, storeRoot)
            .Select(ToRow)
            .ToList();
        var xlsxPath = ExcelPath(edition, storeRoot);
        try
        {
            KuaishouUploadReportWorkbook.Write(records, xlsxPath, edition == "enterprise");
            return xlsxPath;
        }
        catch (IOException)
        {
            var pending = Path.Combine(
                Path.GetDirectoryName(xlsxPath)!,
                Path.GetFileNameWithoutExtension(xlsxPath) + ".pending.xlsx");
            KuaishouUploadReportWorkbook.Write(records, pending, edition == "enterprise");
            return pending;
        }
    }

    public static string ExcelPath(PublishPlatform platform, string? storeRoot = null)
    {
        var edition = Edition(platform) ?? "personal";
        return ExcelPath(edition, storeRoot);
    }

    public static int CountToday(
        PublishPlatform platform,
        string accountId,
        string? workspaceDirectory,
        DateTime? today = null,
        string? storeRoot = null,
        IEnumerable<string>? projectDirectories = null,
        string? archiveRoot = null,
        string? nickname = null)
    {
        _ = workspaceDirectory;
        var edition = Edition(platform);
        if (edition is null)
            return 0;
        var day = (today ?? DateTime.Now).Date;
        var account = accountId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(account))
            return 0;
        EnsureMigrated(storeRoot);
        if (!string.IsNullOrWhiteSpace(nickname))
            KuaishouUploadRecordStore.FillEmptyNicknames(edition, account, nickname, storeRoot);
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in KuaishouUploadRecordStore.ListSuccess(edition, account, day, storeRoot))
            Remember(counted, names, string.IsNullOrWhiteSpace(record.ProjectDirectory) ? record.ProjectName : record.ProjectDirectory);

        var observation = new UploadObservation(platform, account, nickname, storeRoot);
        AddCompletedProjects(counted, names, observation, day, projectDirectories);
        AddArchivedProjects(counted, names, observation, day, archiveRoot);
        return counted.Count;
    }

    private readonly record struct UploadObservation(
        PublishPlatform Platform,
        string AccountId,
        string? Nickname,
        string? StoreRoot);

    private static void AddCompletedProjects(
        HashSet<string> counted,
        HashSet<string> names,
        UploadObservation observation,
        DateTime day,
        IEnumerable<string>? projectDirectories)
    {
        if (projectDirectories is null)
            return;
        var stateName = StateFileName(observation.Platform);
        foreach (var directory in projectDirectories)
            CountIfCompletedToday(counted, names, observation, directory, stateName, day);
    }

    private static void AddArchivedProjects(
        HashSet<string> counted,
        HashSet<string> names,
        UploadObservation observation,
        DateTime day,
        string? archiveRoot)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot) || !Directory.Exists(archiveRoot))
            return;
        var metaRoot = Path.Combine(archiveRoot, "meta");
        if (!Directory.Exists(metaRoot))
            return;
        var stateName = StateFileName(observation.Platform);
        foreach (var file in Directory.EnumerateFiles(metaRoot, "*.json"))
        {
            JsonElement meta;
            try
            {
                meta = ReadObject(file);
            }
            catch (IOException)
            {
                continue;
            }

            var owner = FirstText(
                ReadString(meta, "accountProfileId"),
                ReadString(meta, "account_profile_id"),
                ReadString(meta, "AccountProfileId"));
            if (!string.IsNullOrWhiteSpace(owner) &&
                !string.Equals(owner, observation.AccountId, StringComparison.OrdinalIgnoreCase))
                continue;
            var directory = FirstText(
                ReadString(meta, "archivedWorkflowDir"),
                ReadString(meta, "archived_workflow_dir"),
                ReadString(meta, "archivedSourceDir"),
                ReadString(meta, "archived_source_dir"),
                ReadString(meta, "workflowProjectDir"),
                ReadString(meta, "workflow_project_dir"));
            CountIfCompletedToday(counted, names, observation, directory, stateName, day);
        }
    }

    private static void CountIfCompletedToday(
        HashSet<string> counted,
        HashSet<string> names,
        UploadObservation observation,
        string? directory,
        string stateName,
        DateTime day)
    {
        var key = Normalize(directory);
        if (string.IsNullOrWhiteSpace(key) || counted.Contains(key))
            return;
        var name = ProjectName(key);
        if (!string.IsNullOrWhiteSpace(name) && names.Contains(name))
            return;
        if (!TryCompletedToday(key, stateName, day, out var completedAt))
            return;
        PersistObserved(observation, key, completedAt);
        Remember(counted, names, key);
    }

    private static void PersistObserved(UploadObservation observation, string directory, DateTimeOffset completedAt)
    {
        var edition = Edition(observation.Platform);
        if (edition is null)
            return;
        KuaishouArtifactColumnStatus? artifacts = null;
        try
        {
            if (Directory.Exists(directory))
                artifacts = KuaishouArtifactColumnStatusReader.Read(
                    directory,
                    observation.Platform == PublishPlatform.KuaishouEnterpriseRevenue);
        }
        catch
        {
            artifacts = null;
        }

        var original = string.IsNullOrWhiteSpace(artifacts?.OriginalTitle)
            ? ProjectName(directory).TrimStart('_')
            : artifacts!.OriginalTitle;
        var newer = string.IsNullOrWhiteSpace(artifacts?.NewTitle) ? original : artifacts!.NewTitle;
        KuaishouUploadRecordStore.Upsert(new KuaishouUploadRecord(
            edition,
            observation.AccountId,
            observation.Nickname ?? "",
            completedAt,
            "成功",
            string.IsNullOrWhiteSpace(newer) ? original : newer,
            original,
            newer,
            ReadInfoValue(directory, "作者"),
            artifacts?.EpisodeCount ?? 0,
            directory,
            ""), observation.StoreRoot);
    }

    private static void Remember(HashSet<string> counted, HashSet<string> names, string? path)
    {
        var key = Normalize(path);
        if (string.IsNullOrWhiteSpace(key) || !counted.Add(key))
            return;
        var name = ProjectName(key);
        if (!string.IsNullOrWhiteSpace(name))
            names.Add(name);
    }

    private static string ProjectName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var timeDash = name.LastIndexOf('-');
        if (timeDash <= 0 || name.Length - timeDash != 7)
            return name;
        var time = name[(timeDash + 1)..];
        if (time.Length != 6 || !time.All(char.IsDigit))
            return name;
        var dateDash = name.LastIndexOf('-', timeDash - 1);
        if (dateDash < 0 || timeDash - dateDash != 9)
            return name;
        var date = name[(dateDash + 1)..timeDash];
        return date.Length == 8 && date.All(char.IsDigit) ? name[..dateDash] : name;
    }

    private static string StateFileName(PublishPlatform platform) =>
        platform == PublishPlatform.KuaishouEnterpriseRevenue
            ? ".kuaishou-enterprise-upload-state.json"
            : ".kuaishou-personal-upload-state.json";

    private static bool TryCompletedToday(string projectDirectory, string stateName, DateTime day, out DateTimeOffset completedAt)
    {
        completedAt = default;
        if (!Directory.Exists(projectDirectory))
            return false;
        string workflow;
        try
        {
            workflow = KuaishouArtifactColumnStatusReader.ResolveDirectories(projectDirectory).WorkflowDirectory;
        }
        catch (IOException)
        {
            workflow = projectDirectory;
        }

        var path = Path.Combine(workflow, stateName);
        if (!File.Exists(path))
            path = Path.Combine(projectDirectory, stateName);
        if (!File.Exists(path))
            return false;
        var state = ReadObject(path);
        var status = FirstText(ReadString(state, "Status"), ReadString(state, "status")).ToLowerInvariant();
        if (status is "failed" or "error" or "stopped" or "draft_saved" or "manual_hold" or "final_hold")
            return false;
        var submitted = ReadBool(state, "ReviewSubmitted") || ReadBool(state, "review_submitted");
        if (status is not ("completed" or "success") && !submitted)
            return false;
        var raw = FirstText(ReadString(state, "UpdatedAt"), ReadString(state, "updatedAt"), ReadString(state, "updated_at"));
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            || at.ToLocalTime().Date != day.Date)
            return false;
        completedAt = at;
        return true;
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed);

    private static string ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string? Edition(PublishPlatform platform) => platform switch
    {
        PublishPlatform.KuaishouPersonalRevenue => "personal",
        PublishPlatform.KuaishouEnterpriseRevenue => "enterprise",
        _ => null,
    };

    private static string StorePath(string edition, string? storeRoot)
    {
        var root = string.IsNullOrWhiteSpace(storeRoot) ? PlatformPublisherPaths.DataRoot : storeRoot;
        var name = edition == "enterprise"
            ? "kuaishou_enterprise_upload_records.json"
            : "kuaishou_personal_upload_records.json";
        return Path.Combine(root, name);
    }

    public static void EnsureStore(string? storeRoot = null) => EnsureMigrated(storeRoot);

    private static void EnsureMigrated(string? storeRoot)
    {
        if (KuaishouUploadRecordStore.IsMigrated(storeRoot))
            return;
        lock (Gate)
        {
            if (KuaishouUploadRecordStore.IsMigrated(storeRoot))
                return;
            foreach (var edition in new[] { "personal", "enterprise" })
            {
                var imported = new List<(DateTime At, KuaishouUploadRecord Record)>();
                foreach (var row in Read(StorePath(edition, storeRoot)))
                {
                    var directory = Field(row, "项目路径", "ProjectDirectory");
                    var key = KuaishouUploadRecordStore.ProjectKey(directory);
                    if (string.IsNullOrWhiteSpace(key))
                        key = Field(row, "项目名称", "新剧名", "原剧名");
                    if (string.IsNullOrWhiteSpace(key))
                        continue;
                    var account = Field(row, "账号档案", "AccountId");
                    if (string.IsNullOrWhiteSpace(account))
                        continue;
                    var recorded = FirstText(Field(row, "记录时间", "RecordedAt"), Field(row, "日期"));
                    var at = ParseRecordedAt(recorded);
                    int.TryParse(Field(row, "剧集数", "集数"), out var episodes);
                    imported.Add((at.UtcDateTime, new KuaishouUploadRecord(
                        edition,
                        account,
                        Field(row, "快手昵称"),
                        at,
                        FirstText(Field(row, "上传状态", "Status"), "失败"),
                        FirstText(Field(row, "项目名称"), Field(row, "新剧名"), Field(row, "原剧名"), key),
                        Field(row, "原剧名"),
                        Field(row, "新剧名"),
                        Field(row, "作者"),
                        episodes,
                        directory,
                        Field(row, "失败原因"))));
                }

                foreach (var item in imported.OrderBy(item => item.At))
                    KuaishouUploadRecordStore.Upsert(item.Record, storeRoot);
            }

            KuaishouUploadRecordStore.MarkMigrated(storeRoot);
        }
    }

    private static Dictionary<string, string> ToRow(KuaishouUploadRecord record)
    {
        var local = record.RecordedAt.ToLocalTime();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["记录时间"] = local.ToString("yyyy-MM-dd HH:mm:ss"),
            ["日期"] = local.ToString("yyyy-MM-dd"),
            ["账号档案"] = record.AccountId,
            ["快手昵称"] = record.Nickname,
            ["项目名称"] = record.ProjectName,
            ["原剧名"] = record.OriginalTitle,
            ["新剧名"] = record.NewTitle,
            ["作者"] = record.Author,
            ["集数"] = record.EpisodeCount > 0 ? record.EpisodeCount.ToString() : "",
            ["上传状态"] = record.Status,
            ["失败原因"] = record.Error,
            ["项目路径"] = record.ProjectDirectory,
        };
    }

    private static DateTimeOffset ParseRecordedAt(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset)
            && offset.Offset != TimeSpan.Zero)
            return offset;
        if (DateTime.TryParseExact(raw, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)
            || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out wall))
            return new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Local));
        return DateTimeOffset.Now;
    }

    internal static List<Dictionary<string, string>> ReadRecords(PublishPlatform platform, string? storeRoot = null)
    {
        var edition = Edition(platform);
        if (edition is null)
            return [];
        EnsureMigrated(storeRoot);
        return KuaishouUploadRecordStore.List(edition, storeRoot).Select(ToRow).ToList();
    }

    private static List<Dictionary<string, string>> Read(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var records = new List<Dictionary<string, string>>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var record = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                    record[property.Name] = property.Value.ToString();
                records.Add(NormalizeRecord(record));
            }
            return records;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, string> NormalizeRecord(Dictionary<string, string> record)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in Headers)
            normalized[header] = Field(record, header);
        if (string.IsNullOrWhiteSpace(normalized["日期"]))
            normalized["日期"] = normalized["记录时间"].Length >= 10 ? normalized["记录时间"][..10] : "";
        if (string.IsNullOrWhiteSpace(normalized["上传状态"]))
            normalized["上传状态"] = Field(record, "Status");
        if (string.IsNullOrWhiteSpace(normalized["账号档案"]))
            normalized["账号档案"] = Field(record, "AccountId");
        if (string.IsNullOrWhiteSpace(normalized["项目路径"]))
            normalized["项目路径"] = Field(record, "ProjectDirectory");
        return normalized;
    }

    private static string Field(IReadOnlyDictionary<string, string> record, params string[] names)
    {
        foreach (var name in names)
        {
            if (record.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string ExcelPath(string edition, string? storeRoot)
    {
        var root = string.IsNullOrWhiteSpace(storeRoot) ? PlatformPublisherPaths.DataRoot : storeRoot;
        var name = edition == "enterprise"
            ? "kuaishou_enterprise_upload_records.xlsx"
            : "kuaishou_personal_upload_records.xlsx";
        return Path.Combine(root, name);
    }

    private static bool IsInProgressStatus(string status) =>
        status.Trim().ToLowerInvariant() is "running" or "uploading" or "publishing" or "preparing";

    private static string StatusLabel(string status)
    {
        return status.Trim().ToLowerInvariant() switch
        {
            "success" or "completed" => "成功",
            "failed" or "error" => "失败",
            "running" or "uploading" or "publishing" or "preparing" => "进行中",
            "stopped" => "已停止",
            "draft_saved" => "已保存草稿",
            "manual_hold" or "final_hold" => "已保持",
            "materials_ready" => "本地材料已就绪",
            _ => string.IsNullOrWhiteSpace(status) ? "未知" : status.Trim(),
        };
    }

    private static string FindStateFile(string projectDirectory, string fileName)
    {
        var direct = Path.Combine(projectDirectory, fileName);
        if (File.Exists(direct))
            return direct;
        try
        {
            return Directory.EnumerateFiles(projectDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault() ?? "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static JsonElement ReadObject(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return default;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string ReadInfoValue(string projectDirectory, string key)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(projectDirectory, "短剧信息.txt"),
                 })
        {
            if (!File.Exists(path))
                continue;
            foreach (var line in File.ReadLines(path))
            {
                var split = line.Split(new[] { '：', ':' }, 2);
                if (split.Length == 2 && string.Equals(split[0].Trim(), key, StringComparison.Ordinal))
                    return split[1].Trim();
            }
        }
        return "";
    }

    private static string FirstText(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static readonly string[] Headers =
    [
        "记录时间", "日期", "上传状态", "执行模式", "执行步骤", "项目名称", "原剧名", "新剧名", "作者",
        "项目路径", "剧集数", "已上传集数", "第一页提交动作", "当前阶段", "下载", "改写信息", "生成海报",
        "生成工程图", "补齐字段", "上传剧集", "账号档案", "本机设备名", "失败原因", "prepare_state_path", "upload_state_path",
    ];
}
