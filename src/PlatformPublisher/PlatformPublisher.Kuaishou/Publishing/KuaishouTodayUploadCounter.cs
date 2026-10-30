using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 快手分账今日成功上传剧集数。个人版和企业版分开记，同一账号同一项目当天只计一次。
/// </summary>
public static class KuaishouTodayUploadCounter
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void RecordSuccess(
        PublishPlatform platform,
        string accountId,
        string projectDirectory,
        DateTimeOffset? recordedAt = null,
        string? storeRoot = null)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
            return;
        var edition = Edition(platform);
        if (edition is null)
            return;
        var at = recordedAt ?? DateTimeOffset.Now;
        Append(platform, new Dictionary<string, string>
        {
            ["记录时间"] = at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            ["日期"] = at.ToLocalTime().ToString("yyyy-MM-dd"),
            ["上传状态"] = "成功",
            ["账号档案"] = accountId?.Trim() ?? "",
            ["项目路径"] = Path.GetFullPath(projectDirectory),
        }, storeRoot);
    }

    public static void AppendRun(
        PublishPlatform platform,
        string accountId,
        string accountName,
        string projectDirectory,
        bool succeeded,
        string? error,
        string? storeRoot = null)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || Edition(platform) is null)
            return;
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
        var prepareName = enterprise ? ".kuaishou-enterprise-prepare-state.json" : ".kuaishou-personal-prepare-state.json";
        var statePath = FindStateFile(projectDirectory, stateName);
        var preparePath = FindStateFile(projectDirectory, prepareName);
        var state = ReadObject(statePath);
        var now = DateTimeOffset.Now;
        var rawStatus = succeeded
            ? FirstText(ReadString(state, "Status"), ReadString(state, "status"), "completed")
            : "failed";
        var uploaded = ReadBool(state, "ReviewSubmitted") || ReadBool(state, "review_submitted")
            ? artifacts?.EpisodeCount.ToString() ?? ""
            : "";
        var originalTitle = artifacts?.OriginalTitle ?? Path.GetFileName(projectDirectory);
        var newTitle = artifacts?.NewTitle ?? originalTitle;
        Append(platform, new Dictionary<string, string>
        {
            ["记录时间"] = now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            ["日期"] = now.ToLocalTime().ToString("yyyy-MM-dd"),
            ["上传状态"] = StatusLabel(rawStatus),
            ["执行模式"] = "执行勾选步骤",
            ["执行步骤"] = StepText(artifacts),
            ["项目名称"] = string.IsNullOrWhiteSpace(newTitle) ? originalTitle : newTitle,
            ["原剧名"] = originalTitle,
            ["新剧名"] = newTitle,
            ["作者"] = ReadInfoValue(projectDirectory, "作者"),
            ["项目路径"] = Path.GetFullPath(projectDirectory),
            ["剧集数"] = artifacts is { EpisodeCount: > 0 } ? artifacts.EpisodeCount.ToString() : "",
            ["已上传集数"] = uploaded,
            ["第一页提交动作"] = FirstPageActionLabel(ReadString(state, "CurrentStage")),
            ["当前阶段"] = FirstText(ReadString(state, "CurrentStage"), ReadString(state, "current_stage")),
            ["下载"] = artifacts?.DownloadStatus ?? "",
            ["改写信息"] = artifacts?.RewriteStatus ?? "",
            ["生成海报"] = artifacts?.PosterStatus ?? "",
            ["生成工程图"] = artifacts?.ProjectImageStatus ?? "",
            ["补齐字段"] = artifacts?.AutoFillStatus ?? "",
            ["上传剧集"] = artifacts?.UploadStatus ?? "",
            ["账号档案"] = FirstText(accountId, accountName),
            ["本机设备名"] = Environment.MachineName,
            ["失败原因"] = succeeded ? "" : error?.Trim() ?? "",
            ["prepare_state_path"] = preparePath,
            ["upload_state_path"] = statePath,
        }, storeRoot);
    }

    public static string ExportExcel(PublishPlatform platform, string? storeRoot = null)
    {
        var edition = Edition(platform) ?? throw new InvalidOperationException("只有快手分账可以导出上传记录。");
        var records = Read(StorePath(edition, storeRoot));
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
        string? storeRoot = null)
    {
        var edition = Edition(platform);
        if (edition is null)
            return 0;
        var day = (today ?? DateTime.Now).Date;
        var account = accountId?.Trim() ?? "";
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedByOtherAccount = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in Read(StorePath(edition, storeRoot)))
        {
            if (!string.Equals(Field(record, "上传状态", "Status"), "成功", StringComparison.Ordinal))
                continue;
            if (!IsSameDay(record, day))
                continue;
            var key = Normalize(Field(record, "项目路径", "ProjectDirectory"));
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (string.Equals(Field(record, "账号档案", "AccountId"), account, StringComparison.OrdinalIgnoreCase))
                counted.Add(key);
            else
                ownedByOtherAccount.Add(key);
        }

        if (!string.IsNullOrWhiteSpace(workspaceDirectory) && Directory.Exists(workspaceDirectory))
        {
            var stateName = edition == "enterprise"
                ? ".kuaishou-enterprise-upload-state.json"
                : ".kuaishou-personal-upload-state.json";
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(workspaceDirectory, stateName, SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                files = Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                if (!TryReadSubmittedToday(file, day, out var projectDirectory))
                    continue;
                var key = Normalize(projectDirectory);
                if (ownedByOtherAccount.Contains(key))
                    continue;
                counted.Add(key);
            }
        }

        return counted.Count;
    }

    private static bool TryReadSubmittedToday(string statePath, DateTime day, out string projectDirectory)
    {
        projectDirectory = Path.GetDirectoryName(statePath) ?? "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = document.RootElement;
            if (!ReadBool(root, "ReviewSubmitted") && !ReadBool(root, "review_submitted"))
                return false;
            var updated = ReadString(root, "UpdatedAt");
            if (string.IsNullOrWhiteSpace(updated))
                updated = ReadString(root, "updated_at");
            return DateTimeOffset.TryParse(updated, out var parsed) && parsed.ToLocalTime().Date == day;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
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

    private static void Append(PublishPlatform platform, Dictionary<string, string> record, string? storeRoot)
    {
        var edition = Edition(platform);
        if (edition is null)
            return;
        lock (Gate)
        {
            var path = StorePath(edition, storeRoot);
            var records = Read(path);
            records.Add(NormalizeRecord(record));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOptions));
        }
    }

    internal static List<Dictionary<string, string>> ReadRecords(PublishPlatform platform, string? storeRoot = null)
    {
        var edition = Edition(platform);
        return edition is null ? [] : Read(StorePath(edition, storeRoot));
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

    private static bool IsSameDay(IReadOnlyDictionary<string, string> record, DateTime day)
    {
        var date = Field(record, "日期");
        if (date.Length >= 10 && DateTime.TryParse(date[..10], out var parsedDate))
            return parsedDate.Date == day;
        var time = Field(record, "记录时间", "RecordedAt");
        return DateTimeOffset.TryParse(time, out var parsed) && parsed.ToLocalTime().Date == day;
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

    private static string FirstPageActionLabel(string stage) =>
        stage.Contains("review", StringComparison.OrdinalIgnoreCase) ? "下一步" : "";

    private static string StepText(KuaishouArtifactColumnStatus? artifacts)
    {
        if (artifacts is null)
            return "-";
        var parts = new List<string>();
        if (artifacts.DownloadStatus.StartsWith("已完成", StringComparison.Ordinal)) parts.Add("下载");
        if (artifacts.RewriteStatus.StartsWith("已完成", StringComparison.Ordinal)) parts.Add("改写信息");
        if (artifacts.PosterStatus.StartsWith("已完成", StringComparison.Ordinal)) parts.Add("生成海报");
        if (artifacts.ProjectImageStatus.StartsWith("已完成", StringComparison.Ordinal)) parts.Add("生成工程图");
        if (artifacts.AutoFillStatus.StartsWith("已完成", StringComparison.Ordinal)) parts.Add("补齐字段");
        if (artifacts.UploadStatus.StartsWith("已完成", StringComparison.Ordinal)) parts.Add("上传剧集");
        return parts.Count == 0 ? "-" : string.Join("、", parts);
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
