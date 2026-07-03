using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public sealed record TikTokLoginResult(
    string Email,
    string AuthPath,
    string CurrentUrl,
    bool AlreadyLoggedIn,
    string LoggedInAt);

/// <summary>Playwright 自动登录 TikTok 短剧中心（对齐 Python <c>login_service.py</c>）。</summary>
public static class TikTokLoginService
{
    private static readonly Regex CdpEndpointPattern = new(
        @"(wss?://[^\s""'<>]+|https?://[^\s""'<>]+:\d+[^\s""'<>]*|(?:localhost|127\.0\.0\.1|\d{1,3}(?:\.\d{1,3}){3}|[a-zA-Z0-9.-]+):\d+(?:/[^\s""'<>]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<TikTokLoginResult> LoginAsync(
        TikTokAccountProfile account,
        Action<string>? log,
        CancellationToken ct,
        int timeoutSeconds = 90)
    {
        var email = (account.TiktokLoginEmail ?? "").Trim();
        if (string.IsNullOrEmpty(email))
            throw new InvalidOperationException("请先填写 TikTok 登录邮箱。");

        var authPath = ResolveAuthPath(account);
        Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);

        if (IsCdpMode(account))
            return await LoginWithCdpAsync(account, email, authPath, log, ct, Math.Max(120, timeoutSeconds));

        return await LoginWithLaunchAsync(account, email, authPath, log, ct, Math.Max(15, timeoutSeconds));
    }

    private static async Task<TikTokLoginResult> LoginWithLaunchAsync(
        TikTokAccountProfile account,
        string email,
        string authPath,
        Action<string>? log,
        CancellationToken ct,
        int timeoutSeconds)
    {
        log?.Invoke("正在打开 TikTok Drama Center 登录页…");
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = BuildLaunchArgs(),
            });

            try
            {
                var contextOptions = new BrowserNewContextOptions
                {
                    Locale = "zh-CN",
                    ViewportSize = ViewportSize.NoViewport,
                };
                ApplyProxy(contextOptions, account, log);
                if (File.Exists(authPath))
                {
                    contextOptions.StorageStatePath = authPath;
                    log?.Invoke($"已复用 TikTok 登录态文件：{authPath}");
                }

                var context = await browser.NewContextAsync(contextOptions);
                try
                {
                    var page = await context.NewPageAsync();
                    await page.GotoAsync(TikTokUrls.DefaultLoginUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60000,
                    });
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 12000 }); }
                    catch { /* SPA */ }

                    ct.ThrowIfCancellationRequested();
                    if (await IsLoggedInAsync(page))
                    {
                        await context.StorageStateAsync(new() { Path = authPath });
                        log?.Invoke("TikTok 已处于登录状态，登录态已保存。");
                        return BuildResult(email, authPath, page, alreadyLoggedIn: true);
                    }

                    var password = account.TiktokLoginPassword ?? "";
                    if (!string.IsNullOrEmpty(password))
                        await PerformEmailPasswordLoginAsync(page, email, password, log, ct);
                    else
                    {
                        await PrefillLoginEmailAsync(page, email, log);
                        log?.Invoke("当前账号未配置密码，请在外部浏览器中手动完成 TikTok 登录。");
                    }

                    await WaitForLoginSuccessAsync(page, timeoutSeconds, log, ct);
                    await context.StorageStateAsync(new() { Path = authPath });
                    log?.Invoke($"TikTok 登录成功，登录态已保存：{authPath}");
                    return BuildResult(email, authPath, page, alreadyLoggedIn: false);
                }
                finally
                {
                    await context.CloseAsync();
                }
            }
            finally
            {
                await browser.CloseAsync();
            }
        }
        finally
        {
            playwright.Dispose();
        }
    }

    private static async Task<TikTokLoginResult> LoginWithCdpAsync(
        TikTokAccountProfile account,
        string email,
        string authPath,
        Action<string>? log,
        CancellationToken ct,
        int timeoutSeconds)
    {
        var endpoint = ResolveCdpEndpoint(account, log);
        log?.Invoke($"正在连接指纹浏览器 CDP：{endpoint}");

        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint);
            var context = browser.Contexts.FirstOrDefault() ?? await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "zh-CN",
                ViewportSize = ViewportSize.NoViewport,
            });
            var page = context.Pages.FirstOrDefault(p => !p.IsClosed) ?? await context.NewPageAsync();

            await page.GotoAsync(TikTokUrls.DefaultLoginUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000,
            });
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 12000 }); }
            catch { /* SPA */ }

            ct.ThrowIfCancellationRequested();
            if (await IsLoggedInAsync(page))
            {
                await context.StorageStateAsync(new() { Path = authPath });
                log?.Invoke("指纹浏览器已处于 TikTok 登录状态，登录态已保存。");
                return BuildResult(email, authPath, page, alreadyLoggedIn: true);
            }

            var password = account.TiktokLoginPassword ?? "";
            if (!string.IsNullOrEmpty(password))
            {
                try
                {
                    await PerformEmailPasswordLoginAsync(page, email, password, log, ct);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"自动填写失败，请在指纹浏览器中手动完成登录：{ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                log?.Invoke("当前账号未配置密码，请在指纹浏览器中手动完成 TikTok 登录。");
            }

            await WaitForLoginSuccessAsync(page, timeoutSeconds, log, ct);
            await context.StorageStateAsync(new() { Path = authPath });
            log?.Invoke($"TikTok 登录成功，已从指纹浏览器保存登录态：{authPath}");
            return BuildResult(email, authPath, page, alreadyLoggedIn: false);
        }
        finally
        {
            playwright.Dispose();
        }
    }

    private static async Task PerformEmailPasswordLoginAsync(
        IPage page,
        string email,
        string password,
        Action<string>? log,
        CancellationToken ct)
    {
        await page.Locator("#email").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
        await page.Locator("#email").FillAsync(email);
        await SelectPasswordLoginModeAsync(page, log);
        await page.Locator("#password").FillAsync(password);
        await CheckTermsCheckboxAsync(page);

        var button = page.Locator("button").Filter(new() { HasText = "登录" }).First;
        if (await button.CountAsync() == 0)
            button = page.Locator("button").Nth(1);

        log?.Invoke("已填写邮箱和密码，正在提交 TikTok 登录。");
        await button.ClickAsync(new() { Timeout = 10000 });
        ct.ThrowIfCancellationRequested();
    }

    private static async Task PrefillLoginEmailAsync(IPage page, string email, Action<string>? log)
    {
        try
        {
            await page.Locator("#email").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await page.Locator("#email").FillAsync(email);
        }
        catch (Exception ex)
        {
            log?.Invoke($"自动填写 TikTok 用户名失败，请手动输入：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task SelectPasswordLoginModeAsync(IPage page, Action<string>? log)
    {
        if (await IsVisibleAsync(page.Locator("#password")))
            return;

        var methodSelect = page.Locator(".semi-select").First;
        await methodSelect.ClickAsync(new() { Timeout = 10000 });
        await page.Locator("[role=\"option\"]").First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });

        var passwordOption = page.Locator("[role=\"option\"]").Filter(new() { HasText = "密码" }).Last;
        if (await passwordOption.CountAsync() > 0)
            await passwordOption.ClickAsync(new() { Timeout = 10000 });
        else
            await page.Locator("[role=\"option\"]").Nth(1).ClickAsync(new() { Timeout = 10000 });

        await page.Locator("#password").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        log?.Invoke("已切换到密码登录方式。");
    }

    private static async Task CheckTermsCheckboxAsync(IPage page)
    {
        var checkbox = page.Locator("input[type=\"checkbox\"]").First;
        if (await checkbox.CountAsync() == 0) return;
        try
        {
            if (!await checkbox.IsCheckedAsync())
                await checkbox.CheckAsync(new() { Force = true });
        }
        catch
        {
            await checkbox.ClickAsync(new() { Force = true });
        }
    }

    private static async Task WaitForLoginSuccessAsync(
        IPage page,
        int timeoutSeconds,
        Action<string>? log,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var lastMessage = "";
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsLoggedInAsync(page)) return;

            var message = await ExtractLoginMessageAsync(page);
            if (!string.IsNullOrWhiteSpace(message) && message != lastMessage)
            {
                lastMessage = message;
                log?.Invoke($"TikTok 登录提示：{message}");
            }
            await Task.Delay(500, ct);
        }

        var suffix = string.IsNullOrWhiteSpace(lastMessage) ? "" : $" 页面提示：{lastMessage}";
        throw new TimeoutException($"TikTok 登录超时，请确认账号密码或页面是否需要额外验证。{suffix}");
    }

    private static async Task<bool> IsLoggedInAsync(IPage page)
    {
        var currentUrl = (page.Url ?? "").ToLowerInvariant();
        return currentUrl.Contains("tiktokdramacenter.com") && !currentUrl.Contains("/login");
    }

    private static async Task<string> ExtractLoginMessageAsync(IPage page)
    {
        var selectors = new[] { ".semi-toast", ".semi-toast-content", ".semi-form-field-error-message", ".semi-alert", "[role='alert']" };
        var chunks = new List<string>();
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            var count = Math.Min(await locator.CountAsync(), 5);
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var text = (await locator.Nth(i).InnerTextAsync(new() { Timeout = 300 })).Trim();
                    if (!string.IsNullOrEmpty(text)) chunks.Add(text);
                }
                catch { /* ignore */ }
            }
        }
        return string.Join("；", chunks.Distinct());
    }

    private static async Task<bool> IsVisibleAsync(ILocator locator)
    {
        try
        {
            return await locator.CountAsync() > 0 && await locator.First.IsVisibleAsync(new() { Timeout = 500 });
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyProxy(BrowserNewContextOptions options, TikTokAccountProfile account, Action<string>? log)
    {
        var proxy = TikTokProxyHelper.BuildFromAccount(account);
        if (proxy is null) return;
        options.Proxy = new Proxy
        {
            Server = proxy.Server,
            Username = string.IsNullOrWhiteSpace(proxy.Username) ? null : proxy.Username,
            Password = string.IsNullOrWhiteSpace(proxy.Password) ? null : proxy.Password,
        };
        log?.Invoke($"已启用 TikTok 账号代理：{proxy.Description}");
    }

    private static string ResolveAuthPath(TikTokAccountProfile account)
    {
        var explicitPath = (account.TiktokStorageStatePath ?? "").Trim();
        if (!string.IsNullOrEmpty(explicitPath))
        {
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath)); }
            catch { return explicitPath; }
        }
        return AppPaths.DefaultStorageStatePath(account.Id);
    }

    private static bool IsCdpMode(TikTokAccountProfile account) =>
        string.Equals(account.TiktokLoginBrowserMode, "cdp", StringComparison.OrdinalIgnoreCase);

    private static string ResolveCdpEndpoint(TikTokAccountProfile account, Action<string>? log)
    {
        var endpoint = (account.TiktokFingerprintBrowserCdpEndpoint ?? "").Trim();
        var startCommand = (account.TiktokFingerprintStartCommand ?? "").Trim();
        if (!string.IsNullOrEmpty(startCommand))
        {
            log?.Invoke("正在执行指纹浏览器启动命令…");
            var output = RunStartCommand(startCommand);
            var detected = ExtractCdpEndpoint(output);
            if (!string.IsNullOrEmpty(detected))
            {
                endpoint = detected;
                log?.Invoke($"已从启动输出识别 CDP 地址：{endpoint}");
            }
            else if (string.IsNullOrEmpty(endpoint))
            {
                throw new InvalidOperationException("指纹浏览器启动命令未返回 CDP 地址，请在网络/IP 中填写 CDP 地址。");
            }
        }

        var normalized = NormalizeCdpEndpoint(endpoint);
        if (string.IsNullOrEmpty(normalized))
            throw new InvalidOperationException("请在账号网络/IP 中填写指纹浏览器 CDP 地址，或填写能返回 CDP 地址的启动命令。");
        return normalized;
    }

    private static string RunStartCommand(string command)
    {
        if (command.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            return client.GetStringAsync(command).GetAwaiter().GetResult();
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动指纹浏览器命令。");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(45000);
        return string.Join('\n', new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string ExtractCdpEndpoint(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var match = CdpEndpointPattern.Match(text);
        return match.Success ? NormalizeCdpEndpoint(match.Groups[1].Value) : "";
    }

    private static string NormalizeCdpEndpoint(string value)
    {
        var text = (value ?? "").Trim().Trim('"', '\'', '<', '>', '，', ',', ';');
        if (string.IsNullOrEmpty(text)) return "";
        var lowered = text.ToLowerInvariant();
        if (lowered.StartsWith("ws://") || lowered.StartsWith("wss://") || lowered.StartsWith("http://") || lowered.StartsWith("https://"))
            return text;
        if (Regex.IsMatch(text, @"^(?:localhost|127\.0\.0\.1|\d{1,3}(?:\.\d{1,3}){3}|[a-zA-Z0-9.-]+):\d+", RegexOptions.IgnoreCase))
            return $"http://{text}";
        return "";
    }

    private static IReadOnlyList<string> BuildLaunchArgs() =>
    [
        "--disable-blink-features=AutomationControlled",
        "--no-sandbox",
        "--start-maximized",
    ];

    private static TikTokLoginResult BuildResult(string email, string authPath, IPage page, bool alreadyLoggedIn) =>
        new(
            Email: email,
            AuthPath: authPath,
            CurrentUrl: page.Url ?? "",
            AlreadyLoggedIn: alreadyLoggedIn,
            LoggedInAt: DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
}
