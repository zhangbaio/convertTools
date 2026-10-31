using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PlatformPublisher.Kuaishou.Publishing;

public static class KuaishouPageNotice
{
    public static bool IsBlockingNotice(string? text)
    {
        var compact = Compact(text);
        if (compact.Contains("图片裁剪") || compact.Contains("添加单集") || compact.Contains("批量设置") || compact.Contains("选择集数"))
            return false;
        return compact.Contains("下架");
    }

    public static bool IsNoticeCloseLabel(string? label)
    {
        var compact = Compact(label);
        return compact.StartsWith("关闭", StringComparison.Ordinal) || compact is "我知道了" or "知道了";
    }

    public static async Task<int> DismissAsync(IPage page, CancellationToken cancellationToken)
    {
        var dismissed = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clicked = false;
            var waitingForCountdown = false;
            foreach (var frame in page.Frames)
            {
                var result = await TryDismissAsync(frame);
                if (result == DismissResult.Clicked)
                {
                    dismissed++;
                    clicked = true;
                    break;
                }

                waitingForCountdown |= result == DismissResult.Waiting;
            }

            if (clicked)
            {
                await page.WaitForTimeoutAsync(300);
                continue;
            }

            if (!waitingForCountdown)
                break;
            await page.WaitForTimeoutAsync(400);
        }

        return dismissed;
    }

    private static async Task<DismissResult> TryDismissAsync(IFrame frame)
    {
        ILocator dialogs;
        int count;
        try
        {
            dialogs = frame.Locator(".ant-modal-wrap, .ant-modal, [role=dialog]");
            count = await dialogs.CountAsync();
        }
        catch (PlaywrightException)
        {
            return DismissResult.None;
        }

        for (var index = count - 1; index >= 0; index--)
        {
            var dialog = dialogs.Nth(index);
            string text;
            try
            {
                if (!await dialog.IsVisibleAsync())
                    continue;
                text = await dialog.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1000 });
            }
            catch (PlaywrightException)
            {
                continue;
            }

            if (!IsBlockingNotice(text))
                continue;

            var buttons = dialog.Locator("button, [role=button]");
            var buttonCount = await buttons.CountAsync();
            var sawDisabledClose = false;
            for (var buttonIndex = 0; buttonIndex < buttonCount; buttonIndex++)
            {
                var button = buttons.Nth(buttonIndex);
                string label;
                try
                {
                    if (!await button.IsVisibleAsync())
                        continue;
                    label = await button.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1000 });
                }
                catch (PlaywrightException)
                {
                    continue;
                }

                if (!IsNoticeCloseLabel(label))
                    continue;
                if (!await button.IsEnabledAsync())
                {
                    sawDisabledClose = true;
                    continue;
                }

                await button.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                return DismissResult.Clicked;
            }

            if (sawDisabledClose)
                return DismissResult.Waiting;
        }

        return DismissResult.None;
    }

    private static string Compact(string? value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", "");

    private enum DismissResult
    {
        None,
        Waiting,
        Clicked,
    }
}
