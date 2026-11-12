using System.Text.Json;
using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private static readonly string[] IgnoredSubmitResponsePathMarkers =
    {
        "monitor", "slardar", "/log", "report", "mcs", "metrics", "tea", "collect", "heartbeat",
    };

    private static readonly string[] SubmitResponseCodeKeys =
    {
        "code", "status_code", "statusCode", "err_no", "errno",
    };

    private static readonly string[] SubmitResponseMessageKeys =
    {
        "message", "msg", "status_msg", "statusMessage", "err_msg", "error_msg",
    };

    /// <summary>
    /// 记录点击提交到反馈窗口结束期间，页面向 TikTok 发出的 POST 请求返回。
    /// 只保留路径、HTTP 状态和返回体里的错误码/提示，不记录查询参数和返回体其余内容。
    /// </summary>
    private sealed class SubmitResponseRecorder
    {
        private const int MaxResponses = 8;
        private readonly IPage _page;
        private readonly List<IResponse> _responses = new();
        private readonly object _lock = new();

        public SubmitResponseRecorder(IPage page)
        {
            _page = page;
            _page.Response += OnResponse;
        }

        private void OnResponse(object? sender, IResponse response)
        {
            try
            {
                if (!IsCandidate(response)) return;
                lock (_lock)
                {
                    if (_responses.Count < MaxResponses)
                        _responses.Add(response);
                }
            }
            catch
            {
                // 页面切换时请求对象可能已失效，不影响提交本身。
            }
        }

        public async Task<string> StopAsync()
        {
            _page.Response -= OnResponse;
            IResponse[] responses;
            lock (_lock)
                responses = _responses.ToArray();

            var parts = new List<string>(responses.Length);
            foreach (var response in responses)
                parts.Add(await DescribeAsync(response).ConfigureAwait(false));
            return string.Join("；", parts);
        }

        private static bool IsCandidate(IResponse response)
        {
            var request = response.Request;
            if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)) return false;
            if (request.ResourceType is not ("xhr" or "fetch")) return false;
            if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var uri)) return false;
            if (!uri.Host.Contains("tiktok", StringComparison.OrdinalIgnoreCase)) return false;
            var path = uri.AbsolutePath;
            return !IgnoredSubmitResponsePathMarkers.Any(marker =>
                path.Contains(marker, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string> DescribeAsync(IResponse response)
        {
            var path = Uri.TryCreate(response.Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "";
            var summary = $"POST {path} HTTP {response.Status}";
            try
            {
                var body = await response.TextAsync().ConfigureAwait(false);
                var detail = DescribeSubmitResponseBody(body);
                if (!string.IsNullOrWhiteSpace(detail))
                    summary += " " + detail;
            }
            catch
            {
                // 页面已跳转时返回体可能读不到，只保留状态码。
            }
            return summary;
        }
    }

    internal static string DescribeSubmitResponseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";

            var scopes = new List<JsonElement> { root };
            foreach (var nested in new[] { "BaseResp", "base_resp", "baseResp" })
            {
                if (root.TryGetProperty(nested, out var value) && value.ValueKind == JsonValueKind.Object)
                    scopes.Add(value);
            }

            var parts = new List<string>();
            foreach (var scope in scopes)
            {
                var code = FirstScalar(scope, SubmitResponseCodeKeys);
                if (code is not null && !parts.Any(part => part.StartsWith("code=", StringComparison.Ordinal)))
                    parts.Add($"code={code}");
                var message = FirstScalar(scope, SubmitResponseMessageKeys);
                if (!string.IsNullOrWhiteSpace(message) && !parts.Any(part => part.StartsWith("msg=", StringComparison.Ordinal)))
                    parts.Add($"msg={(message.Length <= 80 ? message : message[..80] + "...")}");
            }
            return string.Join(" ", parts);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string? FirstScalar(JsonElement scope, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!scope.TryGetProperty(key, out var value)) continue;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (text is not null) return text.Trim();
        }
        return null;
    }
}

/// <summary>提交后平台列表和详情仍是草稿；携带草稿详情地址，供上传流程按草稿编辑重新提交。</summary>
internal sealed class TikTokSubmitStillDraftException : InvalidOperationException
{
    public TikTokSubmitStillDraftException(string message, string draftDetailUrl)
        : base(message)
    {
        DraftDetailUrl = draftDetailUrl;
    }

    public string DraftDetailUrl { get; }
}
