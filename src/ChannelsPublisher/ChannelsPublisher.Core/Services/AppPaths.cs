namespace ChannelsPublisher.Core.Services;

/// <summary>应用数据目录解析。每账号的会话目录在 profiles/&lt;accountId&gt; 下。</summary>
public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChannelsPublisher");

    public static string AccountsFile => Path.Combine(DataRoot, "accounts.json");
    public static string ProfilesRoot => Path.Combine(DataRoot, "profiles");
    public static string ProfileDirFor(string accountId) => Path.Combine(ProfilesRoot, accountId);
}
