using System.Text.Json;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 工作目录 ↔ 账号绑定。对齐 Python <c>.tiktok-uploader-workspace.json</c> 契约。
/// </summary>
public static class WorkspaceBindingService
{
    public const string BindingFileName = ".tiktok-uploader-workspace.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class WorkspaceBinding
    {
        public string AccountProfileId { get; set; } = "";
        public string AccountProfileName { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    public static string BindingPath(string workspaceRoot) =>
        Path.Combine(Path.GetFullPath(workspaceRoot), BindingFileName);

    public static WorkspaceBinding? Load(string workspaceRoot)
    {
        try
        {
            var path = BindingPath(workspaceRoot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WorkspaceBinding>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveAccountProfileId(string workspaceRoot)
    {
        var binding = Load(workspaceRoot);
        return string.IsNullOrWhiteSpace(binding?.AccountProfileId) ? null : binding.AccountProfileId.Trim();
    }

    public static void Bind(string workspaceRoot, string accountProfileId, string accountProfileName)
    {
        var root = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(root);
        var payload = new WorkspaceBinding
        {
            AccountProfileId = accountProfileId.Trim(),
            AccountProfileName = accountProfileName.Trim(),
            UpdatedAt = DateTimeOffset.Now.ToString("o"),
        };
        File.WriteAllText(BindingPath(root), JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static void Unbind(string workspaceRoot)
    {
        var path = BindingPath(workspaceRoot);
        if (File.Exists(path)) File.Delete(path);
    }
}
