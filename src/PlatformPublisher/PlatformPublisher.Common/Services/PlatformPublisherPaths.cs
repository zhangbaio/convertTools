namespace PlatformPublisher.Common.Services;

public static class PlatformPublisherPaths
{
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YunfanPlatformPublisher");

    public static string JobStorePath => Path.Combine(DataRoot, "publish-jobs.json");
    public static string AccountStorePath => Path.Combine(DataRoot, "publish-accounts.json");
    public static string MainDatabasePath => Path.Combine(DataRoot, "app.db");
    public static string SettingsDatabasePath => MainDatabasePath;
    public static string AnalyticsDatabasePath => MainDatabasePath;
    public static string LegacySettingsDatabasePath => Path.Combine(DataRoot, "platform-settings.db");
    public static string LegacyAnalyticsDatabasePath => Path.Combine(DataRoot, "analytics.db");
    public static string BackupRoot => Path.Combine(DataRoot, "migration-backups");
}
