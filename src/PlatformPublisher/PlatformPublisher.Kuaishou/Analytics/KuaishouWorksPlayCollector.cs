using System.Collections.Concurrent;
using Microsoft.Playwright;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Kuaishou.Analytics;

public sealed class KuaishouWorksPlayCollector
{
    public const string WorksUrl = "https://cp.kuaishou.com/statistics/works";
    private readonly KuaishouPersonalSessionService _sessionService;

    public KuaishouWorksPlayCollector(KuaishouPersonalSessionService sessionService) => _sessionService = sessionService;

    public async Task<IReadOnlyDictionary<DateOnly, long>> CollectAsync(
        AnalyticsAccount account, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var found = new Dictionary<DateOnly, long>();
        var job = new PublishJob
        {
            Platform = account.Platform,
            AccountId = account.Id,
            AccountName = account.Name,
            ProjectName = "数据统计",
            ProjectDirectory = string.IsNullOrWhiteSpace(account.SessionDirectory) ? Path.GetTempPath() : account.SessionDirectory,
            ConfigPath = account.ConfigPath,
        };
        await _sessionService.ExecuteAuthenticatedAsync(job, async (page, _, ct) =>
        {
            var bodies = new ConcurrentQueue<string>();
            void Handler(object? _, IResponse response) => _ = CaptureAsync(response, bodies);
            page.Response += Handler;
            try
            {
                await page.GotoAsync(WorksUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                await page.WaitForTimeoutAsync(1200);
                if (IsLoginPage(page))
                    throw new InvalidOperationException("创作者服务平台登录态无效，请先在该快手分账账号里重新登录。");
                await ClickThirtyDaysAsync(page);
                var deadline = DateTime.UtcNow.AddSeconds(8);
                do
                {
                    await page.WaitForTimeoutAsync(400);
                    Drain(bodies, found);
                }
                while (DateTime.UtcNow < deadline && !found.ContainsKey(start));
                await ReadMissingDaysAsync(page, bodies, found, start, end, ct);
            }
            finally
            {
                page.Response -= Handler;
            }
        }, cancellationToken, headless: true);
        if (found.Count == 0)
            throw new InvalidOperationException("没有读到按日播放量。请确认这个账号能打开创作者服务平台的作品数据页。");
        return found;
    }

    private static async Task ReadMissingDaysAsync(IPage page, ConcurrentQueue<string> bodies, Dictionary<DateOnly, long> found,
        DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var misses = 0;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.ContainsKey(date)) continue;
            if (!await TrySelectSingleDayAsync(page, date))
            {
                if (++misses >= 2) return;
                continue;
            }
            await page.WaitForTimeoutAsync(800);
            Drain(bodies, found);
            if (found.ContainsKey(date))
            {
                misses = 0;
                continue;
            }
            var text = await page.Locator("body").InnerTextAsync();
            if (KuaishouWorksPlayParser.ShowsSingleDay(text, date) &&
                KuaishouWorksPlayParser.ReadLabeledPlay(text) is long views)
            {
                found[date] = views;
                misses = 0;
                continue;
            }
            if (++misses >= 2) return;
        }
    }

    private static async Task<bool> TrySelectSingleDayAsync(IPage page, DateOnly date)
    {
        var token = date.ToString("yyyy-MM-dd");
        var inputs = page.Locator("input");
        var filled = 0;
        for (var index = 0; index < await inputs.CountAsync() && filled < 2; index++)
        {
            var input = inputs.Nth(index);
            if (!await input.IsVisibleAsync()) continue;
            var value = await input.InputValueAsync();
            var placeholder = await input.GetAttributeAsync("placeholder") ?? string.Empty;
            if (!LooksLikeDate(value) && !placeholder.Contains('日') && !placeholder.Contains("时间")) continue;
            await input.FillAsync(token);
            filled++;
        }
        if (filled == 0) return false;
        await page.Keyboard.PressAsync("Enter");
        return true;
    }

    private static async Task ClickThirtyDaysAsync(IPage page)
    {
        var radio = page.Locator("input.el-radio-button__orig-radio[value='30']");
        for (var attempt = 0; attempt < 10 && await radio.CountAsync() == 0; attempt++)
            await page.WaitForTimeoutAsync(300);
        try
        {
            if (await radio.CountAsync() > 0)
            {
                await radio.First.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3000 });
                return;
            }
            var shortcut = page.GetByText("近30天", new PageGetByTextOptions { Exact = true });
            if (await shortcut.CountAsync() > 0 && await shortcut.First.IsVisibleAsync())
                await shortcut.First.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private static async Task CaptureAsync(IResponse response, ConcurrentQueue<string> bodies)
    {
        try
        {
            if (!response.Url.Contains("kuaishou.com", StringComparison.OrdinalIgnoreCase)) return;
            var type = response.Headers.TryGetValue("content-type", out var header) ? header : string.Empty;
            if (!type.Contains("json", StringComparison.OrdinalIgnoreCase) && !response.Url.Contains("/rest/", StringComparison.OrdinalIgnoreCase))
                return;
            var text = await response.TextAsync();
            if (text.Length == 0 || text.Length > 2_000_000) return;
            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith('{') && !trimmed.StartsWith('[')) return;
            bodies.Enqueue(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private static void Drain(ConcurrentQueue<string> bodies, Dictionary<DateOnly, long> found)
    {
        while (bodies.TryDequeue(out var body))
            KuaishouWorksPlayParser.Merge(found, body);
    }

    private static bool IsLoginPage(IPage page) =>
        page.Url.Contains("login", StringComparison.OrdinalIgnoreCase) ||
        page.Url.Contains("passport", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDate(string value) =>
        value.Length >= 8 && value.Any(char.IsDigit) && (value.Contains('-') || value.Contains('/'));
}
