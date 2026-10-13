using System.Text.Json;
using Microsoft.Playwright;
using PlatformPublisher.Adx.Storage;
using PlatformPublisher.Analytics.Services;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalAdxPublishService
{
    private const string ContentManagementUrl = "https://kdj.kuaishou.com/home/content/content-management?sellingStatus=0&pageNum=1&pageSize=10&auditStatus=3";
    private const string QueryListUrl = "https://kdj.kuaishou.com/rest/ad/miniSeries/product/pc/queryList";
    private readonly KuaishouPersonalSessionService _sessionService;
    private readonly KuaishouAdxBatchResolver _resolver;
    private readonly AdxBatchStore _batchStore;
    private readonly IAnalyticsActivitySink _analyticsSink;

    public KuaishouPersonalAdxPublishService(KuaishouPersonalSessionService sessionService,
        KuaishouAdxBatchResolver resolver, AdxBatchStore batchStore, IAnalyticsActivitySink analyticsSink)
    {
        _sessionService = sessionService;
        _resolver = resolver;
        _batchStore = batchStore;
        _analyticsSink = analyticsSink;
    }

    public async Task PublishAsync(PublishJob job, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var payload = KuaishouAdxPublishPayload.FromJson(job.PlatformOptionsJson);
        if (string.IsNullOrWhiteSpace(payload.OriginalTitle) || string.IsNullOrWhiteSpace(payload.NewTitle))
            throw new InvalidOperationException("快手 ADX 发布任务缺少原剧名或新剧名。");
        if (string.IsNullOrWhiteSpace(payload.Options.MaterialType) || string.IsNullOrWhiteSpace(payload.Options.AuthorDeclaration))
            throw new InvalidOperationException("快手 ADX 发布任务缺少剪辑类型或作者声明。");
        var config = KuaishouPersonalConfig.Load(job);
        var fallbackCover = payload.Options.CoverMode == "single-image"
            ? payload.Options.CoverPath
            : Path.Combine(job.ProjectDirectory, "快手竖屏海报.jpg");
        var requested = payload.Options.CoverMode == "adx"
            ? payload.Items
            : payload.Items.Select(item => new KuaishouAdxPublishItem
            {
                MaterialId = item.MaterialId, Rank = item.Rank, VideoPath = item.VideoPath,
                CoverPath = null, ManifestPath = item.ManifestPath,
            }).ToList();
        var items = _resolver.Validate(job.ProjectDirectory, requested, fallbackCover);
        var accountKey = KuaishouAdxIdentity.AccountKey(job.AccountId);
        items = items.Where(item => !AlreadySucceeded(item.ManifestPath, accountKey, item.MaterialId)).ToArray();
        if (items.Count == 0)
        {
            progress?.Report("所选 ADX 素材已由当前快手账号发布，无需重复提交。");
            return;
        }

        var clickedPublish = false;
        var reconciliationBlocked = items.Any(item => HasStatus(item.ManifestPath, accountKey, item.MaterialId, "submission_unknown"));
        try
        {
            await _sessionService.ExecuteAuthenticatedAsync(job, async (page, _, ct) =>
            {
                progress?.Report($"快手宣发素材：正在按新剧名精确查询《{payload.NewTitle}》…");
                var seriesId = await FindExactSeriesIdAsync(page, payload.NewTitle, ct);
                var formItems = items.Select(item => new MaterialFormItem(
                    item.MaterialId,
                    KuaishouAdxIdentity.FormatTitle(payload.Options.TitleTemplate, payload.NewTitle,
                        payload.OriginalTitle, item.Rank, item.MaterialId),
                    item.VideoPath, item.CoverPath!)).ToArray();
                if (await AllAlreadyPublishedAsync(page, seriesId, formItems.Select(item => item.Title).ToArray(), ct))
                {
                    progress?.Report("平台已存在全部同名宣发素材，正在修复本地发布状态。");
                    return;
                }
                if (reconciliationBlocked)
                    throw new InvalidOperationException("上次提交结果未知，且平台未找到全部同名素材；已停止自动重发，请人工核对后再处理。");
                progress?.Report($"快手宣发素材：正在打开 {formItems.Length} 条剪辑表单…");
                var frame = await OpenMaterialFormAsync(page, seriesId, ct);
                await EnsureFormCountAsync(page, frame, formItems.Length, ct);
                await FillFormsAsync(page, frame, formItems, payload.Options, progress, ct);
                frame = await GoToVideoStepAsync(page, frame, ct);
                await UploadVideosAsync(page, frame, formItems, progress, ct);
                var publish = await FindVisibleAsync(frame.GetByRole(AriaRole.Button,
                    new FrameGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex("发\\s*布") }), ct, 30_000)
                    ?? throw new InvalidOperationException("未找到可用的快手宣发素材发布按钮。");
                progress?.Report("全部素材上传完成，正在提交发布。");
                clickedPublish = true;
                await publish.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });
                await WaitForPublishSuccessAsync(page, frame, ct);
            }, cancellationToken);

            foreach (var item in items) Record(job, item, "success", "快手宣发素材发布成功");
            progress?.Report($"已发布 {items.Count} 条快手 ADX 宣发素材。");
        }
        catch (OperationCanceledException)
        {
            foreach (var item in items) Record(job, item, "cancelled", "快手宣发素材发布已停止");
            throw;
        }
        catch (Exception ex)
        {
            var status = clickedPublish || reconciliationBlocked ? "submission_unknown" : "failed";
            var message = clickedPublish ? "已点击发布但未确认结果：" + ex.Message : ex.Message;
            foreach (var item in items) Record(job, item, status, message);
            throw new InvalidOperationException(message, ex);
        }
    }

    private bool AlreadySucceeded(string manifestPath, string accountKey, string materialId)
    {
        var manifest = _batchStore.Read(manifestPath);
        return manifest is not null && manifest.PublishByAccount.TryGetValue(accountKey, out var account) &&
               account.Items.TryGetValue(materialId, out var item) && item.Status is "success" or "draft_saved";
    }

    private bool HasStatus(string manifestPath, string accountKey, string materialId, string status)
    {
        var manifest = _batchStore.ReadInventory(manifestPath);
        return manifest is not null && manifest.PublishByAccount.TryGetValue(accountKey, out var account) &&
               account.Items.TryGetValue(materialId, out var item) && item.Status.Equals(status, StringComparison.OrdinalIgnoreCase);
    }

    private void Record(PublishJob job, KuaishouAdxPublishItem item, string status, string message)
    {
        _batchStore.RecordItem(item.ManifestPath, KuaishouAdxIdentity.AccountKey(job.AccountId), item.MaterialId, status, message);
        _analyticsSink.Record(job, item.MaterialId, status, DateTimeOffset.UtcNow);
    }

    private static async Task<string> FindExactSeriesIdAsync(IPage page, string title, CancellationToken cancellationToken)
    {
        await page.GotoAsync(ContentManagementUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await page.EvaluateAsync<JsonElement>("""
            async ({ url, title }) => {
              const response = await fetch(url, {
                method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ pageNum: 1, pageSize: 10, episodeTitle: '', sellingStatus: 0,
                  queryType: 0, sortParam: {}, miniSeriesTitle: title, auditStatus: 0, saleType: null,
                  seriesIdList: [], createDateFrom: '', createDateTo: '', sourceType4Filter: null })
              });
              if (!response.ok) throw new Error(`HTTP ${response.status}`);
              return response.json();
            }
            """, new { url = QueryListUrl, title });
        if (payload.TryGetProperty("successful", out var successful) && successful.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException("快手短剧查询失败，登录态可能已失效。");
        var ids = new List<string>();
        if (payload.TryGetProperty("data", out var data) && data.TryGetProperty("data", out var rows))
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("courseName", out var name) || name.GetString()?.Trim() != title.Trim()) continue;
            if (row.TryGetProperty("miniSeriesId", out var id)) ids.Add(id.ToString());
        }
        var unique = ids.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        return unique.Length switch
        {
            1 => unique[0],
            0 => throw new InvalidOperationException($"快手未找到新剧名“{title}”的精确结果。"),
            _ => throw new InvalidOperationException($"快手找到 {unique.Length} 条“{title}”结果，无法安全选择。"),
        };
    }

    private static async Task<bool> AllAlreadyPublishedAsync(IPage page, string seriesId, string[] titles, CancellationToken ct)
    {
        await page.GotoAsync($"https://kdj.kuaishou.com/home/content/content-management/detail?miniSeriesId={Uri.EscapeDataString(seriesId)}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        var tab = await FindVisibleAsync(page.GetByText("宣发剪辑", new PageGetByTextOptions { Exact = true }), ct, 15_000);
        if (tab is null) return false;
        await tab.ClickAsync();
        await page.WaitForTimeoutAsync(800);
        foreach (var title in titles)
        {
            var rows = page.Locator("tbody tr").Filter(new LocatorFilterOptions { HasTextString = title });
            var found = false;
            for (var index = 0; index < await rows.CountAsync(); index++)
            {
                var row = rows.Nth(index);
                if (await row.IsVisibleAsync() && (await row.InnerTextAsync()).Contains("已发布", StringComparison.Ordinal)) { found = true; break; }
            }
            if (!found) return false;
        }
        return titles.Length > 0;
    }

    private static async Task<IFrame> OpenMaterialFormAsync(IPage page, string seriesId, CancellationToken ct)
    {
        if (!page.Url.Contains("/detail", StringComparison.OrdinalIgnoreCase))
            await page.GotoAsync($"https://kdj.kuaishou.com/home/content/content-management/detail?miniSeriesId={Uri.EscapeDataString(seriesId)}",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        var tab = await FindVisibleAsync(page.GetByText("宣发剪辑", new PageGetByTextOptions { Exact = true }), ct, 60_000)
            ?? throw new InvalidOperationException("未找到快手“宣发剪辑”页签。");
        await tab.ClickAsync();
        var add = await FindVisibleAsync(page.GetByRole(AriaRole.Button,
            new PageGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex("添加宣发剪辑|编辑宣发剪辑") }), ct, 60_000)
            ?? throw new InvalidOperationException("未找到快手“添加宣发剪辑”按钮。");
        await add.ClickAsync();
        return await WaitForMaterialFrameAsync(page, ct, 60_000);
    }

    private static async Task<IFrame> WaitForMaterialFrameAsync(IPage page, CancellationToken ct, int timeout)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames.Where(value => value != page.MainFrame))
                if (frame.Url.Contains("material-info", StringComparison.OrdinalIgnoreCase) ||
                    await frame.GetByText("添加剪辑信息", new FrameGetByTextOptions { Exact = true }).First.IsVisibleAsync().CatchFalse())
                    return frame;
            await page.WaitForTimeoutAsync(300);
        }
        throw new TimeoutException("等待快手宣发剪辑表单 iframe 超时。");
    }

    private static async Task EnsureFormCountAsync(IPage page, IFrame frame, int total, CancellationToken ct)
    {
        var forms = EditableForms(frame);
        var existing = await forms.CountAsync();
        if (existing >= total) return;
        var add = await FindVisibleAsync(frame.GetByRole(AriaRole.Button,
            new FrameGetByRoleOptions { Name = "添加剪辑信息", Exact = true }), ct, 30_000)
            ?? throw new InvalidOperationException("未找到“添加剪辑信息”按钮。");
        await add.ClickAsync();
        var dialog = await FindVisibleAsync(frame.Locator(".ks-dialog, .ant-modal, [role=dialog]").Filter(
            new LocatorFilterOptions { HasTextString = "剪辑数量" }), ct, 30_000)
            ?? throw new InvalidOperationException("等待新增剪辑数量弹窗超时。");
        await dialog.Locator("input").First.FillAsync((total - existing).ToString());
        await dialog.GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex("确\\s*定") }).ClickAsync();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (await EditableForms(frame).CountAsync() >= total) return;
            await page.WaitForTimeoutAsync(300);
        }
        throw new TimeoutException($"快手新增剪辑表单失败：需要 {total} 个。");
    }

    private static ILocator EditableForms(IFrame frame) => frame.Locator("form").Filter(
        new LocatorFilterOptions { Has = frame.Locator("input[placeholder='请输入剪辑标题']:not([disabled])") });

    private static async Task FillFormsAsync(IPage page, IFrame frame, MaterialFormItem[] items,
        KuaishouAdxPublishOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var forms = EditableForms(frame);
        for (var index = 0; index < items.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var form = forms.Nth(index);
            var item = items[index];
            progress?.Report($"正在填写快手剪辑 {index + 1}/{items.Length}：{item.Title}");
            await form.Locator("input[placeholder='请输入剪辑标题']").First.FillAsync(item.Title);
            await SelectFormOptionAsync(frame, form, "剪辑类型", options.MaterialType);
            await SelectFormOptionAsync(frame, form, "作者声明", options.AuthorDeclaration);
            var coverInput = form.Locator("input[type=file][accept*='image']").First;
            if (await coverInput.CountAsync() == 0) throw new InvalidOperationException($"剪辑 {index + 1} 缺少封面上传入口。");
            await coverInput.SetInputFilesAsync(item.CoverPath);
        }
    }

    private static async Task SelectFormOptionAsync(IFrame frame, ILocator form, string label, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var item = form.Locator(".ks-form-item, .ant-form-item, [class*='form-item']").Filter(
            new LocatorFilterOptions { HasTextString = label }).First;
        if (await item.CountAsync() == 0) throw new InvalidOperationException($"未找到快手字段：{label}");
        await item.Locator(".ant-select-selector, .ks-select, [role=combobox], input").First.ClickAsync();
        var option = frame.Locator(".ks-select-dropdown:visible, .ant-select-dropdown:visible, [role=listbox]:visible")
            .GetByText(value, new LocatorGetByTextOptions { Exact = true }).First;
        await option.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
    }

    private static async Task<IFrame> GoToVideoStepAsync(IPage page, IFrame frame, CancellationToken ct)
    {
        await frame.GetByRole(AriaRole.Button, new FrameGetByRoleOptions { Name = "下一步", Exact = true }).ClickAsync();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var current = page.Frames.FirstOrDefault(value => value != page.MainFrame && value.Url.Contains("material-info", StringComparison.OrdinalIgnoreCase));
            if (current is not null && await current.Locator("input[type=file]").CountAsync() > 0) return current;
            await page.WaitForTimeoutAsync(300);
        }
        throw new TimeoutException("等待快手素材视频上传步骤超时。");
    }

    private static async Task UploadVideosAsync(IPage page, IFrame frame, MaterialFormItem[] items,
        IProgress<string>? progress, CancellationToken ct)
    {
        var inputs = frame.Locator("input[type=file]");
        if (await inputs.CountAsync() < items.Length)
            throw new InvalidOperationException($"素材视频上传入口不足：需要 {items.Length} 个。");
        for (var index = 0; index < items.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"正在上传快手素材视频 {index + 1}/{items.Length}：{Path.GetFileName(items[index].VideoPath)}");
            await inputs.Nth(index).SetInputFilesAsync(items[index].VideoPath);
        }
        var until = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            var publish = frame.GetByRole(AriaRole.Button,
                new FrameGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex("发\\s*布") }).First;
            if (await publish.IsVisibleAsync().CatchFalse() && await publish.IsEnabledAsync().CatchFalse()) return;
            await page.WaitForTimeoutAsync(1000);
        }
        throw new TimeoutException("素材视频上传超时，发布按钮仍不可用。");
    }

    private static async Task WaitForPublishSuccessAsync(IPage page, IFrame frame, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (frame.IsDetached && page.Url.Contains("/content-management/detail", StringComparison.OrdinalIgnoreCase)) return;
            if (await frame.GetByText(new System.Text.RegularExpressions.Regex("发布成功|提交成功")).First.IsVisibleAsync().CatchFalse()) return;
            await page.WaitForTimeoutAsync(300);
        }
        throw new TimeoutException("提交后未收到快手发布成功确认。");
    }

    private static async Task<ILocator?> FindVisibleAsync(ILocator locator, CancellationToken ct, int timeout)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeout);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            for (var index = 0; index < await locator.CountAsync(); index++)
                if (await locator.Nth(index).IsVisibleAsync().CatchFalse()) return locator.Nth(index);
            await Task.Delay(250, ct);
        }
        return null;
    }

    private sealed record MaterialFormItem(string Id, string Title, string VideoPath, string CoverPath);
}

internal static class KuaishouPlaywrightTaskExtensions
{
    public static async Task<bool> CatchFalse(this Task<bool> task)
    {
        try { return await task; }
        catch { return false; }
    }
}
