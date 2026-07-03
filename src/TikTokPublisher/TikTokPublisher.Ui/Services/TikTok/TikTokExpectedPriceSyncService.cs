using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Ui.Services.TikTok;

/// <summary>从 TikTok 草稿页同步预期全集价格选项（对齐 Python <c>fetch_expected_full_price_options</c>）。</summary>
public static class TikTokExpectedPriceSyncService
{
    public static async Task<IReadOnlyList<ExpectedFullPriceOption>> FetchAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct)
    {
        var authPath = ResolveAuthPath(account);
        if (!File.Exists(authPath))
            throw new InvalidOperationException("未找到 TikTok 登录态，请先登录 TikTok。");

        log?.Invoke("正在连接 Playwright 同步价格选项…");
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--window-size=1440,1200",
                ],
            });

            try
            {
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    Locale = "zh-CN",
                    ViewportSize = new ViewportSize { Width = 1440, Height = 1200 },
                    StorageStatePath = authPath,
                });

                try
                {
                    var page = await context.NewPageAsync();
                    var draftDetailUrl = await TikTokEditFlowService.DiscoverEditableDraftDetailUrlAsync(
                        page, titleCandidates: null, log, ct);
                    if (string.IsNullOrWhiteSpace(draftDetailUrl))
                        throw new InvalidOperationException("未在原剧管理中找到可用于同步价格选项的草稿。");

                    log?.Invoke($"进入草稿详情：{draftDetailUrl}");
                    await page.GotoAsync(draftDetailUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60000,
                    });
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15000 }); }
                    catch { /* SPA */ }

                    await TikTokBrowserActions.DismissFloatingAssistantAsync(page, log);
                    await OpenBusinessModeStepAsync(page, ct);

                    var trigger = page.Locator("#business-mode-section button[role='combobox']").First;
                    await trigger.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
                    await trigger.ClickAsync(new() { Timeout = 10000 });
                    await page.WaitForTimeoutAsync(500);

                    var rawOptions = await CollectRawPriceOptionsAsync(page);
                    if (rawOptions.Count == 0)
                        throw new InvalidOperationException("未找到价格选项列表。");

                    try { await page.Locator("body").PressAsync("Escape"); }
                    catch { /* ignore */ }

                    var normalized = NormalizeOptions(rawOptions);
                    log?.Invoke($"已同步 {normalized.Count} 个价格选项。");
                    return normalized;
                }
                finally
                {
                    await context.CloseAsync();
                }
            }
            finally
            {
                await browser.CloseAsync();
            }
        }
        finally
        {
            playwright.Dispose();
        }
    }

    private static async Task OpenBusinessModeStepAsync(IPage page, CancellationToken ct)
    {
        foreach (var label in new[] { "商业模式", "鍟嗕笟妯″紡" })
        {
            foreach (var tag in new[] { "button", "div", "span" })
            {
                var locator = page.Locator(tag).Filter(new() { HasText = label }).First;
                if (await locator.CountAsync() == 0) continue;
                try
                {
                    await locator.ClickAsync(new() { Force = true, Timeout = 5000 });
                    await page.WaitForTimeoutAsync(800);
                    return;
                }
                catch { /* try next */ }
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<List<(string Value, string Label)>> CollectRawPriceOptionsAsync(IPage page)
    {
        var selectors = new[]
        {
            "[role='dialog'] .Select__item",
            "[role='listbox'] [role='option']",
            "[role='dialog'] [role='option']",
            ".semi-select-option",
        };

        var results = new List<(string Value, string Label)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            var count = await locator.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var option = locator.Nth(i);
                string text;
                try { text = NormalizeWhitespace(await option.InnerTextAsync(new() { Timeout = 300 })); }
                catch { continue; }
                if (string.IsNullOrEmpty(text) || !seen.Add(text)) continue;

                var dataValue = (await option.GetAttributeAsync("data-value") ?? "").Trim().Trim('"');
                var optionId = (await option.GetAttributeAsync("id") ?? "").Trim();
                var value = dataValue;
                if (string.IsNullOrEmpty(value))
                    value = ExtractPriceValue(optionId);
                if (string.IsNullOrEmpty(value))
                    value = ExtractPriceValue(text);
                var label = NormalizePriceLabel(text);
                if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(label)) continue;
                results.Add((value, label));
            }
        }
        return results;
    }

    private static IReadOnlyList<ExpectedFullPriceOption> NormalizeOptions(IReadOnlyList<(string Value, string Label)> raw)
    {
        var normalized = new List<ExpectedFullPriceOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rawValue, rawLabel) in raw)
        {
            var total = ExtractTotalPrice(rawLabel) ?? ExtractTotalPrice(rawValue);
            var unit = ExtractUnitPrice(rawLabel) ?? ExtractUnitPrice(rawValue);
            if (string.IsNullOrEmpty(total) || string.IsNullOrEmpty(unit)) continue;
            var key = $"{total}|{unit}";
            if (!seen.Add(key)) continue;
            normalized.Add(new ExpectedFullPriceOption(total, $"${total} | ${unit}/EP"));
        }

        return normalized
            .OrderBy(o => double.TryParse(o.Value, out var n) ? n : double.MaxValue)
            .ToArray();
    }

    private static string ResolveAuthPath(TikTokAccountProfile account)
    {
        var explicitPath = (account.TiktokStorageStatePath ?? "").Trim();
        if (!string.IsNullOrEmpty(explicitPath))
        {
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath)); }
            catch { return explicitPath; }
        }
        return AppPaths.DefaultStorageStatePath(account.Id);
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', (text ?? "").Replace('\u00a0', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ExtractPriceValue(string text)
    {
        var normalized = (text ?? "").Trim().Trim('"');
        if (normalized.StartsWith("option-", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["option-".Length..].Trim().Trim('"');
        if (normalized.StartsWith('$')) normalized = normalized[1..];
        var candidate = "";
        foreach (var ch in normalized)
        {
            if (char.IsDigit(ch) || ch == '.') candidate += ch;
            else if (candidate.Length > 0) break;
        }
        return candidate;
    }

    private static string NormalizePriceLabel(string text)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrEmpty(normalized)) return "";
        if (normalized.Contains("每集", StringComparison.Ordinal)) return normalized;
        var value = ExtractPriceValue(normalized);
        return string.IsNullOrEmpty(value) ? normalized : $"${value}";
    }

    private static string? ExtractTotalPrice(string text)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrEmpty(normalized)) return null;
        var values = Regex.Matches(normalized, @"\$?(\d+(?:\.\d+)?)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        return values.Count > 0 ? values[0] : null;
    }

    private static string? ExtractUnitPrice(string text)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrEmpty(normalized)) return null;
        var match = Regex.Match(normalized, @"每集\$?(\d+(?:\.\d+)?)");
        if (match.Success) return match.Groups[1].Value;
        var values = Regex.Matches(normalized, @"\$?(\d+(?:\.\d+)?)")
            .Select(m => m.Groups[1].Value)
            .ToList();
        if (values.Count == 1) return values[0];
        return values.Count >= 2 ? values[^1] : null;
    }
}
