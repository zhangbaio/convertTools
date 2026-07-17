using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Controls;
using TikTokPublisher.Ui.Services;

namespace TikTokUploadHeadedSpike;

internal static class EmbeddedSpikeRunner
{
    public static async Task RunAsync(
        WebView2Host host,
        SpikeHostContext ctx,
        Action<string> setStatus,
        CancellationToken ct = default)
    {
        void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Console.WriteLine(line);
            File.AppendAllText(ctx.LogPath, line + Environment.NewLine);
            setStatus(msg);
        }

        var opts = ctx.Options;
        var outDir = Path.Combine(AppContext.BaseDirectory, "embedded-run");
        Directory.CreateDirectory(outDir);

        var homeUrl = EmbeddedBrowserLoginHelper.ResolveHomeUrl(ctx.Account);
        var draftUrl = string.IsNullOrWhiteSpace(ctx.Account.TiktokSeriesUrl)
            ? TikTokUrls.DefaultSeriesDraftUrl
            : ctx.Account.TiktokSeriesUrl.Trim();
        Log($"内置浏览器打开：{homeUrl}");
        await host.NavigateAsync(homeUrl).ConfigureAwait(false);
        await Task.Delay(2500, ct).ConfigureAwait(false);

        if (!await EnsureLoggedInAsync(host, ctx, draftUrl, Log, ct).ConfigureAwait(false))
            throw new InvalidOperationException("账号未登录（请先在内置浏览器完成 TikTok 登录）");

        if (opts.Mode == "dom")
        {
            await RunDomProbeAsync(host, ctx, outDir, Log, ct).ConfigureAwait(false);
            return;
        }

        if (opts.Mode == "edit")
            Log("编辑模式：EmbeddedBrowserPublishAutomation（内置 WebView2 + 视频补传）");
        else
            Log("新建模式：EmbeddedBrowserPublishAutomation（内置 WebView2 + 视频上传）");

        await using var automation = new EmbeddedBrowserPublishAutomation();
        var result = await automation.PublishAsync(
            ctx.Account,
            ctx.Item,
            host,
            opts.FinalAction,
            Log,
            ct).ConfigureAwait(false);

        if (!result.Ok)
            throw new InvalidOperationException(result.Message);

        Log($"✅ {result.Message}");
        await DumpFinishedPageAsync(host, outDir, Log, ct).ConfigureAwait(false);
        Log($"日志与截图：{outDir}");
    }

    private static async Task RunDomProbeAsync(
        WebView2Host host,
        SpikeHostContext ctx,
        string outDir,
        Action<string> log,
        CancellationToken ct)
    {
        var targetUrl = string.IsNullOrWhiteSpace(ctx.Account.TiktokSeriesUrl)
            ? TikTokUrls.DefaultSeriesDraftUrl
            : ctx.Account.TiktokSeriesUrl.Trim();
        log($"dom 模式导航：{targetUrl}");
        await host.NavigateAsync(targetUrl).ConfigureAwait(false);
        await Task.Delay(2000, ct).ConfigureAwait(false);

        if (!await EnsureLoggedInAsync(host, ctx, targetUrl, log, ct).ConfigureAwait(false))
            throw new InvalidOperationException("账号未登录");

        Microsoft.Playwright.IPlaywright? pw = null;
        Microsoft.Playwright.IBrowser? chromium = null;
        try
        {
            (pw, chromium, var page) = await EmbeddedBrowserAutomationBridge
                .ConnectPageAsync(host, null, log, ct)
                .ConfigureAwait(false);
            log("dom 模式：导出页面结构");
            await DumpDomInventoryAsync(page, outDir, log).ConfigureAwait(false);
            log($"输出目录：{outDir}");
        }
        finally
        {
            try { await (chromium?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false); }
            catch { /* disconnect CDP only */ }
            pw?.Dispose();
        }
    }

    private static async Task<bool> EnsureLoggedInAsync(
        WebView2Host host,
        SpikeHostContext ctx,
        string verifyUrl,
        Action<string> log,
        CancellationToken ct)
    {
        await host.NavigateAsync(verifyUrl).ConfigureAwait(false);
        await Task.Delay(2500, ct).ConfigureAwait(false);

        var currentUrl = await WaitForCurrentUrlAsync(host, ct).ConfigureAwait(false);
        if (!EmbeddedBrowserLoginHelper.IsLoginUrl(currentUrl))
            return true;

        log("草稿页需要登录，尝试从 storage_state 导入会话…");
        await TryImportStorageStateAsync(host, ctx, log, ct).ConfigureAwait(false);

        await host.NavigateAsync(verifyUrl).ConfigureAwait(false);
        await Task.Delay(2500, ct).ConfigureAwait(false);
        currentUrl = await WaitForCurrentUrlAsync(host, ct).ConfigureAwait(false);
        if (!EmbeddedBrowserLoginHelper.IsLoginUrl(currentUrl))
            return true;

        log("仍处登录页：请在内置浏览器窗口手动登录，等待最多 5 分钟…");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            currentUrl = await WaitForCurrentUrlAsync(host, ct).ConfigureAwait(false);
            if (!EmbeddedBrowserLoginHelper.IsLoginUrl(currentUrl))
                return true;
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }

        currentUrl = await WaitForCurrentUrlAsync(host, ct).ConfigureAwait(false);
        return !EmbeddedBrowserLoginHelper.IsLoginUrl(currentUrl);
    }

    private static async Task TryImportStorageStateAsync(
        WebView2Host host,
        SpikeHostContext ctx,
        Action<string> log,
        CancellationToken ct)
    {
        Microsoft.Playwright.IPlaywright? pw = null;
        Microsoft.Playwright.IBrowser? chromium = null;
        try
        {
            (pw, chromium, var page) = await EmbeddedBrowserAutomationBridge
                .ConnectPageAsync(host, null, log, ct)
                .ConfigureAwait(false);
            await EmbeddedStorageStateImporter.TryImportAsync(page.Context, page, ctx.AuthPath, log, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            try { await (chromium?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false); }
            catch { /* ignore */ }
            pw?.Dispose();
        }
    }

    private static async Task<string?> WaitForCurrentUrlAsync(WebView2Host host, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var url = host.CurrentUrl;
            if (!string.IsNullOrWhiteSpace(url))
                return url;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return host.CurrentUrl;
    }

    private static async Task DumpFinishedPageAsync(
        WebView2Host host,
        string outDir,
        Action<string> log,
        CancellationToken ct)
    {
        Microsoft.Playwright.IPlaywright? pw = null;
        Microsoft.Playwright.IBrowser? chromium = null;
        try
        {
            (pw, chromium, var page) = await EmbeddedBrowserAutomationBridge
                .ConnectPageAsync(host, null, log, ct)
                .ConfigureAwait(false);
            await DumpAsync(page, outDir, "99-finished", log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log($"完成后截图失败：{ex.Message}");
        }
        finally
        {
            try { await (chromium?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false); }
            catch { /* ignore */ }
            pw?.Dispose();
        }
    }

    private static async Task DumpAsync(Microsoft.Playwright.IPage page, string outDir, string tag, Action<string> log)
    {
        var prefix = Path.Combine(outDir, tag);
        try
        {
            await page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions
            {
                Path = prefix + ".png",
                FullPage = true,
            }).ConfigureAwait(false);
            log($"截图：{tag}.png");
        }
        catch (Exception ex) { log($"截图失败 {tag}: {ex.Message}"); }

        try
        {
            var body = await page.Locator("body").InnerTextAsync(new() { Timeout = 10000 }).ConfigureAwait(false);
            await File.WriteAllTextAsync(prefix + "-body.txt", body).ConfigureAwait(false);
            log($"正文：{tag}-body.txt ({body.Length} 字符)");
        }
        catch (Exception ex) { log($"正文抓取失败 {tag}: {ex.Message}"); }
    }

    private static async Task DumpDomInventoryAsync(Microsoft.Playwright.IPage page, string outDir, Action<string> log)
    {
        var json = await page.EvaluateAsync<string>(
            """
            () => {
              const pick = (sel) => Array.from(document.querySelectorAll(sel)).slice(0, 30).map((el, i) => ({
                i,
                tag: el.tagName,
                id: el.id || '',
                class: (el.className || '').toString().slice(0, 120),
                role: el.getAttribute('role') || '',
                text: (el.innerText || '').trim().slice(0, 80),
                accept: el.getAttribute('accept') || '',
              }));
              return JSON.stringify({
                url: location.href,
                title: document.title,
                inputs: pick('input'),
                buttons: pick('button'),
                selects: pick('.semi-select'),
                uploads: pick('.semi-upload, .semi-upload-picture-add, input[type=file]'),
                fields: pick('#title, #description, #totalVideoNum, #anchorPromotionStatus'),
              }, null, 2);
            }
            """).ConfigureAwait(false);
        var path = Path.Combine(outDir, "dom-inventory.json");
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        log($"DOM 清单：{path}");
        await DumpAsync(page, outDir, "dom", log).ConfigureAwait(false);
    }
}
