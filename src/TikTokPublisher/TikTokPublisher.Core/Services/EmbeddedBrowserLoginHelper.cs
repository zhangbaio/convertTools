using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

public static class EmbeddedBrowserLoginHelper
{
  private static readonly string[] AuthCookieDomainMarkers = ["tiktokdramacenter.com", "tiktok.com"];

  public static bool IsLoginUrl(string? url)
  {
    var lowered = (url ?? "").Trim().ToLowerInvariant();
    return lowered.Contains("tiktokdramacenter.com") && lowered.Contains("/login");
  }

  public static string ResolveHomeUrl(TikTokAccountProfile profile)
  {
    var configured = (profile.TiktokSeriesUrl ?? "").Trim();
    var normalized = string.IsNullOrEmpty(configured) ? TikTokUrls.DefaultSeriesListUrl : NormalizeUrl(configured);
    var lowered = normalized.ToLowerInvariant();
    if (lowered.Contains("/login") || lowered.Contains("/series/draft"))
      return TikTokUrls.DefaultSeriesListUrl;
    return normalized;
  }

  public static string NormalizeUrl(string value)
  {
    var text = (value ?? "").Trim();
    if (string.IsNullOrEmpty(text))
      return TikTokUrls.DefaultSeriesListUrl;
    if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^[a-zA-Z][a-zA-Z0-9+.-]*://"))
      return $"https://{text}";
    return text;
  }

  public static bool HasTikTokAuthCookie(IEnumerable<EmbeddedBrowserCookie> cookies) =>
    cookies.Any(c => AuthCookieDomainMarkers.Any(m => c.Domain.Contains(m, StringComparison.OrdinalIgnoreCase)));

  public static string ResolveAuthPath(TikTokAccountProfile profile)
  {
    var explicitPath = (profile.TiktokStorageStatePath ?? "").Trim();
    if (!string.IsNullOrEmpty(explicitPath))
    {
      try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath)); }
      catch { return explicitPath; }
    }
    return AppPaths.DefaultStorageStatePath(profile.Id);
  }

  public static string ResolveAccountKey(TikTokAccountProfile profile) =>
    FirstNonEmpty(profile.ResolveTikTokAccountName(), profile.DisplayName, profile.Id);

  private static string FirstNonEmpty(params string?[] values)
  {
    foreach (var value in values)
    {
      var text = (value ?? "").Trim();
      if (!string.IsNullOrWhiteSpace(text))
        return text;
    }

    return "";
  }
}

public sealed record EmbeddedBrowserCookie(
  string Name,
  string Value,
  string Domain,
  string Path,
  long Expires,
  bool HttpOnly,
  bool Secure,
  string SameSite);

public sealed record EmbeddedAuthSaveResult(
  string AuthPath,
  int CookieCount,
  int OriginCount,
  string SavedAt);
