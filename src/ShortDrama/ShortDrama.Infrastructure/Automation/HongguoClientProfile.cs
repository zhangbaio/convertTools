namespace ShortDrama.Infrastructure.Automation;

public sealed record HongguoClientProfile(
    string Edition,
    string DisplayName,
    string ApiBase,
    string AppId,
    string ClientVersion,
    string RegistryKey,
    string LocalDataDirectory,
    string CacheDirectory,
    string EnvironmentPrefix,
    bool UsesServerBatchPlayback)
{
    public const string HighEdition = "high";
    public const string StandardEdition = "standard";

    public static HongguoClientProfile High { get; } = new(
        HighEdition,
        "高码率版 2.1.6",
        "https://m.iusc.cc/api/hbr/client/v1",
        "hongguo_high_bitrate_desktop",
        "2.1.6",
        @"Software\HongGuoHighDownloader",
        "HongguoHighDownloader",
        "HongguoHighClient",
        "HGHIGH",
        UsesServerBatchPlayback: true);

    public static HongguoClientProfile Standard { get; } = new(
        StandardEdition,
        "标准版 2.1.7",
        "https://m.iusc.cc/api/hongguo/client/v1",
        "hongguo_desktop",
        "2.1.7",
        @"Software\HongguoDownloader",
        "HongguoDownloader",
        "HongguoHighClient",
        "HGSTANDARD",
        UsesServerBatchPlayback: false);

    public byte[] DpapiEntropy => System.Text.Encoding.ASCII.GetBytes(AppId);

    public static string NormalizeEdition(string? edition) =>
        string.Equals(edition?.Trim(), StandardEdition, StringComparison.OrdinalIgnoreCase)
            ? StandardEdition
            : HighEdition;

    public static HongguoClientProfile Resolve(string? edition) =>
        NormalizeEdition(edition) == StandardEdition ? Standard : High;
}
