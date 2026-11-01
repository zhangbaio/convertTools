using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 匿名设备注册后，向 multi_video_model/v1 取播放地址。这条链路不带签名头。
/// 注册包格式来自 tsukiyo-zero/hongguoTV DeviceRegister.kt（GPL-3.0）。
/// </summary>
internal sealed class HongguoGuestPlayback
{
    private const string RegisterUrl =
        "https://log5-applog-lf.fqnovel.com/service/2/device_register/?tt_data=a&cronet_version=8d40f833_2026-03-03&ttnet_version=4.2.243.31-douyin&use_store_region_cookie=1";
    private const string VideoApi = "https://api5-normal-sinfonlineb.fqnovel.com/novel/player/multi_video_model/v1/";
    private const string RegisterUserAgent =
        "com.phoenix.read/73932 (Linux; U; Android 16; zh_CN; 25053RT47C; Build/BP2A.250605.031.A3)";
    private const string PlaybackUserAgent =
        "com.phoenix.read/71532 (Linux; U; Android 9; SM-N9860; Build/PQ3A.190705.10241111;tt-ok/3.12.13.20)";

    private static readonly byte[] KdfSalt = Convert.FromHexString(
        "4dd4c2e6b83162090e52b3c7a6733ba41cb2462b829ab58a196b39db57177524" +
        "f49baf7f08e8d68d26a72e37c1a95a2f1f05a51892aef2949732b62a38aadd58");
    private static readonly (string Key, string Value)[] DeviceBase =
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

    internal static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Identity? _identity;

    internal HongguoGuestPlayback(HttpClient httpClient) => _httpClient = httpClient;

    internal async Task<HongguoHighVideoPlayback> GetAsync(
        string videoId,
        string quality,
        CancellationToken cancellationToken,
        string? deviceId = null,
        string? installId = null,
        Action<string, string>? persist = null)
    {
        var model = await ModelAsync(videoId, deviceId, installId, persist, cancellationToken);
        return Pick(model, quality);
    }

    internal async Task<TrackProbe> ProbeAsync(
        string videoId,
        CancellationToken cancellationToken,
        string? deviceId = null,
        string? installId = null,
        Action<string, string>? persist = null)
    {
        var model = await ModelAsync(videoId, deviceId, installId, persist, cancellationToken);
        var tracks = TracksOf(model);
        var count = 0;
        var allUrl = tracks.Count > 0;
        var allSpade = tracks.Count > 0;
        foreach (var track in tracks)
        {
            count++;
            if (HttpUrl(Text(track, "main_url")) is null)
                allUrl = false;
            if (string.IsNullOrWhiteSpace(SpadeOf(track)))
                allSpade = false;
        }
        return new TrackProbe(count, allUrl, allSpade);
    }

    internal static bool IsSavedIdentity(string? deviceId, string? installId) =>
        IsIdentityPart(deviceId) && IsIdentityPart(installId);

    internal async Task<(string DeviceId, string InstallId)> SharedIdentityAsync(
        bool refresh,
        string? deviceId,
        string? installId,
        Action<string, string>? persist,
        CancellationToken cancellationToken)
    {
        var identity = await IdentityAsync(refresh, deviceId, installId, persist, cancellationToken);
        return (identity.DeviceId, identity.InstallId);
    }

    private async Task<JsonObject> ModelAsync(
        string videoId,
        string? deviceId,
        string? installId,
        Action<string, string>? persist,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var refresh = 0; refresh < 2; refresh++)
        {
            try
            {
                var data = await PostAsync(videoId, refresh == 1, deviceId, installId, persist, cancellationToken);
                var model = ModelOf(data[videoId]?["video_model"]);
                if (model is not null)
                    return model;
                last = new HongguoHighException("播放地址为空");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last switch
        {
            HongguoHighException high => high,
            _ => new HongguoHighException("播放地址获取失败", 0, last)
        };
    }

    private async Task<JsonObject> PostAsync(
        string videoId,
        bool refresh,
        string? deviceId,
        string? installId,
        Action<string, string>? persist,
        CancellationToken cancellationToken)
    {
        var identity = await IdentityAsync(refresh, deviceId, installId, persist, cancellationToken);
        var ticket = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pairs = new List<string>();
        foreach (var (key, value) in DeviceBase)
            pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
        pairs.Add("device_id=" + Uri.EscapeDataString(identity.DeviceId));
        pairs.Add("iid=" + Uri.EscapeDataString(identity.InstallId));
        pairs.Add("_rticket=" + ticket);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, VideoApi + "?" + string.Join("&", pairs))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString(CompactJson)))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        request.Headers.TryAddWithoutValidation("User-Agent", PlaybackUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8,application/x-protobuf");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        var bytes = await ReadBodyAsync(response, timeout.Token);
        if (bytes.Length == 0)
            throw new HongguoHighException("播放地址为空");
        var root = JsonNode.Parse(bytes) as JsonObject ?? throw new HongguoHighException("播放地址为空");
        if (CodeOf(root) != 0 || root["data"] is not JsonObject data)
            throw new HongguoHighException("播放地址被拒绝");
        return data;
    }

    private async Task<Identity> IdentityAsync(
        bool refresh,
        string? deviceId,
        string? installId,
        Action<string, string>? persist,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var saved = IsSavedIdentity(deviceId, installId)
                ? new Identity(deviceId!.Trim(), installId!.Trim())
                : null;
            if (!refresh)
            {
                if (_identity is not null && (saved is null || Same(_identity, saved)))
                    return _identity;
                if (saved is not null)
                {
                    _identity = saved;
                    return saved;
                }

                if (_identity is not null)
                    return _identity;
            }

            var created = await RegisterAsync(cancellationToken);
            _identity = created;
            persist?.Invoke(created.DeviceId, created.InstallId);
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool Same(Identity left, Identity right) =>
        string.Equals(left.DeviceId, right.DeviceId, StringComparison.Ordinal) &&
        string.Equals(left.InstallId, right.InstallId, StringComparison.Ordinal);

    private static bool IsIdentityPart(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length is >= 10 and <= 24 && text.All(char.IsDigit) && text.Any(digit => digit != '0');
    }

    private async Task<Identity> RegisterAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cdid = Guid.NewGuid().ToString();
        var body = RegisterBody(now, cdid);
        using var request = new HttpRequestMessage(HttpMethod.Post, RegisterUrl)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream;tt-data=a");
        request.Headers.TryAddWithoutValidation("User-Agent", RegisterUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("log-encode-type", "gzip");
        request.Headers.TryAddWithoutValidation("x-ss-dp", "8662");
        request.Headers.TryAddWithoutValidation("x-ss-req-ticket", now.ToString());
        request.Headers.TryAddWithoutValidation("x-vc-bdturing-sdk-version", "4.0.3.cn");
        request.Headers.TryAddWithoutValidation("lc", "101");
        request.Headers.TryAddWithoutValidation("sdk-version", "2");
        request.Headers.TryAddWithoutValidation("passport-sdk-version", "5051452");
        request.Headers.TryAddWithoutValidation("x-neptune", "-8|50:51:59:30:40:47:49:39");
        request.Headers.TryAddWithoutValidation("x-ss-stub", Convert.ToHexString(MD5.HashData(body)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        var bytes = await ReadBodyAsync(response, timeout.Token);
        var root = JsonNode.Parse(bytes) as JsonObject ?? throw new HongguoHighException("设备注册失败");
        var deviceId = IdOf(root, "device_id");
        var installId = IdOf(root, "install_id");
        if (deviceId.Length == 0 || installId.Length == 0 || CodeOf(root) != 0)
            throw new HongguoHighException("设备注册失败");
        return new Identity(deviceId, installId);
    }

    private static byte[] RegisterBody(long nowMs, string cdid)
    {
        var header = new JsonObject
        {
            ["display_name"] = "红果免费短剧",
            ["update_version_code"] = 73932,
            ["manifest_version_code"] = 73932,
            ["app_version_minor"] = "7.3.9.32",
            ["aid"] = 8662,
            ["channel"] = "update_64",
            ["package"] = "com.phoenix.read",
            ["app_version"] = "7.3.9.32",
            ["version_code"] = 73932,
            ["sdk_version"] = "3.7.0-rc.34-fanqie-xiaoshuo",
            ["sdk_target_version"] = 29,
            ["git_hash"] = "3a88f4e",
            ["os"] = "Android",
            ["os_version"] = "16",
            ["os_api"] = 36,
            ["device_model"] = "25053RT47C",
            ["device_brand"] = "Redmi",
            ["device_manufacturer"] = "Redmi",
            ["cpu_abi"] = "arm64-v8a",
            ["release_build"] = "b5e187f",
            ["density_dpi"] = 520,
            ["display_density"] = "mdpi",
            ["resolution"] = "1280x2772",
            ["language"] = "zh",
            ["timezone"] = 8,
            ["access"] = "wifi",
            ["not_request_sender"] = 0,
            ["carrier"] = "CHINA MOBILE",
            ["mcc_mnc"] = "46000",
            ["rom"] = "eng.root.20231116.160340",
            ["rom_version"] = "W528JS release-keys",
            ["cdid"] = cdid,
            ["sig_hash"] = "56a962410c494bbaf0b58dba20cae56f",
            ["openudid"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            ["clientudid"] = Guid.NewGuid().ToString(),
            ["ipv6_list"] = new JsonArray(),
            ["region"] = "CN",
            ["tz_name"] = "Asia/Shanghai",
            ["tz_offset"] = 28800,
            ["sim_region"] = "cn",
            ["sim_serial_number"] = new JsonArray(),
            ["oaid_may_support"] = false,
            ["req_id"] = Guid.NewGuid().ToString(),
            ["device_platform"] = "android",
            ["custom"] = new JsonObject
            {
                ["account_region_src"] = "fallback",
                ["account_region"] = "cn",
                ["host_bit"] = 64,
                ["dragon_device_type"] = "phone"
            },
            ["pre_installed_channel"] = "",
            ["apk_first_install_time"] = nowMs,
            ["is_system_app"] = 0,
            ["sdk_flavor"] = "china",
            ["guest_mode"] = 0
        };
        var raw = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["magic_tag"] = "ss_app_log",
            ["header"] = header,
            ["_gen_time"] = nowMs
        }.ToJsonString(CompactJson));
        var compressed = Gzip(raw);
        var random = RandomNumberGenerator.GetBytes(32);
        var derived = SHA512.HashData(Concat(SHA512.HashData(random), KdfSalt));
        var plain = Concat(SHA512.HashData(compressed), compressed);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = derived[..16];
        aes.IV = derived[16..32];
        var cipher = aes.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);
        return Concat(Convert.FromHexString("746305100000"), random, cipher);
    }

    internal static HongguoHighVideoPlayback Pick(JsonObject model, string quality)
    {
        var tracks = TracksOf(model);
        if (tracks.Count == 0)
            throw new HongguoHighException("播放地址为空");
        var requested = int.TryParse(HongguoHighCrypto.NormalizeQuality(quality), out var parsed) ? parsed : 1080;
        var chosen = tracks
            .Select(track => (Track: track, Definition: DefinitionNumber(track)))
            .Where(item => item.Definition == requested)
            .Select(item => item.Track)
            .ToList();
        if (chosen.Count == 0)
        {
            var lower = tracks
                .Select(track => (Track: track, Definition: DefinitionNumber(track)))
                .Where(item => item.Definition > 0 && item.Definition <= requested)
                .OrderBy(item => item.Definition)
                .Select(item => item.Track)
                .LastOrDefault();
            chosen = lower is null ? [tracks.OrderBy(DefinitionNumber).First()] : [lower];
        }

        var track = chosen.FirstOrDefault(IsByteVc1) ?? chosen[0];
        var main = HttpUrl(Text(track, "main_url")) ?? throw new HongguoHighException("播放地址为空");
        var urls = new List<string> { main };
        foreach (var key in new[] { "backup_url", "backup_url_1", "backup_url_2", "backup_url_3", "backup" })
            CollectUrls(track[key], urls);
        var spade = SpadeOf(track);
        var size = LongOf(track["video_meta"] as JsonObject, "size");
        return new HongguoHighVideoPlayback(main, size, urls, spade, spade.Length > 0);
    }

    private static List<JsonObject> TracksOf(JsonObject model)
    {
        var node = model["video_list"];
        if (node is JsonArray array)
            return array.OfType<JsonObject>().ToList();
        if (node is JsonObject obj)
            return obj.Select(pair => pair.Value).OfType<JsonObject>().ToList();
        return [];
    }

    internal static JsonObject? ModelOf(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (node is not JsonObject model)
            return null;
        if (model["video_list"] is JsonObject tracks)
        {
            var list = new JsonArray();
            foreach (var pair in tracks)
            {
                if (pair.Value is not null)
                    list.Add(pair.Value.DeepClone());
            }
            model = (JsonObject)model.DeepClone();
            model["video_list"] = list;
        }

        return TracksOf(model).Count == 0 ? null : model;
    }

    private static void CollectUrls(JsonNode? node, List<string> urls)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                var url = HttpUrl(text);
                if (url is not null && !urls.Contains(url, StringComparer.Ordinal))
                    urls.Add(url);
                break;
            case JsonArray array:
                foreach (var item in array)
                    CollectUrls(item, urls);
                break;
            case JsonObject obj:
                foreach (var pair in obj)
                    CollectUrls(pair.Value, urls);
                break;
        }
    }

    private static string? HttpUrl(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return text;
        if (text.Length == 0)
            return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();
            if (decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                decoded.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return decoded;
        }
        catch (FormatException)
        {
            // 不是 Base64 时按原文放弃。
        }
        return null;
    }

    private static string SpadeOf(JsonObject track)
    {
        var encrypt = track["encrypt_info"] as JsonObject ?? track["encryptInfo"] as JsonObject;
        return Text(encrypt, "spade_a") ?? Text(encrypt, "spadeA") ?? Text(track, "spade_a") ?? "";
    }

    private static bool IsByteVc1(JsonObject track)
    {
        var meta = track["video_meta"] as JsonObject;
        var codec = Text(track, "codec_type") ?? Text(meta, "codec_type") ?? "";
        return codec.Contains("bytevc1", StringComparison.OrdinalIgnoreCase) ||
               codec.Contains("hvc1", StringComparison.OrdinalIgnoreCase);
    }

    private static int DefinitionNumber(JsonObject track)
    {
        var meta = track["video_meta"] as JsonObject;
        var text = Text(meta, "definition") ?? Text(track, "definition") ?? "";
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : 0;
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var gzip = response.Content.Headers.ContentEncoding.Any(value =>
                       value.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                   || (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B);
        if (!gzip)
            return bytes;
        using var input = new MemoryStream(bytes);
        using var decoder = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    internal static int CodeOf(JsonObject obj)
    {
        var node = obj["code"] ?? obj["Code"];
        if (node is null)
            return 0;
        if (node is JsonValue value && value.TryGetValue<int>(out var number))
            return number;
        return int.TryParse(node.ToString(), out var parsed) ? parsed : -1;
    }

    private static string IdOf(JsonObject obj, string name)
    {
        var text = Text(obj, name + "_str") ?? Text(obj, name) ?? "";
        return text.Length > 0 && text.All(char.IsDigit) && text != "0" ? text : "";
    }

    internal static string? Text(JsonObject? obj, string name)
    {
        if (obj is null || obj[name] is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text))
            return string.IsNullOrWhiteSpace(text) ? null : text;
        var raw = value.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static long LongOf(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value)
            return 0;
        if (value.TryGetValue<long>(out var number))
            return number;
        return long.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(raw, 0, raw.Length);
        return output.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var output = new byte[parts.Sum(part => part.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(output, at);
            at += part.Length;
        }
        return output;
    }

    private sealed record Identity(string DeviceId, string InstallId);

    internal readonly record struct TrackProbe(int Count, bool AllHaveUrl, bool AllHaveSpade);
}
