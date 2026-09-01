namespace PlatformPublisher.Common.Services;

public static class PlatformPublisherPaths
{
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YunfanPlatformPublisher");

    public static string JobStorePath => Path.Combine(DataRoot, "publish-jobs.json");
    public static string AccountStorePath => Path.Combine(DataRoot, "publish-accounts.json");
    public static string SettingsDatabasePath => Path.Combine(DataRoot, "platform-settings.db");
}
