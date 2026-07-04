using Microsoft.Playwright;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation.Weixin.Pages;

public sealed class WeixinSystemHighlightPublishPage
{
    private const string DetailActionManage = "管理";
    private const string DetailActionDetail = "详情";
    private const string PreviewPublishText = "去发表";
    private const string PublishUrlMarker = "/platform/post/create";
    private const string MenuSectionText = "收入与服务";
    private const string MenuItemText = "剧集管理";
    private const string GenerationSkipMessage = "系统高光视频仍在生成中，已跳过当前项目；待全部高光生成完成后再执行。";

    private static readonly string[] GeneratingMarkers = ["生成中", "正在生成", "生成队列", "大约需要24小时", "仍将继续为你生成"];
    private static readonly string[] SupportedVideoTypes = ["混剪", "解说", "切片"];
    private static readonly string[] PublishedMarkers = ["已发表", "已发布", "发表成功", "发布成功"];
    private static readonly string[] PendingMarkers = ["待发表", "待发布", "审核中", "发布中"];

    public sealed record SystemHighlightVideoCandidate(
        int SlotIndex,
        string DurationText,
        string TypeText,
        string TitleText,
        string ThumbnailUrl,
        bool IsPlatformPublished,
        bool IsPlatformPendingPublish,
        string PlatformStatusText);

    public sealed record SystemHighlightSelectionPlan(
        string DramaTitle,
        string DetailUrl,
        IReadOnlyList<SystemHighlightVideoCandidate> Candidates,
        IReadOnlyList<int> RequestedIndexes,
        IReadOnlyList<int> MissingRequestedIndexes,
        IReadOnlyList<int> PlatformPublishedIndexes,
        IReadOnlyList<SystemHighlightVideoCandidate> SelectedCandidates,
        bool GenerationInProgress);

    public async Task<SystemHighlightSelectionPlan> ResolvePublishTargetsAsync(
        IPage page,
        string baseUrl,
        WeixinNavigationOptions navigation,
        WeixinVideoPublishOptions options,
        string projectTitle,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dramaTitle = ResolveDramaTitle(options, projectTitle);
        if (string.IsNullOrWhiteSpace(dramaTitle))
        {
            throw new InvalidOperationException("系统生成高光视频来源无法确定剧名，请检查当前项目的新剧名。");
        }

        await OpenSeriesDetailPageAsync(page, baseUrl, navigation, dramaTitle, progress, cancellationToken);
        var detailUrl = page.Url;
        if (await IsGenerationInProgressAsync(page))
        {
            progress?.Report($"系统高光发布：{GenerationSkipMessage}");
            return new SystemHighlightSelectionPlan(dramaTitle, detailUrl, [], [], [], [], [], GenerationInProgress: true);
        }

        var candidates = await ReadCandidatesAsync(page);
        if (candidates.Count == 0)
        {
            progress?.Report("系统高光发布：当前剧集详情页没有系统高光卡片，本次跳过。");
            return new SystemHighlightSelectionPlan(dramaTitle, detailUrl, [], [], [], [], [], GenerationInProgress: false);
        }

        var requestedIndexes = ResolveRequestedIndexes(options, candidates);
        var targetIndexes = string.Equals(options.SystemHighlightPublishTargetMode, "type", StringComparison.OrdinalIgnoreCase)
            ? ResolveIndexesByType(options, candidates)
            : requestedIndexes;
        var availableIndexes = candidates.Select(item => item.SlotIndex).ToHashSet();
        var missingIndexes = targetIndexes.Where(index => !availableIndexes.Contains(index)).ToArray();
        var platformPublishedIndexes = candidates
            .Where(item => item.IsPlatformPublished)
            .Select(item => item.SlotIndex)
            .ToArray();
        var platformPublishedSet = platformPublishedIndexes.ToHashSet();
        var targetSet = targetIndexes.ToHashSet();
        var selected = candidates
            .Where(item => targetSet.Contains(item.SlotIndex) && !platformPublishedSet.Contains(item.SlotIndex))
            .ToArray();

        progress?.Report(
            "系统高光候选：" + string.Join(", ", candidates.Select(item =>
                $"{item.SlotIndex}({(string.IsNullOrWhiteSpace(item.TypeText) ? "-" : item.TypeText)} {(string.IsNullOrWhiteSpace(item.DurationText) ? "-" : item.DurationText)}" +
                (string.IsNullOrWhiteSpace(item.PlatformStatusText) ? ")" : $" {item.PlatformStatusText})"))));
        if (platformPublishedIndexes.Length > 0)
        {
            progress?.Report("系统高光发布：平台已发表，跳过编号 " + string.Join(",", platformPublishedIndexes));
        }

        return new SystemHighlightSelectionPlan(
            dramaTitle,
            detailUrl,
            candidates,
            requestedIndexes,
            missingIndexes,
            platformPublishedIndexes,
            selected,
            GenerationInProgress: false);
    }

    public async Task<IPage> OpenPublishPageFromDetailPageAsync(
        IPage detailPage,
        int slotIndex,
        WeixinVideoPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cards = detailPage.Locator("div.video-list div.video-item");
        if (await cards.CountAsync() < slotIndex)
        {
            throw new InvalidOperationException($"未找到第 {slotIndex} 个系统高光卡片。");
        }

        var target = cards.Nth(Math.Max(0, slotIndex - 1));
        await target.ScrollIntoViewIfNeededAsync();
        await target.ClickAsync();
        await WaitForPreviewAsync(detailPage, cancellationToken);

        var beforePages = detailPage.Context.Pages.ToHashSet();
        var publishButton = await FirstVisibleAsync(
            [
                detailPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = PreviewPublishText, Exact = false }).First,
                detailPage.GetByText(PreviewPublishText, new PageGetByTextOptions { Exact = false }).First
            ],
            15_000,
            cancellationToken);
        await publishButton.ClickAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in detailPage.Context.Pages)
            {
                if (!candidate.Url.Contains(PublishUrlMarker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await WaitBrieflyForLoadAsync(candidate, cancellationToken);
                await candidate.BringToFrontAsync();
                await WaitPublishPageReadyAsync(candidate, options, cancellationToken);
                progress?.Report($"系统高光发布：已打开第 {slotIndex} 个高光视频的发表页。");
                return candidate;
            }

            if (beforePages.Count != detailPage.Context.Pages.Count)
            {
                await Task.Delay(200, cancellationToken);
                continue;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("点击“去发表”后未进入素材发表页。");
    }

    public async Task<IPage> RestoreDetailPageAsync(
        IPage detailPage,
        IPage publishPage,
        string detailUrl,
        string dramaTitle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(detailPage, publishPage))
        {
            try
            {
                await publishPage.CloseAsync();
            }
            catch
            {
            }

            await detailPage.BringToFrontAsync();
        }

        if (!await IsDetailPageReadyAsync(detailPage, dramaTitle))
        {
            await detailPage.GotoAsync(detailUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await WaitBrieflyForLoadAsync(detailPage, cancellationToken);
            await WaitForDetailPageReadyAsync(detailPage, dramaTitle, cancellationToken);
        }

        return detailPage;
    }

    public async Task WaitForCoverPreviewReadyAsync(
        IPage page,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await FirstVisibleAsync(
            [
                page.GetByText("封面预览", new PageGetByTextOptions { Exact = false }).First,
                page.Locator(".cover-preview-wrap").First,
                page.Locator(".cover-preview-wrap img").First
            ],
            15_000,
            cancellationToken);

        var imageLocator = page.Locator(".cover-preview-wrap img.cover-img-vertical, .cover-preview-wrap .vertical-img-wrap img, .cover-preview-wrap .horizon-cover-wrap img, .cover-preview-wrap img");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HasReadyImageAsync(imageLocator))
            {
                progress?.Report("系统高光发布：封面预览已加载完成。");
                return;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("等待系统高光封面预览加载完成超时。");
    }

    public async Task TryRegenerateSystemHighlightsAsync(
        IPage detailPage,
        WeixinVideoPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.SystemHighlightRegenerateAfterPublish)
        {
            return;
        }

        try
        {
            var trigger = await FirstVisibleOrNullAsync(
                [
                    detailPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "重新生成", Exact = false }).First,
                    detailPage.GetByText("重新生成", new PageGetByTextOptions { Exact = false }).First,
                    detailPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "生成高光视频", Exact = false }).First,
                    detailPage.GetByText("生成高光视频", new PageGetByTextOptions { Exact = false }).First
                ],
                3_000,
                cancellationToken);
            if (trigger is null)
            {
                progress?.Report("系统高光发布：未找到重新生成入口，已跳过重新生成。");
                return;
            }

            await trigger.ClickAsync(new LocatorClickOptions { Force = true });
            await Task.Delay(500, cancellationToken);

            var confirm = await FirstVisibleOrNullAsync(
                [
                    detailPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "重新生成", Exact = false }).First,
                    detailPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "生成", Exact = false }).First
                ],
                2_000,
                cancellationToken);
            if (confirm is not null)
            {
                await confirm.ClickAsync(new LocatorClickOptions { Force = true });
            }

            progress?.Report(
                "系统高光发布：已触发高光重新生成。" +
                (options.SystemHighlightRegenerateVideoTypes.Count > 0
                    ? $" 类型：{string.Join("、", options.SystemHighlightRegenerateVideoTypes)}"
                    : string.Empty));
        }
        catch (Exception ex)
        {
            progress?.Report($"系统高光发布：重新生成未完成，已继续后续流程。{ex.Message}");
        }
    }

    public static string BuildVirtualVideoPath(string projectDir, int slotIndex)
    {
        var virtualDir = Path.Combine(projectDir, ".system-highlight-virtual");
        Directory.CreateDirectory(virtualDir);
        return Path.Combine(virtualDir, $"system-highlight-{slotIndex:D2}.mp4");
    }

    private static string ResolveDramaTitle(WeixinVideoPublishOptions options, string projectTitle)
    {
        return !string.IsNullOrWhiteSpace(options.SystemHighlightDramaTitle)
            ? options.SystemHighlightDramaTitle.Trim()
            : projectTitle.Trim();
    }

    private static IReadOnlyList<int> ResolveRequestedIndexes(
        WeixinVideoPublishOptions options,
        IReadOnlyList<SystemHighlightVideoCandidate> candidates)
    {
        if (string.Equals(options.EpisodeSelectionMode, "explicit", StringComparison.OrdinalIgnoreCase) &&
            options.EpisodeIndexes.Count > 0)
        {
            return options.EpisodeIndexes
                .Where(index => index > 0)
                .Distinct()
                .ToArray();
        }

        if (string.Equals(options.EpisodeSelectionMode, "all", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Range(1, Math.Max(1, candidates.Count)).ToArray();
        }

        var start = Math.Max(1, options.StartEpisodeIndex);
        var count = Math.Max(1, options.PublishCount);
        return Enumerable.Range(start, count).ToArray();
    }

    private static IReadOnlyList<int> ResolveIndexesByType(
        WeixinVideoPublishOptions options,
        IReadOnlyList<SystemHighlightVideoCandidate> candidates)
    {
        var requested = (options.SystemHighlightPublishVideoTypes.Count == 0
                ? SupportedVideoTypes
                : options.SystemHighlightPublishVideoTypes)
            .ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(item => requested.Contains(item.TypeText.Trim()))
            .Select(item => item.SlotIndex)
            .ToArray();
    }

    private static async Task OpenSeriesDetailPageAsync(
        IPage page,
        string baseUrl,
        WeixinNavigationOptions navigation,
        string dramaTitle,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await CanReuseNavigationPageAsync(page, baseUrl, cancellationToken))
        {
            progress?.Report("系统高光发布：进入视频号后台并打开剧集管理。");
            await page.GotoAsync(baseUrl.TrimEnd('/'), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await WaitBrieflyForLoadAsync(page, cancellationToken);
        }
        else
        {
            progress?.Report("系统高光发布：复用当前视频号后台页面。");
        }

        await NavigateToListPageAsync(page, navigation, cancellationToken);
        await WaitForSeriesListPageReadyAsync(page, cancellationToken);
        progress?.Report($"系统高光发布：搜索剧名 {dramaTitle}");
        await PerformSearchAsync(page, dramaTitle, cancellationToken);
        var row = await FindResultRowAsync(page, dramaTitle, cancellationToken);
        if (row is null)
        {
            await ClickSearchSuggestionAsync(page, dramaTitle, cancellationToken);
            await Task.Delay(1_000, cancellationToken);
            row = await FindResultRowAsync(page, dramaTitle, cancellationToken);
        }

        if (row is null)
        {
            throw new InvalidOperationException($"未在剧集管理列表中搜索到剧名：{dramaTitle}");
        }

        var action = await FindRowActionAsync(row, DetailActionManage, cancellationToken)
                     ?? await FindRowActionAsync(row, DetailActionDetail, cancellationToken);
        if (action is null)
        {
            throw new InvalidOperationException($"未找到剧名“{dramaTitle}”对应的管理入口。");
        }

        await row.ScrollIntoViewIfNeededAsync();
        await action.ClickAsync(new LocatorClickOptions { Force = true });
        await WaitForDetailPageReadyAsync(page, dramaTitle, cancellationToken);
        progress?.Report("系统高光发布：已进入剧集详情页。");
    }

    private static async Task<bool> CanReuseNavigationPageAsync(IPage page, string baseUrl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!page.Url.StartsWith(baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await FirstVisibleOrNullAsync(
            [
                page.GetByText(MenuSectionText, new PageGetByTextOptions { Exact = false }).First,
                page.GetByText(MenuItemText, new PageGetByTextOptions { Exact = false }).First
            ],
            1_200,
            cancellationToken) is not null;
    }

    private static async Task NavigateToListPageAsync(
        IPage page,
        WeixinNavigationOptions navigation,
        CancellationToken cancellationToken)
    {
        await ClickFirstVisibleTextAsync(page, string.IsNullOrWhiteSpace(navigation.Section) ? MenuSectionText : navigation.Section, 4_000, cancellationToken);
        await WaitBrieflyForLoadAsync(page, cancellationToken);
        await ClickFirstVisibleTextAsync(page, string.IsNullOrWhiteSpace(navigation.Item) ? MenuItemText : navigation.Item, 4_000, cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Url.Contains("/platform/playlet", StringComparison.OrdinalIgnoreCase))
            {
                await WaitBrieflyForLoadAsync(page, cancellationToken);
                return;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("点击“剧集管理”后未进入剧集管理列表页。");
    }

    private static async Task WaitForSeriesListPageReadyAsync(IPage page, CancellationToken cancellationToken)
    {
        await FirstVisibleAsync(
            [
                page.GetByText("剧集发表", new PageGetByTextOptions { Exact = false }).First,
                page.Locator("input[placeholder*='剧集'], input[placeholder*='名称'], input[placeholder*='搜索']").First,
                page.Locator("table tbody tr, [role='row']").First
            ],
            15_000,
            cancellationToken);
    }

    private static async Task PerformSearchAsync(IPage page, string dramaTitle, CancellationToken cancellationToken)
    {
        var input = await FirstVisibleAsync(
            [
                page.Locator("input[placeholder*='剧集']").First,
                page.Locator("input[placeholder*='名称']").First,
                page.Locator("input[placeholder*='搜索']").First,
                page.Locator(".weui-desktop-search-bar input").First,
                page.Locator("input[type='search']").First,
                page.Locator("input[type='text']").First
            ],
            8_000,
            cancellationToken);
        await input.ClickAsync();
        await input.FillAsync(string.Empty);
        await input.FillAsync(dramaTitle);
        await Task.Delay(1_000, cancellationToken);
    }

    private static async Task<ILocator?> FindResultRowAsync(IPage page, string dramaTitle, CancellationToken cancellationToken)
    {
        var rows = page.Locator("tr.ant-table-row, table tbody tr, .weui-desktop-table tbody tr, [role='row']");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await rows.CountAsync();
            for (var index = 0; index < Math.Min(count, 50); index++)
            {
                var row = rows.Nth(index);
                if (!await IsVisibleAsync(row))
                {
                    continue;
                }

                var text = await SafeInnerTextAsync(row);
                if (text.Contains(dramaTitle, StringComparison.Ordinal))
                {
                    return row;
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return null;
    }

    private static async Task ClickSearchSuggestionAsync(IPage page, string dramaTitle, CancellationToken cancellationToken)
    {
        var suggestion = await FirstVisibleOrNullAsync(
            [
                page.Locator($"span.match:has-text(\"{Escape(dramaTitle)}\")").First,
                page.Locator($"[role='option']:has-text(\"{Escape(dramaTitle)}\")").First,
                page.Locator($".weui-desktop-popover:visible *:has-text(\"{Escape(dramaTitle)}\")").First,
                page.GetByText(dramaTitle, new PageGetByTextOptions { Exact = false }).First
            ],
            5_000,
            cancellationToken);
        if (suggestion is not null)
        {
            await suggestion.ClickAsync(new LocatorClickOptions { Force = true });
        }
    }

    private static async Task<ILocator?> FindRowActionAsync(ILocator row, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await FirstVisibleOrNullAsync(
            [
                row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameString = text, Exact = false }).First,
                row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { NameString = text, Exact = false }).First,
                row.Locator($"a:has-text(\"{Escape(text)}\"), button:has-text(\"{Escape(text)}\"), .playlet-action-item:has-text(\"{Escape(text)}\")").First,
                row.GetByText(text, new LocatorGetByTextOptions { Exact = false }).First
            ],
            1_500,
            cancellationToken);
    }

    private static async Task WaitForDetailPageReadyAsync(IPage page, string dramaTitle, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsDetailPageReadyAsync(page, dramaTitle))
            {
                return;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("等待剧集详情页就绪超时。");
    }

    private static async Task<bool> IsDetailPageReadyAsync(IPage page, string dramaTitle)
    {
        if (!page.Url.Contains("/platform/playlet/playlet-detail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = await SafeInnerTextAsync(page.Locator("body").First);
        if (!text.Contains(dramaTitle, StringComparison.Ordinal) || !text.Contains("生成高光视频", StringComparison.Ordinal))
        {
            return false;
        }

        return await page.Locator("div.video-list div.video-item").CountAsync() > 0 ||
               text.Contains("生成高光视频", StringComparison.Ordinal);
    }

    private static async Task<bool> IsGenerationInProgressAsync(IPage page)
    {
        var bodyText = await SafeInnerTextAsync(page.Locator("body").First);
        if (!bodyText.Contains("生成高光视频", StringComparison.Ordinal))
        {
            return false;
        }

        if (GeneratingMarkers.Any(marker => bodyText.Contains(marker, StringComparison.Ordinal)))
        {
            return true;
        }

        return await page.Locator("div.video-list div.video-item").Filter(new LocatorFilterOptions { HasTextString = "生成中" }).CountAsync() > 0;
    }

    private static async Task<IReadOnlyList<SystemHighlightVideoCandidate>> ReadCandidatesAsync(IPage page)
    {
        var candidates = new List<SystemHighlightVideoCandidate>();
        var cards = page.Locator("div.video-list div.video-item");
        var count = await cards.CountAsync();
        for (var index = 0; index < Math.Min(count, 30); index++)
        {
            var card = cards.Nth(index);
            if (!await IsVisibleAsync(card))
            {
                continue;
            }

            var typeText = (await SafeInnerTextAsync(card.Locator("div.video-type").First)).Trim();
            var durationText = (await SafeInnerTextAsync(card.Locator("div.video-time").First)).Trim();
            var thumbnailUrl = await SafeAttributeAsync(card.Locator("img").First, "src");
            var cardText = await SafeInnerTextAsync(card);
            var published = PublishedMarkers.Any(marker => cardText.Contains(marker, StringComparison.Ordinal));
            var pending = PendingMarkers.Any(marker => cardText.Contains(marker, StringComparison.Ordinal));
            var statusText = PublishedMarkers.Concat(PendingMarkers)
                .FirstOrDefault(marker => cardText.Contains(marker, StringComparison.Ordinal)) ?? string.Empty;
            candidates.Add(new SystemHighlightVideoCandidate(
                SlotIndex: candidates.Count + 1,
                DurationText: durationText,
                TypeText: typeText,
                TitleText: string.Join(" ", new[] { typeText, durationText }.Where(item => !string.IsNullOrWhiteSpace(item))),
                ThumbnailUrl: thumbnailUrl,
                IsPlatformPublished: published,
                IsPlatformPendingPublish: pending,
                PlatformStatusText: statusText));
        }

        return candidates;
    }

    private static async Task WaitForPreviewAsync(IPage page, CancellationToken cancellationToken)
    {
        await FirstVisibleAsync(
            [
                page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = PreviewPublishText, Exact = false }).First,
                page.GetByText(PreviewPublishText, new PageGetByTextOptions { Exact = false }).First
            ],
            12_000,
            cancellationToken);
    }

    private static async Task WaitPublishPageReadyAsync(IPage page, WeixinVideoPublishOptions options, CancellationToken cancellationToken)
    {
        await FirstVisibleAsync(
            [
                page.GetByText(options.ReadyText, new PageGetByTextOptions { Exact = false }).First,
                page.GetByText("发表视频", new PageGetByTextOptions { Exact = false }).First,
                page.Locator("textarea[placeholder*='添加描述'], textarea, [contenteditable='true']").First
            ],
            15_000,
            cancellationToken);
    }

    private static async Task ClickFirstVisibleTextAsync(IPage page, string text, int timeoutMs, CancellationToken cancellationToken)
    {
        var target = await FirstVisibleAsync(
            [
                page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = text, Exact = false }).First,
                page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameString = text, Exact = false }).First,
                page.GetByText(text, new PageGetByTextOptions { Exact = false }).First
            ],
            timeoutMs,
            cancellationToken);
        await target.ClickAsync(new LocatorClickOptions { Force = true });
    }

    private static async Task<ILocator> FirstVisibleAsync(
        IReadOnlyList<ILocator> candidates,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var locator = await FirstVisibleOrNullAsync(candidates, timeoutMs, cancellationToken);
        return locator ?? throw new TimeoutException("未找到目标页面元素。");
    }

    private static async Task<ILocator?> FirstVisibleOrNullAsync(
        IReadOnlyList<ILocator> candidates,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1, timeoutMs));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in candidates)
            {
                try
                {
                    var count = await candidate.CountAsync();
                    for (var index = 0; index < Math.Min(count, 5); index++)
                    {
                        var item = candidate.Nth(index);
                        if (await IsVisibleAsync(item))
                        {
                            return item;
                        }
                    }
                }
                catch
                {
                }
            }

            await Task.Delay(150, cancellationToken);
        }

        return null;
    }

    private static async Task<bool> HasReadyImageAsync(ILocator locator)
    {
        try
        {
            return await locator.EvaluateAsync<bool>(
                """
                elements => {
                  const list = Array.isArray(elements) ? elements : [elements];
                  return list.some(img => {
                    if (!img || !img.isConnected) return false;
                    const src = String(img.currentSrc || img.src || img.getAttribute?.("src") || "").trim();
                    const naturalReady = !!img.complete && Number(img.naturalWidth || 0) > 0 && Number(img.naturalHeight || 0) > 0;
                    const renderedReady = Number(img.clientWidth || 0) > 0 && Number(img.clientHeight || 0) > 0;
                    return !!src && (naturalReady || renderedReady);
                  });
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsVisibleAsync(ILocator locator)
    {
        try
        {
            return await locator.IsVisibleAsync();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> SafeInnerTextAsync(ILocator locator)
    {
        try
        {
            return await locator.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1_000 });
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> SafeAttributeAsync(ILocator locator, string name)
    {
        try
        {
            return await locator.GetAttributeAsync(name) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task WaitBrieflyForLoadAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 5_000 });
        }
        catch
        {
        }

        await Task.Delay(500, cancellationToken);
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
