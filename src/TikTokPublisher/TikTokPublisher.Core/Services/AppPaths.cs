namespace TikTokPublisher.Core.Services;

/// <summary>TikTok Publisher 独立应用数据目录（与旧 Python 客户端路径无关）。</summary>
public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".tiktok_publisher");

    public static string AccountsFile => Path.Combine(DataRoot, "accounts.json");
    public static string ActiveAccountFile => Path.Combine(DataRoot, "active-account.json");
    public static string AppDatabaseFile => Path.Combine(DataRoot, "app.db");
    public static string ProfilesRoot => Path.Combine(DataRoot, "profiles");

    public static string ProfileDirFor(string accountId) => Path.Combine(ProfilesRoot, accountId);

    public static string DefaultStorageStatePath(string accountId) =>
        Path.Combine(ProfileDirFor(accountId), "tiktok_auth_state.json");
}
