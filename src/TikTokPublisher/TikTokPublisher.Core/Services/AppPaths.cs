namespace TikTokPublisher.Core.Services;

/// <summary>
/// 应用数据目录。与 Python 客户端一致：<c>%USERPROFILE%/.tiktok_uploader_client</c>，
/// 便于后续复用已有 profiles / storage_state。
/// </summary>
public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".tiktok_uploader_client");

    public static string AccountsFile => Path.Combine(DataRoot, "tiktok-accounts.json");
    public static string ActiveAccountFile => Path.Combine(DataRoot, "active-tiktok-account.json");
    public static string PythonDatabaseFile => Path.Combine(DataRoot, "tiktok_uploader.db");
    public static string ProfilesRoot => Path.Combine(DataRoot, "profiles");
    public static string ProfileDirFor(string accountId) => Path.Combine(ProfilesRoot, accountId);

    public static string DefaultStorageStatePath(string accountId) =>
        Path.Combine(ProfileDirFor(accountId), "tiktok_auth_state.json");
}
