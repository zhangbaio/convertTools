using System.Text.Json;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services;

/// <summary>从内置浏览器 Cookie/localStorage 导出 Playwright storage_state（对齐 Python embedded_auth_export_service）。</summary>
public static class EmbeddedAuthExportService
{
  public static EmbeddedAuthSaveResult SaveAuthState(
    TikTokAccountProfile profile,
    IReadOnlyList<EmbeddedBrowserCookie> cookies,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> localStorageByOrigin)
  {
    var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(profile);
    var accountKey = EmbeddedBrowserLoginHelper.ResolveAccountKey(profile);
    if (string.IsNullOrWhiteSpace(accountKey))
      throw new InvalidOperationException("请先在账号资料中填写 TikTok 用户名，再保存授权。");

    var state = BuildPlaywrightStorageState(cookies, localStorageByOrigin);
    if (state.Cookies.Count == 0)
      throw new InvalidOperationException("未读取到内置浏览器 Cookie，请先在内置浏览器完成 TikTok 登录。");
    if (!EmbeddedBrowserLoginHelper.HasTikTokAuthCookie(cookies))
      throw new InvalidOperationException("未读取到 TikTok 登录 Cookie，请确认页面已登录成功后再保存授权。");

    Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
    var payload = new Dictionary<string, object>
    {
      ["cookies"] = state.Cookies.Select(c => new Dictionary<string, object>
      {
        ["name"] = c.Name,
        ["value"] = c.Value,
        ["domain"] = c.Domain,
        ["path"] = c.Path,
        ["expires"] = c.Expires,
        ["httpOnly"] = c.HttpOnly,
        ["secure"] = c.Secure,
        ["sameSite"] = c.SameSite,
      }).ToList(),
      ["origins"] = state.Origins.Select(o => new Dictionary<string, object>
      {
        ["origin"] = o.Origin,
        ["localStorage"] = o.LocalStorage.Select(entry => new Dictionary<string, object>
        {
          ["name"] = entry.Name,
          ["value"] = entry.Value,
        }).ToList(),
      }).ToList(),
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(authPath, json);

    var savedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
    profile.TiktokStorageStatePath = authPath;
    profile.TiktokLastLoginEmail = string.IsNullOrWhiteSpace(profile.TiktokLoginEmail)
      ? profile.TiktokLastLoginEmail
      : profile.TiktokLoginEmail.Trim();
    profile.TiktokLastLoginAt = savedAt;
    profile.TiktokLoginBrowserMode = "embedded";

    return new EmbeddedAuthSaveResult(authPath, state.Cookies.Count, state.Origins.Count, savedAt);
  }

  public static PlaywrightStorageState BuildPlaywrightStorageState(
    IReadOnlyList<EmbeddedBrowserCookie> cookies,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> localStorageByOrigin)
  {
    var cookiePayloads = cookies
      .Where(c => !string.IsNullOrWhiteSpace(c.Name))
      .Select(c => new PlaywrightCookie
      {
        Name = c.Name,
        Value = c.Value,
        Domain = string.IsNullOrWhiteSpace(c.Domain) ? "www.tiktokdramacenter.com" : c.Domain,
        Path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path,
        Expires = c.Expires,
        HttpOnly = c.HttpOnly,
        Secure = c.Secure,
        SameSite = string.IsNullOrWhiteSpace(c.SameSite) ? "Lax" : c.SameSite,
      })
      .OrderBy(c => c.Domain)
      .ThenBy(c => c.Path)
      .ThenBy(c => c.Name)
      .ToList();

    var origins = localStorageByOrigin
      .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value.Count > 0)
      .OrderBy(pair => pair.Key, StringComparer.Ordinal)
      .Select(pair => new PlaywrightOrigin
      {
        Origin = pair.Key,
        LocalStorage = pair.Value
          .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
          .OrderBy(entry => entry.Key, StringComparer.Ordinal)
          .Select(entry => new PlaywrightLocalStorageEntry { Name = entry.Key, Value = entry.Value })
          .ToList(),
      })
      .Where(origin => origin.LocalStorage.Count > 0)
      .ToList();

    return new PlaywrightStorageState(cookiePayloads, origins);
  }

  public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseLocalStorageExport(string? json)
  {
    if (string.IsNullOrWhiteSpace(json))
      return new Dictionary<string, IReadOnlyDictionary<string, string>>();

    try
    {
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;
      var origin = root.TryGetProperty("origin", out var originEl) ? originEl.GetString() ?? "" : "";
      if (string.IsNullOrWhiteSpace(origin) || !root.TryGetProperty("localStorage", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
        return new Dictionary<string, IReadOnlyDictionary<string, string>>();

      var entries = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var item in entriesEl.EnumerateArray())
      {
        var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(name)) continue;
        var value = item.TryGetProperty("value", out var valueEl) ? valueEl.GetString() ?? "" : "";
        entries[name] = value;
      }

      return entries.Count == 0
        ? new Dictionary<string, IReadOnlyDictionary<string, string>>()
        : new Dictionary<string, IReadOnlyDictionary<string, string>> { [origin] = entries };
    }
    catch
    {
      return new Dictionary<string, IReadOnlyDictionary<string, string>>();
    }
  }
}

public sealed record PlaywrightStorageState(
  IReadOnlyList<PlaywrightCookie> Cookies,
  IReadOnlyList<PlaywrightOrigin> Origins);

public sealed class PlaywrightCookie
{
  public string Name { get; init; } = "";
  public string Value { get; init; } = "";
  public string Domain { get; init; } = "";
  public string Path { get; init; } = "/";
  public long Expires { get; init; } = -1;
  public bool HttpOnly { get; init; }
  public bool Secure { get; init; }
  public string SameSite { get; init; } = "Lax";
}

public sealed class PlaywrightOrigin
{
  public string Origin { get; init; } = "";
  public IReadOnlyList<PlaywrightLocalStorageEntry> LocalStorage { get; init; } = [];
}

public sealed class PlaywrightLocalStorageEntry
{
  public string Name { get; init; } = "";
  public string Value { get; init; } = "";
}
