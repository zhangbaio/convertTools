using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

internal sealed class VolcengineVodClient
{
    private const string Host = "vod.volcengineapi.com";
    private const string ProcessingVersion = "2025-01-01";
    private const string UploadVersion = "2022-01-01";
    private const int UploadPartSize = 20 * 1024 * 1024;
    private readonly ClientSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public VolcengineVodClient(ClientSettings settings) => _settings = settings;

    public async Task<string> StartSubtitleEraseAsync(string vid, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["Input"] = new JsonObject { ["Type"] = "Vid", ["Vid"] = vid },
            ["Operation"] = new JsonObject
            {
                ["Type"] = "Task",
                ["Task"] = new JsonObject
                {
                    ["Type"] = "Erase",
                    ["Erase"] = new JsonObject
                    {
                        ["Mode"] = "Auto",
                        ["Auto"] = new JsonObject
                        {
                            ["Type"] = "Subtitle",
                            ["SubtitleFilter"] = new JsonObject(),
                            ["Locations"] = new JsonArray(new JsonObject
                            {
                                ["RatioLocation"] = new JsonObject
                                {
                                    ["TopLeftX"] = 0.0, ["TopLeftY"] = 0.5,
                                    ["BottomRightX"] = 1.0, ["BottomRightY"] = 1.0,
                                },
                            }),
                        },
                        ["WithEraseInfo"] = true,
                        ["NewVid"] = true,
                    },
                },
            },
        };
        var json = await SendAsync(HttpMethod.Post, "StartExecution", ProcessingVersion, body, null, ct).ConfigureAwait(false);
        return ReadRequired(json, "Result", "RunId");
    }

    public async Task<string> StartAideoSubtitleEraseAsync(string vid, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["SpaceName"] = _settings.VolcSubtitleSpaceName,
            ["MultiInputs"] = new JsonArray(new JsonObject { ["Type"] = "Vid", ["Vid"] = vid }),
            ["SkillType"] = "Erase",
            ["SkillParams"] = "{\"Mode\":\"Auto\",\"Auto\":{\"Type\":\"Subtitle\"},\"NewVid\":true}",
        };
        var json = await SendAsync(HttpMethod.Post, "SubmitAideoTaskAsync", "2025-03-03", body, null, ct)
            .ConfigureAwait(false);
        return ReadRequired(json, "Result", "TaskId");
    }

    public async Task<string> StartAideoTranslationAsync(string vid, CancellationToken ct,
        string? recognitionType = null, int? hardSubtitleFontSize = null,
        bool? hardSubtitle = null, bool? eraseSourceSubtitle = null)
    {
        var subtitleSource = string.IsNullOrWhiteSpace(recognitionType)
            ? _settings.VolcVideoTranslateSubtitleSource
            : recognitionType;
        var translationTypes = new List<string> { "SubtitleTranslation" };
        if (_settings.VolcVideoTranslateVoice) translationTypes.Add("VoiceTranslation");
        if (_settings.VolcVideoTranslateFacial) translationTypes.Add("FacialTranslation");
        var recognitionConfig = new Dictionary<string, object?>
        {
            ["RecognitionType"] = string.Equals(subtitleSource, "OCR", StringComparison.OrdinalIgnoreCase)
                ? "OCR"
                : "ASR",
        };
        if (string.Equals(subtitleSource, "SubtitleFile", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_settings.VolcVideoTranslateSubtitleFileName))
                throw new InvalidOperationException("火山视频翻译选择了上传字幕文件，但未配置 VOD 字幕 FileName。");
            recognitionConfig["RecognitionType"] = "SubtitleFile";
            recognitionConfig["SubtitleFileName"] = _settings.VolcVideoTranslateSubtitleFileName.Trim();
        }
        var skillParams = JsonSerializer.Serialize(new
        {
            TranslationConfig = new
            {
                SourceLanguage = _settings.ElevenLabsSourceLanguage,
                TargetLanguage = _settings.ElevenLabsTargetLanguage,
                TranslationTypeList = translationTypes,
            },
            OperatorConfig = new
            {
                SubtitleRecognitionConfig = recognitionConfig,
            },
            SubtitleConfig = new
            {
                IsHardSubtitle = hardSubtitle ?? _settings.VolcVideoTranslateHardSubtitle,
                IsEraseSource = eraseSourceSubtitle ?? _settings.VolcVideoTranslateEraseSourceSubtitle,
                IsBilingualSubtitle = _settings.VideoTranslateBilingual,
                FontSize = Math.Clamp(
                    hardSubtitleFontSize ?? _settings.VolcVideoTranslateFontSize, 16, 36),
                MarginV = Math.Clamp(_settings.VideoTranslateMarginV / 1280.0, 0, 0.45),
            },
        });
        var body = new JsonObject
        {
            ["SpaceName"] = _settings.VolcSubtitleSpaceName,
            ["MultiInputs"] = new JsonArray(new JsonObject { ["Type"] = "Vid", ["Vid"] = vid }),
            ["SkillType"] = "AITranslation",
            ["SkillParams"] = skillParams,
        };
        var json = await SendAsync(HttpMethod.Post, "SubmitAideoTaskAsync", "2025-03-03", body, null, ct)
            .ConfigureAwait(false);
        return ReadRequired(json, "Result", "TaskId");
    }

    public async Task<VolcTranslationResult> GetAideoTranslationResultAsync(string taskId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "GetAideoTaskResult", "2025-03-03", null,
            new Dictionary<string, string>
            {
                ["SpaceName"] = _settings.VolcSubtitleSpaceName,
                ["TaskId"] = taskId,
            }, ct).ConfigureAwait(false);
        var result = json["Result"] as JsonObject ?? throw ApiError(json, "火山视频翻译查询响应缺少 Result");
        var status = result["Status"]?.GetValue<string>() ?? "";
        var outputVideo = FindObject(result, "OutputVideo");
        var targetVid = FindString(result, "TargetVid") ??
                        outputVideo?["Vid"]?.GetValue<string>() ?? FindTranslationOutputVid(result) ?? "";
        var outputUrl = outputVideo?["Url"]?.GetValue<string>() ?? "";
        return new VolcTranslationResult(status, targetVid, outputUrl,
            FindString(result, "ErrorMsg") ?? FindString(result, "Message") ?? FindString(result, "Error") ?? "");
    }

    public async Task<VolcAideoResult> GetAideoTaskResultAsync(string taskId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "GetAideoTaskResult", "2025-03-03", null,
            new Dictionary<string, string>
            {
                ["SpaceName"] = _settings.VolcSubtitleSpaceName,
                ["TaskId"] = taskId,
            }, ct).ConfigureAwait(false);
        var result = json["Result"] as JsonObject ?? throw ApiError(json, "Aideo 查询响应缺少 Result");
        var status = result["Status"]?.GetValue<string>() ?? "";
        var output = FindObjectWithFile(result);
        return new VolcAideoResult(status,
            output?["Vid"]?.GetValue<string>() ?? "",
            output?["FileName"]?.GetValue<string>() ?? "",
            FindString(result, "Message") ?? FindString(result, "Error") ?? "");
    }

    public async Task<string> GetDirectUrlAsync(string fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("Aideo 产物 FileName 不能为空。", nameof(fileName));
        if (string.IsNullOrWhiteSpace(_settings.VolcSubtitlePlaybackDomain))
            throw new InvalidOperationException("Aideo Agent 下载需要配置 VOD 播放域名。");
        var config = await SendAsync(HttpMethod.Get, "DescribeDomainConfig", "2023-07-01", null,
            new Dictionary<string, string>
            {
                ["SpaceName"] = _settings.VolcSubtitleSpaceName,
                ["DomainType"] = "play",
                ["Domain"] = NormalizeDomainHost(_settings.VolcSubtitlePlaybackDomain),
            }, ct).ConfigureAwait(false);
        var signedUrlAuth = FindObject(config, "SignedUrlAuth");
        var enabled = signedUrlAuth?["Switch"]?.GetValue<bool>() ?? false;
        if (!enabled) return BuildDirectUrl(_settings.VolcSubtitlePlaybackDomain, fileName);
        var secret = FindString(config, "MasterSecretKey");
        if (string.IsNullOrWhiteSpace(secret)) throw ApiError(config, "播放域名已开启 URL 鉴权，但配置响应缺少主 Key");
        var signName = FindString(config, "SignName") ?? "auth_key";
        return BuildSignedDirectUrl(_settings.VolcSubtitlePlaybackDomain, fileName, signName, secret,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
    }

    public async Task<VolcExecutionResult> GetExecutionAsync(string runId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "GetExecution", ProcessingVersion, null,
            new Dictionary<string, string> { ["RunId"] = runId }, ct).ConfigureAwait(false);
        var result = json["Result"] as JsonObject ?? throw ApiError(json, "火山字幕擦除查询响应缺少 Result");
        var status = result["Status"]?.GetValue<string>() ?? "";
        var erase = result["Output"]?["Task"]?["Erase"];
        return new VolcExecutionResult(status,
            erase?["File"]?["Vid"]?.GetValue<string>() ?? "",
            erase?["File"]?["FileName"]?.GetValue<string>() ?? "",
            result["Error"]?.ToJsonString() ?? result["Message"]?.GetValue<string>() ?? "");
    }

    public async Task<string> GetPlayUrlAsync(string vid, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "GetPlayInfo", "2020-08-01", null,
            new Dictionary<string, string> { ["Vid"] = vid, ["Ssl"] = "0" }, ct).ConfigureAwait(false);
        var list = json["Result"]?["PlayInfoList"] as JsonArray;
        if (list is not null)
        {
            foreach (var item in list.OfType<JsonObject>())
            {
                foreach (var key in new[] { "MainPlayUrl", "PlayURL", "PlayUrl", "Url" })
                {
                    var value = item[key]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(value))
                        return ApplyPlaybackDomain(value, _settings.VolcSubtitlePlaybackDomain);
                }
            }
        }
        throw ApiError(json, "GetPlayInfo 响应缺少播放地址");
    }

    public async Task<string> UploadMediaAsync(string filePath, Action<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.VolcSubtitleSpaceName))
            throw new InvalidOperationException("自动上传 VOD 需要配置空间名。");
        var info = new FileInfo(filePath);
        var apply = await SendAsync(HttpMethod.Get, "ApplyUploadInfo", UploadVersion, null,
            new Dictionary<string, string>
            {
                ["SpaceName"] = _settings.VolcSubtitleSpaceName,
                ["FileSize"] = info.Length.ToString(CultureInfo.InvariantCulture),
                ["FileType"] = "media",
                ["FileName"] = $"{Path.GetFileNameWithoutExtension(filePath)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}",
                ["FileExtension"] = info.Extension.ToLowerInvariant(),
                ["StorageClass"] = "1",
                ["UploadHostPrefer"] = "",
            }, ct).ConfigureAwait(false);
        var address = apply["Result"]?["Data"]?["UploadAddress"]
            ?? throw ApiError(apply, "ApplyUploadInfo 响应缺少 UploadAddress");
        var oid = address["StoreInfos"]?[0]?["StoreUri"]?.GetValue<string>() ?? "";
        var auth = address["StoreInfos"]?[0]?["Auth"]?.GetValue<string>() ?? "";
        var host = address["UploadHosts"]?[0]?.GetValue<string>() ?? "";
        var sessionKey = address["SessionKey"]?.GetValue<string>() ?? "";
        if (new[] { oid, auth, host, sessionKey }.Any(string.IsNullOrWhiteSpace))
            throw ApiError(apply, "ApplyUploadInfo 返回的上传凭据不完整");

        await UploadFileAsync(filePath, host, oid, auth, progress, ct).ConfigureAwait(false);
        var functions = JsonSerializer.Serialize(new object[]
        {
            new { Name = "GetMeta" },
            new { Name = "AddOptionInfo", Input = new { FileTitle = Path.GetFileName(filePath) } },
        });
        var commit = await SendAsync(HttpMethod.Get, "CommitUploadInfo", UploadVersion, null,
            new Dictionary<string, string>
            {
                ["SpaceName"] = _settings.VolcSubtitleSpaceName,
                ["SessionKey"] = sessionKey,
                ["Functions"] = functions,
            }, ct).ConfigureAwait(false);
        return ReadRequired(commit, "Result", "Data", "Vid");
    }

    public async Task SetMediaTitleAsync(string vid, string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vid)) throw new ArgumentException("Vid 不能为空。", nameof(vid));
        var normalizedTitle = title.Trim();
        if (normalizedTitle.Length == 0) throw new ArgumentException("媒资标题不能为空。", nameof(title));
        if (normalizedTitle.Length > 512) normalizedTitle = normalizedTitle[..512];
        await SendAsync(HttpMethod.Get, "UpdateMediaInfo", "2020-08-01", null,
            new Dictionary<string, string>
            {
                ["Vid"] = vid,
                ["Title"] = normalizedTitle,
            }, ct).ConfigureAwait(false);
    }

    private async Task UploadFileAsync(string filePath, string host, string oid, string auth,
        Action<double>? progress, CancellationToken ct)
    {
        var size = new FileInfo(filePath).Length;
        if (size <= UploadPartSize)
        {
            var data = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
            await UploadBytesAsync($"https://{host}/{EncodePath(oid)}", data, auth, false, ct).ConfigureAwait(false);
            progress?.Invoke(1);
            return;
        }

        using var init = new HttpRequestMessage(HttpMethod.Put, $"https://{host}/{EncodePath(oid)}?uploads");
        init.Headers.TryAddWithoutValidation("Authorization", auth);
        init.Headers.TryAddWithoutValidation("X-Storage-Mode", "gateway");
        using var initResponse = await _http.SendAsync(init, ct).ConfigureAwait(false);
        var initText = await initResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!initResponse.IsSuccessStatusCode) throw new InvalidOperationException($"初始化 VOD 分片上传失败：{(int)initResponse.StatusCode} {initText}");
        var uploadId = JsonNode.Parse(initText)?["payload"]?["uploadID"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(uploadId)) throw new InvalidOperationException("初始化 VOD 分片上传未返回 uploadID。");

        var checksums = new List<string>();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            UploadPartSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var uploaded = 0L;
        var partNumber = 1;
        while (uploaded < size)
        {
            var count = (int)Math.Min(UploadPartSize, size - uploaded);
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            var checksum = Crc32Hex(buffer);
            await UploadBytesAsync($"https://{host}/{EncodePath(oid)}?partNumber={partNumber}&uploadID={Uri.EscapeDataString(uploadId)}",
                buffer, auth, true, ct).ConfigureAwait(false);
            checksums.Add(checksum);
            uploaded += count;
            partNumber++;
            progress?.Invoke((double)uploaded / size);
        }
        var mergeBody = string.Join(',', checksums.Select((crc, index) => $"{index + 1}:{crc}"));
        using var merge = new HttpRequestMessage(HttpMethod.Put,
            $"https://{host}/{EncodePath(oid)}?uploadID={Uri.EscapeDataString(uploadId)}");
        merge.Headers.TryAddWithoutValidation("Authorization", auth);
        merge.Headers.TryAddWithoutValidation("X-Storage-Mode", "gateway");
        merge.Content = new StringContent(mergeBody, Encoding.UTF8, "text/plain");
        using var mergeResponse = await _http.SendAsync(merge, ct).ConfigureAwait(false);
        var mergeText = await mergeResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!mergeResponse.IsSuccessStatusCode) throw new InvalidOperationException($"合并 VOD 分片失败：{(int)mergeResponse.StatusCode} {mergeText}");
    }

    private async Task UploadBytesAsync(string url, byte[] data, string auth, bool gateway, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        request.Headers.TryAddWithoutValidation("Content-CRC32", Crc32Hex(data));
        if (gateway) request.Headers.TryAddWithoutValidation("X-Storage-Mode", "gateway");
        request.Content = new ByteArrayContent(data);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"上传 VOD 文件失败：{(int)response.StatusCode} {text}");
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string action, string version, JsonObject? body,
        IReadOnlyDictionary<string, string>? extraQuery, CancellationToken ct)
    {
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Action"] = action, ["Version"] = version,
        };
        if (extraQuery is not null) foreach (var pair in extraQuery) query[pair.Key] = pair.Value;
        var canonicalQuery = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var payload = body?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = Hex(SHA256.HashData(bytes));
        var now = DateTime.UtcNow;
        var xDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        const string signedHeaders = "host;x-content-sha256;x-date";
        var canonicalHeaders = $"host:{Host}\nx-content-sha256:{hash}\nx-date:{xDate}\n";
        var canonicalRequest = $"{method.Method}\n/\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{hash}";
        var region = _settings.VolcSubtitleRegion;
        var scope = $"{date}/{region}/vod/request";
        var stringToSign = $"HMAC-SHA256\n{xDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var signingKey = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes(_settings.VolcSubtitleSecretAccessKey), date), region), "vod"), "request");
        var signature = Hex(Hmac(signingKey, stringToSign));

        using var request = new HttpRequestMessage(method, $"https://{Host}/?{canonicalQuery}");
        request.Headers.TryAddWithoutValidation("X-Date", xDate);
        request.Headers.TryAddWithoutValidation("X-Content-Sha256", hash);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"HMAC-SHA256 Credential={_settings.VolcSubtitleAccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        if (body is not null) request.Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" } } };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject json;
        try { json = JsonNode.Parse(responseText)?.AsObject() ?? new JsonObject(); }
        catch { throw new InvalidOperationException($"火山 VOD 返回了无效响应（HTTP {(int)response.StatusCode}）：{responseText}"); }
        if (!response.IsSuccessStatusCode || json["ResponseMetadata"]?["Error"] is not null)
            throw ApiError(json, $"火山 VOD 请求失败（HTTP {(int)response.StatusCode}）");
        return json;
    }

    private static string ReadRequired(JsonObject root, params string[] path)
    {
        JsonNode? node = root;
        foreach (var part in path) node = node?[part];
        var value = node?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(value) ? value : throw ApiError(root, $"火山 VOD 响应缺少 {string.Join('.', path)}");
    }
    private static Exception ApiError(JsonObject root, string prefix) =>
        new InvalidOperationException($"{prefix}：{root["ResponseMetadata"]?["Error"]?.ToJsonString() ?? root.ToJsonString()}");
    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    private static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private static string Crc32Hex(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return (~crc).ToString("x8", CultureInfo.InvariantCulture);
    }
    internal static string Crc32HexForTest(byte[] data) => Crc32Hex(data);

    internal static string ApplyPlaybackDomain(string playUrl, string configuredDomain)
    {
        if (string.IsNullOrWhiteSpace(configuredDomain)) return playUrl;
        if (!Uri.TryCreate(playUrl, UriKind.Absolute, out var source))
            throw new InvalidOperationException($"火山 VOD 返回了无效播放地址：{playUrl}");

        var domainText = configuredDomain.Trim().TrimEnd('/');
        if (!domainText.Contains("://", StringComparison.Ordinal))
            domainText = "https://" + domainText;
        if (!Uri.TryCreate(domainText, UriKind.Absolute, out var domain) || string.IsNullOrWhiteSpace(domain.Host))
            throw new InvalidOperationException($"火山 VOD 播放域名无效：{configuredDomain}");

        var builder = new UriBuilder(source)
        {
            Scheme = domain.Scheme,
            Host = domain.Host,
            Port = domain.IsDefaultPort ? -1 : domain.Port,
        };
        return builder.Uri.AbsoluteUri;
    }

    internal static string BuildSignedDirectUrl(string configuredDomain, string fileName, string signName,
        string secret, long timestamp, string random)
    {
        var baseUrl = BuildDirectUrl(configuredDomain, fileName);
        var uri = new Uri(baseUrl).AbsolutePath;
        const string uid = "0";
        var signatureText = $"{uri}-{timestamp}-{random}-{uid}-{secret}";
        var signature = Hex(MD5.HashData(Encoding.UTF8.GetBytes(signatureText)));
        return $"{baseUrl}?{Uri.EscapeDataString(signName)}={timestamp}-{random}-{uid}-{signature}";
    }

    private static string BuildDirectUrl(string configuredDomain, string fileName)
    {
        var domain = configuredDomain.Trim().TrimEnd('/');
        if (!domain.Contains("://", StringComparison.Ordinal)) domain = "http://" + domain;
        if (!Uri.TryCreate(domain, UriKind.Absolute, out var parsed) || string.IsNullOrWhiteSpace(parsed.Host))
            throw new InvalidOperationException($"火山 VOD 播放域名无效：{configuredDomain}");
        return domain + "/" + fileName.TrimStart('/');
    }

    private static string NormalizeDomainHost(string configuredDomain)
    {
        var value = configuredDomain.Contains("://", StringComparison.Ordinal)
            ? configuredDomain
            : "http://" + configuredDomain;
        return Uri.TryCreate(value, UriKind.Absolute, out var parsed) ? parsed.Host : configuredDomain.Trim().Trim('/');
    }

    private static JsonObject? FindObjectWithFile(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["FileName"] is not null && obj["Vid"] is not null) return obj;
            foreach (var pair in obj)
            {
                var found = FindObjectWithFile(pair.Value);
                if (found is not null) return found;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                var found = FindObjectWithFile(child);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static string? FindString(JsonNode? node, string key)
    {
        if (node is JsonObject obj)
        {
            if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
            foreach (var pair in obj)
            {
                var found = FindString(pair.Value, key);
                if (found is not null) return found;
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array)
            {
                var found = FindString(child, key);
                if (found is not null) return found;
            }
        return null;
    }

    private static string? FindTranslationOutputVid(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["OutputVideo"] is JsonObject outputVideo &&
                outputVideo["Vid"] is JsonValue outputVidValue &&
                outputVidValue.TryGetValue<string>(out var outputVid) &&
                !string.IsNullOrWhiteSpace(outputVid))
                return outputVid;
            foreach (var key in new[] { "OutputVid", "ExportVid", "TranslatedVid" })
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                    return text;
            foreach (var pair in obj)
            {
                var found = FindTranslationOutputVid(pair.Value);
                if (found is not null) return found;
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array)
            {
                var found = FindTranslationOutputVid(child);
                if (found is not null) return found;
            }
        return null;
    }

    private static JsonObject? FindObject(JsonNode? node, string key)
    {
        if (node is JsonObject obj)
        {
            if (obj[key] is JsonObject value) return value;
            foreach (var pair in obj)
            {
                var found = FindObject(pair.Value, key);
                if (found is not null) return found;
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array)
            {
                var found = FindObject(child, key);
                if (found is not null) return found;
            }
        return null;
    }

}

internal sealed record VolcExecutionResult(string Status, string Vid, string FileName, string Error);
internal sealed record VolcAideoResult(string Status, string Vid, string FileName, string Error);
internal sealed record VolcTranslationResult(string Status, string TargetVid, string OutputUrl, string Error);
