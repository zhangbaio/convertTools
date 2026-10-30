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

    public async Task ConfigureEpisodeInfoAsync(
        IPage page,
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        CancellationToken cancellationToken)
    {
        var episodeCount = data.VideoPaths.Count;
        if (episodeCount < 1) throw new InvalidOperationException("没有可设置的剧集视频，无法确定开始集和结束集。");
        var additional = Math.Max(0, episodeCount - 1);
        if (additional > 0)
        {
            if (!await ClickVisibleAsync(page, ["添加单集信息"], 8_000))
                throw new InvalidOperationException("未找到“添加单集信息”按钮。");
            var addDialog = await VisibleDialogAsync(page, 8_000);
            var input = addDialog.Locator("input").First;
            if (await input.CountAsync() > 0) await input.FillAsync(additional.ToString());
            await ClickDialogConfirmAsync(addDialog);
            await page.WaitForTimeoutAsync(1000);
        }

        if (!await ClickVisibleAsync(page, ["批量设置"], 8_000))
            throw new InvalidOperationException("未找到单集信息“批量设置”按钮。");
        var dialog = await MarkOverlayAsync(page, "选择集数", 8_000);
        var queries = dialog.Locator("input[placeholder*='输入查询']");
        var queryCount = await queries.CountAsync();
        if (queryCount < 2) throw new InvalidOperationException("批量设置弹窗未找到集数选择器。");
        await SelectQueryOptionAsync(page, queries.Nth(0), "第1集");
        await SelectQueryOptionAsync(page, queries.Nth(queryCount - 1), $"第{episodeCount}集");
        await page.WaitForTimeoutAsync(400);
        var titleInput = dialog.Locator("input[placeholder*=标题], input[placeholder*=单集]").Last;
        if (await titleInput.CountAsync() > 0) await titleInput.FillAsync(data.Title);
        if (config.StoragePlatform == PlatformPublisher.Common.Models.PublishPlatform.KuaishouEnterpriseRevenue)
            await ConfigureEnterpriseEpisodePriceAsync(dialog, config);
        if (string.IsNullOrWhiteSpace(data.VerticalCoverPath) || !File.Exists(data.VerticalCoverPath))
            throw new FileNotFoundException("缺少快手个人版竖屏单集封面。", data.VerticalCoverPath);
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
        await page.Locator("input[type=file][data-kuaishou-batch-cover='1']").First.SetInputFilesAsync(data.VerticalCoverPath);
        await HandleCropDialogAsync(page, 18, cancellationToken);
        await ClickDialogConfirmAsync(dialog);
        var closed = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await page.WaitForTimeoutAsync(300);
            closed = !await page.EvaluateAsync<bool>(
                """
                () => Array.from(document.querySelectorAll('div, span, label'))
                  .some(element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden'
                      && (element.innerText || '').replace(/\s+/g, '') === '选择集数';
                  });
                """);
            if (closed) break;
        }
        if (!closed) throw new InvalidOperationException("批量设置弹窗确认后仍未关闭。");
        if (config.StoragePlatform == PlatformPublisher.Common.Models.PublishPlatform.KuaishouEnterpriseRevenue)
            await ConfirmEnterpriseFreeEpisodeWarningAsync(page);
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
              const matches = element => {
                const text = normalize(element.innerText || element.textContent || '');
                if (!text) return false;
                if (text === target || compact(text) === compactTarget) return true;
                return text.includes(target) && text.length <= target.length + 12;
              };
              const popups = Array.from(document.querySelectorAll('.ks-select__popper,.ks-popper,.ant-select-dropdown,.el-select-dropdown,[role="listbox"]')).filter(popup => {
                if (!visible(popup)) return false;
                const text = compact(popup.innerText || '');
                return !text.includes('承诺函') && !text.includes('现场拍摄图') && !text.includes('版权证明');
              });
              const preferred = popups.filter(popup => popperId && (popup.id === popperId || popup.getAttribute('aria-describedby') === popperId));
              for (const popup of preferred.concat(popups)) {
                const options = Array.from(popup.querySelectorAll('li.ks-select-dropdown__item,.ks-select-dropdown__item,[role="option"],li,div,span'))
                  .filter(element => visible(element) && matches(element) && element.getBoundingClientRect().height < 56);
                options.sort((left, right) => left.getBoundingClientRect().width - right.getBoundingClientRect().width);
                const option = options[0];
                if (!option) continue;
                const rect = option.getBoundingClientRect();
                return [rect.left + rect.width / 2, rect.top + rect.height / 2];
              }
              return null;
            }
            """,
            new { optionText = option, popperId });
        if (point is { Length: 2 })
            await page.Mouse.ClickAsync((float)point[0], (float)point[1]);
        else
        {
            await input.PressAsync("ArrowDown");
            await page.WaitForTimeoutAsync(100);
            await input.PressAsync("Enter");
        }
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

    private static async Task WaitForUploadPageAsync(IPage page, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        if (page.Url.Contains("step=1", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return await page.GetByText("视频上传", new PageGetByTextOptions { Exact = false }).CountAsync() > 0 ||
                   await page.GetByText("上传视频", new PageGetByTextOptions { Exact = false }).CountAsync() > 0;
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
