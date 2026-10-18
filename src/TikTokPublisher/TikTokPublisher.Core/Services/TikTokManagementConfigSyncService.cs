using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public sealed record TikTokConfigPackageMeta(
    int Revision,
    string UpdatedAt,
    string SourceMachineId,
    string SourceDeviceName,
    string Sha256,
    int AccountCount,
    bool HasAppSettings,
    int LoginStateCount,
    int SealCount)
{
    public string BuildConfirmationText()
    {
        var settings = HasAppSettings ? "有" : "无";
        return string.Join(
            Environment.NewLine,
            "将下载该登录账号在云端的配置，并按账号合并到本机。",
            $"来源设备：{Display(SourceDeviceName)}",
            $"更新时间：{Display(UpdatedAt)}",
            $"版本：{Revision}",
            $"账号 {AccountCount} 个",
            $"系统设置：{settings}",
            $"TikTok 登录态 {LoginStateCount} 个",
            $"印章 {SealCount} 个",
            "不会删除本机多出来的账号，也不会覆盖队列、运行记录和软件授权。",
            "工作目录和归档目录仍是上传那台电脑上的路径。如果本机没有这些目录，请在账号的基础设置里重新选择。");
    }

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未知" : value.Trim();
}

public sealed record TikTokConfigMetaResult(
    bool Ok,
    bool Exists,
    TikTokConfigPackageMeta? Meta,
    string Message);

public sealed record TikTokConfigSyncResult(bool Ok, string Message);

public sealed class TikTokConfigUploadPackage : IDisposable
{
    internal TikTokConfigUploadPackage(bool ok, string summary, string message, string? packagePath)
    {
        Ok = ok;
        Summary = summary;
        Message = message;
        PackagePath = packagePath;
    }

    public bool Ok { get; }
    public string Summary { get; }
    public string Message { get; }
    internal string? PackagePath { get; }
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (string.IsNullOrWhiteSpace(PackagePath))
            return;
        try
        {
            if (File.Exists(PackagePath))
                File.Delete(PackagePath);
        }
        catch (IOException)
        {
            // 临时包删不掉不影响上传结果。
        }
    }
}

/// <summary>把当前客户端的配置包上传到管理系统，或从同一登录账号下载并合并。</summary>
public sealed class TikTokManagementConfigSyncService
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly HttpClient _http;
    private readonly Func<ClientSettings> _settingsProvider;
    private readonly Func<LicenseState> _stateProvider;
    private readonly Func<string> _deviceNameProvider;

    public TikTokManagementConfigSyncService()
        : this(
            SharedHttp,
            static () => ClientSettingsStore.Load(),
            LicenseStore.Load,
            static () => LicenseAuthService.DeviceName)
    {
    }

    internal TikTokManagementConfigSyncService(
        HttpClient http,
        Func<ClientSettings> settingsProvider,
        Func<LicenseState> stateProvider,
        Func<string>? deviceNameProvider = null)
    {
        _http = http;
        _settingsProvider = settingsProvider;
        _stateProvider = stateProvider;
        _deviceNameProvider = deviceNameProvider ?? (static () => LicenseAuthService.DeviceName);
    }

    public TikTokConfigUploadPackage PrepareUpload()
    {
        var authError = DescribeAuthError();
        if (authError is not null)
            return new TikTokConfigUploadPackage(false, "", authError, null);

        var packagePath = Path.Combine(
            Path.GetTempPath(),
            "tiktok-config-upload-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            PublisherConfigTransfer.ExportTo(packagePath, includeRunData: false);
            var info = PublisherConfigTransfer.Inspect(packagePath);
            var summary = info.Summary + Environment.NewLine + "上传后会覆盖该登录账号在云端已有的配置。";
            return new TikTokConfigUploadPackage(true, summary, "", packagePath);
        }
        catch (Exception ex)
        {
            TryDelete(packagePath);
            return new TikTokConfigUploadPackage(false, "", $"导出配置失败：{ex.Message}", null);
        }
    }

    public async Task<TikTokConfigSyncResult> UploadAsync(
        TikTokConfigUploadPackage package,
        CancellationToken ct = default)
    {
        if (!package.Ok || string.IsNullOrWhiteSpace(package.PackagePath))
            return new(false, string.IsNullOrWhiteSpace(package.Message) ? "没有可上传的配置包" : package.Message);

        var authError = DescribeAuthError();
        if (authError is not null)
            return new(false, authError);

        try
        {
            var bytes = await File.ReadAllBytesAsync(package.PackagePath, ct).ConfigureAwait(false);
            using var request = CreateRequest(HttpMethod.Put, "/client-api/tt/config");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = SafeJson(body);
            if (response.IsSuccessStatusCode && ReadOk(parsed) is true)
                return new(true, "配置已上传到云端");

            return new(false, $"上传配置失败：{ReadFailureMessage(response.StatusCode, parsed)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "上传配置超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new(false, $"上传配置失败：{ex.Message}");
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            return new(false, $"管理系统地址无效：{ex.Message}");
        }
    }

    public async Task<TikTokConfigMetaResult> FetchMetaAsync(CancellationToken ct = default)
    {
        var authError = DescribeAuthError();
        if (authError is not null)
            return new(false, false, null, authError);

        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/client-api/tt/config/meta");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = SafeJson(body);
            if (!response.IsSuccessStatusCode || ReadOk(parsed) is not true)
                return new(false, false, null, $"读取云端配置失败：{ReadFailureMessage(response.StatusCode, parsed)}");

            if (parsed?["data"] is null || parsed["data"] is JsonValue)
                return new(true, false, null, "云端还没有配置");

            if (parsed["data"] is not JsonObject data)
                return new(false, false, null, "读取云端配置失败：管理系统响应格式无效");

            return new(true, true, ReadMeta(data), "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, false, null, "读取云端配置超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new(false, false, null, $"读取云端配置失败：{ex.Message}");
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            return new(false, false, null, $"管理系统地址无效：{ex.Message}");
        }
    }

    public async Task<TikTokConfigSyncResult> DownloadAndImportAsync(
        TikTokConfigPackageMeta meta,
        CancellationToken ct = default)
    {
        var authError = DescribeAuthError();
        if (authError is not null)
            return new(false, authError);

        var packagePath = Path.Combine(
            Path.GetTempPath(),
            "tiktok-config-download-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/client-api/tt/config");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new(false, $"下载配置失败：{ReadFailureMessage(response.StatusCode, SafeJson(errorBody))}");
            }

            var revisionHeader = ReadHeader(response, "X-TT-Config-Revision");
            var shaHeader = ReadHeader(response, "X-TT-Config-Sha256");
            if (!string.Equals(revisionHeader, meta.Revision.ToString(), StringComparison.Ordinal)
                || !string.Equals(shaHeader, meta.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, "云端配置已更新，请重新确认后再下载。");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualSha, meta.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(false, "下载的配置包校验失败");

            await File.WriteAllBytesAsync(packagePath, bytes, ct).ConfigureAwait(false);
            var imported = PublisherConfigTransfer.ImportFrom(packagePath, includeRunData: false);
            var message = imported
                + Environment.NewLine
                + "工作目录和归档目录仍是上传那台电脑上的路径。如果本机没有这些目录，请在账号的基础设置里重新选择。";
            return new(true, message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "下载配置超时");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return new(false, $"下载配置失败：{ex.Message}");
        }
        finally
        {
            TryDelete(packagePath);
        }
    }

    private string? DescribeAuthError()
    {
        var settings = _settingsProvider();
        var state = _stateProvider();
        if (string.IsNullOrWhiteSpace(ResolveBaseUrl(settings, state)))
            return "管理系统地址未配置，无法同步配置";
        if (string.IsNullOrWhiteSpace(state.MachineId) || string.IsNullOrWhiteSpace(state.Token))
            return "软件未登录，无法同步配置";
        return null;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var settings = _settingsProvider();
        var state = _stateProvider();
        var baseUrl = ResolveBaseUrl(settings, state);
        var request = new HttpRequestMessage(method, $"{baseUrl}{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(
            "X-TT-Account",
            FirstNonEmpty(state.AccountUsername, state.Email, state.LicenseKey));
        request.Headers.TryAddWithoutValidation("X-TT-Machine-Id", (state.MachineId ?? "").Trim());
        request.Headers.TryAddWithoutValidation("X-TT-Token", (state.Token ?? "").Trim());
        request.Headers.TryAddWithoutValidation("X-TT-Device-Name", (_deviceNameProvider() ?? "").Trim());
        return request;
    }

    private static TikTokConfigPackageMeta ReadMeta(JsonObject data)
    {
        var summary = data["summary"] as JsonObject;
        return new TikTokConfigPackageMeta(
            ReadInt(data["revision"]),
            ReadString(data["updated_at"]),
            ReadString(data["source_machine_id"]),
            ReadString(data["source_device_name"]),
            ReadString(data["sha256"]),
            ReadInt(summary?["account_count"]),
            ReadBool(summary?["has_app_settings"]),
            ReadInt(summary?["login_state_count"]),
            ReadInt(summary?["seal_count"]));
    }

    private static string ResolveBaseUrl(ClientSettings settings, LicenseState state)
    {
        var baseUrl = CleanBaseUrl(state.ServerUrl);
        return string.IsNullOrWhiteSpace(baseUrl)
            ? CleanBaseUrl(settings.AuthServerUrl)
            : baseUrl;
    }

    private static string CleanBaseUrl(string? value) => (value ?? "").Trim().TrimEnd('/');

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => (value ?? "").Trim())
            .FirstOrDefault(value => value.Length > 0) ?? "";

    private static JsonNode? SafeJson(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body); }
        catch { return null; }
    }

    private static bool? ReadOk(JsonNode? parsed)
    {
        if (parsed?["ok"] is not JsonValue value) return null;
        return value.TryGetValue<bool>(out var result) ? result : null;
    }

    private static string ReadFailureMessage(HttpStatusCode status, JsonNode? parsed)
    {
        var message = ReadMessage(parsed);
        return string.IsNullOrWhiteSpace(message) ? $"管理系统返回 HTTP {(int)status}" : message;
    }

    private static string ReadMessage(JsonNode? parsed)
    {
        foreach (var key in new[] { "message", "error" })
        {
            if (parsed?[key] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return "";
    }

    private static string ReadHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()?.Trim() ?? ""
            : "";

    private static string ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

    private static int ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return 0;
        if (value.TryGetValue<int>(out var number))
            return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : 0;
    }

    private static bool ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // 临时包删不掉不影响结果。
        }
    }
}
