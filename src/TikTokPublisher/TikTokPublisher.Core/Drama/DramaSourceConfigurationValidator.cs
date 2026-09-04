using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Drama;

public sealed record DramaSourceConfigurationStatus(string Source, bool IsConfigured, string Message);

public static class DramaSourceConfigurationValidator
{
    public static DramaSourceConfigurationStatus Check(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var source = Normalize(settings.DramaSourceChain);
        var missing = source switch
        {
            "hgnew" => Missing((settings.HgnewAccount, "账号"), (settings.HgnewPassword, "密码"), (settings.HgnewUdid, "UDID/DeviceId")),
            "hghigh" => Missing((settings.HghighAccount, "账号"), (settings.HghighPassword, "密码")),
            "mapleleaf" => Missing((settings.MapleleafAccount, "账号"), (settings.MapleleafPassword, "密码"), (settings.MapleleafUdid, "DeviceUDID")),
            "hglocal" => Missing((settings.HongguoLocalBaseUrl, "本地服务地址")),
            "pikachu" => Missing((settings.PikachuServerUrl, "服务器地址")),
            "downloader" => Missing((settings.DownloaderApiBaseUrl, "下载器地址")),
            _ => ["有效的数据链路"],
        };

        var label = Label(source);
        return missing.Count == 0
            ? new(source, true, $"数据链路：{label}（已配置）")
            : new(source, false, $"数据链路“{label}”未配置：{string.Join("、", missing)}。请前往“系统设置 → 数据链路与下载参数”完成配置。");
    }

    private static List<string> Missing(params (string? Value, string Label)[] values) =>
        values.Where(item => string.IsNullOrWhiteSpace(item.Value)).Select(item => item.Label).ToList();

    private static string Normalize(string? value) => (value ?? "hgnew").Trim().ToLowerInvariant();

    private static string Label(string source) => source switch
    {
        "hgnew" => "红果新接口",
        "hghigh" => "红果高码率",
        "mapleleaf" => "Mapleleaf",
        "hglocal" => "本地直连",
        "pikachu" => "皮卡丘",
        "downloader" => "统一下载器",
        _ => source,
    };
}
