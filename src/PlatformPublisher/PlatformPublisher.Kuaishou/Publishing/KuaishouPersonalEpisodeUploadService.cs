using Microsoft.Playwright;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalEpisodeUploadService
{
    public enum UploadAction
    {
        UploadAll,
        FillMissing,
        Skip,
    }

    public readonly record struct EpisodeVideoSlot(int Number, bool Uploaded);

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
            if (stageChanged is not null) await stageChanged("episode_info_completed");
            if (!await ClickVisibleAsync(page, ["下一步"], 10_000))
                throw new InvalidOperationException("快手个人版第一页未找到“下一步”按钮。");
            progress?.Report("快手分账个人版：已点击下一步，准备确认进入视频上传。 ");
        }
        await WaitForUploadPageAsync(page, progress, cancellationToken);
        if (stageChanged is not null) await stageChanged("first_page_completed");
        progress?.Report("快手分账个人版：已进入剧集视频上传步骤。 ");

        if (!videosAlreadyUploaded)
        {
            await EnsureEpisodeVideosUploadedAsync(page, data, config, progress, cancellationToken);
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
            if (!await ClickVisibleEnabledButtonAsync(page, "提交审核", 120_000, cancellationToken))
                throw new InvalidOperationException("剧集上传完成，但未找到可点击的“提交审核”按钮。");
            progress?.Report("快手分账个人版：已点击提交审核，等待确认弹窗和平台结果。");
            await WaitForReviewSubmissionAsync(page, data.Title, progress, cancellationToken);
            if (stageChanged is not null) await stageChanged("review_submitted");
            progress?.Report("快手分账个人版：已提交审核。 ");
        }
        else
        {
            progress?.Report("快手分账个人版：视频上传完成，按配置保持在提交审核前。 ");
        }
    }

    public async Task ConfigureEpisodeInfoAsync(
        IPage page,
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        CancellationToken cancellationToken)
    {
        var episodeCount = data.VideoPaths.Count;
        if (episodeCount < 1) throw new InvalidOperationException("没有可设置的剧集视频，无法确定开始集和结束集。");
        // 页面默认已有第 1 集。添加单集信息填“总集数 - 1”，批量设置再选第 1 集到第总集数集。
        var additional = Math.Max(0, episodeCount - 1);
        if (additional > 0)
        {
            if (!await ClickVisibleAsync(page, ["添加单集信息"], 8_000))
                throw new InvalidOperationException("未找到“添加单集信息”按钮。");
            var addDialog = await VisibleDialogAsync(page, 8_000);
            var input = addDialog.Locator("input:not([type='hidden']):not([type='file']):not([placeholder*='输入查询'])").First;
            if (await input.CountAsync() == 0) input = addDialog.Locator("input:not([type='hidden']):not([type='file'])").First;
            if (await input.CountAsync() == 0) throw new InvalidOperationException("添加单集信息弹窗未找到集数输入框。");
            await input.FillAsync(additional.ToString());
            var filled = (await input.InputValueAsync()).Trim();
            if (filled != additional.ToString())
                throw new InvalidOperationException($"添加单集信息应填写总集数减 1（{additional}），实际为“{filled}”。");
            await ClickDialogConfirmAsync(addDialog);
            await page.WaitForTimeoutAsync(1000);
        }

        if (string.IsNullOrWhiteSpace(data.VerticalCoverPath) || !File.Exists(data.VerticalCoverPath))
            throw new FileNotFoundException("缺少快手竖屏单集封面。", data.VerticalCoverPath);
        var enterprise = config.StoragePlatform == PlatformPublisher.Common.Models.PublishPlatform.KuaishouEnterpriseRevenue;
        string? gap = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ClickVisibleAsync(page, ["批量设置"], 8_000))
                throw new InvalidOperationException("未找到单集信息“批量设置”按钮。");
            var dialog = await MarkOverlayAsync(page, "选择集数", 8_000);
            var queries = await VisibleQueryInputsAsync(dialog);
            if (queries.Count < 2) throw new InvalidOperationException("批量设置弹窗未找到集数选择器。");
            await SelectQueryOptionAsync(page, queries[0], "第1集");
            await SelectQueryOptionAsync(page, queries[1], $"第{episodeCount}集");
            await page.WaitForTimeoutAsync(400);
            await UploadBatchCoverAsync(dialog, data.VerticalCoverPath);
            // 企业版第二次重试不再缩小，避免把已经裁好的封面再次裁小。
            var shrinkClicks = !enterprise || attempt == 0 ? 18 : 0;
            await HandleCropDialogAsync(page, shrinkClicks, cancellationToken);
            // 裁剪弹窗关闭会重绘批量设置，先填的标题会被清空，封面数据键也要等预览出现后再确认。
            await page.WaitForTimeoutAsync(400);
            dialog = await MarkOverlayAsync(page, "选择集数", 8_000);
            await FillBatchEpisodeTitleAsync(dialog, data.Title);
            if (enterprise)
                await ConfigureEnterpriseEpisodePriceAsync(dialog, config);
            var previewReady = await WaitForBatchCoverPreviewAsync(page, cancellationToken);
            if (!previewReady)
            {
                try { await page.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { /* 弹窗已经关闭。 */ }
                if (attempt == 0) continue;
                throw new InvalidOperationException("批量设置弹窗中的单集封面预览未就绪。");
            }

            await page.WaitForTimeoutAsync(1500);
            await ClickDialogConfirmAsync(dialog);
            if (!await WaitForBatchDialogClosedAsync(page))
                throw new InvalidOperationException("批量设置弹窗确认后仍未关闭。");
            var status = await ReadEpisodeInfoStatusAsync(page);
            if (status.Episodes > 0 && status.MissingTitles == 0 && status.MissingCovers == 0)
            {
                gap = null;
                break;
            }

            gap = status.Episodes <= 0
                ? "页面上没有读到单集卡片"
                : $"当前可见 {status.Episodes} 集，缺标题 {status.MissingTitles} 集，缺封面 {status.MissingCovers} 集";
            if (attempt == 0) continue;
            throw new InvalidOperationException($"批量设置未应用到单集信息：{gap}。");
        }

        if (enterprise)
            await ConfirmEnterpriseFreeEpisodeWarningAsync(page);
    }

    private static async Task UploadBatchCoverAsync(ILocator dialog, string coverPath)
    {
        var markedCover = await dialog.EvaluateAsync<bool>(
            """
            (el) => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const blocks = Array.from(el.querySelectorAll('div, section, label'))
                .filter(element => compact(element.innerText).startsWith('单集封面') || compact(element.innerText).includes('上传图片'))
                .sort((left, right) => {
                  const leftBox = left.getBoundingClientRect();
                  const rightBox = right.getBoundingClientRect();
                  return leftBox.width * leftBox.height - rightBox.width * rightBox.height;
                });
              for (const block of blocks) {
                let node = block;
                for (let depth = 0; node && depth < 5; depth += 1, node = node.parentElement) {
                  const input = node.querySelector('input[type=file]');
                  if (!input) continue;
                  input.setAttribute('data-kuaishou-batch-cover', '1');
                  return true;
                }
              }
              const fallback = el.querySelector('input[type=file]');
              if (!fallback) return false;
              fallback.setAttribute('data-kuaishou-batch-cover', '1');
              return true;
            }
            """);
        if (!markedCover) throw new InvalidOperationException("批量设置弹窗未找到单集封面上传入口。");
        await dialog.Page.Locator("input[type=file][data-kuaishou-batch-cover='1']").First.SetInputFilesAsync(coverPath);
    }

    private static async Task FillBatchEpisodeTitleAsync(ILocator dialog, string title)
    {
        var titleInput = dialog.Locator("input[placeholder*='单集标题'], input[placeholder*='标题']").Last;
        if (await titleInput.CountAsync() == 0)
            throw new InvalidOperationException("批量设置弹窗未找到单集标题输入框。");
        await titleInput.FillAsync(title);
        var actual = (await titleInput.InputValueAsync()).Trim();
        if (string.IsNullOrWhiteSpace(actual))
            throw new InvalidOperationException("批量设置的单集标题填写后仍为空。");
    }

    private static async Task<bool> WaitForBatchCoverPreviewAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = await page.EvaluateAsync<bool>(
                """
                () => {
                  const visible = element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
                  };
                  const dialogs = Array.from(document.querySelectorAll('.ks-dialog,[role="dialog"],.ant-modal')).filter(visible);
                  const images = dialogs.flatMap(dialog => Array.from(dialog.querySelectorAll(
                    '.batch-setting-dialog-form .cover-img img, .batch-setting-dialog-form img[alt="cropper-img"], .cover-img img, img[alt="cropper-img"]'
                  )));
                  return images.some(img => visible(img) && img.complete && img.naturalWidth > 0);
                }
                """);
            if (ready) return true;
            await page.WaitForTimeoutAsync(250);
        }

        return false;
    }

    private static async Task<bool> WaitForBatchDialogClosedAsync(IPage page)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await page.WaitForTimeoutAsync(300);
            var open = await page.EvaluateAsync<bool>(
                """
                () => Array.from(document.querySelectorAll('div, span, label'))
                  .some(element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden'
                      && (element.innerText || '').replace(/\s+/g, '') === '选择集数';
                  });
                """);
            if (!open) return true;
        }

        return false;
    }

    private readonly record struct EpisodeInfoStatus(int Episodes, int MissingTitles, int MissingCovers);

    private static async Task<EpisodeInfoStatus> ReadEpisodeInfoStatusAsync(IPage page)
    {
        var tabs = page.Locator(".set-list li");
        var tabCount = 0;
        try { tabCount = await tabs.CountAsync(); }
        catch (PlaywrightException) { tabCount = 0; }
        if (tabCount <= 1)
            return await ReadVisibleEpisodeInfoAsync(page);

        var episodes = 0;
        var missingTitles = 0;
        var missingCovers = 0;
        for (var index = 0; index < tabCount; index++)
        {
            var tab = tabs.Nth(index);
            try
            {
                if (!await tab.IsVisibleAsync()) continue;
                await tab.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                await page.WaitForTimeoutAsync(250);
            }
            catch (PlaywrightException)
            {
                continue;
            }

            var status = await ReadVisibleEpisodeInfoAsync(page);
            episodes += status.Episodes;
            missingTitles += status.MissingTitles;
            missingCovers += status.MissingCovers;
        }

        try
        {
            await tabs.First.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
            await page.WaitForTimeoutAsync(250);
        }
        catch (PlaywrightException)
        {
            // 核对结束后回到第一页失败不影响提交流程。
        }

        return new EpisodeInfoStatus(episodes, missingTitles, missingCovers);
    }

    private static async Task<EpisodeInfoStatus> ReadVisibleEpisodeInfoAsync(IPage page)
    {
        var status = await page.EvaluateAsync<int[]>(
            """
            () => {
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
              };
              const episodes = Array.from(document.querySelectorAll('.wrapper'))
                .filter(item => item.querySelector('.episode-header') && visible(item));
              let missingTitles = 0;
              let missingCovers = 0;
              for (const episode of episodes) {
                const title = episode.querySelector('input[placeholder*="单集标题"], input[placeholder="请输入"]');
                if (!String(title?.value || '').trim()) missingTitles += 1;
                const cover = episode.querySelector('.cover-img img, img[class*="cover"], img[class*="preview"]');
                const src = String(cover?.getAttribute('src') || '');
                if (!cover || !src || src.startsWith('data:image/svg')) missingCovers += 1;
              }
              return [episodes.length, missingTitles, missingCovers];
            }
            """);
        return status is { Length: 3 }
            ? new EpisodeInfoStatus(status[0], status[1], status[2])
            : new EpisodeInfoStatus(0, 0, 0);
    }

    private static async Task<IReadOnlyList<ILocator>> VisibleQueryInputsAsync(ILocator dialog)
    {
        var queries = dialog.Locator("input[placeholder*='输入查询']");
        var count = await queries.CountAsync();
        var visible = new List<ILocator>();
        for (var index = 0; index < count; index++)
        {
            var candidate = queries.Nth(index);
            if (!await candidate.IsVisibleAsync()) continue;
            var box = await candidate.BoundingBoxAsync();
            if (box is null || box.Y < 0) continue;
            visible.Add(candidate);
        }

        return visible;
    }

    private static async Task SelectQueryOptionAsync(IPage page, ILocator input, string option)
    {
        try { await input.ScrollIntoViewIfNeededAsync(); } catch (PlaywrightException) { /* 输入框已经在视野内。 */ }
        try { await input.ClickAsync(new LocatorClickOptions { Timeout = 3_000 }); }
        catch (PlaywrightException) { await input.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3_000 }); }
        await page.WaitForTimeoutAsync(200);
        try { await input.FillAsync(option); } catch (PlaywrightException) { /* 只读查询框改为直接点选项。 */ }
        await page.WaitForTimeoutAsync(200);
        var popperId = await input.EvaluateAsync<string>(
            """
            element => {
              const nodes = [element, element.closest('.select-trigger'), element.closest('.ks-select,.ant-select,.el-select')].filter(Boolean);
              for (const node of nodes) {
                const id = node.getAttribute('aria-describedby') || node.getAttribute('aria-controls');
                if (id) return id;
              }
              return '';
            }
            """);
        var point = await page.EvaluateAsync<double[]?>(
            """
            ({ optionText, popperId }) => {
              const normalize = value => String(value || '').replace(/\s+/g, ' ').trim();
              const compact = value => normalize(value).replace(/\s+/g, '');
              const target = normalize(optionText);
              const compactTarget = compact(optionText);
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && rect.bottom >= 0 && rect.right >= 0
                  && rect.top <= window.innerHeight && rect.left <= window.innerWidth
                  && style.display !== 'none' && style.visibility !== 'hidden' && style.pointerEvents !== 'none';
              };
              const episodeNumber = value => {
                const text = compact(value);
                const labeled = text.match(/^第0*(\d+)集$/);
                if (labeled) return Number(labeled[1]);
                return /^\d+$/.test(text) ? Number(text) : null;
              };
              const targetEpisode = episodeNumber(optionText);
              const matches = element => {
                const text = normalize(element.innerText || element.textContent || '');
                if (!text) return false;
                if (text === target || compact(text) === compactTarget) return true;
                return targetEpisode !== null && episodeNumber(text) === targetEpisode;
              };
              const popups = Array.from(document.querySelectorAll('.ks-select__popper,.ks-popper,.ant-select-dropdown,.el-select-dropdown,[role="listbox"]')).filter(popup => {
                if (!visible(popup)) return false;
                const text = compact(popup.innerText || '');
                return !text.includes('承诺函') && !text.includes('现场拍摄图') && !text.includes('版权证明');
              });
              const preferred = popups.filter(popup => popperId && (popup.id === popperId || popup.getAttribute('aria-describedby') === popperId));
              for (const popup of preferred.concat(popups)) {
                const options = Array.from(popup.querySelectorAll('li.ks-select-dropdown__item,.ks-select-dropdown__item,[role="option"],li'))
                  .filter(element => visible(element) && matches(element) && element.getBoundingClientRect().height < 56);
                const exact = options.filter(element => {
                  const text = normalize(element.innerText || element.textContent || '');
                  return text === target || compact(text) === compactTarget;
                });
                const option = exact[0] || options[0];
                if (!option) continue;
                const rect = option.getBoundingClientRect();
                return [rect.left + rect.width / 2, rect.top + rect.height / 2];
              }
              return null;
            }
            """,
            new { optionText = option, popperId });
        if (point is not { Length: 2 })
            throw new InvalidOperationException($"批量设置未找到集数选项：{option}");
        await page.Mouse.ClickAsync((float)point[0], (float)point[1]);
        await page.WaitForTimeoutAsync(250);
    }

    private static async Task ConfigureEnterpriseEpisodePriceAsync(ILocator dialog, KuaishouPersonalConfig config)
    {
        var price = First(config.SeriesPrice, config.EpisodePrice);
        if (string.IsNullOrWhiteSpace(price)) return;
        var candidates = dialog.Locator(
            "input[placeholder*=价格], input[placeholder*=金额], input[name*=price i], input[id*=price i]");
        var count = Math.Min(await candidates.CountAsync(), 10);
        for (var index = count - 1; index >= 0; index--)
        {
            var input = candidates.Nth(index);
            if (!await input.IsVisibleAsync() || !await input.IsEnabledAsync()) continue;
            await input.FillAsync(price);
            return;
        }
    }

    private static async Task ConfirmEnterpriseFreeEpisodeWarningAsync(IPage page)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var dialogs = page.Locator("[role=dialog]:visible, .ks-dialog:visible, .ant-modal:visible");
            var count = await dialogs.CountAsync();
            for (var index = count - 1; index >= 0; index--)
            {
                var dialog = dialogs.Nth(index);
                var text = await dialog.InnerTextAsync();
                if (!text.Contains("免费", StringComparison.Ordinal) &&
                    !text.Contains("付费", StringComparison.Ordinal)) continue;
                await ClickDialogConfirmAsync(dialog);
                return;
            }
            await page.WaitForTimeoutAsync(100);
        }
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    public static UploadAction ChooseUploadAction(IReadOnlyList<EpisodeVideoSlot> slots)
    {
        if (slots.Count == 0 || slots.All(slot => !slot.Uploaded))
            return UploadAction.UploadAll;
        if (slots.All(slot => slot.Uploaded))
            return UploadAction.Skip;
        return UploadAction.FillMissing;
    }

    public static bool CardShowsUploadedVideo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return Compact(text).Contains("视频预览", StringComparison.Ordinal);
    }

    public static IReadOnlyList<EpisodeVideoSlot> NormalizeEpisodeSlots(IEnumerable<EpisodeVideoSlot> slots)
    {
        var uploadedByNumber = new Dictionary<int, bool>();
        foreach (var slot in slots)
        {
            if (slot.Number <= 0)
                continue;
            uploadedByNumber[slot.Number] = uploadedByNumber.TryGetValue(slot.Number, out var uploaded)
                ? uploaded || slot.Uploaded
                : slot.Uploaded;
        }

        if (uploadedByNumber.Count == 0)
            return [];
        var max = uploadedByNumber.Keys.Max();
        var normalized = new EpisodeVideoSlot[max];
        for (var number = 1; number <= max; number++)
            normalized[number - 1] = new EpisodeVideoSlot(number, uploadedByNumber.TryGetValue(number, out var uploaded) && uploaded);
        return normalized;
    }

    public static string? MatchEpisodeFile(IEnumerable<string> paths, int episode)
    {
        foreach (var path in paths)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                Path.GetFileNameWithoutExtension(path),
                @"第\s*(\d+)\s*集");
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var number) &&
                number == episode)
                return path;
        }

        return null;
    }

    private static async Task EnsureEpisodeVideosUploadedAsync(
        IPage page,
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var slots = await ReadEpisodeVideoSlotsAsync(page);
        var action = ChooseUploadAction(slots);
        if (action == UploadAction.Skip)
        {
            progress?.Report("快手分账个人版：页面上的剧集视频已齐全，跳过重复上传。 ");
        }
        else if (action == UploadAction.FillMissing)
        {
            await UploadMissingEpisodesAsync(page, data, slots, config.UploadTimeoutMinutes, progress, cancellationToken);
        }
        else
        {
            try
            {
                await SelectBatchVideoFilesAsync(page, data.VideoPaths, progress);
                await ConfirmUploadPositionAsync(page, cancellationToken);
                if (!await ClickDrawerStartUploadAsync(page))
                    throw new InvalidOperationException("未找到本地视频抽屉里的“开始上传”。不要点“视频链接”页的上传按钮。");
                await WaitForUploadsAsync(page, data.VideoPaths.Count, data.Title, config.UploadTimeoutMinutes, progress, cancellationToken);
            }
            catch (Exception ex) when (CanFillAfterInterruptedUpload(ex))
            {
                progress?.Report($"{ex.Message} 将补传未成功的剧集。 ");
                await CloseUploadDrawerIfOpenAsync(page);
                var after = await ReadEpisodeVideoSlotsAsync(page);
                var retry = ChooseUploadAction(after);
                if (retry == UploadAction.UploadAll)
                    throw;
                if (retry == UploadAction.FillMissing)
                    await UploadMissingEpisodesAsync(page, data, after, config.UploadTimeoutMinutes, progress, cancellationToken);
            }
        }

        var finalSlots = await ReadEpisodeVideoSlotsAsync(page);
        var missing = finalSlots.Where(slot => !slot.Uploaded).Select(slot => slot.Number).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"快手个人版仍有未上传剧集：{FormatEpisodeList(missing)}。");
    }

    private static bool CanFillAfterInterruptedUpload(Exception exception) =>
        exception is TimeoutException ||
        (exception is InvalidOperationException &&
         (exception.Message.Contains("集失败", StringComparison.Ordinal) ||
          exception.Message.Contains("失败条目", StringComparison.Ordinal)));

    private static async Task UploadMissingEpisodesAsync(
        IPage page,
        KuaishouPersonalProjectData data,
        IReadOnlyList<EpisodeVideoSlot> slots,
        int timeoutMinutes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var missing = slots.Where(slot => !slot.Uploaded).Select(slot => slot.Number).Distinct().Order().ToArray();
        var files = new List<(int Episode, string Path)>(missing.Length);
        foreach (var episode in missing)
        {
            var path = MatchEpisodeFile(data.VideoPaths, episode);
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException($"未找到第{episode}集的本地视频，无法补传。");
            files.Add((episode, path));
        }

        progress?.Report($"快手分账个人版：发现未上传剧集，开始补传 {FormatEpisodeList(missing)}。 ");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(timeoutMinutes, 5, 240));
        foreach (var (episode, path) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UploadOneMissingEpisodeAsync(page, episode, path, deadline, cancellationToken);
            progress?.Report($"快手分账个人版：第{episode}集已补传。 ");
        }
    }

    private static async Task UploadOneMissingEpisodeAsync(
        IPage page,
        int episode,
        string path,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        await OpenEpisodeTabAsync(page, episode);
        var marked = await page.EvaluateAsync<string>(
            """
            episode => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              document.querySelectorAll('[data-kuaishou-gap-upload]').forEach(element => element.removeAttribute('data-kuaishou-gap-upload'));
              const card = Array.from(document.querySelectorAll('.episode-video')).find(element =>
                compact(element.querySelector('.episode-video__header__index')?.innerText) === `第${episode}集`);
              if (!card) return 'no-card';
              const text = compact(card.innerText);
              if (text.includes('视频预览') || card.querySelector('label[for^="replace-video-"]')) return 'already';
              const input = Array.from(card.querySelectorAll('input.ks-upload__input')).find(element =>
                !String(element.id || '').startsWith('replace-video-'));
              if (!input) return 'no-input';
              input.setAttribute('data-kuaishou-gap-upload', '1');
              return 'ok';
            }
            """,
            episode);
        if (marked == "already")
            return;
        if (marked != "ok")
            throw new InvalidOperationException($"第{episode}集未找到可补传的上传框。");

        await page.Locator("input[data-kuaishou-gap-upload='1']").First.SetInputFilesAsync(path);
        if (await TryConfirmUploadPositionAsync(page, episode, cancellationToken) ||
            await UploadDrawerVisibleAsync(page))
        {
            if (!await ClickDrawerStartUploadAsync(page))
                throw new InvalidOperationException($"第{episode}集补传时未找到“开始上传”。");
        }

        await WaitForGapEpisodeAsync(page, episode, deadline, cancellationToken);
    }

    private static async Task WaitForGapEpisodeAsync(
        IPage page,
        int episode,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await OpenEpisodeTabAsync(page, episode);
            var state = await page.EvaluateAsync<string>(
                """
                episode => {
                  const compact = value => String(value || '').replace(/\s+/g, '');
                  const card = Array.from(document.querySelectorAll('.episode-video')).find(element =>
                    compact(element.querySelector('.episode-video__header__index')?.innerText) === `第${episode}集`);
                  if (!card) return 'pending';
                  const text = compact(card.innerText);
                  if (text.includes('视频预览') || card.querySelector('label[for^="replace-video-"]')) return 'uploaded';
                  if (text.includes('上传失败') || text.includes('校验失败')) return 'failed';
                  return 'pending';
                }
                """,
                episode);
            if (state == "uploaded")
                return;
            if (state == "failed")
                throw new InvalidOperationException($"快手个人版第{episode}集补传失败。");
            var body = (await page.Locator("body").InnerTextAsync()).Replace("为避免上传失败", string.Empty, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(ReadFinishedFailure(body)))
                throw new InvalidOperationException($"快手个人版第{episode}集补传失败。");
            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException($"等待第{episode}集补传完成超时。");
    }

    private static async Task<IReadOnlyList<EpisodeVideoSlot>> ReadEpisodeVideoSlotsAsync(IPage page)
    {
        var tabs = page.Locator(".set-list li");
        var tabCount = 0;
        try { tabCount = await tabs.CountAsync(); }
        catch (PlaywrightException) { tabCount = 0; }
        if (tabCount <= 1)
            return NormalizeEpisodeSlots(await ReadVisibleEpisodeVideoSlotsAsync(page));

        var slots = new List<EpisodeVideoSlot>();
        for (var index = 0; index < tabCount; index++)
        {
            var tab = tabs.Nth(index);
            try
            {
                if (!await tab.IsVisibleAsync()) continue;
                await tab.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                await page.WaitForTimeoutAsync(400);
            }
            catch (PlaywrightException)
            {
                throw new InvalidOperationException("未能切换剧集分页，无法确认哪些视频还未上传。");
            }

            slots.AddRange(await ReadVisibleEpisodeVideoSlotsAsync(page));
        }

        return NormalizeEpisodeSlots(slots);
    }

    private static async Task<IReadOnlyList<EpisodeVideoSlot>> ReadVisibleEpisodeVideoSlotsAsync(IPage page)
    {
        var values = await page.EvaluateAsync<int[]>(
            """
            () => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const pairs = [];
              for (const card of document.querySelectorAll('.episode-video')) {
                const match = compact(card.querySelector('.episode-video__header__index')?.innerText).match(/^第(\d+)集$/);
                if (!match) continue;
                const text = compact(card.innerText);
                const uploaded = text.includes('视频预览') || Boolean(card.querySelector('label[for^="replace-video-"]'));
                pairs.push(Number(match[1]), uploaded ? 1 : 0);
              }
              return pairs;
            }
            """);
        var slots = new List<EpisodeVideoSlot>(values.Length / 2);
        for (var index = 0; index + 1 < values.Length; index += 2)
            slots.Add(new EpisodeVideoSlot(values[index], values[index + 1] == 1));
        return slots;
    }

    private static async Task OpenEpisodeTabAsync(IPage page, int episode)
    {
        var clicked = await page.EvaluateAsync<bool>(
            """
            episode => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const tab = Array.from(document.querySelectorAll('.set-list li')).find(item => {
                const match = compact(item.innerText).match(/(\d+)\s*-\s*(\d+)集/);
                if (!match) return false;
                return episode >= Number(match[1]) && episode <= Number(match[2]);
              });
              if (!tab) return false;
              tab.click();
              return true;
            }
            """,
            episode);
        if (clicked)
            await page.WaitForTimeoutAsync(400);
    }

    private static async Task<bool> TryConfirmUploadPositionAsync(
        IPage page,
        int episode,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = page.Locator(".ks-dialog:visible, .ant-modal:visible, [role=dialog]:visible")
                .Filter(new LocatorFilterOptions { HasText = "请选择批量上传位置" });
            if (await dialog.CountAsync() == 0)
            {
                await page.WaitForTimeoutAsync(200);
                continue;
            }

            var input = dialog.Last.Locator("input").First;
            if (await input.CountAsync() > 0)
                await input.FillAsync(episode.ToString());
            await ClickDialogConfirmAsync(dialog.Last);
            return true;
        }

        return false;
    }

    private static async Task CloseUploadDrawerIfOpenAsync(IPage page)
    {
        if (await UploadDrawerVisibleAsync(page))
            await ClickDrawerConfirmAsync(page);
    }

    private static Task<bool> UploadDrawerVisibleAsync(IPage page) =>
        page.EvaluateAsync<bool>(
            """
            () => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
              };
              return Array.from(document.querySelectorAll('.ks-drawer,.ks-drawer-content,[class*="drawer"]'))
                .some(element => visible(element) && compact(element.innerText).includes('批量上传视频'));
            }
            """);

    private static string FormatEpisodeList(IReadOnlyList<int> episodes) =>
        string.Join("、", episodes.Select(episode => $"第{episode}集"));

    private static string Compact(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"\s+", "");

    private static async Task SelectBatchVideoFilesAsync(IPage page, IReadOnlyList<string> paths, IProgress<string>? progress)
    {
        try
        {
            var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
            {
                if (!await OpenLocalVideoUploadAsync(page))
                    throw new InvalidOperationException("未打开批量上传视频菜单。");
            }, new PageRunAndWaitForFileChooserOptions { Timeout = 8_000 });
            await chooser.SetFilesAsync(paths.ToArray());
            progress?.Report($"快手分账个人版：已通过“上传视频”选择 {paths.Count} 集。 ");
            return;
        }
        catch (PlaywrightException)
        {
            // 菜单没有拉起文件选择器时，改用批量视频 input。
        }
        catch (InvalidOperationException)
        {
        }

        var input = page.Locator("#batch-upload, .batch-wrapper input[type=file][multiple][accept*=video], input[type=file][multiple][accept*=video], input[type=file][multiple][accept*=mp4]").First;
        if (await input.CountAsync() == 0)
            input = await FindVideoInputAsync(page);
        await input.SetInputFilesAsync(paths.ToArray());
        progress?.Report($"快手分账个人版：已选择 {paths.Count} 集本地视频。 ");
    }

    private static async Task<bool> OpenLocalVideoUploadAsync(IPage page)
    {
        var menu = page.Locator(".batch-wrapper .batch-btn, .batch-btn").Filter(new LocatorFilterOptions { HasText = "批量上传视频" }).First;
        if (await menu.CountAsync() == 0 || !await menu.IsVisibleAsync())
        {
            if (!await ClickVisibleAsync(page, ["批量上传视频"], 3_000)) return false;
        }
        else
        {
            await menu.HoverAsync();
        }
        await page.WaitForTimeoutAsync(350);
        var item = page.Locator("label[for='batch-upload'], .batch-btn-list label, .batch-btn-list li").Filter(new LocatorFilterOptions { HasText = "上传视频" }).First;
        if (await item.CountAsync() > 0 && await item.IsVisibleAsync())
        {
            await item.ClickAsync();
            return true;
        }
        return await ClickVisibleAsync(page, ["上传视频"], 3_000);
    }

    private static async Task<bool> ClickDrawerStartUploadAsync(IPage page)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var clicked = await page.EvaluateAsync<bool>(
                """
                () => {
                  const compact = value => String(value || '').replace(/\s+/g, '');
                  const visible = element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
                  };
                  const disabled = element => element.disabled === true
                    || String(element.getAttribute('aria-disabled') || '').toLowerCase() === 'true'
                    || element.classList.contains('is-disabled')
                    || Boolean(element.closest('[aria-disabled="true"],.is-disabled,.disabled'));
                  const panels = Array.from(document.querySelectorAll('.ks-drawer,.ks-drawer-content,[class*="drawer"]'))
                    .filter(element => visible(element) && compact(element.innerText).includes('批量上传视频'));
                  let best = null;
                  for (const panel of panels) {
                    for (const button of panel.querySelectorAll('button,[role="button"],.ks-button')) {
                      if (!visible(button) || disabled(button)) continue;
                      const text = compact(button.innerText);
                      if (!text.includes('开始上传')) continue;
                      const local = button.closest('.drawer-footer')?.parentElement || button.parentElement;
                      const localText = compact(local?.innerText || '');
                      let score = 0;
                      if (button.closest('.drawer-footer,[class*="drawer-footer"]')) score += 1000;
                      if (/(mp4|mov|webm|ogg)/i.test(localText)) score += 500;
                      if (/第\d+集/.test(localText)) score += 300;
                      if (button.closest('.sort-wrapper,.cdn-content') && !button.closest('.drawer-footer')) score -= 10000;
                      if (/填写视频链接|上传链接文件|视频链接/.test(localText) && !/(mp4|mov)/i.test(localText)) score -= 800;
                      if (!best || score > best.score) best = { button, score };
                    }
                  }
                  if (!best || best.score < 500) return false;
                  best.button.click();
                  return true;
                }
                """);
            if (clicked) return true;
            await page.WaitForTimeoutAsync(500);
        }
        return false;
    }

    private static async Task ClickDrawerConfirmAsync(IPage page)
    {
        var clicked = await page.EvaluateAsync<bool>(
            """
            () => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
              };
              const panels = Array.from(document.querySelectorAll('.ks-drawer,.ks-drawer-content,[class*="drawer"]'))
                .filter(element => visible(element) && compact(element.innerText).includes('批量上传视频'));
              for (const panel of panels) {
                const button = Array.from(panel.querySelectorAll('button,[role="button"],.ks-button'))
                  .filter(element => visible(element) && ['确定', '确认'].includes(compact(element.innerText)))
                  .at(-1);
                if (!button) continue;
                button.click();
                return true;
              }
              return false;
            }
            """);
        if (!clicked)
            await ClickVisibleAsync(page, ["确定"], 2_000);
    }

    private static async Task<ILocator> FindVideoInputAsync(IPage page)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var index = await page.EvaluateAsync<int>(
                """
                () => {
                  const normalize = value => String(value || '').replace(/\s+/g, ' ').trim();
                  const videoWords = ['剧集视频', '上传视频', '视频上传', '上传剧集', '批量上传视频'];
                  const rejectWords = ['版权证明', '承诺函', '现场拍摄图', '短剧工程文件', '短剧封面', '单集封面'];
                  const rejectAcceptRe = /(pdf|image|jpg|jpeg|png|bmp|gif|webp)/i;
                  const videoAcceptRe = /(video|mp4|mov|m4v|avi|mkv)/i;
                  const contextText = element => {
                    let node = element;
                    const parts = [];
                    for (let depth = 0; node && depth < 9; depth += 1, node = node.parentElement) {
                      const value = normalize(node.innerText || node.textContent || '');
                      if (value) parts.push(value);
                    }
                    return parts.join(' ');
                  };
                  const inputs = Array.from(document.querySelectorAll('input[type="file"]'));
                  let best = null;
                  inputs.forEach((element, index) => {
                    const accept = String(element.getAttribute('accept') || '');
                    const acceptVideo = videoAcceptRe.test(accept);
                    if (rejectAcceptRe.test(accept) && !acceptVideo) return;
                    const nearText = contextText(element);
                    const nearVideo = videoWords.some(word => nearText.includes(word));
                    const nearReject = rejectWords.some(word => nearText.includes(word));
                    if (nearReject && !nearVideo) return;
                    if (!acceptVideo && element.multiple !== true) return;
                    let score = 0;
                    if (element.id === 'batch-upload') score += 200;
                    if (acceptVideo) score += 120;
                    if (element.multiple === true) score += 80;
                    if (nearVideo) score += 60;
                    if (nearReject) score -= 100;
                    if (!best || score > best.score) best = { index, score };
                  });
                  return best ? best.index : -1;
                }
                """);
            if (index >= 0)
                return page.Locator("input[type=file]").Nth(index);
            await page.WaitForTimeoutAsync(300);
        }
        throw new InvalidOperationException("视频上传步骤未找到可批量选择的剧集视频入口。");
    }

    private static async Task ConfirmUploadPositionAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = page.Locator(".ks-dialog:visible, .ant-modal:visible, [role=dialog]:visible")
                .Filter(new LocatorFilterOptions { HasText = "请选择批量上传位置" });
            if (await dialog.CountAsync() == 0)
            {
                await page.WaitForTimeoutAsync(300);
                continue;
            }
            var input = dialog.Last.Locator("input").First;
            if (await input.CountAsync() > 0) await input.FillAsync("1");
            await ClickDialogConfirmAsync(dialog.Last);
            return;
        }
        throw new InvalidOperationException("选择视频后未出现“请选择批量上传位置”弹窗。");
    }

    private static async Task WaitForUploadsAsync(
        IPage page,
        int expected,
        string title,
        int timeoutMinutes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(timeoutMinutes, 5, 240));
        var last = string.Empty;
        var baselineCaptured = false;
        string? baselineFailure = null;
        var sawThisUpload = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = (await page.Locator("body").InnerTextAsync()).Replace("为避免上传失败", string.Empty, StringComparison.Ordinal);
            var drawerDone = body.Contains("全部上传完成", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(body, $"{expected}\\s*集成功\\s*[，,]\\s*0\\s*集失败");
            var stillUploading = IndicatesActiveUpload(body);
            var finishedFailure = ReadFinishedFailure(body);
            if (!baselineCaptured)
            {
                baselineFailure = finishedFailure;
                baselineCaptured = true;
            }
            else if (stillUploading)
            {
                sawThisUpload = true;
            }

            if (await HasReviewingRowAsync(page, title) || ReviewSucceeded(body))
            {
                progress?.Report("快手分账个人版：剧集已在审核中，视为上传成功。 ");
                return;
            }
            if (drawerDone && !stillUploading)
            {
                progress?.Report($"快手分账个人版：{expected} 集视频上传完成。 ");
                await ClickDrawerConfirmAsync(page);
                return;
            }
            if (ShouldStopForUploadFailure(finishedFailure, baselineFailure, sawThisUpload, stillUploading))
                throw new InvalidOperationException(finishedFailure);
            if (sawThisUpload && (body.Contains("上传失败", StringComparison.Ordinal) || body.Contains("校验失败", StringComparison.Ordinal)))
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

    public static bool IndicatesActiveUpload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("上传中", StringComparison.Ordinal)
            || text.Contains("等待上传", StringComparison.Ordinal)
            || text.Contains("解析中", StringComparison.Ordinal);
    }

    public static bool ShouldStopForUploadFailure(
        string? finishedFailure,
        string? baselineFailure,
        bool sawThisUpload,
        bool stillUploading)
    {
        if (string.IsNullOrWhiteSpace(finishedFailure) || stillUploading || !sawThisUpload)
            return false;
        return !string.Equals(finishedFailure, baselineFailure, StringComparison.Ordinal);
    }

    public static string? ReadFinishedFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"上传完成\s*(\d+)\s*集成功\s*[，,]?\s*(\d+)\s*集失败");
        if (!match.Success ||
            !int.TryParse(match.Groups[2].Value, out var failed) ||
            failed <= 0 ||
            !int.TryParse(match.Groups[1].Value, out var succeeded))
            return null;
        return $"快手个人版视频上传结束：{succeeded} 集成功，{failed} 集失败。";
    }

    private static string ExtractProgress(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"上传完成\s*\d+\s*集成功\s*，?\s*\d+\s*集失败|\d+%");
        return match.Success ? match.Value : string.Empty;
    }

    private static async Task WaitForUploadPageAsync(IPage page, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fillError = await ReadEpisodeFillErrorAsync(page);
            if (!string.IsNullOrWhiteSpace(fillError))
                throw new InvalidOperationException($"快手单集信息未通过校验：{fillError}");
            if (await ConfirmNextStepDialogAsync(page))
            {
                progress?.Report("快手分账个人版：已点击下一步确认弹窗。 ");
                await page.WaitForTimeoutAsync(700);
                continue;
            }
            if (await IsVideoUploadStepAsync(page)) return;
            await page.WaitForTimeoutAsync(300);
        }
        if (await NextStepDialogVisibleAsync(page))
            throw new TimeoutException("下一步确认弹窗未处理，未进入视频上传步骤。");
        throw new TimeoutException("点击下一步后未进入视频上传步骤。");
    }

    private static async Task<bool> ConfirmNextStepDialogAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                """
                () => {
                  const dense = value => String(value || '').replace(/\s+/g, '');
                  const visible = element => {
                    if (!element || !(element instanceof Element)) return false;
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && rect.bottom >= 0 && rect.right >= 0
                      && rect.top <= window.innerHeight && rect.left <= window.innerWidth
                      && style.display !== 'none' && style.visibility !== 'hidden' && style.pointerEvents !== 'none';
                  };
                  const disabled = element => {
                    const ariaDisabled = String(element.getAttribute('aria-disabled') || '').toLowerCase();
                    return ariaDisabled === 'true'
                      || element.disabled === true
                      || element.classList.contains('is-disabled')
                      || element.classList.contains('disabled')
                      || Boolean(element.closest('[aria-disabled="true"],.is-disabled,.disabled'));
                  };
                  const dialogs = Array.from(document.querySelectorAll(
                    '.ks-dialog,.ks-dialog-wrapper,.ks-modal,.ks-modal-content,.ks-message-box,.ant-modal,.ant-modal-content,.el-dialog,.semi-modal,.semi-modal-content,[role="dialog"]'
                  )).filter(element => {
                    if (!visible(element)) return false;
                    const text = dense(element.innerText || element.textContent);
                    return text.includes('确认提示') && text.includes('剧集审核通过后仅支持修改封面');
                  });
                  const dialog = dialogs.sort((left, right) => right.getBoundingClientRect().top - left.getBoundingClientRect().top)[0];
                  if (!dialog) return false;
                  const candidates = Array.from(dialog.querySelectorAll(
                    'button,[role="button"],.ks-button,.ant-btn,.el-button,.semi-button,span,div'
                  )).filter(element => visible(element) && !disabled(element));
                  let best = null;
                  for (const element of candidates) {
                    const value = dense(element.innerText || element.textContent || element.value);
                    if (!value) continue;
                    const exact = value === '确认';
                    const loose = value.includes('确认') && value.length <= 6;
                    if (!exact && !loose) continue;
                    const className = String(element.className || '');
                    const rect = element.getBoundingClientRect();
                    const inFooter = Boolean(element.closest(
                      '.ks-dialog__footer,.ks-modal-footer,.dialog-footer,.ant-modal-footer,.el-dialog__footer,.semi-modal-footer,.semi-modal-confirm-btns'
                    ));
                    const score = (exact ? 1000 : 700) + (inFooter ? 120 : 0) + (/primary/i.test(className) ? 80 : 0) + Math.max(0, rect.left);
                    if (!best || score > best.score) best = { element, score };
                  }
                  if (!best) {
                    const primary = candidates.filter(element => /primary/i.test(String(element.className || '')) && element.closest(
                      '.ks-dialog__footer,.ks-modal-footer,.dialog-footer,.ant-modal-footer,.el-dialog__footer,.semi-modal-footer,.semi-modal-confirm-btns'
                    ));
                    const element = primary[primary.length - 1];
                    if (element) best = { element, score: 500 };
                  }
                  if (!best) return false;
                  best.element.scrollIntoView({ block: 'center', inline: 'center' });
                  best.element.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true, view: window }));
                  best.element.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true, view: window }));
                  best.element.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadEpisodeFillErrorAsync(IPage page)
    {
        try
        {
            var text = await page.EvaluateAsync<string>(
                """
                () => {
                  const compact = value => String(value || '').replace(/\s+/g, '');
                  const visible = element => {
                    if (!element || !(element instanceof Element)) return false;
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
                  };
                  const dialogs = Array.from(document.querySelectorAll(
                    '.ks-dialog,.ks-message-box,.ant-modal,.el-dialog,[role="dialog"]'
                  )).filter(element => visible(element) && compact(element.innerText).includes('信息填写错误'));
                  if (dialogs.length === 0) return '';
                  const message = compact(dialogs[0].innerText);
                  return message.length > 240 ? message.slice(0, 240) : message;
                }
                """);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private static async Task<bool> NextStepDialogVisibleAsync(IPage page)
    {
        try
        {
            var body = await page.Locator("body").InnerTextAsync();
            var compact = body.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
            return compact.Contains("确认提示", StringComparison.Ordinal) &&
                   compact.Contains("剧集审核通过后仅支持修改封面", StringComparison.Ordinal);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> IsVideoUploadStepAsync(IPage page)
    {
        if (await NextStepDialogVisibleAsync(page)) return false;
        try
        {
            return await page.EvaluateAsync<bool>(
                """
                () => {
                  const normalize = value => String(value || '').replace(/\s+/g, ' ').trim();
                  const text = normalize(document.body?.innerText || '');
                  if (text.includes('确认提示') && text.includes('剧集审核通过后仅支持修改封面')) return false;
                  const firstPage = text.includes('保存草稿') && text.includes('下一步') && text.includes('请勾选同意');
                  const videoWords = ['剧集视频', '上传视频', '视频上传', '上传剧集'];
                  const rejectAcceptRe = /(pdf|image|jpg|jpeg|png|bmp|gif|webp)/i;
                  const videoAcceptRe = /(video|mp4|mov|m4v|avi|mkv)/i;
                  const rejectContext = ['版权证明', '承诺函', '现场拍摄图', '短剧工程文件', '短剧封面', '单集封面'];
                  const contextText = element => {
                    let node = element;
                    const parts = [];
                    for (let depth = 0; node && depth < 8; depth += 1, node = node.parentElement) {
                      const value = normalize(node.innerText || '');
                      if (value) parts.push(value);
                    }
                    return parts.join(' ');
                  };
                  const hasVideoInput = Array.from(document.querySelectorAll('input[type="file"]')).some(element => {
                    const accept = String(element.getAttribute('accept') || '');
                    if (rejectAcceptRe.test(accept) && !videoAcceptRe.test(accept)) return false;
                    const nearText = contextText(element);
                    const nearVideo = videoWords.some(word => nearText.includes(word));
                    const nearReject = rejectContext.some(word => nearText.includes(word));
                    if (nearReject && !nearVideo) return false;
                    return videoAcceptRe.test(accept) || element.multiple === true || nearVideo;
                  });
                  return hasVideoInput || (!firstPage && videoWords.some(word => text.includes(word)));
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<ILocator> MarkOverlayAsync(IPage page, string marker, float timeout)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var marked = await page.EvaluateAsync<bool>(
                """
                (marker) => {
                  const visible = element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
                  };
                  const overlays = Array.from(document.querySelectorAll('.ks-overlay, .ks-dialog, [role="dialog"]'))
                    .filter(element => visible(element) && (element.innerText || '').includes(marker))
                    .sort((left, right) => {
                      const leftBox = left.getBoundingClientRect();
                      const rightBox = right.getBoundingClientRect();
                      return leftBox.width * leftBox.height - rightBox.width * rightBox.height;
                    });
                  document.querySelectorAll('[data-kuaishou-batch]').forEach(element => element.removeAttribute('data-kuaishou-batch'));
                  const overlay = overlays[0];
                  if (!overlay) return false;
                  overlay.setAttribute('data-kuaishou-batch', '1');
                  return true;
                }
                """,
                marker);
            if (marked) return page.Locator("[data-kuaishou-batch='1']").First;
            await page.WaitForTimeoutAsync(300);
        }

        throw new TimeoutException("未找到批量设置弹窗。");
    }

    private static async Task<ILocator> VisibleDialogAsync(IPage page, float timeout)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var marked = await page.EvaluateAsync<bool>(
                """
                () => {
                  const visible = element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
                  };
                  const ownText = element => Array.from(element.childNodes)
                    .filter(node => node.nodeType === Node.TEXT_NODE)
                    .map(node => node.textContent || '')
                    .join('')
                    .replace(/\s+/g, '');
                  const titles = Array.from(document.querySelectorAll('div, span, p, h1, h2, h3, label'))
                    .filter(element => visible(element) && ['添加单集信息', '批量设置'].includes(ownText(element)));
                  let dialog = null;
                  let dialogArea = Number.POSITIVE_INFINITY;
                  for (const title of titles) {
                    let node = title;
                    for (let depth = 0; node && depth < 8; depth += 1, node = node.parentElement) {
                      const text = node.innerText || '';
                      if (!node.querySelector('button, input') || !/确定|确认|取消/.test(text)) continue;
                      const rect = node.getBoundingClientRect();
                      const area = rect.width * rect.height;
                      if (area <= 0 || area >= dialogArea) continue;
                      dialog = node;
                      dialogArea = area;
                    }
                  }
                  document.querySelectorAll('[data-kuaishou-dialog]').forEach(element => element.removeAttribute('data-kuaishou-dialog'));
                  if (!dialog) return false;
                  dialog.setAttribute('data-kuaishou-dialog', '1');
                  return true;
                }
                """);
            if (marked) return page.Locator("[data-kuaishou-dialog='1']").First;
            await page.WaitForTimeoutAsync(300);
        }

        throw new TimeoutException("未找到单集信息弹窗。");
    }

    private static async Task ClickDialogConfirmAsync(ILocator dialog)
    {
        var clicked = await dialog.EvaluateAsync<bool>(
            """
            (el) => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
              };
              const buttons = Array.from(el.querySelectorAll('button, [role="button"], a, span, div'))
                .filter(element => visible(element) && ['确定', '确认'].includes(compact(element.innerText)));
              const button = buttons[buttons.length - 1];
              if (!button) return false;
              button.click();
              return true;
            }
            """);
        if (!clicked) throw new InvalidOperationException("弹窗未找到确认按钮。");
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
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(240);
        var lastText = string.Empty;
        var confirmClicked = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ClickSubmitReviewConfirmAsync(page))
            {
                if (!confirmClicked)
                    progress?.Report("快手分账个人版：已点击提交审核确认弹窗。");
                confirmClicked = true;
                await page.WaitForTimeoutAsync(800);
                continue;
            }

            var body = (await ReadAllBodyTextAsync(page)).Replace("为避免上传失败", string.Empty, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(body)) lastText = body;
            var leftEditPage = !page.Url.Contains("content-management/edit", StringComparison.OrdinalIgnoreCase);
            // 列表页的「审核状态」筛选项本身就包含“审核中”，不能拿整页文字判断。
            // 与 weixin-channel-tool 一致：成功文案，或确认弹窗已点且已离开编辑页，或标题所在行是审核中。
            if (ReviewSucceeded(body) ||
                (confirmClicked && leftEditPage) ||
                await HasReviewingRowAsync(page, title))
                return;
            if (new[] { "发布失败", "上传失败", "校验失败", "请重新上传", "提交失败", "审核提交失败" }.Any(body.Contains))
                throw new InvalidOperationException("快手个人版提交审核失败，平台页面返回失败提示。");
            await Task.Delay(1500, cancellationToken);
        }

        var detail = string.IsNullOrWhiteSpace(lastText) ? string.Empty : $"最近页面：{TrimForError(lastText)}";
        throw new TimeoutException($"等待提交审核成功提示超时。{detail}");
    }

    private static bool ReviewSucceeded(string body) =>
        new[] { "发布成功", "提交成功", "提交审核成功", "审核提交成功", "已提交审核", "已提交至审核", "内容提交成功" }
            .Any(marker => body.Contains(marker, StringComparison.Ordinal));

    private static string TrimForError(string text)
    {
        var compact = Normalize(text);
        return compact.Length <= 180 ? compact : compact[..180];
    }

    private static async Task<bool> ClickSubmitReviewConfirmAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                """
                () => {
                  const compact = value => String(value || '').replace(/\s+/g, '');
                  const visible = element => {
                    if (!element || !(element instanceof Element)) return false;
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && rect.bottom >= 0 && rect.right >= 0
                      && rect.top <= window.innerHeight && rect.left <= window.innerWidth
                      && style.display !== 'none' && style.visibility !== 'hidden' && style.pointerEvents !== 'none';
                  };
                  const disabled = element => element.disabled === true
                    || String(element.getAttribute('aria-disabled') || '').toLowerCase() === 'true'
                    || element.classList.contains('is-disabled')
                    || Boolean(element.closest('[aria-disabled="true"],.is-disabled,.disabled'));
                  const dialogs = Array.from(document.querySelectorAll(
                    '.ks-dialog,.ks-dialog-wrapper,.ks-modal,.ks-modal-content,.ks-message-box,.ant-modal,.ant-modal-content,.el-dialog,.semi-modal,[role="dialog"]'
                  )).filter(element => {
                    if (!visible(element)) return false;
                    const text = compact(element.innerText || '');
                    if (!text || text.includes('批量上传视频') || text.includes('图片裁剪')) return false;
                    return text.includes('提交审核') || text.includes('确认提示') || text.includes('审核通过后')
                      || text.includes('不可修改') || text.includes('确认提交');
                  });
                  for (const dialog of dialogs) {
                    const button = Array.from(dialog.querySelectorAll('button,[role="button"],.ks-button,.ant-btn'))
                      .filter(element => visible(element) && !disabled(element))
                      .filter(element => ['确认', '确定'].includes(compact(element.innerText)))
                      .sort((left, right) => {
                        const leftFooter = left.closest('.ks-dialog__footer,.ant-modal-footer') ? 1 : 0;
                        const rightFooter = right.closest('.ks-dialog__footer,.ant-modal-footer') ? 1 : 0;
                        return rightFooter - leftFooter;
                      })[0];
                    if (!button) continue;
                    button.click();
                    return true;
                  }
                  return false;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
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

    private static Task HandleCropDialogAsync(IPage page, int shrinkClicks, CancellationToken cancellationToken) =>
        KuaishouCropDialog.ConfirmAsync(page, shrinkClicks, cancellationToken);

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
