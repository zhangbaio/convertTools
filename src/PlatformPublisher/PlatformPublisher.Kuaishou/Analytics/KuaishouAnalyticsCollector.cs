using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using PlatformPublisher.Analytics.Models;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Kuaishou.Publishing;

namespace PlatformPublisher.Kuaishou.Analytics;

public sealed class KuaishouAnalyticsCollector
{
    public const string AnalyticsUrl = "https://kdj.kuaishou.com/home/data/iaa-ad-data";
    private readonly KuaishouPersonalSessionService _sessionService;

    public KuaishouAnalyticsCollector(KuaishouPersonalSessionService sessionService) => _sessionService = sessionService;

    public async Task<IReadOnlyList<SubjectDailyAnalyticsRecord>> CollectAsync(
        AnalyticsAccount account, DateOnly metricDate, CancellationToken cancellationToken)
    {
        var records = new List<SubjectDailyAnalyticsRecord>();
        var projectDirectory = string.IsNullOrWhiteSpace(account.SessionDirectory)
            ? Path.GetTempPath()
            : account.SessionDirectory;
        var job = new PublishJob
        {
            Platform = PublishPlatform.KuaishouPersonalRevenue, AccountId = account.Id, AccountName = account.Name,
            ProjectName = "数据统计", ProjectDirectory = projectDirectory, ConfigPath = account.ConfigPath,
        };
        await _sessionService.ExecuteAuthenticatedAsync(job, async (page, _, ct) =>
        {
            records.AddRange(await CollectPageAsync(page, account.Id, metricDate, ct));
        }, cancellationToken);
        return records;
    }

    public static async Task<IReadOnlyList<SubjectDailyAnalyticsRecord>> CollectPageAsync(
        IPage page, string accountId, DateOnly metricDate, CancellationToken cancellationToken)
    {
        await page.GotoAsync(AnalyticsUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        if (page.Url.Contains("login", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("快手登录态已失效，请重新登录。");
        var tab = await VisibleAsync(page.GetByText(new Regex("^T-1日数据$")));
        if (tab is not null) await tab.ClickAsync();
        var native = await VisibleAsync(page.Locator("select"));
        var result = native is not null
            ? await CollectNativeAsync(page, native, accountId, metricDate, cancellationToken)
            : await CollectCustomAsync(page, accountId, metricDate, cancellationToken);
        if (result.Count == 0) throw new InvalidOperationException("快手账号下没有可采集的短剧。");
        return result;
    }

    private static async Task<List<SubjectDailyAnalyticsRecord>> CollectNativeAsync(IPage page, ILocator select,
        string accountId, DateOnly date, CancellationToken cancellationToken)
    {
        var options = select.Locator("option");
        var result = new List<SubjectDailyAnalyticsRecord>();
        for (var index = 0; index < await options.CountAsync(); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var option = options.Nth(index);
            var id = (await option.GetAttributeAsync("value"))?.Trim() ?? string.Empty;
            var name = (await option.InnerTextAsync()).Trim();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || name.Contains("请选择")) continue;
            await select.SelectOptionAsync(id);
            await page.WaitForTimeoutAsync(300);
            result.Add(await ReadRecordAsync(page, accountId, id, name, date));
        }
        return result;
    }

    private static async Task<List<SubjectDailyAnalyticsRecord>> CollectCustomAsync(IPage page, string accountId,
        DateOnly date, CancellationToken cancellationToken)
    {
        var combobox = await VisibleAsync(page.Locator("[role=combobox], .ks-select, .ant-select"))
            ?? throw new InvalidOperationException("快手收益页未找到短剧选择器。");
        await combobox.ClickAsync();
        var optionLocator = page.Locator("[role=option], .ks-select-dropdown li, .ant-select-dropdown [class*=option]");
        await optionLocator.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        var options = new List<(string Id, string Name)>();
        for (var i = 0; i < await optionLocator.CountAsync(); i++)
        {
            var option = optionLocator.Nth(i);
            if (!await option.IsVisibleAsync()) continue;
            var name = (await option.InnerTextAsync()).Trim();
            if (string.IsNullOrEmpty(name) || name.Contains("请选择")) continue;
            var id = await option.GetAttributeAsync("data-value") ?? await option.GetAttributeAsync("value") ?? name;
            if (options.All(item => item.Id != id)) options.Add((id, name));
        }
        await page.Keyboard.PressAsync("Escape");
        var result = new List<SubjectDailyAnalyticsRecord>();
        foreach (var option in options)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await combobox.ClickAsync();
            var target = await VisibleAsync(optionLocator.Filter(new() { HasText = option.Name }));
            if (target is null) throw new InvalidOperationException("无法选择快手短剧：" + option.Name);
            await target.ClickAsync();
            await page.WaitForTimeoutAsync(300);
            result.Add(await ReadRecordAsync(page, accountId, option.Id, option.Name, date));
        }
        return result;
    }

    private static async Task<SubjectDailyAnalyticsRecord> ReadRecordAsync(IPage page, string accountId,
        string subjectId, string subjectName, DateOnly expected)
    {
        var rows = page.Locator("table tbody tr");
        await rows.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        for (var index = 0; index < await rows.CountAsync(); index++)
        {
            var cells = await rows.Nth(index).Locator("td").AllTextContentsAsync();
            if (cells.Count < 6 || ParseDate(cells[0]) != expected) continue;
            return new SubjectDailyAnalyticsRecord
            {
                Platform = PublishPlatform.KuaishouPersonalRevenue, AccountId = accountId,
                SubjectId = subjectId, SubjectName = subjectName, MetricDate = expected,
                CollectedAt = DateTimeOffset.UtcNow, Status = AnalyticsRecordStatus.Success,
                Views = Whole(ParseNumber(cells[1])), Likes = Whole(ParseNumber(cells[2])),
                Comments = Whole(ParseNumber(cells[3])), Favorites = Whole(ParseNumber(cells[4])),
                AdIncomeFen = Fen(ParseNumber(cells[5])),
            };
        }
        throw new InvalidOperationException($"快手尚未生成 {expected:yyyy-MM-dd} 的 T-1 数据。");
    }

    private static async Task<ILocator?> VisibleAsync(ILocator locator)
    {
        for (var i = 0; i < await locator.CountAsync(); i++) if (await locator.Nth(i).IsVisibleAsync()) return locator.Nth(i);
        return null;
    }

    public static decimal ParseNumber(string value)
    {
        var normalized = Regex.Replace(value.Trim(), "[￥¥元,，\\s]", "");
        if (string.IsNullOrEmpty(normalized) || normalized is "-" or "—") throw new FormatException("快手统计字段暂无数据：" + value);
        var match = Regex.Match(normalized, @"^(-?\d+(?:\.\d+)?)(万|w)?$", RegexOptions.IgnoreCase);
        if (!decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            throw new FormatException("无法解析快手统计数值：" + value);
        return number * (match.Groups[2].Success && match.Groups[2].Value.Length > 0 ? 10_000m : 1m);
    }

    public static DateOnly ParseDate(string value)
    {
        var match = Regex.Match(value.Trim(), @"(\d{4})[年/.-](\d{1,2})[月/.-](\d{1,2})日?");
        if (!match.Success) throw new FormatException("无法解析快手统计日期：" + value);
        return new DateOnly(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }
    private static long Whole(decimal value) => checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
    private static long Fen(decimal value) => checked((long)Math.Round(value * 100m, MidpointRounding.AwayFromZero));
}
