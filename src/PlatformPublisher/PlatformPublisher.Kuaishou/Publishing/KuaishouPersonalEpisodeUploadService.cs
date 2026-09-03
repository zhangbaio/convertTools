using Microsoft.Playwright;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalEpisodeUploadService
{
    public async Task UploadAsync(
        IPage page,
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        IProgress<string>? progress,
        bool resumeAtVideoPage,
        bool videosAlreadyUploaded,
        Func<string, Task>? stageChanged,
        CancellationToken cancellationToken)
    {
        if (!resumeAtVideoPage)
        {
            await ConfigureEpisodeInfoAsync(page, data, cancellationToken);
            if (stageChanged is not null) await stageChanged("episode_info_completed");
            if (!await ClickVisibleAsync(page, ["下一步"], 10_000))
                throw new InvalidOperationException("快手个人版第一页未找到“下一步”按钮。");
        }
        await WaitForUploadPageAsync(page, cancellationToken);
        if (stageChanged is not null) await stageChanged("first_page_completed");
        progress?.Report("快手分账个人版：已进入剧集视频上传步骤。 ");

        if (!videosAlreadyUploaded)
        {
            var input = await FindVideoInputAsync(page);
            await input.SetInputFilesAsync(data.VideoPaths.ToArray());
            await ConfirmUploadPositionAsync(page, cancellationToken);
            if (!await ClickVisibleAsync(page, ["开始上传"], 10_000))
                throw new InvalidOperationException("未找到批量上传抽屉中的“开始上传”按钮。");
            await WaitForUploadsAsync(page, data.VideoPaths.Count, config.UploadTimeoutMinutes, progress, cancellationToken);
            if (stageChanged is not null) await stageChanged("videos_uploaded");
        }
        else
        {
            progress?.Report("快手分账个人版：状态记录显示视频已上传，跳过重复上传。 ");
        }

        if (string.Equals(config.FinalAction, "submit_review", StringComparison.OrdinalIgnoreCase))
        {
            if (config.SubmitPreCheckWaitSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(config.SubmitPreCheckWaitSeconds, 0, 120)), cancellationToken);
            if (!await ClickVisibleEnabledButtonAsync(page, "提交审核", 30_000, cancellationToken))
                throw new InvalidOperationException("剧集上传完成，但未找到“提交审核”按钮。");
            await ConfirmDialogAsync(page);
            await WaitForReviewSubmissionAsync(page, data.Title, config, cancellationToken);
            if (stageChanged is not null) await stageChanged("review_submitted");
            progress?.Report("快手分账个人版：已提交审核。 ");
        }
        else
        {
            progress?.Report("快手分账个人版：视频上传完成，按配置保持在提交审核前。 ");
        }
    }

    private static async Task ConfigureEpisodeInfoAsync(IPage page, KuaishouPersonalProjectData data, CancellationToken cancellationToken)
    {
        if (!await ClickVisibleAsync(page, ["添加单集信息"], 8_000))
            throw new InvalidOperationException("未找到“添加单集信息”按钮。");
        var dialog = await VisibleDialogAsync(page, 8_000);
        var input = dialog.Locator("input").First;
        if (await input.CountAsync() > 0) await input.FillAsync(Math.Max(0, data.VideoPaths.Count - 1).ToString());
        await ClickDialogConfirmAsync(dialog);

        if (!await ClickVisibleAsync(page, ["批量设置"], 8_000))
            throw new InvalidOperationException("未找到单集信息“批量设置”按钮。");
        dialog = await VisibleDialogAsync(page, 8_000);
        var inputs = dialog.Locator("input:not([type=file])");
        var count = await inputs.CountAsync();
        if (count >= 2)
        {
            await inputs.Nth(0).FillAsync("1");
            await inputs.Nth(1).FillAsync(data.VideoPaths.Count.ToString());
        }
        var titleInput = dialog.Locator("input[placeholder*=标题], input[placeholder*=单集]").Last;
        if (await titleInput.CountAsync() > 0) await titleInput.FillAsync(data.Title);
        if (string.IsNullOrWhiteSpace(data.VerticalCoverPath) || !File.Exists(data.VerticalCoverPath))
            throw new FileNotFoundException("缺少快手个人版竖屏单集封面。", data.VerticalCoverPath);
        var fileInput = dialog.Locator("input[type=file]").Last;
        if (await fileInput.CountAsync() == 0) throw new InvalidOperationException("批量设置弹窗未找到单集封面上传入口。");
        await fileInput.SetInputFilesAsync(data.VerticalCoverPath);
        await HandleCropDialogAsync(page, 18, cancellationToken);
        await ClickDialogConfirmAsync(dialog);
    }

    private static async Task<ILocator> FindVideoInputAsync(IPage page)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var selector in new[]
                     {
                         "input[type=file][accept*=video]",
                         "input[type=file][accept*=mp4]",
                         "input[type=file]",
                     })
            {
                var inputs = page.Locator(selector);
                var count = await inputs.CountAsync();
                for (var index = count - 1; index >= 0; index--)
                {
                    var input = inputs.Nth(index);
                    if (await input.IsEnabledAsync()) return input;
                }
            }
            await page.WaitForTimeoutAsync(300);
        }
        throw new InvalidOperationException("视频上传步骤未找到剧集视频文件入口。");
    }

    private static async Task ConfirmUploadPositionAsync(IPage page, CancellationToken cancellationToken)
    {
        var dialog = page.Locator("[role=dialog]:visible, .ks-dialog:visible, .ant-modal:visible").Last;
        try { await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 8_000 }); }
        catch { return; }
        var input = dialog.Locator("input").First;
        if (await input.CountAsync() > 0) await input.FillAsync("1");
        cancellationToken.ThrowIfCancellationRequested();
        await ClickDialogConfirmAsync(dialog);
    }

    private static async Task WaitForUploadsAsync(
        IPage page,
        int expected,
        int timeoutMinutes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(timeoutMinutes, 5, 240));
        var last = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = (await page.Locator("body").InnerTextAsync()).Replace("为避免上传失败", string.Empty, StringComparison.Ordinal);
            if (body.Contains("全部上传完成", StringComparison.Ordinal) ||
                body.Contains($"{expected}集成功，0集失败", StringComparison.Ordinal) ||
                body.Contains("提交审核", StringComparison.Ordinal) && !body.Contains("上传中", StringComparison.Ordinal))
            {
                progress?.Report($"快手分账个人版：{expected} 集视频上传完成。 ");
                await ClickVisibleAsync(page, ["确定"], 2_000);
                return;
            }
            if (body.Contains("上传失败", StringComparison.Ordinal) || body.Contains("校验失败", StringComparison.Ordinal))
                throw new InvalidOperationException("快手个人版视频上传出现失败条目，请检查上传抽屉。");
            var progressText = ExtractProgress(body);
            if (!string.IsNullOrWhiteSpace(progressText) && progressText != last)
            {
                last = progressText;
                progress?.Report($"快手分账个人版上传进度：{progressText}");
            }
            await Task.Delay(1000, cancellationToken);
        }
        throw new TimeoutException($"等待 {expected} 集视频上传完成超时。");
    }

    private static string ExtractProgress(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"上传完成\s*\d+\s*集成功\s*，?\s*\d+\s*集失败|\d+%");
        return match.Success ? match.Value : string.Empty;
    }

    private static async Task WaitForUploadPageAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Url.Contains("step=1", StringComparison.OrdinalIgnoreCase) ||
                await page.GetByText("视频上传", new PageGetByTextOptions { Exact = false }).CountAsync() > 0)
                return;
            await page.WaitForTimeoutAsync(300);
        }
        throw new TimeoutException("点击下一步后未进入视频上传步骤。");
    }

    private static async Task<ILocator> VisibleDialogAsync(IPage page, float timeout)
    {
        var dialog = page.Locator("[role=dialog]:visible, .ks-dialog:visible, .ant-modal:visible").Last;
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeout });
        return dialog;
    }

    private static async Task ClickDialogConfirmAsync(ILocator dialog)
    {
        foreach (var text in new[] { "确认", "确定" })
        {
            var button = dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = text, Exact = false }).Last;
            if (await button.CountAsync() == 0) continue;
            await button.ClickAsync();
            return;
        }
        throw new InvalidOperationException("弹窗未找到确认按钮。");
    }

    private static async Task ConfirmDialogAsync(IPage page)
    {
        try { await ClickDialogConfirmAsync(await VisibleDialogAsync(page, 5_000)); }
        catch (TimeoutException) { /* 部分提交动作没有二次确认。 */ }
    }

    private static async Task<bool> ClickVisibleEnabledButtonAsync(
        IPage page,
        string text,
        int timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buttons = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = text, Exact = false });
            var count = Math.Min(await buttons.CountAsync(), 20);
            for (var index = 0; index < count; index++)
            {
                var button = buttons.Nth(index);
                if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync()) continue;
                await button.ClickAsync();
                return true;
            }
            await page.WaitForTimeoutAsync(300);
        }
        return false;
    }

    private static async Task WaitForReviewSubmissionAsync(
        IPage page,
        string title,
        KuaishouPersonalConfig config,
        CancellationToken cancellationToken)
    {
        var intervalSeconds = Math.Clamp(config.SubmitReadyCheckIntervalSeconds, 1, 60);
        var maxChecks = Math.Clamp(config.SubmitReadyCheckMax, 1, 600);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(intervalSeconds * maxChecks);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await ReadAllBodyTextAsync(page);
            if (new[] { "提交成功", "发布成功", "已提交审核" }.Any(body.Contains) ||
                !page.Url.Contains("content-management/edit", StringComparison.OrdinalIgnoreCase) &&
                await HasReviewingRowAsync(page, title))
                return;
            if (new[] { "提交失败", "发布失败", "审核提交失败" }.Any(body.Contains))
                throw new InvalidOperationException("快手个人版提交审核失败，平台页面返回失败提示。");
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
        }
        throw new TimeoutException("点击提交审核后，未检测到“提交成功/审核中”等平台最终状态；任务不会标记为完成。");
    }

    private static async Task<bool> HasReviewingRowAsync(IPage page, string title)
    {
        if (await RowsContainReviewingAsync(
                page.Locator("tr, [class*=table-row], [class*=list-item]"), title)) return true;
        foreach (var frame in page.Frames.Where(frame => frame != page.MainFrame))
            if (await RowsContainReviewingAsync(
                    frame.Locator("tr, [class*=table-row], [class*=list-item]"), title)) return true;
        return false;
    }

    private static async Task<bool> RowsContainReviewingAsync(ILocator candidates, string title)
    {
        var rows = candidates
            .Filter(new LocatorFilterOptions { HasTextString = title });
        var count = Math.Min(await rows.CountAsync(), 20);
        for (var index = 0; index < count; index++)
        {
            var row = rows.Nth(index);
            if (await row.IsVisibleAsync() &&
                (await row.InnerTextAsync()).Contains("审核中", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static async Task<string> ReadAllBodyTextAsync(IPage page)
    {
        var parts = new List<string> { await page.Locator("body").InnerTextAsync() };
        foreach (var frame in page.Frames.Where(frame => frame != page.MainFrame))
        {
            try { parts.Add(await frame.Locator("body").InnerTextAsync()); }
            catch (PlaywrightException) { /* 子框架导航中的瞬时读取失败，下轮重试。 */ }
        }
        return Normalize(string.Join(' ', parts));
    }

    private static async Task HandleCropDialogAsync(IPage page, int shrinkClicks, CancellationToken cancellationToken)
    {
        ILocator dialog;
        try { dialog = await VisibleDialogAsync(page, 5_000); }
        catch { return; }
        if (!Normalize(await dialog.InnerTextAsync()).Contains("图片裁剪", StringComparison.Ordinal)) return;
        var minus = dialog.Locator("button[aria-label*=缩小], button[title*=缩小], button:has-text('-')").First;
        for (var index = 0; index < shrinkClicks && await minus.CountAsync() > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await minus.ClickAsync();
        }
        await ClickDialogConfirmAsync(dialog);
    }

    private static async Task<bool> ClickVisibleAsync(IPage page, IReadOnlyList<string> texts, float timeout)
    {
        foreach (var text in texts)
        {
            var items = page.GetByText(text, new PageGetByTextOptions { Exact = false });
            var count = Math.Min(await items.CountAsync(), 20);
            for (var index = 0; index < count; index++)
            {
                var item = items.Nth(index);
                if (!await item.IsVisibleAsync()) continue;
                try { await item.ClickAsync(new LocatorClickOptions { Timeout = timeout }); return true; }
                catch { }
            }
        }
        return false;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
