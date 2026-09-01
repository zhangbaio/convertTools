namespace PlatformPublisher.Core.Services;

public static class PlatformPublisherPaths
{
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YunfanPlatformPublisher");

    public static string JobStorePath => Path.Combine(DataRoot, "publish-jobs.json");
}
