namespace TikTokPublisher.Core.Drama;

/// <summary>
/// 对齐 Python <c>normalize_local_media_url</c>：把 hglocal 相对海报地址补成可下载的绝对 URL。
/// </summary>
public static class HongguoLocalMediaUrl
{
    public static string Normalize(string? value, string? hongguoLocalBaseUrl)
    {
        var text = (value ?? "").Trim().Trim('"', '\'').Replace("\\/", "/", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
            return "";

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (text.StartsWith("//", StringComparison.Ordinal))
            return "https:" + text;

        var mediaBase = NormalizeApiBaseUrl(hongguoLocalBaseUrl);
        if (string.IsNullOrWhiteSpace(mediaBase))
            return text;

        if (text.StartsWith("img?", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("img/", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(mediaBase, UriKind.Absolute, out var parsed))
                return text;
            var origin = $"{parsed.Scheme}://{parsed.Authority}/";
            return new Uri(new Uri(origin), text).ToString();
        }

        // /img?url=... 这类绝对路径挂在站点根，而不是 /api/hongguo 下。
        if (text.StartsWith('/'))
        {
            if (!Uri.TryCreate(mediaBase, UriKind.Absolute, out var parsed))
                return text;
            var origin = $"{parsed.Scheme}://{parsed.Authority}/";
            return new Uri(new Uri(origin), text.TrimStart('/')).ToString();
        }

        var baseWithSlash = mediaBase.TrimEnd('/') + "/";
        return new Uri(new Uri(baseWithSlash), text).ToString();
    }

    private static string NormalizeApiBaseUrl(string? value)
    {
        var baseUrl = (value ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "";
        return baseUrl.EndsWith("/api/hongguo", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/api/hongguo";
    }
}
