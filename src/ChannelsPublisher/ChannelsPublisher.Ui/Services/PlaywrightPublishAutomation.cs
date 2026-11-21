using System.Diagnostics;
using ChannelsPublisher.Core.Publishing;
using Microsoft.Playwright;

namespace ChannelsPublisher.Desktop.Services;

/// <summary>经 CDP 连接账号的内嵌 WebView2，跑 P1 已验证的视频号发表流程。
/// 选择器与时序取自 spike（ChannelsUploadCdpSpike 的 real/fill/extra 模式，均真机验证过）。</summary>
public sealed class PlaywrightPublishAutomation : IPublishAutomation, IAsyncDisposable
{
    private const string PostCreateUrl = "https://channels.weixin.qq.com/platform/post/create";
    private readonly Lazy<Task<IPlaywright>> _playwright = new(Playwright.CreateAsync);

    private Task<IPlaywright> PwAsync() => _playwright.Value;

    public async Task<bool> TrySaveVideoChannelSessionAsync(string endpoint, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var playwright = await PwAsync();
        await using var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint, new() { Timeout = 10_000 });
        var context = browser.Contexts.FirstOrDefault();
        if (context is null || !context.Pages.Any(page => Uri.TryCreate(page.Url, UriKind.Absolute, out var url)
            && IsVideoChannelWorkspace(url)))
            return false;

        var cookies = await context.CookiesAsync(new[] { "https://channels.weixin.qq.com" });
        if (cookies.Count == 0)
            return false;
        var state = await context.StorageStateAsync();
        ct.ThrowIfCancellationRequested();
        var temporaryPath = destination + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, state, ct);
        File.Move(temporaryPath, destination, overwrite: true);
        return true;
    }

    internal static bool IsVideoChannelWorkspace(Uri location) =>
        location.Scheme == Uri.UriSchemeHttps && location.Host == "channels.weixin.qq.com" &&
        location.AbsolutePath.StartsWith("/platform", StringComparison.OrdinalIgnoreCase) &&
        !location.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase) &&
        !location.AbsolutePath.Contains("passport", StringComparison.OrdinalIgnoreCase);

    public async Task<PublishResult> PublishAsync(
        PublishItem item, string cdpEndpoint, FinalAction finalAction, Action<string>? log, CancellationToken ct,
        Action? beforeSubmit = null)
    {
        void L(string m) => log?.Invoke(m);

        if (!File.Exists(item.VideoPath))
            return PublishResult.Fail($"视频不存在：{item.VideoPath}");

        ct.ThrowIfCancellationRequested();
        var pw = await PwAsync();
        await using var browser = await pw.Chromium.ConnectOverCDPAsync(cdpEndpoint, new() { Timeout = 15_000 });
        using var cancelBrowser = ct.Register(() => _ = DisconnectAsync(browser));
        ct.ThrowIfCancellationRequested();
        var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync();
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

        L("进入发表页…");
        await page.GotoAsync(PostCreateUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 20000 }); } catch { }
        await page.WaitForTimeoutAsync(6000); // wujie 微前端懒加载
        if (page.Url.Contains("/login"))
            return PublishResult.Fail("账号未登录（请先在该账号扫码登录）");

        // ① 上传视频（隐藏的 input[type=file][accept*=video]）
        L($"上传视频 {item.DisplayName}…");
        var fileInput = page.Locator("input[type=file][accept*='video']").First;
        await fileInput.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 });
        await fileInput.SetInputFilesAsync(item.VideoPath);
        try { await page.GetByText("删除").First.WaitForAsync(new() { Timeout = 180000 }); } catch { }
        await page.WaitForTimeoutAsync(1500);
        ct.ThrowIfCancellationRequested();

        // ② 描述（contenteditable）
        if (!string.IsNullOrWhiteSpace(item.Description))
            await TryAsync(L, "描述", async () => await page.Locator("div.input-editor").First.FillAsync(item.Description));

        // ③ 短标题
        if (!string.IsNullOrWhiteSpace(item.ShortTitle))
            await TryAsync(L, "短标题", async () => await page.Locator("input[placeholder*='填写短标题']").First.FillAsync(item.ShortTitle));

        // ④ 封面（编辑按钮被 img 遮挡 → Force；弹框 → 上传封面 → setInputFiles → 确认）
        if (!string.IsNullOrWhiteSpace(item.CoverPath) && File.Exists(item.CoverPath))
            await TryAsync(L, "封面", async () =>
            {
                await page.Locator(".cover-preview-wrap .edit-btn").First.ClickAsync(new() { Force = true, Timeout = 5000 });
                await page.WaitForTimeoutAsync(1200);
                await ClickFirstAsync(page, "text=上传封面", "text=本地上传", "text=更换封面");
                await page.WaitForTimeoutAsync(600);
                await page.Locator("input[type=file][accept*='image']").Last.SetInputFilesAsync(item.CoverPath!);
                await page.WaitForTimeoutAsync(2000);
                await ClickFirstAsync(page, "button:has-text('确认')", "button:has-text('确定')", "button:has-text('完成')");
            });

        // ⑤ 挂载视频号剧集（链接→视频号剧集→选择剧集→搜新剧名→点可见结果）
        if (!string.IsNullOrWhiteSpace(item.DramaName))
            await TryAsync(L, "挂载剧集", async () =>
            {
                await ClickFirstAsync(page, ".post-with-link", ".link-placeholder");
                await page.WaitForTimeoutAsync(1000);
                await ClickFirstAsync(page, "text=视频号剧集");
                await page.WaitForTimeoutAsync(1000);
                await ClickFirstAsync(page, "text=选择需要关联的剧集", "text=选择需要添加的剧集", "text=选择需要关联", "text=选择需要添加");
                await page.WaitForTimeoutAsync(1000);
                var search = page.Locator("input[placeholder*='搜索内容']:visible").First;
                await search.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
                await search.FillAsync(item.DramaName!);
                await page.WaitForTimeoutAsync(3500);
                var matches = await page.GetByText(item.DramaName!, new() { Exact = false }).AllAsync();
                foreach (var m in matches)
                {
                    if (!await m.IsVisibleAsync()) continue;
                    await m.ClickAsync(new() { Timeout = 5000 });
                    await page.WaitForTimeoutAsync(800);
                    await ClickFirstAsync(page, "button:has-text('确定')", "button:has-text('确认')", "button:has-text('完成')");
                    return;
                }
                throw new Exception($"未搜到剧集「{item.DramaName}」");
            });

        // ⑥ 原创声明（勾选框→对话框→勾同意→声明原创）
        if (item.DeclareOriginal)
            await TryAsync(L, "原创声明", async () =>
            {
                await ClickFirstAsync(page, ".declare-original-checkbox label", ".declare-original-checkbox");
                await page.WaitForTimeoutAsync(1200);
                await ClickFirstAsync(page, ".weui-desktop-dialog .ant-checkbox", "text=我已阅读并同意");
                await page.WaitForTimeoutAsync(500);
                await ClickFirstAsync(page, ".weui-desktop-dialog button:has-text('声明原创')", "button:has-text('声明原创')");
            });

        ct.ThrowIfCancellationRequested();

        // ⑦ 结束动作
        switch (finalAction)
        {
            case FinalAction.Draft:
                return await SubmitAsync(page, "保存草稿", "已保存草稿", ["保存成功", "草稿保存成功", "已保存草稿"], beforeSubmit, ct);
            case FinalAction.Publish:
                return await SubmitAsync(page, "发表", "已发表", ["发表成功", "发布成功", "已发表"], beforeSubmit, ct);
            default:
                L("已填表（未点发表/保存草稿）");
                return PublishResult.Success("已填表（未提交）");
        }
    }

    private static async Task<PublishResult> SubmitAsync(
        IPage page, string buttonText, string successMessage, string[] confirmationTexts,
        Action? beforeSubmit, CancellationToken ct)
    {
        var button = page.GetByRole(AriaRole.Button, new() { NameString = buttonText, Exact = true }).First;
        await button.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
        if (!await button.IsEnabledAsync())
            return PublishResult.Fail($"“{buttonText}”按钮当前不可用");

        var notices = confirmationTexts.Select(text => page.GetByText(text, new() { Exact = true })
            .Filter(new() { Visible = true }).First).ToArray();
        foreach (var notice in notices)
        {
            if (await notice.IsVisibleAsync())
                return PublishResult.Fail("上一条提交提示尚未消失");
        }

        ct.ThrowIfCancellationRequested();
        beforeSubmit?.Invoke();
        ct.ThrowIfCancellationRequested();
        await button.ClickAsync(new() { Timeout = 5000 });

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(12))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var notice in notices)
            {
                if (await notice.IsVisibleAsync())
                    return PublishResult.Success(successMessage);
            }
            await Task.Delay(200, ct);
        }

        return PublishResult.Fail($"未收到平台的“{buttonText}”成功提示");
    }

    private static async Task TryAsync(Action<string> log, string name, Func<Task> action)
    {
        try { await action(); log($"✓ {name}"); }
        catch (Exception ex) { log($"⚠ {name} 失败：{ex.Message}"); }
    }

    private static async Task<bool> ClickFirstAsync(IPage page, params string[] selectors)
    {
        foreach (var s in selectors)
        {
            try
            {
                var loc = page.Locator(s).First;
                if (await loc.CountAsync() > 0 && await loc.IsVisibleAsync())
                {
                    await loc.ClickAsync(new() { Timeout = 5000 });
                    return true;
                }
            }
            catch { /* 试下一个候选选择器 */ }
        }
        return false;
    }

    private static async Task DisconnectAsync(IBrowser browser)
    {
        try { await browser.CloseAsync(); }
        catch (PlaywrightException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_playwright.IsValueCreated)
            (await _playwright.Value).Dispose();
    }
}
