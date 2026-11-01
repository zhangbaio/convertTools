using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 链路 2：用已注册的播放设备身份对 multi_video_model/v1 做 PureSign，
/// 再按 fallback_api 取媒体模型，并解密 key_seed 保护的地址。
/// 签名与地址解密来自 jackotom/hongguo-drama 的 app-source.js（GPL-3.0），
/// 经 sunnysky123/hongguo-web 625e4e1 的 AppSource。
/// </summary>
internal sealed class HongguoSignedPlayback
{
    private const string VideoApi = "https://api5-normal-sinfonlineb.fqnovel.com/novel/player/multi_video_model/v1/";
    private const string UserAgent =
        "com.phoenix.read/71332 (Linux; U; Android 16; zh_CN; 25053RT47C; "
        + "Build/BP2A.250605.031.A3; Cronet/TTNetVersion:04657795 2026-01-23 "
        + "QuicVersion:c67e9834 2025-09-08)";

    internal static readonly string[] ApiDomains = ["fqnovel.com", "snssdk.com"];
    internal static readonly string[] VodDomains = ["qznovelvod.com", "fqnovelvod.com", "douyinvod.com"];

    private static readonly (string Key, string Value)[] Device =
    [
        ("ac", "wifi"),
        ("channel", "update_64"),
        ("aid", "8662"),
        ("app_name", "novelread"),
        ("version_code", "71332"),
        ("version_name", "7.1.3.32"),
        ("device_platform", "android"),
        ("os", "android"),
        ("ssmix", "a"),
        ("device_type", "25053RT47C"),
        ("device_brand", "Redmi"),
        ("language", "zh"),
        ("os_api", "36"),
        ("os_version", "16"),
        ("manifest_version_code", "71332"),
        ("resolution", "1280*2772"),
        ("dpi", "520"),
        ("update_version_code", "71332"),
        ("host_abi", "arm64-v8a"),
        ("dragon_device_type", "phone"),
        ("pv_player", "71332"),
        ("compliance_status", "0"),
        ("need_personal_recommend", "1"),
        ("player_so_load", "1"),
        ("is_android_pad_screen", "0"),
    ];

    private static readonly HttpClient Direct = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly HongguoGuestPlayback _playback;

    internal HongguoSignedPlayback(HongguoGuestPlayback playback) => _playback = playback;

    internal async Task<HongguoHighVideoPlayback> GetAsync(
        string videoId,
        string quality,
        CancellationToken cancellationToken,
        string? deviceId = null,
        string? installId = null,
        Action<string, string>? persist = null)
    {
        Exception? last = null;
        for (var refresh = 0; refresh < 2; refresh++)
        {
            try
            {
                var identity = await _playback.SharedIdentityAsync(
                    refresh == 1, deviceId, installId, persist, cancellationToken);
                var model = await ModelAsync(videoId, identity.DeviceId, identity.InstallId, cancellationToken);
                RevealUrls(model);
                try
                {
                    return HongguoGuestPlayback.Pick(model, quality);
                }
                catch (HongguoHighException ex) when (ex.Message == "播放地址为空")
                {
                    throw new HongguoHighException("链路 2 缺少媒体档位", 0, ex);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HongguoHighException ex) when (refresh == 0 && ex.Message == "链路 2 接口返回业务错误")
            {
                last = ex;
            }
        }

        throw last ?? new HongguoHighException("链路 2 接口返回业务错误");
    }

    private static async Task<JsonObject> ModelAsync(
        string videoId,
        string deviceId,
        string installId,
        CancellationToken cancellationToken)
    {
        var body = Body(videoId);
        var root = await PostAsync(body, deviceId, installId, cancellationToken);
        if (HongguoGuestPlayback.CodeOf(root) != 0 || root["data"] is not JsonObject data)
            throw new HongguoHighException("链路 2 接口返回业务错误");
        var model = HongguoGuestPlayback.ModelOf(data[videoId]?["video_model"]);
        if (model is null)
            throw new HongguoHighException("链路 2 缺少分集模型");
        if (HasTracks(model))
            return model;

        var fallback = ParseFallback(model["fallback_api"]);
        if (string.IsNullOrWhiteSpace(fallback))
            throw new HongguoHighException("链路 2 缺少媒体模型地址");
        return await FallbackAsync(fallback, cancellationToken);
    }

    private static async Task<JsonObject> PostAsync(
        byte[] body,
        string deviceId,
        string installId,
        CancellationToken cancellationToken)
    {
        var khronos = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ticket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = BuildUrl(deviceId, installId, ticket, khronos);
        for (var guard = 0; HongguoPureSign.BranchOf(url, body, khronos) == 1 && guard < 32; guard++)
        {
            ticket++;
            url = BuildUrl(deviceId, installId, ticket, khronos);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8,application/x-protobuf");
        request.Headers.TryAddWithoutValidation("x-xs-from-web", "0");
        request.Headers.TryAddWithoutValidation("x-ss-req-ticket", ticket.ToString());
        request.Headers.TryAddWithoutValidation("x-tt-request-tag", "t=0;n=0");
        request.Headers.TryAddWithoutValidation("sdk-version", "2");
        request.Headers.TryAddWithoutValidation("passport-sdk-version", "50561");
        request.Headers.TryAddWithoutValidation("x-vc-bdturing-sdk-version", "3.7.2.cn");
        request.Headers.TryAddWithoutValidation("x-ss-stub", Convert.ToHexString(MD5.HashData(body)));
        foreach (var header in HongguoPureSign.Sign(url, body, khronos))
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Headers.TryAddWithoutValidation("x-tt-dt", "");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return await ReadObjectAsync(request, timeout.Token);
    }

    private static async Task<JsonObject> FallbackAsync(string fallback, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequireHttpsHost(fallback, ApiDomains));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var outer = await ReadObjectAsync(request, timeout.Token);
        var info = outer["video_info"] as JsonObject;
        if (HongguoGuestPlayback.CodeOf(outer) != 0 || info is null || HongguoGuestPlayback.CodeOf(info) != 0)
            throw new HongguoHighException("链路 2 接口返回业务错误");
        if (info["data"] is not JsonObject data || !HasTracks(data))
            throw new HongguoHighException("链路 2 缺少分集模型");
        return data;
    }

    private static async Task<JsonObject> ReadObjectAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await Direct.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HongguoHighException("链路 2 不接受接口跳转");
        var bytes = await HongguoGuestPlayback.ReadBodyAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode || bytes.Length == 0)
            throw new HongguoHighException("链路 2 接口返回业务错误");
        if (JsonNode.Parse(bytes) is not JsonObject root)
            throw new HongguoHighException("链路 2 接口返回业务错误");
        return root;
    }

    private static string BuildUrl(string deviceId, string installId, long ticket, int khronos)
    {
        var pairs = new List<string>();
        foreach (var (key, value) in Device)
            pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
        pairs.Add("iid=" + Uri.EscapeDataString(installId));
        pairs.Add("device_id=" + Uri.EscapeDataString(deviceId));
        pairs.Add("ts=" + khronos);
        pairs.Add("_rticket=" + ticket);
        return VideoApi + "?" + string.Join("&", pairs);
    }

    private static byte[] Body(string videoId)
    {
        var body = new JsonObject
        {
            ["biz_param"] = new JsonObject
            {
                ["detail_page_version"] = 0,
                ["device_level"] = 3,
                ["disable_digg_stat"] = false,
                ["need_all_video_definition"] = true,
                ["need_mp4_align"] = false,
                ["use_os_player"] = false,
                ["use_server_dns"] = false,
                ["video_platform"] = 1024
            },
            ["mixed_video_id_map"] = new JsonObject
            {
                ["1004"] = new JsonArray(videoId)
            }
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString(HongguoGuestPlayback.CompactJson));
    }

    private static bool HasTracks(JsonObject model)
    {
        return model["video_list"] switch
        {
            JsonArray array => array.OfType<JsonObject>().Any(),
            JsonObject obj => obj.Any(pair => pair.Value is JsonObject),
            _ => false
        };
    }

    private static string ParseFallback(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                var trimmed = text.Trim();
                if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                {
                    try
                    {
                        return ParseFallback(JsonNode.Parse(trimmed));
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        return trimmed;
                    }
                }
                return trimmed;
            case JsonArray { Count: > 0 } array:
                return ParseFallback(array[0]);
            case JsonObject obj:
                return ParseFallback(obj["fallback_api"]);
            default:
                return "";
        }
    }

    private static void RevealUrls(JsonObject model)
    {
        byte[]? seed = null;
        var seedText = HongguoGuestPlayback.Text(model, "key_seed");
        if (!string.IsNullOrWhiteSpace(seedText))
        {
            try
            {
                seed = Convert.FromBase64String(seedText);
            }
            catch (FormatException)
            {
                throw new HongguoHighException("链路 2 地址密文格式无效");
            }
        }

        switch (model["video_list"])
        {
            case JsonArray array:
                foreach (var track in array.OfType<JsonObject>())
                    RevealTrack(track, seed);
                break;
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    if (pair.Value is JsonObject track)
                        RevealTrack(track, seed);
                }
                break;
        }
    }

    private static void RevealTrack(JsonObject track, byte[]? seed)
    {
        foreach (var name in new[] { "main_url", "backup_url", "backup_url_1", "backup_url_2", "backup_url_3" })
            RevealField(track, name, seed);
        if (track["backup_url_list"] is not JsonArray list)
            return;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonValue value || !value.TryGetValue<string>(out var text))
                continue;
            var opened = OpenUrl(text, seed, required: false);
            if (opened is null)
                list[i] = "";
            else
                list[i] = opened;
        }
    }

    private static void RevealField(JsonObject track, string name, byte[]? seed)
    {
        var text = HongguoGuestPlayback.Text(track, name);
        if (string.IsNullOrWhiteSpace(text))
            return;
        var opened = OpenUrl(text, seed, name == "main_url");
        if (opened is null)
            track.Remove(name);
        else
            track[name] = opened;
    }

    private static string? OpenUrl(string text, byte[]? seed, bool required)
    {
        var clean = Clean(text);
        if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return RequireHttpsHost(clean, VodDomains);
        if (seed is { Length: > 0 })
        {
            try
            {
                return RequireHttpsHost(DecryptMediaUrl(clean, seed), VodDomains);
            }
            catch (HongguoHighException) when (!required)
            {
                return null;
            }
            catch (HongguoHighException)
            {
                // 带 key_seed 时仍可能是普通 Base64 直链，下面再试一次。
            }
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(clean)).Trim();
            if (decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return RequireHttpsHost(Clean(decoded), VodDomains);
        }
        catch (FormatException)
        {
            // 不是 Base64 时继续按密文失败处理。
        }

        if (required && seed is { Length: > 0 })
            throw new HongguoHighException("链路 2 地址密文格式无效");
        return null;
    }

    internal static string DecryptMediaUrl(string value, byte[] seed)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new HongguoHighException("链路 2 地址密文格式无效");
        }

        if (raw.Length < 5 || raw[0] != 0xA8 || raw[2] != 0x01 || raw[3] != 0x00)
            throw new HongguoHighException("链路 2 地址密文格式无效");
        var cipherLength = (raw.Length - 4) / 16 * 16;
        if (cipherLength == 0)
            throw new HongguoHighException("链路 2 地址密文格式无效");

        var hash = SHA512.HashData(seed);
        var salt = UrlConstants();
        var mixed = new byte[hash.Length + salt.Length];
        hash.CopyTo(mixed, 0);
        salt.CopyTo(mixed, hash.Length);
        var derived = SHA512.HashData(mixed);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = derived[..16];
        aes.IV = derived[16..32];
        var plain = aes.CreateDecryptor().TransformFinalBlock(raw, 4, cipherLength);
        var end = plain.Length;
        if (end > 0)
        {
            var pad = plain[end - 1];
            if (pad is >= 1 and <= 16 && pad <= end)
                end -= pad;
        }
        while (end > 0 && plain[end - 1] == 0)
            end--;
        return Encoding.UTF8.GetString(plain, 0, end);
    }

    internal static string RequireHttpsHost(string value, IReadOnlyList<string> domains)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!uri.IsDefaultPort && uri.Port != 443) ||
            !HostAllowed(uri.IdnHost, domains))
            throw new HongguoHighException("链路 2 地址不受支持");
        return uri.ToString();
    }

    private static bool HostAllowed(string host, IReadOnlyList<string> domains)
    {
        foreach (var domain in domains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Clean(string value) =>
        value.Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    private static byte[] UrlConstants() => Convert.FromHexString(
        "4dd4c2e6b83162090e52b3c7a6733ba41cb2462b829ab58a196b39db57177524" +
        "f49baf7f08e8d68d26a72e37c1a95a2f1f05a51892aef2949732b62a38aadd58");
}
