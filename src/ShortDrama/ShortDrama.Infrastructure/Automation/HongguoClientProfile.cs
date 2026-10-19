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
    public const string StandardPlaybackSingle = "single";
    public const string StandardPlaybackBatch = "batch";

    public static string NormalizeStandardPlaybackMode(string? value) =>
        string.Equals(value?.Trim(), StandardPlaybackBatch, StringComparison.OrdinalIgnoreCase)
            ? StandardPlaybackBatch
            : StandardPlaybackSingle;

    public static bool IsStandardBatchPlayback(string? value) =>
        NormalizeStandardPlaybackMode(value) == StandardPlaybackBatch;

    public static HongguoClientProfile High { get; } = new(
        HighEdition,
        "高码率版",
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
        "标准版",
        "https://m.iusc.cc/api/hongguo/client/v1",
        "hongguo_desktop",
        "2.2.1",
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

    public HongguoClientProfile WithClientVersion(string? version)
    {
        var text = (version ?? "").Trim();
        return string.IsNullOrWhiteSpace(text) || string.Equals(text, ClientVersion, StringComparison.Ordinal)
            ? this
            : this with { ClientVersion = text };
    }
}
