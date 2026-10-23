using Microsoft.Playwright;

namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 快手图片剪裁弹窗。裁剪画布会挡住坐标点击，确认先点底部主按钮，点不中再调用按钮自身的 click。
/// </summary>
internal static class KuaishouCropDialog
{
    public static Task ConfirmAsync(IPage page, int shrinkClicks, CancellationToken cancellationToken) =>
        ConfirmAsync(page, shrinkClicks, TimeSpan.FromSeconds(12), cancellationToken);

    public static async Task ConfirmAsync(
        IPage page,
        int shrinkClicks,
        TimeSpan appearTimeout,
        CancellationToken cancellationToken)
    {
        var dialog = await WaitForDialogAsync(page, appearTimeout, cancellationToken);
        if (dialog is null) return;

        await ShrinkAsync(dialog, shrinkClicks, cancellationToken);
        for (var round = 0; round < 8; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dialog = await FindDialogAsync(page);
            if (dialog is null) return;
            if (!await ClickConfirmAsync(dialog))
            {
                await page.WaitForTimeoutAsync(300);
                continue;
            }

            if (await WaitClosedAsync(page, cancellationToken)) return;
            dialog = await FindDialogAsync(page);
            if (dialog is null) return;
            await ShrinkAsync(dialog, 4, cancellationToken);
        }

        if (await FindDialogAsync(page) is not null)
            throw new InvalidOperationException("图片裁剪弹窗已出现，但确认按钮点击后弹窗仍未关闭。");
    }

    private static async Task<ILocator?> WaitForDialogAsync(
        IPage page,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = await FindDialogAsync(page);
            if (dialog is not null) return dialog;
            await page.WaitForTimeoutAsync(300);
        }

        return null;
    }

    private static async Task<bool> WaitClosedAsync(IPage page, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await FindDialogAsync(page) is null) return true;
            await page.WaitForTimeoutAsync(250);
        }

        return false;
    }

    private static async Task<ILocator?> FindDialogAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            ILocator? chosen = null;
            var chosenArea = 0d;
            ILocator dialogs;
            int count;
            try
            {
                dialogs = frame.Locator(".ks-dialog, [role='dialog'], .ant-modal, .ks-overlay");
                count = await dialogs.CountAsync();
            }
            catch (PlaywrightException)
            {
                continue;
            }

            for (var index = 0; index < count; index++)
            {
                var dialog = dialogs.Nth(index);
                try
                {
                    if (!await dialog.IsVisibleAsync()) continue;
                    var box = await dialog.BoundingBoxAsync();
                    if (box is null || box.Y < 0 || box.Width < 80 || box.Height < 80) continue;
                    var text = await dialog.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1_000 });
                    var hasControls = await dialog.Locator(".handle-img, .cropper-container, i.sys-icon-subtract").CountAsync() > 0;
                    if (!text.Contains("图片剪裁", StringComparison.Ordinal) &&
                        !text.Contains("图片裁剪", StringComparison.Ordinal) &&
                        !hasControls)
                        continue;
                    var area = box.Width * box.Height;
                    if (area <= chosenArea) continue;
                    chosen = dialog;
                    chosenArea = area;
                }
                catch (PlaywrightException)
                {
                    // 弹窗正在关闭或框架正在跳转。
                }
            }

            if (chosen is not null) return chosen;
        }

        return null;
    }

    private static async Task ShrinkAsync(ILocator dialog, int clicks, CancellationToken cancellationToken)
    {
        for (var index = 0; index < clicks; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ClickMinusAsync(dialog)) return;
            await dialog.Page.WaitForTimeoutAsync(180);
        }
    }

    private static async Task<bool> ClickMinusAsync(ILocator dialog)
    {
        var button = dialog.Locator(
            ".handle-img button:has(i.sys-icon-subtract), button:has(i.sys-icon-subtract)").Last;
        try
        {
            if (await button.CountAsync() > 0 && await button.IsVisibleAsync())
            {
                await button.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                return true;
            }
        }
        catch (PlaywrightException)
        {
            // 画布挡住指针时改走按钮自身的 click。
        }

        try
        {
            return await dialog.EvaluateAsync<bool>(
                """
                (el) => {
                  const icon = el.querySelector('.handle-img i.sys-icon-subtract, i.sys-icon-subtract');
                  const button = icon ? icon.closest('button') : null;
                  if (!button) return false;
                  button.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> ClickConfirmAsync(ILocator dialog)
    {
        var button = dialog.Locator(
            ".ks-dialog__footer button.ks-button--primary:not(.is-disabled), .dialog-footer button.ks-button--primary:not(.is-disabled)").Last;
        try
        {
            if (await button.CountAsync() > 0 && await button.IsVisibleAsync())
            {
                await button.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                return true;
            }
        }
        catch (PlaywrightException)
        {
            // 裁剪画布挡住指针时改走按钮自身的 click。
        }

        try
        {
            return await dialog.EvaluateAsync<bool>(
                """
                (el) => {
                  const visible = element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
                  };
                  const buttons = Array.from(el.querySelectorAll('.ks-dialog__footer .ks-button--primary, .dialog-footer .ks-button--primary, button'))
                    .filter(button => visible(button)
                      && !button.classList.contains('is-disabled')
                      && (button.innerText || '').replace(/\s+/g, '') === '确认');
                  const button = buttons[buttons.length - 1];
                  if (!button) return false;
                  button.click();
                  return true;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }
}
