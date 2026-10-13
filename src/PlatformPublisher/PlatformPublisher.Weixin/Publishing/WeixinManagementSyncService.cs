using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlatformPublisher.Common.Models;
using ShortDrama.Core.Interfaces;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinManagementCredentials(string BaseUrl, string Username, string Password);
public sealed record WeixinManagementSyncResult(string Action, int DramaId, string Uploaded, string Uploader, string Message);

public sealed class WeixinManagementSyncService
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];
    private readonly IWorkService _workService;

    public WeixinManagementSyncService(IWorkService workService) => _workService = workService;

    public async Task<WeixinManagementSyncResult> SyncAsync(
        PublishJob job,
        WeixinManagementCredentials credentials,
        string uploaded,
        string uploader,
        CancellationToken cancellationToken)
    {
        var baseUrl = credentials.BaseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("管理系统地址未配置。");
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
            throw new InvalidOperationException("管理系统账号或密码未配置。");

        var configPath = await _workService.EnsureWeixinUploadConfigAsync(job.ProjectDirectory, null, cancellationToken);
        var workflowDirectory = Path.GetDirectoryName(configPath)
                                ?? throw new InvalidOperationException("无法定位工作项目目录。");
        var payload = BuildPayload(workflowDirectory, uploaded, uploader);

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("yunfan-platform-publisher-sync");
        using (var login = await http.PostAsync(
                   $"{baseUrl}/login",
                   new FormUrlEncodedContent(new Dictionary<string, string>
                   {
                       ["username"] = credentials.Username.Trim(),
                       ["password"] = credentials.Password,
                   }),
                   cancellationToken))
        {
            await EnsureSuccessAsync(login, "管理系统登录", cancellationToken);
        }
        using (var me = await http.GetAsync($"{baseUrl}/api/me", cancellationToken))
        {
            await EnsureSuccessAsync(me, "管理系统会话校验", cancellationToken);
        }

        var existing = await FindExistingAsync(http, baseUrl, payload["original_name"]?.GetValue<string>() ?? string.Empty,
            payload["new_name"]?.GetValue<string>() ?? string.Empty, cancellationToken);
        var dramaId = existing?["id"]?.GetValue<int>() ?? 0;
        string action;
        if (dramaId > 0)
        {
            using var response = await http.PutAsJsonAsync($"{baseUrl}/api/dramas/{dramaId}", payload, cancellationToken);
            await EnsureSuccessAsync(response, "更新管理系统短剧", cancellationToken);
            action = "updated";
        }
        else
        {
            using var response = await http.PostAsJsonAsync($"{baseUrl}/api/dramas", payload, cancellationToken);
            await EnsureSuccessAsync(response, "创建管理系统短剧", cancellationToken);
            var created = await SafeJsonAsync(response, cancellationToken);
            dramaId = created?["id"]?.GetValue<int>() ?? 0;
            action = "created";
            if (dramaId <= 0)
            {
                existing = await FindExistingAsync(http, baseUrl, payload["original_name"]?.GetValue<string>() ?? string.Empty,
                    payload["new_name"]?.GetValue<string>() ?? string.Empty, cancellationToken);
                dramaId = existing?["id"]?.GetValue<int>() ?? 0;
            }
        }

        var normalizedUploaded = NormalizeUploaded(uploaded);
        var normalizedUploader = uploader.Trim();
        if (dramaId > 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/api/dramas/{dramaId}/upload-state")
            {
                Content = JsonContent.Create(new { uploaded = normalizedUploaded, uploader = normalizedUploader }),
            };
            using var response = await http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "更新管理系统上传状态", cancellationToken);
        }
        var message = $"管理系统同步完成：{action} / 上传={normalizedUploaded}" +
                      (string.IsNullOrWhiteSpace(normalizedUploader) ? string.Empty : $" / 上传者={normalizedUploader}");
        return new WeixinManagementSyncResult(action, dramaId, normalizedUploaded, normalizedUploader, message);
    }

    public static JsonObject BuildPayload(string workflowDirectory, string uploaded, string uploader)
    {
        var info = ParseInfo(Path.Combine(workflowDirectory, "短剧信息.txt"));
        var metadata = ReadObject(Path.Combine(workflowDirectory, "shortdrama-project.json"));
        var videosDir = Path.Combine(workflowDirectory, "videos");
        var videoCount = Directory.Exists(videosDir)
            ? Directory.EnumerateFiles(videosDir).Count(path => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            : 0;
        var originalName = First(metadata["originalTitle"]?.GetValue<string>(), metadata["sourceName"]?.GetValue<string>(),
            info.GetValueOrDefault("原剧名"), Path.GetFileName(workflowDirectory).TrimStart('_'));
        var newName = First(info.GetValueOrDefault("新剧名"), metadata["displayName"]?.GetValue<string>(), originalName);
        var projectImages = Directory.Exists(workflowDirectory)
            ? Directory.EnumerateFiles(workflowDirectory, "工程图_*.png").Count()
            : 0;
        var hasPoster = Directory.EnumerateFiles(workflowDirectory, "海报图片.*").Any();
        var hasCost = Directory.EnumerateFiles(workflowDirectory, "成本报表.*").Any();
        return new JsonObject
        {
            ["date"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["original_name"] = originalName,
            ["new_name"] = newName,
            ["episodes"] = videoCount,
            ["duration"] = ParseNumber(info.GetValueOrDefault("时间（分钟）") ?? info.GetValueOrDefault("时长")),
            ["review_passed"] = "否",
            ["uploaded"] = NormalizeUploaded(uploaded),
            ["uploader"] = uploader.Trim(),
            ["materials"] = $"海报:{(hasPoster ? "是" : "否")};报表:{(hasCost ? "是" : "否")};工程图:{projectImages}张;视频:{videoCount}集",
            ["promo_text"] = info.GetValueOrDefault("推荐语"),
            ["description"] = info.GetValueOrDefault("简介"),
            ["company"] = info.GetValueOrDefault("制作公司"),
            ["remark1"] = workflowDirectory,
            ["remark2"] = $"海报{(hasPoster ? "已生成" : "待生成")} / 报表{(hasCost ? "已生成" : "待生成")} / 工程图{projectImages}张 / 视频{videoCount}集",
            ["remark3"] = $"最近同步：{DateTime.Now:yyyy-MM-ddTHH:mm:ss}",
        };
    }

    private static async Task<JsonObject?> FindExistingAsync(HttpClient http, string baseUrl, string originalName, string newName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalName)) return null;
        var url = $"{baseUrl}/api/dramas?search={Uri.EscapeDataString(originalName)}&page_size=100";
        using var response = await http.GetAsync(url, cancellationToken);
        await EnsureSuccessAsync(response, "查询管理系统短剧", cancellationToken);
        var root = await SafeJsonAsync(response, cancellationToken);
        var items = root?["items"] as JsonArray;
        var matches = items?.OfType<JsonObject>()
            .Where(item => string.Equals(item["original_name"]?.GetValue<string>(), originalName, StringComparison.Ordinal))
            .ToArray() ?? [];
        return matches.FirstOrDefault(item => string.Equals(item["new_name"]?.GetValue<string>(), newName, StringComparison.Ordinal))
               ?? matches.FirstOrDefault();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"{operation}失败：{(int)response.StatusCode} {response.ReasonPhrase}；{body}");
    }

    private static async Task<JsonObject?> SafeJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try { return JsonNode.Parse(body) as JsonObject; }
        catch { return null; }
    }

    private static Dictionary<string, string> ParseInfo(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOfAny([':', '：']);
            if (index <= 0) continue;
            result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return result;
    }

    private static JsonObject ReadObject(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    private static int? ParseNumber(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var result) ? result : null;
    }
    private static string NormalizeUploaded(string? value) => value?.Trim() is "是" or "否" ? value.Trim() : "否";
}
