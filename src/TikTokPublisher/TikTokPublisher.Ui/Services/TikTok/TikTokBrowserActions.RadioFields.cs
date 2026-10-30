using System.Text.Json;
using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private const string AdOneClickRadioGroupSelector = "#ad-section + div .semi-radioGroup";

    private static string RadioGroupSelectorForField(string fieldId) =>
        $"[x-field-id='{fieldId}'] .semi-radioGroup";

    /// <summary>
    /// Matches by visible text first (exact, then contains). TikTok sometimes renders raw
    /// i18n keys instead of labels, so the option position is the last resort.
    /// </summary>
    internal static int ResolveRadioOptionIndex(
        IReadOnlyList<string> optionTexts,
        IReadOnlyList<string> labels,
        int fallbackIndex)
    {
        var texts = optionTexts.Select(NormalizeWhitespace).ToArray();
        var wanted = labels.Where(l => !string.IsNullOrWhiteSpace(l)).Select(NormalizeWhitespace).ToArray();

        foreach (var label in wanted)
        {
            var exact = Array.FindIndex(texts, t => string.Equals(t, label, StringComparison.Ordinal));
            if (exact >= 0) return exact;
        }

        foreach (var label in wanted)
        {
            var matches = texts
                .Select((text, index) => (text, index))
                .Where(item => item.text.Contains(label, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 1) return matches[0].index;
        }

        var looksTranslated = texts.Any(t => t.Length > 0 && !t.Contains('_'));
        if (!looksTranslated && fallbackIndex >= 0 && fallbackIndex < texts.Length)
            return fallbackIndex;
        return -1;
    }

    private sealed record RadioOptionState(string Text, bool Checked, bool Disabled);

    private static async Task<IReadOnlyList<RadioOptionState>> ReadRadioOptionsAsync(ILocator group)
    {
        var json = await group.EvaluateAsync<string>(
            """
            group => JSON.stringify(Array.from(group.querySelectorAll('label.semi-radio')).map(label => ({
              text: (label.querySelector('.semi-radio-addon')?.textContent || label.textContent || '').replace(/\s+/g, ' ').trim(),
              checked: label.classList.contains('semi-radio-checked') || !!label.querySelector('input[type=radio]')?.checked,
              disabled: label.classList.contains('semi-radio-disabled') || !!label.querySelector('input[type=radio]')?.disabled
            })))
            """);
        return JsonSerializer.Deserialize<List<RadioOptionState>>(json, ScrapeJsonOptions) ?? [];
    }

    private static async Task<ILocator?> WaitForRadioGroupAsync(
        IPage page,
        string groupSelector,
        int timeoutMs,
        CancellationToken ct)
    {
        var group = page.Locator(groupSelector).First;
        var found = await WaitUntilAsync(async () =>
        {
            try { return await group.CountAsync() > 0; }
            catch { return false; }
        }, timeoutMs, 250, ct);
        return found ? group : null;
    }

    /// <summary>
    /// Selects one option in a Semi radio group and verifies it. Returns false only when the
    /// group does not exist, so callers can fall back to legacy switch/combobox controls.
    /// </summary>
    private static async Task<bool> TrySelectRadioGroupOptionAsync(
        IPage page,
        string groupSelector,
        IReadOnlyList<string> labels,
        int fallbackIndex,
        string fieldName,
        Action<string>? log,
        CancellationToken ct,
        int waitForGroupMs = 3000)
    {
        ct.ThrowIfCancellationRequested();
        var group = await WaitForRadioGroupAsync(page, groupSelector, waitForGroupMs, ct);
        if (group is null) return false;

        await group.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        var options = await ReadRadioOptionsAsync(group);
        var index = ResolveRadioOptionIndex(options.Select(o => o.Text).ToArray(), labels, fallbackIndex);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"TikTok「{fieldName}」没有选项 {string.Join("/", labels)}；当前选项：{string.Join(" | ", options.Select(o => o.Text))}");
        }

        var expectedText = options[index].Text;
        if (options[index].Checked)
        {
            Log(log, $"TikTok {fieldName}已确认：{expectedText}");
            return true;
        }

        if (options[index].Disabled)
            throw new InvalidOperationException($"TikTok「{fieldName}」的选项「{expectedText}」当前不可选。");

        var label = group.Locator("label.semi-radio").Nth(index);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await ClickWithFallbackAsync(label, ct);
            var confirmed = await WaitUntilAsync(async () =>
            {
                try
                {
                    var current = await ReadRadioOptionsAsync(group);
                    return index < current.Count && current[index].Checked;
                }
                catch
                {
                    return false;
                }
            }, 4000, 200, ct);
            if (confirmed)
            {
                Log(log, $"TikTok {fieldName}已选择并确认：{expectedText}");
                return true;
            }
        }

        throw new InvalidOperationException($"TikTok「{fieldName}」选择「{expectedText}」后校验失败。");
    }
}
