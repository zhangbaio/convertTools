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
        var record = new UploadRecord
        {
            AccountId = accountId?.Trim() ?? "",
            ProjectDirectory = Path.GetFullPath(projectDirectory),
            Status = "成功",
            RecordedAt = recordedAt ?? DateTimeOffset.Now,
        };
        lock (Gate)
        {
            var path = StorePath(edition, storeRoot);
            var records = Read(path);
            records.Add(record);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOptions));
        }
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
            if (!string.Equals(record.Status, "成功", StringComparison.Ordinal))
                continue;
            if (record.RecordedAt.ToLocalTime().Date != day)
                continue;
            var key = Normalize(record.ProjectDirectory);
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (string.Equals(record.AccountId, account, StringComparison.OrdinalIgnoreCase))
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

    private static List<UploadRecord> Read(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<UploadRecord>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
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

    private sealed class UploadRecord
    {
        public string AccountId { get; set; } = "";
        public string ProjectDirectory { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTimeOffset RecordedAt { get; set; }
    }
}
