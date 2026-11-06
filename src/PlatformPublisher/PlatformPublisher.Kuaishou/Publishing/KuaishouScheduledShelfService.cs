using Microsoft.Playwright;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouScheduledShelfResult(int Shelved, int Failed, int Skipped, int Scanned);

public sealed class KuaishouScheduledShelfService
{
    private readonly KuaishouPersonalSessionService _session;

    public KuaishouScheduledShelfService(KuaishouPersonalSessionService session) => _session = session;

    public async Task<KuaishouScheduledShelfResult> RunAsync(
        PublishJob job,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var config = KuaishouPersonalConfig.Load(job);
        var max = Math.Clamp(config.ScheduledShelfMaxItemsPerRound, 1, 200);
        var shelved = 0;
        var failed = 0;
        var skipped = 0;
        var scanned = 0;
        await _session.ExecuteAuthenticatedAsync(job, async (page, _, ct) =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pageNum = 1;
            while (shelved < max && pageNum <= 30)
            {
                ct.ThrowIfCancellationRequested();
                await page.GotoAsync(ListUrl(pageNum), new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000,
                });
                await page.WaitForTimeoutAsync(1500);
                if (page.Url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                    page.Url.Contains("passport", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("经营者平台登录态已失效，请重新登录。");

                var rows = await VisibleRowsAsync(page);
                if (rows.Count == 0)
                {
                    var body = await page.Locator("body").InnerTextAsync();
                    if (body.Contains("暂无数据", StringComparison.Ordinal) || pageNum > 1)
                        break;
                    await page.WaitForTimeoutAsync(2000);
                    rows = await VisibleRowsAsync(page);
                    if (rows.Count == 0)
                        break;
                }

                var acted = false;
                foreach (var row in rows)
                {
                    var text = await row.InnerTextAsync();
                    var key = KuaishouScheduledShelfRow.Key(text);
                    if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                        continue;
                    scanned++;
                    var title = KuaishouScheduledShelfRow.Title(text);
                    if (!KuaishouScheduledShelfRow.CanShelf(text))
                    {
                        if (text.Contains("平台风控", StringComparison.Ordinal))
                        {
                            skipped++;
                            progress?.Report($"跳过平台风控下架：{title}");
                        }
                        continue;
                    }

                    var action = await FindShelfButtonAsync(row);
                    if (action is null)
                    {
                        skipped++;
                        progress?.Report($"没有上架按钮，跳过：{title}");
                        continue;
                    }

                    acted = true;
                    try
                    {
                        progress?.Report($"正在上架：{title}");
                        await action.ClickAsync();
                        await ConfirmShelfAsync(page);
                        shelved++;
                        progress?.Report($"已上架：{title}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failed++;
                        progress?.Report($"上架失败：{title}；{OneLine(ex.Message)}");
                    }

                    break;
                }

                if (!acted)
                    pageNum++;
            }
        }, cancellationToken, headless: true);

        progress?.Report($"定时上架完成：上架 {shelved}，失败 {failed}，跳过 {skipped}，扫描 {scanned}。");
        return new KuaishouScheduledShelfResult(shelved, failed, skipped, scanned);
    }

    private static string ListUrl(int pageNum) =>
        $"https://kdj.kuaishou.com/home/content/content-management?sellingStatus=2&pageNum={pageNum}&pageSize=10&auditStatus=3";

    private static async Task<IReadOnlyList<ILocator>> VisibleRowsAsync(IPage page)
    {
        foreach (var selector in new[] { "table tbody tr", ".ant-table-tbody > tr", "[class*='table-row']", "[role='row']" })
        {
            var locator = page.Locator(selector);
            var count = Math.Min(await locator.CountAsync(), 30);
            var rows = new List<ILocator>();
            for (var index = 0; index < count; index++)
            {
                var row = locator.Nth(index);
                if (!await row.IsVisibleAsync())
                    continue;
                var text = await row.InnerTextAsync();
                if (string.IsNullOrWhiteSpace(text) || text.Contains("短剧标题", StringComparison.Ordinal))
                    continue;
                if (!text.Contains("短剧ID", StringComparison.Ordinal) &&
                    !text.Contains("已下架", StringComparison.Ordinal) &&
                    !text.Contains("平台风控", StringComparison.Ordinal))
                    continue;
                rows.Add(row);
            }

            if (rows.Count > 0)
                return rows;
        }

        return [];
    }

    private static async Task<ILocator?> FindShelfButtonAsync(ILocator row)
    {
        var actions = row.GetByText("上架", new LocatorGetByTextOptions { Exact = true });
        var count = await actions.CountAsync();
        for (var index = 0; index < count; index++)
        {
            var action = actions.Nth(index);
            if (!await action.IsVisibleAsync())
                continue;
            var label = (await action.InnerTextAsync()).Trim();
            if (label != "上架")
                continue;
            var disabled = await action.GetAttributeAsync("aria-disabled");
            var cls = await action.GetAttributeAsync("class") ?? string.Empty;
            if (string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                continue;
            return action;
        }

        return null;
    }

    private static async Task ConfirmShelfAsync(IPage page)
    {
        var dialog = page.Locator("[role='dialog']:visible, .ant-modal:visible, .ant-popconfirm:visible").Last;
        try
        {
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 8_000 });
        }
        catch (TimeoutException)
        {
            return;
        }

        var text = await dialog.InnerTextAsync();
        if (text.Contains("不可上架", StringComparison.Ordinal) || text.Contains("平台风控", StringComparison.Ordinal))
        {
            var cancel = dialog.GetByText("取消", new LocatorGetByTextOptions { Exact = true }).First;
            if (await cancel.CountAsync() > 0 && await cancel.IsVisibleAsync())
                await cancel.ClickAsync();
            throw new InvalidOperationException(OneLine(text));
        }

        foreach (var name in new[] { "确定", "确认", "上架" })
        {
            var candidate = dialog.GetByText(name, new LocatorGetByTextOptions { Exact = true });
            if (await candidate.CountAsync() == 0)
                continue;
            var button = candidate.Last;
            if (!await button.IsVisibleAsync())
                continue;
            await button.ClickAsync();
            await page.WaitForTimeoutAsync(800);
            return;
        }

        throw new InvalidOperationException("上架确认窗口里没有确定按钮。");
    }

    private static string OneLine(string message)
    {
        var text = string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return text.Length <= 120 ? text : text[..120];
    }
}
