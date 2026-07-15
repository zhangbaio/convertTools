using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShortDrama.Core.Services;

public static partial class AiApiErrorMessage
{
    public static string Create(
        string operation,
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string? responseText,
        string? genericHint = null)
    {
        var statusText = $"HTTP {(int)statusCode} {reasonPhrase}".TrimEnd();
        if (IsBalanceOrQuotaError(responseText))
        {
            var requestId = ExtractRequestId(responseText);
            var requestIdText = string.IsNullOrWhiteSpace(requestId) ? "" : $"（Request id：{requestId}）";
            return $"{operation}失败：AI 账号余额不足或已欠费，请到接口服务商后台充值/续费后重试；如果已充值，请确认当前 API Key 属于已充值账号。{requestIdText} {statusText}";
        }

        var preview = PreviewResponse(responseText);
        var message = string.IsNullOrWhiteSpace(preview)
            ? $"{operation}失败：{statusText}"
            : $"{operation}失败：{statusText}；响应：{preview}";
        return string.IsNullOrWhiteSpace(genericHint)
            ? message
            : $"{message}。{genericHint}";
    }

    public static bool IsBalanceOrQuotaError(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return false;

        return responseText.Contains("AccountOverdueError", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("insufficient balance", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("not enough balance", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("balance is not enough", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("account balance", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("billing", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("exceeded your current quota", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("insufficient quota", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("余额不足", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("账户余额", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("额度不足", StringComparison.OrdinalIgnoreCase)
               || responseText.Contains("欠费", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractRequestId(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return "";

        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (TryGetString(document.RootElement, "request_id", out var requestId) ||
                TryGetString(document.RootElement, "requestId", out requestId))
            {
                return requestId;
            }
        }
        catch (JsonException)
        {
            // Fall back to regex extraction below.
        }

        var match = RequestIdRegex().Match(responseText);
        return match.Success ? match.Groups["id"].Value.Trim() : "";
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(value);
        }

        if (element.ValueKind != JsonValueKind.Object) return false;

        foreach (var child in element.EnumerateObject())
        {
            if (TryGetString(child.Value, name, out value))
                return true;
        }

        return false;
    }

    private static string PreviewResponse(string? responseText)
    {
        var text = (responseText ?? "").Trim();
        if (text.Length <= 240) return text;
        return text[..240] + "...";
    }

    [GeneratedRegex("\"request[_-]?id\"\\s*:\\s*\"(?<id>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex RequestIdRegex();
}
