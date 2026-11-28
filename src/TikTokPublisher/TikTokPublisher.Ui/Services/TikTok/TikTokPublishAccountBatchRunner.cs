using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.ViewModels;

namespace TikTokPublisher.Ui.Services.TikTok;

internal enum PublishAccountCreationState { Accepted, Retryable, Pending }

internal sealed record PublishAccountCreationResult(PublishAccountCreationState State, string Message);

internal sealed class TikTokPublishAccountBatchRunner(BrowserSessionHost browserHost, MainViewModel viewModel)
{
    public static string PublishingUrl(TikTokAccountProfile account) =>
        TikTokApiSites.Origin(account.TiktokApiSite) + "/accounts/publishing";

    public async Task<PublishAccountCreationResult> CreateAsync(
        TikTokAccountProfile account, PublishAccountMaterial material, CancellationToken ct)
    {
        TikTokPublishAccountMaterials.MaterialReservation? reservation = null;
        try
        {
            reservation = await Task.Run(() => TikTokPublishAccountMaterials.Reserve(material, account.Id), ct);
            var viewAccount = viewModel.Accounts.FirstOrDefault(item => ReferenceEquals(item.Model, account))
                ?? throw new InvalidOperationException("主账号已移除");
            if (viewModel.IsAccountQueueBusy(account.Id))
                throw new InvalidOperationException("该主账号的浏览器正用于队列任务，请在任务结束后重试");

            if (!await TikTokApiUploadService.VerifySavedLoginAsync(account, ct))
            {
                var login = await TikTokApiLoginService.TryLoginAsync(account, ct);
                if (login.State is null)
                    throw new InvalidOperationException(login.FailureReason + "，请先登录该主账号再重试");
                ct.ThrowIfCancellationRequested();
                var saved = TikTokApiLoginService.SaveVerifiedLogin(
                    EmbeddedBrowserLoginHelper.ResolveAuthPath(account), account.TiktokLoginEmail.Trim(), login.State);
                viewModel.HandleApiLoginSaved(viewAccount, saved);
            }

            var host = browserHost.GetOrCreateHost(viewAccount);
            using var readyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyTimeout.CancelAfter(TimeSpan.FromSeconds(25));
            if (!await browserHost.EnsureReadyAsync(account, readyTimeout.Token))
                throw new InvalidOperationException("内置浏览器未就绪");

            var response = await SubmitInBrowserAsync(host, account, material, reservation.BeginSubmission, ct);
            if (response.State == PublishAccountCreationState.Accepted)
                reservation.Complete(accepted: true, response.ReceiptId);
            else if (response.State == PublishAccountCreationState.Retryable)
                reservation.Complete(accepted: false, "");
            return new(response.State, response.Message);
        }
        catch (OperationCanceledException)
        {
            return reservation?.SubmissionStarted == true
                ? new(PublishAccountCreationState.Pending, "已开始提交，结果待核对")
                : new(PublishAccountCreationState.Retryable, ct.IsCancellationRequested ? "已停止，未提交" : "内置浏览器初始化超时");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or PlaywrightException or TimeoutException or JsonException or ArgumentException)
        {
            if (reservation?.SubmissionStarted == true)
                return new(PublishAccountCreationState.Pending, "提交结果或使用标记待核对，请查看平台记录");
            var message = ex is InvalidOperationException ? ex.Message : "无法完成平台表单或读取素材，请检查登录状态、素材和创建权限";
            return new(PublishAccountCreationState.Retryable, message);
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    private static async Task<(PublishAccountCreationState State, string Message, string ReceiptId)> SubmitInBrowserAsync(
        Controls.WebView2Host host, TikTokAccountProfile account, PublishAccountMaterial material, Action markSubmitting, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = host.AcquireAutomation();
        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectOverCDPAsync(
            host.CdpEndpoint ?? throw new InvalidOperationException("内置浏览器连接不可用"), new() { Timeout = 12000 });
        var context = browser.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("内置浏览器会话不可用");
        var page = await context.NewPageAsync();
        try
        {
            var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
            if (!await EmbeddedStorageStateImporter.TryImportAsync(context, page, authPath, log: null, ct,
                mapCookieDomain: PublishingCookieDomain).WaitAsync(ct))
                throw new InvalidOperationException("无法将该主账号的授权导入发布账号页面");

            page.SetDefaultTimeout(12000);
            var url = PublishingUrl(account);
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 }).WaitAsync(ct);
            if (EmbeddedBrowserLoginHelper.IsLoginUrl(page.Url))
                throw new InvalidOperationException("发布账号页要求重新登录");
            if (!IsPublishingPage(page.Url, url))
                throw new InvalidOperationException("平台未打开该账号的发布账号页");

            var create = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("创建新账号|^Create (a )?new account$", RegexOptions.IgnoreCase) });
            await create.First.ClickAsync().WaitAsync(ct);
            await page.GetByText(new Regex("^(自动批量创建|Automatic batch creation|Auto batch create)$", RegexOptions.IgnoreCase))
                .First.ClickAsync().WaitAsync(ct);
            await page.Locator("input[placeholder*='机构标准英文名称'], input[placeholder*='standard English' i]")
                .FillAsync(material.Nickname).WaitAsync(ct);
            var biography = page.Locator("textarea[placeholder*='账号简介'], textarea[placeholder*='account bio' i]");
            await biography.FillAsync(material.Biography).WaitAsync(ct);
            if (await biography.InputValueAsync().WaitAsync(ct) != material.Biography)
                throw new InvalidOperationException("平台表单未能完整填写简介，请检查长度与内容");
            await page.Locator("input[type=file][accept*='.png']").First.SetInputFilesAsync(material.AvatarPath).WaitAsync(ct);
            var confirm = page.Locator("button:visible").Filter(new() { HasTextRegex = new Regex("^\\s*(确认|Confirm)\\s*$", RegexOptions.IgnoreCase) }).Last;
            await confirm.WaitForAsync(new() { State = WaitForSelectorState.Visible }).WaitAsync(ct);
            if (!await confirm.IsEnabledAsync().WaitAsync(ct))
            {
                // The avatar upload can keep the button disabled until it finishes.
                await confirm.ClickAsync(new() { Trial = true, Timeout = 15000 }).WaitAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            if (!IsPublishingPage(page.Url, url))
                throw new InvalidOperationException("平台页面已离开发布账号页");
            var response = await page.RunAndWaitForResponseAsync(async () =>
            {
                ct.ThrowIfCancellationRequested();
                markSubmitting();
                await confirm.ClickAsync();
            }, response => IsSubmissionResponse(response, url, material.Nickname), new() { Timeout = 20000 }).WaitAsync(ct);

            if (!response.Ok)
                return (PublishAccountCreationState.Pending, $"平台返回 HTTP {response.Status}，请核对是否受理", "");
            return ParseSubmission(await response.TextAsync().WaitAsync(ct));
        }
        finally
        {
            try { await page.CloseAsync(); }
            catch (PlaywrightException) { }
        }
    }

    internal static string PublishingCookieDomain(string domain) =>
        string.Equals(domain, "tiktokdramacenter.com", StringComparison.OrdinalIgnoreCase)
            ? ".tiktokdramacenter.com"
            : domain;

    private static bool IsPublishingPage(string actual, string expected) =>
        Uri.TryCreate(actual, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Path) == expected;

    private static bool IsSubmissionResponse(IResponse response, string pageUrl, string nickname)
    {
        if (response.Request.Method != "POST" || !Uri.TryCreate(response.Url, UriKind.Absolute, out var uri) ||
            uri.GetLeftPart(UriPartial.Authority) != new Uri(pageUrl).GetLeftPart(UriPartial.Authority) ||
            uri.AbsolutePath != "/api/content-partner/institution/creator-hosting")
            return false;
        return MatchesSubmissionBody(response.Request.PostData, nickname);
    }

    internal static bool MatchesSubmissionBody(string? body, string nickname)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True &&
                (root.TryGetProperty("unique_name", out var name) || root.TryGetProperty("uniqueName", out name)) &&
                name.ValueKind == JsonValueKind.String && name.GetString() == nickname;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static (PublishAccountCreationState State, string Message, string ReceiptId) ParseSubmission(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) ||
            !int.TryParse(code.ToString(), out var number))
            return (PublishAccountCreationState.Pending, "平台未明确确认受理，请核对平台记录", "");
        if (number != 0)
            return (PublishAccountCreationState.Retryable, $"平台未受理（业务码 {number}）", "");
        var receiptId = "";
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            (data.TryGetProperty("request_id", out var id) || data.TryGetProperty("application_id", out id) ||
             data.TryGetProperty("requestId", out id) || data.TryGetProperty("applicationId", out id)) &&
            id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            receiptId = id.ToString();
        return (PublishAccountCreationState.Accepted, "平台已受理，后续状态以平台审核为准", receiptId);
    }
}
