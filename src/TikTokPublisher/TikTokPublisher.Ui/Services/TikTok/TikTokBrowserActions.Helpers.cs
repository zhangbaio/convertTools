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
        await ConfirmCoverCropDialogIfPresentAsync(page, log, ct);
        await OpenComboboxAsync(page, combo, ct);
        var optionSelectors = new[]
        {
            "[role=\"dialog\"] .Select__item",
            "[role=\"dialog\"] [role=\"option\"]",
            "[role=\"listbox\"] .Select__item",
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
                await page.WaitForTimeoutAsync(400);
                return;
            }

            var popup = await FindPopupOptionByTextAsync(page, label);
            if (popup is not null)
            {
                await ClickWithFallbackAsync(popup, ct);
                await page.WaitForTimeoutAsync(400);
                return;
            }
        }

        var visible = await CollectVisiblePopupTextsAsync(page);
        throw new InvalidOperationException(
            $"TikTok 下拉选项不存在: {string.Join("/", labels)}；当前可见选项: {string.Join(" | ", visible.Take(20))}");
    }

    private static async Task SelectContentCreationTypeAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        if (!options.IsAiDrama)
        {
            Log(log, "TikTok 是否 AI 短剧为否，跳过内容创作类型。");
            return;
        }

        var key = TikTokPublishConstants.NormalizeContentCreationType(options.ContentCreationType);
        var value = TikTokPublishConstants.ContentCreationTypeValues[key].ToString();
        var labels = TikTokPublishConstants.ContentCreationTypeLabels[key];
        var field = page.Locator("[x-field-id='isRemakeV2']").First;
        var appeared = await WaitUntilAsync(async () =>
        {
            try
            {
                return await field.CountAsync() > 0 &&
                       await field.IsVisibleAsync(new() { Timeout = 500 });
            }
            catch
            {
                return false;
            }
        }, DefaultFieldVerifyTimeoutMs, 300, ct);

        if (!appeared)
        {
            throw new InvalidOperationException(
                "TikTok 已选择「是否 AI 短剧=是」，但动态字段「内容创作类型」(isRemakeV2) 未出现。");
        }

        await ClosePopupIfOpenAsync(page);
        var combo = field.Locator("button[role='combobox']").First;
        await OpenComboboxAsync(page, combo, ct);

        ILocator? matched = null;
        foreach (var selector in new[]
                 {
                     $"[role='dialog'] [role='option'][data-value='{value}']",
                     $"[role='listbox'] [role='option'][data-value='{value}']",
                     $"[role='option'][data-value='{value}']",
                 })
        {
            var candidate = page.Locator(selector).First;
            try
            {
                if (await candidate.CountAsync() > 0 && await candidate.IsVisibleAsync())
                {
                    matched = candidate;
                    break;
                }
            }
            catch { /* try next stable selector */ }
        }

        if (matched is null)
        {
            var visible = await CollectVisiblePopupTextsAsync(page);
            throw new InvalidOperationException(
                $"TikTok 内容创作类型选项值不存在: {value} ({key})；当前可见选项: {string.Join(" | ", visible.Take(20))}");
        }

        await ClickWithFallbackAsync(matched, ct);
        await page.WaitForTimeoutAsync(400);

        var confirmed = await WaitUntilAsync(async () =>
        {
            try
            {
                var text = NormalizeWhitespace(await combo.InnerTextAsync(new() { Timeout = 2000 }));
                return labels.Any(label => text.Contains(label, StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }, DefaultFieldVerifyTimeoutMs, 300, ct);

        if (!confirmed)
            throw new InvalidOperationException($"TikTok 内容创作类型填写后校验失败，期望：{string.Join("/", labels)}");
        Log(log, $"TikTok 内容创作类型已确认：{labels[0]} (isRemakeV2={value})");
    }

    private static async Task SelectExpectedFullPriceAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        var trigger = await FindComboboxByFieldLabelAsync(page, ["预期全集价格设置", "预期全集价格"]);
        if (trigger is null)
            trigger = page.Locator("#business-mode-section button[role='combobox']").First;
        if (await trigger.CountAsync() == 0)
            throw new InvalidOperationException("未找到 TikTok「预期全集价格设置」下拉框。");

        await trigger.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await ClickWithFallbackAsync(trigger, ct);
        await page.WaitForTimeoutAsync(600);

        var candidates = await CollectPriceOptionCandidatesAsync(page);
        if (candidates.Count == 0)
        {
            await page.WaitForTimeoutAsync(800);
            candidates = await CollectPriceOptionCandidatesAsync(page);
        }

        var selected = ChooseExpectedFullPriceOption(candidates, options);
        if (selected is not null)
        {
            await ClickWithFallbackAsync(selected, ct);
            await page.WaitForTimeoutAsync(400);
            Log(log, $"TikTok 预期全集价格已选择：{await SafeInnerTextAsync(selected)}");
            return;
        }

        if (options.ExpectedFullPriceMode == "manual")
        {
            var fallback = await FindPopupOptionByTextAsync(page, options.ExpectedFullPriceValue);
            if (fallback is not null)
            {
                await ClickWithFallbackAsync(fallback, ct);
                await page.WaitForTimeoutAsync(400);
                return;
            }
        }

        await page.Locator("body").PressAsync("Escape");
        var visible = candidates.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (visible.Count == 0) visible = await CollectVisiblePopupTextsAsync(page);
        throw new InvalidOperationException(
            options.ExpectedFullPriceMode == "option_index"
                ? $"未找到预期全集价格第 {options.ExpectedFullPriceOptionIndex} 个选项；当前可见: {string.Join(" | ", visible.Take(20))}"
                : $"未找到预期全集价格设置选项：{options.ExpectedFullPriceLabel ?? options.ExpectedFullPriceValue}；当前可见: {string.Join(" | ", visible.Take(20))}");
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
        var selectors = new[] { ".semi-select-option", "[role='listbox'] .Select__item", "[role='listbox'] [role='option']", "[role='dialog'] [role='option']" };
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
            var xpaths = new[]
            {
                $"xpath=//*[self::label or self::span or self::div][normalize-space(.)={literal}]/following::button[@role='combobox'][1]",
                $"xpath=//*[self::label or self::span or self::div][contains(@class,'label') and contains(normalize-space(.), {literal})]/following::button[@role='combobox'][1]",
            };
            foreach (var xpath in xpaths)
            {
                var locator = page.Locator(xpath);
                if (await locator.CountAsync() > 0) return locator.First;
            }
        }
        return null;
    }

    private static async Task<ILocator?> FindContractOptionAsync(IPage page, string contractId)
    {
        var selectors = new[]
        {
            $".semi-select-option:has-text(\"{contractId}\")",
            $"[role=\"option\"]:has-text(\"{contractId}\")",
            $".Select__item:has-text(\"{contractId}\")",
            $"[class*='contractInfo']:has-text(\"{contractId}\")",
            $"span:has-text(\"{contractId}\")",
        };
        foreach (var selector in selectors)
        {
            var loc = page.Locator(selector).First;
            if (await loc.CountAsync() == 0) continue;
            var clickable = await ResolveClickableContractOptionAsync(loc);
            if (clickable is not null) return clickable;
        }
        return null;
    }

    private static async Task<ILocator?> FindFirstContractOptionAsync(IPage page) =>
        await WaitForFirstContractOptionAsync(page, CancellationToken.None);

    private static async Task<ILocator?> WaitForFirstContractOptionAsync(IPage page, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var option = await FindFirstContractOptionOnceAsync(page);
            if (option is not null) return option;
            await page.WaitForTimeoutAsync(500);
        }
        return null;
    }

    private static async Task<ILocator?> FindFirstContractOptionOnceAsync(IPage page)
    {
        var selectors = new[]
        {
            ".semi-select-option",
            "[role='option']",
            ".semi-select-option-list > div",
            ".semi-select-option-list-wrapper > div",
            "[class*='contractInfo']",
            ".Select__item",
        };
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            int count;
            try { count = await locator.CountAsync(); }
            catch { continue; }

            for (var i = 0; i < count; i++)
            {
                var clickable = await ResolveClickableContractOptionAsync(locator.Nth(i));
                if (clickable is null) continue;
                var text = await SafeInnerTextAsync(clickable);
                if (!string.IsNullOrWhiteSpace(text)) return clickable;
            }
        }
        return null;
    }

    private static async Task<ILocator?> ResolveClickableContractOptionAsync(ILocator locator)
    {
        var candidates = new[]
        {
            locator,
            locator.Locator("xpath=ancestor::div[contains(@class,'semi-select-option')][1]"),
            locator.Locator("xpath=ancestor::*[@role='option'][1]"),
            locator.Locator("xpath=ancestor::div[contains(@class,'Select__item')][1]"),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                if (await candidate.CountAsync() == 0) continue;
                if (await candidate.First.IsVisibleAsync()) return candidate.First;
            }
            catch { /* try next */ }
        }
        return null;
    }

    private static async Task<List<string>> CollectContractOptionTextsAsync(IPage page)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in new[]
                 {
                     ".semi-select-option",
                     "[role='option']",
                     ".semi-select-option-list > div",
                     ".semi-select-option-list-wrapper > div",
                     "[class*='contractInfo']",
                     ".Select__item",
                 })
        {
            var locator = page.Locator(selector);
            int count;
            try { count = await locator.CountAsync(); }
            catch { continue; }

            for (var i = 0; i < count; i++)
            {
                var text = await SafeInnerTextAsync(locator.Nth(i));
                if (string.IsNullOrWhiteSpace(text) || !seen.Add(text)) continue;
                result.Add(text);
            }
        }
        return result;
    }

    private static async Task<ILocator?> FindPopupOptionByTextAsync(IPage page, string label)
    {
        var selectors = new[]
        {
            "[role=\"dialog\"] *",
            "[role=\"listbox\"] *",
            ".Select__item",
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

    private static async Task<ILocator?> FindGenreOptionAsync(IPage page, string genre)
    {
        foreach (var selector in new[]
                 {
                     "[role=\"dialog\"] .Select__item",
                     "[role=\"listbox\"] .Select__item",
                     "[role=\"dialog\"] [role=\"option\"]",
                     "[role=\"listbox\"] [role=\"option\"]",
                     ".Select__item",
                 })
        {
            var option = page.Locator(selector).Filter(new() { HasText = genre }).First;
            if (await option.CountAsync() == 0) continue;
            try
            {
                var text = NormalizeWhitespace(await option.InnerTextAsync(new() { Timeout = 3000 }));
                if (!string.Equals(text, genre, StringComparison.Ordinal)) continue;
            }
            catch
            {
                continue;
            }

            return option;
        }

        return null;
    }

    private static async Task<List<string>> CollectSelectedGenreTextsAsync(IPage page)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in new[]
                 {
                     "[role=\"dialog\"] .Select__item[aria-selected=\"true\"]",
                     "[role=\"dialog\"] .Select__item[data-selected=\"true\"]",
                     "[role=\"listbox\"] .Select__item[aria-selected=\"true\"]",
                     "[role=\"listbox\"] .Select__item[data-selected=\"true\"]",
                     ".Select__item[aria-selected=\"true\"]",
                     ".Select__item[data-selected=\"true\"]",
                 })
        {
            var locator = page.Locator(selector);
            var count = await locator.CountAsync();
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var text = NormalizeWhitespace(await locator.Nth(i).InnerTextAsync(new() { Timeout = 3000 }));
                    if (string.IsNullOrWhiteSpace(text) || !seen.Add(text)) continue;
                    result.Add(text);
                }
                catch { /* ignore */ }
            }
        }

        if (result.Count > 0) return result;

        try
        {
            var field = await FindComboboxByFieldLabelAsync(page, ["题材类型", "题材"]);
            if (field is not null)
            {
                var container = field.Locator("xpath=ancestor::*[contains(@class,'semi-form-field')][1]");
                var tags = container.Locator(".semi-tag-content");
                var count = await tags.CountAsync();
                for (var i = 0; i < count; i++)
                {
                    var text = NormalizeWhitespace(await tags.Nth(i).InnerTextAsync(new() { Timeout = 3000 }));
                    if (!string.IsNullOrWhiteSpace(text) && seen.Add(text))
                        result.Add(text);
                }
            }
        }
        catch { /* ignore */ }

        return result;
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
        var drawer = page.Locator("[role='dialog']").Filter(new() { HasText = "版权内容自查清单" }).Last;
        if (await drawer.CountAsync() == 0)
            drawer = page.Locator("[role='dialog']")
                .Filter(new() { Has = page.Locator("input[type='checkbox'], [role='checkbox']") })
                .Last;
        if (await drawer.CountAsync() == 0)
            return false;

        await DismissFloatingAssistantAsync(page, log);

        var checkedCount = await CheckPromiseDrawerItemsAsync(drawer, page, ct);
        if (checkedCount > 0)
            Log(log, $"TikTok 版权内容自查清单已勾选 {checkedCount} 个子项。");

        var agree = await ResolvePromiseAgreeButtonAsync(drawer, page);
        if (await agree.CountAsync() == 0)
            return false;

        var enabled = await WaitUntilAsync(async () =>
        {
            try
            {
                await CheckPromiseDrawerItemsAsync(drawer, page, ct);
                return await IsPromiseAgreeButtonReadyAsync(agree);
            }
            catch { return false; }
        }, 30000, 500, ct);

        if (!enabled)
        {
            var uncheckedItems = await CollectUncheckedPromiseItemsAsync(drawer);
            var buttonState = await DescribeButtonStateAsync(agree);
            var uncheckedText = uncheckedItems.Count == 0 ? "未检测到未勾选项" : string.Join("；", uncheckedItems.Take(8));
            throw new InvalidOperationException(
                $"版权内容自查清单子项已勾选，但「同意」按钮仍不可用。{uncheckedText}。按钮状态：{buttonState}");
        }

        await ClickWithFallbackAsync(agree, ct);
        await page.WaitForTimeoutAsync(500);
        Log(log, "已勾选本人承诺抽屉并点击同意。");
        return true;
    }

    private static async Task<int> CheckPromiseDrawerItemsAsync(ILocator drawer, IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var changed = 0;

        try
        {
            changed += await drawer.EvaluateAsync<int>(
                """
                root => {
                  const skipIds = new Set(['anchorPromotionStatus', 'consignmentStatus']);
                  const checkedSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'checked')?.set;
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                  const resolveScope = rootNode => {
                    const role = normalize(rootNode.getAttribute?.('role')).toLowerCase();
                    if (role === 'dialog') return rootNode;
                    const title = Array.from(rootNode.querySelectorAll('*'))
                      .find(node => normalize(node.textContent).includes('版权内容自查清单'));
                    return title?.closest('[role="dialog"], .semi-modal, .semi-drawer, .semi-modal-content, .semi-drawer-content') || rootNode;
                  };
                  const scope = resolveScope(root);
                  const itemText = node => normalize((node.closest('label, .semi-checkbox-wrapper, [role="checkbox"], li, .semi-modal-content, .semi-drawer-content') || node).innerText || '');
                  const isVisible = node => {
                    const rect = node.getBoundingClientRect();
                    const style = window.getComputedStyle(node);
                    return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
                  };
                  const shouldSkipInput = input => {
                    const id = normalize(input.id);
                    const role = normalize(input.getAttribute('role')).toLowerCase();
                    if (role === 'switch' || skipIds.has(id)) return true;
                    const text = itemText(input);
                    return /不同意|取消/.test(text) && text.length <= 12;
                  };
                  const markChecked = input => {
                    if (input.checked) return false;
                    const target = input.closest('label') || input.closest('.semi-checkbox-wrapper') || input.closest('.semi-checkbox') || input;
                    try { target.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {}
                    try { target.click(); } catch {}
                    if (!input.checked && checkedSetter) {
                      checkedSetter.call(input, true);
                      input.dispatchEvent(new Event('input', { bubbles: true }));
                      input.dispatchEvent(new Event('change', { bubbles: true }));
                    }
                    return input.checked;
                  };

                  let changed = 0;
                  for (const input of Array.from(scope.querySelectorAll('input[type="checkbox"]'))) {
                    if (shouldSkipInput(input)) continue;
                    if (markChecked(input)) changed += 1;
                  }

                  for (const box of Array.from(scope.querySelectorAll('[role="checkbox"]'))) {
                    if (box.querySelector('input[type="checkbox"]')) continue;
                    const aria = normalize(box.getAttribute('aria-checked')).toLowerCase();
                    if (aria === 'true') continue;
                    const text = itemText(box);
                    if ((/不同意|取消/.test(text) && text.length <= 12) || !isVisible(box)) continue;
                    try { box.scrollIntoView({ block: 'center', inline: 'nearest' }); } catch {}
                    try {
                      box.click();
                      changed += 1;
                    } catch {}
                  }

                  return changed;
                }
                """);
        }
        catch
        {
            // Fall back to Playwright locators below.
        }

        var boxes = drawer.Locator("input[type=\"checkbox\"]");
        var count = Math.Min(await boxes.CountAsync(), 100);
        for (var index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var item = boxes.Nth(index);
            try
            {
                var role = (await item.GetAttributeAsync("role") ?? "").Trim().ToLowerInvariant();
                var itemId = (await item.GetAttributeAsync("id") ?? "").Trim();
                if (role == "switch" || itemId is "anchorPromotionStatus" or "consignmentStatus")
                    continue;

                if (await item.IsCheckedAsync())
                    continue;

                try
                {
                    await item.CheckAsync(new LocatorCheckOptions { Force = true, Timeout = 5000 });
                }
                catch
                {
                    var clickable = await ResolvePromiseCheckboxClickTargetAsync(item);
                    if (await clickable.CountAsync() > 0)
                        await ClickWithFallbackAsync(clickable, ct);
                }

                await page.WaitForTimeoutAsync(200);
                if (await item.IsCheckedAsync())
                    changed += 1;
            }
            catch { /* try next checkbox */ }
        }

        await page.WaitForTimeoutAsync(300);
        return changed;
    }

    private static async Task<ILocator> ResolvePromiseCheckboxClickTargetAsync(ILocator checkbox)
    {
        foreach (var xpath in new[]
                 {
                     "xpath=ancestor::label[1]",
                     "xpath=ancestor::*[contains(concat(' ', normalize-space(@class), ' '), ' semi-checkbox-wrapper ')][1]",
                     "xpath=ancestor::*[contains(concat(' ', normalize-space(@class), ' '), ' semi-checkbox ')][1]",
                 })
        {
            var candidate = checkbox.Locator(xpath).First;
            if (await candidate.CountAsync() > 0)
                return candidate;
        }

        return checkbox;
    }

    private static async Task<ILocator> ResolvePromiseAgreeButtonAsync(ILocator drawer, IPage page)
    {
        foreach (var text in new[]
                 {
                     "同意",
                     "Agree",
                     "contentPartnerHub_seriesEditPage_copyrightProof_agree",
                 })
        {
            var buttons = drawer.Locator("button").Filter(new() { HasText = text });
            var count = await buttons.CountAsync();
            for (var index = count - 1; index >= 0; index--)
            {
                var candidate = buttons.Nth(index);
                try
                {
                    if (await candidate.IsVisibleAsync(new() { Timeout = 500 }))
                        return candidate;
                }
                catch { /* try previous */ }
            }
        }

        // 文案未知时只在已确认的版权弹窗内选择最后一个可见主操作按钮。
        var allButtons = drawer.Locator("button");
        for (var index = await allButtons.CountAsync() - 1; index >= 0; index--)
        {
            var candidate = allButtons.Nth(index);
            try
            {
                if (await candidate.IsVisibleAsync(new() { Timeout = 500 }))
                    return candidate;
            }
            catch { /* try previous */ }
        }

        return drawer.Locator("button").Last;
    }

    private static async Task<bool> IsPromiseAgreeButtonReadyAsync(ILocator button)
    {
        if (await button.CountAsync() == 0)
            return false;
        try
        {
            if (!await button.IsVisibleAsync(new() { Timeout = 500 }))
                return false;
            if (await IsAriaDisabledAsync(button))
                return false;
            var cls = (await button.GetAttributeAsync("class") ?? "").ToLowerInvariant();
            if (cls.Contains("disabled", StringComparison.Ordinal))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> CollectUncheckedPromiseItemsAsync(ILocator drawer)
    {
        try
        {
            var result = await drawer.EvaluateAsync<string[]>(
                """
                root => {
                  const skipIds = new Set(['anchorPromotionStatus', 'consignmentStatus']);
                  const normalize = value => (value || '').replace(/\s+/g, ' ').trim();
                  const resolveScope = rootNode => {
                    const role = normalize(rootNode.getAttribute?.('role')).toLowerCase();
                    if (role === 'dialog') return rootNode;
                    const title = Array.from(rootNode.querySelectorAll('*'))
                      .find(node => normalize(node.textContent).includes('版权内容自查清单'));
                    return title?.closest('[role="dialog"], .semi-modal, .semi-drawer, .semi-modal-content, .semi-drawer-content') || rootNode;
                  };
                  const scope = resolveScope(root);
                  const labelText = node => normalize((node.closest('label, .semi-checkbox-wrapper, [role="checkbox"], li, .semi-modal-content, .semi-drawer-content') || node).innerText || '');
                  const result = [];
                  for (const input of Array.from(scope.querySelectorAll('input[type="checkbox"]'))) {
                    const id = normalize(input.id);
                    const role = normalize(input.getAttribute('role')).toLowerCase();
                    if (role === 'switch' || skipIds.has(id) || input.checked) continue;
                    const text = labelText(input);
                    if (!text || (/不同意|取消/.test(text) && text.length <= 12)) continue;
                    result.push(text);
                  }
                  for (const box of Array.from(scope.querySelectorAll('[role="checkbox"]'))) {
                    if (box.querySelector('input[type="checkbox"]')) continue;
                    if (normalize(box.getAttribute('aria-checked')).toLowerCase() === 'true') continue;
                    const text = labelText(box);
                    if (!text || (/不同意|取消/.test(text) && text.length <= 12)) continue;
                    result.push(text);
                  }
                  return Array.from(new Set(result)).slice(0, 12);
                }
                """);
            return result;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<string> DescribeButtonStateAsync(ILocator button)
    {
        try
        {
            var visible = await button.IsVisibleAsync(new() { Timeout = 500 });
            var disabledAttr = await button.GetAttributeAsync("disabled") ?? "";
            var ariaDisabled = await button.GetAttributeAsync("aria-disabled") ?? "";
            var cls = await button.GetAttributeAsync("class") ?? "";
            return $"visible={visible}, disabled={disabledAttr}, aria-disabled={ariaDisabled}, class={cls}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static async Task<bool> IsMainPromiseCheckedAsync(IPage page)
    {
        try
        {
            var signed = page.Locator(
                "[x-field-id='signed'] input[type='checkbox']").First;
            if (await signed.CountAsync() > 0)
                return await signed.IsCheckedAsync();

            var promiseLabel = page.Locator("label").Filter(new() { HasText = "本人承诺" }).First;
            if (await promiseLabel.CountAsync() > 0)
            {
                var scoped = promiseLabel.Locator("input.semi-checkbox-input, input[type='checkbox']").First;
                if (await scoped.CountAsync() > 0)
                    return await scoped.IsCheckedAsync();
            }
        }
        catch { /* fallback */ }

        return false;
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
