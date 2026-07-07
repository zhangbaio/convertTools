using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private const int DefaultFieldSettleMs = 500;
    private const int DefaultFieldVerifyTimeoutMs = 15000;

    private static Task PauseBetweenFieldsAsync(IPage page, int ms = DefaultFieldSettleMs) =>
        page.WaitForTimeoutAsync(ms);

    private static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> predicate,
        int timeoutMs,
        int pollMs,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await predicate()) return true;
            await Task.Delay(pollMs, ct);
        }

        return await predicate();
    }

    private static async Task VerifyTargetAudienceAsync(
        IPage page,
        string targetAudience,
        Action<string>? log,
        CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(targetAudience) ? "female" : targetAudience;
        var labels = TikTokPublishConstants.TargetAudienceAliases.TryGetValue(key, out var aliases)
            ? aliases
            : TikTokPublishConstants.TargetAudienceAliases["female"];

        var ok = await WaitUntilAsync(async () =>
        {
            try
            {
                var combo = await FindComboboxByFieldLabelAsync(page, ["目标观众", "目标受众"]);
                if (combo is null) return false;
                var text = NormalizeWhitespace(await combo.InnerTextAsync(new() { Timeout = 2000 }));
                return labels.Any(label => text.Contains(label, StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }, DefaultFieldVerifyTimeoutMs, 400, ct);

        if (!ok)
            throw new InvalidOperationException($"TikTok 目标观众填写后校验失败，期望：{string.Join("/", labels)}");
        Log(log, $"TikTok 目标观众已确认：{string.Join("/", labels.Take(1))}");
    }

    private static async Task VerifyEpisodeCountAsync(
        IPage page,
        int episodeCount,
        Action<string>? log,
        CancellationToken ct)
    {
        var expected = Math.Max(1, episodeCount).ToString();
        var ok = await WaitUntilAsync(async () =>
        {
            try
            {
                var actual = await page.Locator("#totalVideoNum").First.InputValueAsync();
                return string.Equals(actual?.Trim(), expected, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }, DefaultFieldVerifyTimeoutMs, 400, ct);

        if (!ok)
            throw new InvalidOperationException($"TikTok 总集数填写后校验失败，期望：{expected}");
        Log(log, $"TikTok 总集数已确认：{expected}");
    }

    private static async Task VerifyComboboxFieldAsync(
        IPage page,
        IReadOnlyList<string> fieldLabels,
        IReadOnlyList<string> expectedLabels,
        string fieldName,
        Action<string>? log,
        CancellationToken ct)
    {
        var ok = await WaitUntilAsync(async () =>
        {
            try
            {
                var combo = await FindComboboxByFieldLabelAsync(page, fieldLabels);
                if (combo is null) return false;
                var text = NormalizeWhitespace(await combo.InnerTextAsync(new() { Timeout = 2000 }));
                return expectedLabels.Any(label => text.Contains(label, StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }, DefaultFieldVerifyTimeoutMs, 400, ct);

        if (!ok)
            throw new InvalidOperationException(
                $"TikTok {fieldName}填写后校验失败，期望：{string.Join("/", expectedLabels)}");
        Log(log, $"TikTok {fieldName}已确认：{expectedLabels[0]}");
    }

    private static async Task<bool> WaitForMainPromiseCheckedAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct,
        int timeoutMs = DefaultFieldVerifyTimeoutMs)
    {
        var ok = await WaitUntilAsync(() => IsMainPromiseCheckedAsync(page), timeoutMs, 400, ct);
        if (ok) Log(log, "TikTok 本人承诺已确认勾选。");
        return ok;
    }

    private static async Task VerifyCoverUploadCompleteAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        if (await IsCoverAlreadyUploadedAsync(page))
        {
            Log(log, "TikTok 封面已确认上传完成。");
            return;
        }

        await ConfirmCoverCropDialogIfPresentAsync(page, log, ct);
        await WaitForCoverUploadAppliedAsync(page, log, ct);
    }

    private static async Task VerifyNumericFieldAsync(
        IPage page,
        string selector,
        int expectedValue,
        string fieldName,
        Action<string>? log,
        CancellationToken ct)
    {
        var expected = Math.Max(0, expectedValue).ToString();
        var locator = await ResolveVisibleInputAsync(page, selector, fieldName, ct);
        var ok = await WaitUntilAsync(async () =>
        {
            try
            {
                var actual = await locator.InputValueAsync();
                return string.Equals(actual?.Trim(), expected, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }, DefaultFieldVerifyTimeoutMs, 400, ct);

        if (!ok)
            throw new InvalidOperationException($"TikTok {fieldName}填写后校验失败，期望：{expected}");
        Log(log, $"TikTok {fieldName}已确认：{expected}");
    }

    private static async Task VerifyExpectedFullPriceAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        var ok = await WaitUntilAsync(async () =>
        {
            try
            {
                var combo = await FindComboboxByFieldLabelAsync(page, ["预期全集价格设置", "预期全集价格"])
                            ?? page.Locator("#business-mode-section button[role='combobox']").First;
                if (await combo.CountAsync() == 0) return false;
                var text = NormalizeWhitespace(await combo.InnerTextAsync(new() { Timeout = 2000 }));
                if (string.IsNullOrWhiteSpace(text)) return false;
                if (text.Contains("输入价格", StringComparison.Ordinal)) return false;

                if (string.Equals(options.ExpectedFullPriceMode, "option_index", StringComparison.OrdinalIgnoreCase))
                    return text.Contains('$') || text.Any(char.IsDigit);

                if (!string.IsNullOrWhiteSpace(options.ExpectedFullPriceLabel))
                    return text.Contains(options.ExpectedFullPriceLabel, StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(options.ExpectedFullPriceValue))
                    return text.Contains(options.ExpectedFullPriceValue, StringComparison.Ordinal);
                return true;
            }
            catch
            {
                return false;
            }
        }, DefaultFieldVerifyTimeoutMs, 400, ct);

        if (!ok)
            throw new InvalidOperationException("TikTok 预期全集价格设置填写后校验失败。");
        Log(log, "TikTok 预期全集价格设置已确认。");
    }

}
