using Microsoft.Playwright;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using System.Text.RegularExpressions;

namespace ShortDrama.Desktop.Services;

public sealed class WeixinMaterialChannelVideoDeleteService
{
    private const string VideoManagerUrl = "https://channels.weixin.qq.com/platform/post/list";
    private static readonly Regex DateRegex = new(@"20\d{2}([年./-])\d{1,2}\1\d{1,2}", RegexOptions.Compiled);

    private readonly IWeixinAutomationConfigLoader _configLoader;
    private readonly WeixinBrowserRuntimeService _browserRuntimeService;
    private readonly WeixinHomePage _homePage;

    public WeixinMaterialChannelVideoDeleteService(
        IWeixinAutomationConfigLoader configLoader,
        WeixinBrowserRuntimeService browserRuntimeService,
        WeixinHomePage homePage)
    {
        _configLoader = configLoader;
        _browserRuntimeService = browserRuntimeService;
        _homePage = homePage;
    }

    public async Task<MaterialChannelVideoDeleteResult> DeleteAsync(
        string projectDirectory,
        string? configPath,
        string keyword,
        int deleteCount,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var resolvedKeyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(resolvedKeyword))
        {
            throw new InvalidOperationException("删除视频号素材必须填写搜索关键词。");
        }

        var targetDeleteCount = Math.Max(1, deleteCount);
        var config = await _configLoader.LoadAsync(configPath, projectDirectory, cancellationToken);

        var runtimeStatus = await _browserRuntimeService.InspectAsync(cancellationToken);
        if (!runtimeStatus.IsReady)
        {
            throw new InvalidOperationException(runtimeStatus.Message);
        }

        _browserRuntimeService.ConfigureEnvironment(runtimeStatus);
        using var playwright = await _browserRuntimeService.CreatePlaywrightAsync(cancellationToken);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = false,
            Args =
            [
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--start-maximized"
            ]
        });

        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = ViewportSize.NoViewport,
            UserAgent = config.Browser.UserAgent
        };
        if (!string.IsNullOrWhiteSpace(config.AuthFilePath) && File.Exists(config.AuthFilePath))
        {
            contextOptions.StorageStatePath = config.AuthFilePath;
            progress?.Report($"已复用本地视频号登录态：{config.AuthFilePath}");
        }

        await using var context = await browser.NewContextAsync(contextOptions);
        var page = await context.NewPageAsync();
        await page.GotoAsync(VideoManagerUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        await WaitForVideoManagerPageReadyAsync(page, cancellationToken);
        if (!await _homePage.IsLoggedInAsync(page, cancellationToken))
        {
            throw new InvalidOperationException("当前登录态未登录视频号助手，请先登录后再删除素材视频。");
        }

        await SearchVideoManagerByTitleAsync(page, resolvedKeyword, cancellationToken);

        var deletedTitles = new List<string>();
        for (var index = 0; index < targetDeleteCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await CollectVideoManagerRowsAsync(page, resolvedKeyword, cancellationToken, maxMatches: 2);
            if (rows.Count == 0)
            {
                if (deletedTitles.Count > 0)
                {
                    break;
                }

                throw new InvalidOperationException($"未找到可删除的视频号素材：{resolvedKeyword}");
            }

            var row = rows[0];
            var rowTitle = await ExtractVideoManagerRowTitleAsync(row);
            var rowSignatureBefore = NormalizeSpace(await row.InnerTextAsync());
            progress?.Report($"正在删除第 {index + 1}/{targetDeleteCount} 条：{(string.IsNullOrWhiteSpace(rowTitle) ? resolvedKeyword : rowTitle)}");

            var deleteAction = await FindVideoManagerDeleteActionAsync(row, cancellationToken);
            if (deleteAction is null)
            {
                throw new InvalidOperationException($"未找到删除按钮：{rowTitle}");
            }

            await deleteAction.ClickAsync();
            await ConfirmVideoDeleteDialogAsync(page, cancellationToken);

            var deletedThisRound = false;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SearchVideoManagerByTitleAsync(page, resolvedKeyword, cancellationToken);
                var remainingRows = await CollectVideoManagerRowsAsync(page, resolvedKeyword, cancellationToken, maxMatches: 2);
                if (remainingRows.Count == 0)
                {
                    deletedThisRound = true;
                    break;
                }

                var currentSignature = NormalizeSpace(await remainingRows[0].InnerTextAsync());
                if (!string.IsNullOrWhiteSpace(rowSignatureBefore) &&
                    !string.IsNullOrWhiteSpace(currentSignature) &&
                    !string.Equals(rowSignatureBefore, currentSignature, StringComparison.Ordinal))
                {
                    deletedThisRound = true;
                    break;
                }

                await Task.Delay(500, cancellationToken);
            }

            if (!deletedThisRound)
            {
                throw new InvalidOperationException($"删除后视频仍出现在当前列表中：{rowTitle}");
            }

            deletedTitles.Add(string.IsNullOrWhiteSpace(rowTitle) ? $"第{deletedTitles.Count + 1}条视频" : rowTitle);
        }

        if (!string.IsNullOrWhiteSpace(config.AuthFilePath))
        {
            await context.StorageStateAsync(new BrowserContextStorageStateOptions
            {
                Path = config.AuthFilePath
            });
        }

        return new MaterialChannelVideoDeleteResult(
            deletedTitles.Count > 0,
            deletedTitles.Count,
            deletedTitles,
            resolvedKeyword);
    }

    private static async Task WaitForVideoManagerPageReadyAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = new ILocator[]
        {
            page.GetByText("视频管理", new PageGetByTextOptions { Exact = false }).First,
            page.Locator("input[placeholder*='搜索视频']").First,
            page.GetByText("发表视频", new PageGetByTextOptions { Exact = false }).First,
            page.GetByText("删除", new PageGetByTextOptions { Exact = false }).First
        };

        await FirstVisibleAsync(candidates, TimeSpan.FromSeconds(15));
        await Task.Delay(500, cancellationToken);
    }

    private static async Task SearchVideoManagerByTitleAsync(IPage page, string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var searchInput = await FirstVisibleAsync(
            [
                page.Locator("input[placeholder*='搜索视频']").First,
                page.Locator("input[placeholder*='搜索']").First,
                page.Locator(".weui-desktop-search-bar input").First,
                page.Locator("input[type='search']").First,
                page.Locator("input[type='text']").First
            ],
            TimeSpan.FromSeconds(10));
        await searchInput.ClickAsync();
        await searchInput.FillAsync(string.Empty);
        await searchInput.FillAsync(title);
        try
        {
            await searchInput.PressAsync("Enter");
        }
        catch
        {
            await page.Keyboard.PressAsync("Enter");
        }

        await Task.Delay(700, cancellationToken);
    }

    private static async Task<IReadOnlyList<ILocator>> CollectVideoManagerRowsAsync(
        IPage page,
        string keyword,
        CancellationToken cancellationToken,
        int maxMatches)
    {
        var matches = new List<ILocator>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var anchorCandidates = new[]
        {
            page.Locator("text=/20\\\\d{2}年\\\\d{1,2}月\\\\d{1,2}日(?:\\\\s+\\\\d{1,2}:\\\\d{2})?/"),
            page.Locator("text=/20\\\\d{2}[./-]\\\\d{1,2}[./-]\\\\d{1,2}(?:\\\\s+\\\\d{1,2}:\\\\d{2})?/")
        };

        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var group in anchorCandidates)
            {
                var count = await group.CountAsync();
                for (var index = 0; index < Math.Min(count, 20); index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var anchor = group.Nth(index);
                    if (!await anchor.IsVisibleAsync())
                    {
                        continue;
                    }

                    var row = await ResolveVideoManagerRowFromAnchorAsync(anchor, keyword, cancellationToken);
                    if (row is null)
                    {
                        continue;
                    }

                    var rowText = NormalizeSpace(await row.InnerTextAsync());
                    if (!seen.Add(rowText))
                    {
                        continue;
                    }

                    matches.Add(row);
                    if (matches.Count >= maxMatches)
                    {
                        return matches;
                    }
                }
            }

            if (matches.Count > 0)
            {
                return matches;
            }

            await Task.Delay(200, cancellationToken);
        }

        return matches;
    }

    private static async Task<ILocator?> ResolveVideoManagerRowFromAnchorAsync(
        ILocator anchorLocator,
        string keyword,
        CancellationToken cancellationToken)
    {
        var current = anchorLocator;
        for (var depth = 0; depth < 10; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = current.Locator("xpath=..").First;
            if (!await current.IsVisibleAsync())
            {
                continue;
            }

            var text = NormalizeSpace(await current.InnerTextAsync());
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(keyword) &&
                !text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await current.ScrollIntoViewIfNeededAsync();
                await current.HoverAsync(new LocatorHoverOptions { Timeout = 1000 });
            }
            catch
            {
            }

            var deleteAction = await FindVideoManagerDeleteActionAsync(current, cancellationToken, timeoutMs: 800);
            if (deleteAction is not null)
            {
                return current;
            }
        }

        return null;
    }

    private static async Task<ILocator?> FindVideoManagerDeleteActionAsync(
        ILocator scope,
        CancellationToken cancellationToken,
        int timeoutMs = 1200)
    {
        var candidates = new ILocator[]
        {
            scope.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "删除", Exact = false }),
            scope.Locator("button:has-text('删除')"),
            scope.Locator("span:has-text('删除')").Locator("xpath=ancestor::button[1]"),
            scope.GetByText("删除", new LocatorGetByTextOptions { Exact = true }),
            scope.GetByText("删除", new LocatorGetByTextOptions { Exact = false })
        };

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in candidates)
            {
                var count = await candidate.CountAsync();
                for (var index = 0; index < Math.Min(count, 5); index++)
                {
                    var item = candidate.Nth(index);
                    if (await item.IsVisibleAsync())
                    {
                        return item;
                    }
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        return null;
    }

    private static async Task<string> ExtractVideoManagerRowTitleAsync(ILocator row)
    {
        var rowText = NormalizeSpace(await row.InnerTextAsync());
        if (string.IsNullOrWhiteSpace(rowText))
        {
            return string.Empty;
        }

        var ignoredFragments = new[]
        {
            "置顶",
            "分享",
            "弹窗管理",
            "评论管理",
            "修改描述和封面",
            "可见权限",
            "删除"
        };

        var lines = Regex.Split(rowText, @"[\r\n]+")
            .Select(NormalizeSpace)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        foreach (var line in lines)
        {
            if (ignoredFragments.Any(fragment => line.Contains(fragment, StringComparison.Ordinal)))
            {
                continue;
            }

            if (DateRegex.IsMatch(line))
            {
                continue;
            }

            if (Regex.IsMatch(line, @"^[\d\s❤♡💬👍↗️↘️/]+$"))
            {
                continue;
            }

            return line.Length > 120 ? line[..120] : line;
        }

        return lines.FirstOrDefault() ?? string.Empty;
    }

    private static async Task ConfirmVideoDeleteDialogAsync(IPage page, CancellationToken cancellationToken)
    {
        var dialog = await FirstVisibleAsync(
            [
                page.Locator("[role='dialog']:visible").First,
                page.Locator(".weui-desktop-dialog:visible").First,
                page.Locator(".weui-desktop-dialog__wrp:visible").First
            ],
            TimeSpan.FromSeconds(10));

        var confirmButton = await FirstVisibleAsync(
            [
                dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = "确定", Exact = false }),
                dialog.Locator("button:has-text('确定')").First,
                dialog.GetByText("确定", new LocatorGetByTextOptions { Exact = false }).First
            ],
            TimeSpan.FromSeconds(10));
        await confirmButton.ClickAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await dialog.IsVisibleAsync())
            {
                return;
            }

            await Task.Delay(300, cancellationToken);
        }
    }

    private static async Task<ILocator> FirstVisibleAsync(IEnumerable<ILocator> candidates, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    if (await candidate.IsVisibleAsync())
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            await Task.Delay(120);
        }

        throw new InvalidOperationException("未找到可操作的页面元素。");
    }

    private static string NormalizeSpace(string? value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }
}

public sealed record MaterialChannelVideoDeleteResult(
    bool Deleted,
    int DeletedCount,
    IReadOnlyList<string> DeletedTitles,
    string Keyword);
