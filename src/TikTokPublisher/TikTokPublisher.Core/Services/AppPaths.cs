namespace TikTokPublisher.Core.Services;

/// <summary>TikTok Publisher 独立应用数据目录（与旧 Python 客户端路径无关）。</summary>
public static class AppPaths
{
    private static readonly string DefaultDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".tiktok_publisher");

    /// <summary>测试把数据目录指到临时文件夹，避免碰到正在使用的本机配置。</summary>
    internal static string? DataRootOverride { get; set; }

    public static string DataRoot =>
        string.IsNullOrWhiteSpace(DataRootOverride) ? DefaultDataRoot : DataRootOverride;

    public static string AccountsFile => Path.Combine(DataRoot, "accounts.json");
    public static string AccountConfigSyncStateFile => Path.Combine(DataRoot, "account-config-sync.json");
    public static string ActiveAccountFile => Path.Combine(DataRoot, "active-account.json");
    public static string AppDatabaseFile => Path.Combine(DataRoot, "app.db");
    public static string AnalyticsDatabaseFile => Path.Combine(DataRoot, "analytics.db");
    public static string ProfilesRoot => Path.Combine(DataRoot, "profiles");

    public static string ProfileDirFor(string accountId) => Path.Combine(ProfilesRoot, accountId);

    public static string DefaultStorageStatePath(string accountId) =>
        Path.Combine(ProfileDirFor(accountId), "tiktok_auth_state.json");
}
