using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private static async Task SelectTuxOptionByFieldAsync(
        IPage page,
        IReadOnlyList<string> fieldLabels,
        IReadOnlyList<string> labels,
        int fallbackIndex,
        Action<string>? log,
        CancellationToken ct)
    {
        var combo = await FindComboboxByFieldLabelAsync(page, fieldLabels)
                    ?? page.Locator("button[role='combobox']").Nth(fallbackIndex);
        await SelectComboboxOptionAsync(page, combo, labels, log, ct);
    }

    private static async Task SelectComboboxOptionAsync(
        IPage page,
        ILocator combo,
        IReadOnlyList<string> labels,
        Action<string>? log,
        CancellationToken ct)
    {
        await OpenComboboxAsync(page, combo, ct);
        var optionSelectors = new[]
        {
            "[role=\"dialog\"] .Select__item",
            "[role=\"dialog\"] [role=\"option\"]",
            "[role=\"listbox\"] [role=\"option\"]",
            ".semi-select-option",
            ".semi-cascader-option",
        };

        foreach (var label in labels.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            foreach (var selector in optionSelectors)
            {
                var option = page.Locator(selector).Filter(new() { HasText = label }).First;
                if (await option.CountAsync() == 0) continue;
                await ClickWithFallbackAsync(option, ct);
                await page.WaitForTimeoutAsync(300);
                return;
            }

            var popup = await FindPopupOptionByTextAsync(page, label);
            if (popup is not null)
            {
                await ClickWithFallbackAsync(popup, ct);
                await page.WaitForTimeoutAsync(300);
                return;
            }
        }

        var visible = await CollectVisiblePopupTextsAsync(page);
        throw new InvalidOperationException(
            $"TikTok 下拉选项不存在: {string.Join("/", labels)}；当前可见选项: {string.Join(" | ", visible.Take(20))}");
    }

    private static async Task SelectExpectedFullPriceAsync(IPage page, TikTokPublishOptions options, CancellationToken ct)
    {
        var trigger = page.Locator("#business-mode-section button[role='combobox']").First;
        await trigger.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await ClickWithFallbackAsync(trigger, ct);
        await page.WaitForTimeoutAsync(500);

        var candidates = await CollectPriceOptionCandidatesAsync(page);
        var selected = ChooseExpectedFullPriceOption(candidates, options);
        if (selected is not null)
        {
            await ClickWithFallbackAsync(selected, ct);
            await page.WaitForTimeoutAsync(300);
            return;
        }

        if (options.ExpectedFullPriceMode == "manual")
        {
            var fallback = await FindPopupOptionByTextAsync(page, options.ExpectedFullPriceValue);
            if (fallback is not null)
            {
                await ClickWithFallbackAsync(fallback, ct);
                await page.WaitForTimeoutAsync(300);
                return;
            }
        }

        await page.Locator("body").PressAsync("Escape");
        var visible = candidates.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (visible.Count == 0) visible = await CollectVisiblePopupTextsAsync(page);
        throw new InvalidOperationException(
            options.ExpectedFullPriceMode == "option_index"
                ? $"未找到预期全集价格第 {options.ExpectedFullPriceOptionIndex} 个选项"
                : $"未找到预期全集价格设置选项：{options.ExpectedFullPriceLabel ?? options.ExpectedFullPriceValue}");
    }

    private static ILocator? ChooseExpectedFullPriceOption(IReadOnlyList<(ILocator Locator, string Text, string Value)> options, TikTokPublishOptions settings)
    {
        if (settings.ExpectedFullPriceMode == "option_index")
        {
            var index = Math.Max(1, settings.ExpectedFullPriceOptionIndex) - 1;
            return index >= 0 && index < options.Count ? options[index].Locator : null;
        }

        foreach (var option in options)
        {
            if (option.Value == settings.ExpectedFullPriceValue ||
                (!string.IsNullOrWhiteSpace(settings.ExpectedFullPriceLabel) && option.Text == settings.ExpectedFullPriceLabel))
                return option.Locator;
        }
        return null;
    }

    private static async Task OpenComboboxAsync(IPage page, ILocator combo, CancellationToken ct)
    {
        if (await HasVisiblePopupOptionsAsync(page)) return;
        await combo.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await ClickWithFallbackAsync(combo, ct);
        await page.WaitForTimeoutAsync(400);
    }

    private static async Task ClosePopupIfOpenAsync(IPage page)
    {
        try { await page.Locator("body").PressAsync("Escape"); }
        catch { /* ignore */ }
        await page.WaitForTimeoutAsync(200);
    }

    private static async Task<bool> HasVisiblePopupOptionsAsync(IPage page)
    {
        var selectors = new[] { ".semi-select-option", "[role='listbox'] [role='option']", "[role='dialog'] [role='option']" };
        foreach (var selector in selectors)
        {
            var loc = page.Locator(selector).First;
            try
            {
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync()) return true;
            }
            catch { /* ignore */ }
        }
        return false;
    }

    private static async Task<ILocator?> FindComboboxByFieldLabelAsync(IPage page, IReadOnlyList<string> fieldLabels)
    {
        foreach (var fieldLabel in fieldLabels)
        {
            var literal = XPathLiteral(fieldLabel);
            var xpath =
                $"xpath=//*[self::label or self::span or self::div][normalize-space(.)={literal}]/following::button[@role='combobox'][1]";
            var locator = page.Locator(xpath);
            if (await locator.CountAsync() > 0) return locator.First;
        }
        return null;
    }

    private static async Task<ILocator?> FindContractOptionAsync(IPage page, string contractId)
    {
        var selectors = new[]
        {
            $".semi-select-option:has-text(\"{contractId}\")",
            $"[role=\"option\"]:has-text(\"{contractId}\")",
            $"span:has-text(\"{contractId}\")",
        };
        foreach (var selector in selectors)
        {
            var loc = page.Locator(selector).First;
            if (await loc.CountAsync() > 0) return loc;
        }
        return null;
    }

    private static async Task<ILocator?> FindFirstContractOptionAsync(IPage page)
    {
        var selectors = new[] { ".semi-select-option", "[role=\"option\"]" };
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            var count = await locator.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var item = locator.Nth(i);
                var text = await SafeInnerTextAsync(item);
                if (!string.IsNullOrWhiteSpace(text)) return item;
            }
        }
        return null;
    }

    private static async Task<List<string>> CollectContractOptionTextsAsync(IPage page)
    {
        var result = new List<string>();
        foreach (var selector in new[] { ".semi-select-option", "[role=\"option\"]" })
        {
            var texts = await page.Locator(selector).AllInnerTextsAsync();
            result.AddRange(texts.Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)));
        }
        return result.Distinct().ToList();
    }

    private static async Task<ILocator?> FindPopupOptionByTextAsync(IPage page, string label)
    {
        var selectors = new[]
        {
            "[role=\"dialog\"] *",
            "[role=\"listbox\"] *",
            ".semi-select-option-list *",
            ".semi-popover-content *",
        };
        foreach (var selector in selectors)
        {
            var loc = page.Locator(selector).Filter(new() { HasText = label }).First;
            if (await loc.CountAsync() > 0) return loc;
        }
        return null;
    }

    private static async Task<ILocator?> FindGenreOptionAsync(IPage page, string genre) =>
        await FindPopupOptionByTextAsync(page, genre);

    private static async Task<List<string>> CollectSelectedGenreTextsAsync(IPage page)
    {
        var result = new List<string>();
        try
        {
            var tags = await page.Locator(".semi-tag-content, .semi-select-selection-text").AllInnerTextsAsync();
            result.AddRange(tags.Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)));
        }
        catch { /* ignore */ }
        return result.Distinct().ToList();
    }

    private static async Task<List<string>> CollectVisiblePopupTextsAsync(IPage page)
    {
        var result = new List<string>();
        foreach (var selector in new[] { ".semi-select-option", "[role='option']", "[role='dialog'] *" })
        {
            try
            {
                var texts = await page.Locator(selector).AllInnerTextsAsync();
                result.AddRange(texts.Select(NormalizeWhitespace).Where(t => !string.IsNullOrEmpty(t)));
            }
            catch { /* ignore */ }
        }
        return result.Distinct().Take(40).ToList();
    }

    private static async Task<List<(ILocator Locator, string Text, string Value)>> CollectPriceOptionCandidatesAsync(IPage page)
    {
        var result = new List<(ILocator, string, string)>();
        var locator = page.Locator("[role='option'], .semi-select-option");
        var count = await locator.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var item = locator.Nth(i);
            var text = NormalizeWhitespace(await SafeInnerTextAsync(item));
            if (string.IsNullOrEmpty(text)) continue;
            result.Add((item, text, ExtractPriceValue(text)));
        }
        return result;
    }

    private static async Task<bool> HandlePromiseDrawerAsync(IPage page, Action<string>? log, CancellationToken ct)
    {
        var agree = page.Locator("button").Filter(new() { HasText = "同意" }).First;
        if (await agree.CountAsync() > 0 && await agree.IsVisibleAsync())
        {
            await ClickWithFallbackAsync(agree, ct);
            await page.WaitForTimeoutAsync(600);
            Log(log, "已在承诺抽屉中点击同意。");
            return true;
        }
        return false;
    }

    private static async Task<bool> IsMainPromiseCheckedAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                """
                () => {
                  const boxes = Array.from(document.querySelectorAll('input.semi-checkbox-input, .semi-checkbox input'));
                  return boxes.some(el => el.checked);
                }
                """);
        }
        catch { return false; }
    }

    private static string XPathLiteral(string value)
    {
        if (!value.Contains('"')) return $"\"{value}\"";
        if (!value.Contains('\'')) return $"'{value}'";
        var parts = value.Split('"');
        return "concat(" + string.Join(", '\"', ", parts.Select(p => $"\"{p}\"")) + ")";
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string ExtractPriceValue(string text)
    {
        var match = Regex.Match(text, @"[\d,.]+");
        return match.Success ? match.Value : text.Trim();
    }
}
