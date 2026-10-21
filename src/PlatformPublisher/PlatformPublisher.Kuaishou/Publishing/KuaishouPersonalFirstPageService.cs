using Microsoft.Playwright;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed class KuaishouPersonalFirstPageService
{
    public async Task FillAndSaveDraftAsync(
        IPage page,
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var size = await ProbeVideoSizeAsync(data.VideoPaths.FirstOrDefault());
        var form = KuaishouPersonalFormResolver.Resolve(config, size.Width, size.Height);
        if (string.IsNullOrWhiteSpace(form.CreditName))
            throw new InvalidOperationException("请先在快手分账配置的“分账配置”中填写用户名。");
        if (string.IsNullOrWhiteSpace(config.KuaishouNickname) || !form.ProductionOrg.Contains('+'))
            throw new InvalidOperationException("请先在快手分账配置的“登录授权”中填写快手昵称和用户快手号 ID。");

        await NavigateToUploadAsync(page, cancellationToken);
        await FillTextAsync(page, "短剧标题", data.Title, required: true);
        await FillTextAsync(page, "短剧简介", data.Intro, required: true);
        await SelectAsync(page, "内容类型", form.ContentTypeLabel, required: false);
        await SelectAsync(page, "漫剧制作方式", form.ComicMethodLabel, required: false);
        await SelectAsync(page, "作者声明", form.AuthorDeclarationLabel, required: true);
        await FillTextAsync(page, "短标题", data.ShortTitle, required: false);
        await FillTextAsync(page, "标签", string.Join(' ', data.Tags), required: false);
        await SelectAsync(page, "版权证明类型", form.CopyrightProofTypeLabel, required: false);
        await SetRadioAsync(page, "是否完结", form.Finished ? "是" : "否", required: true);
        await SetRadioAsync(page, "是否全场景展示", form.FullSceneDisplay ? "是" : "否", required: false);
        await SetRadioAsync(page, "是否有备案号", form.HasRecordNumber ? "是" : "否", required: true);
        await page.WaitForTimeoutAsync(600);
        await SetRadioAsync(page, "制作形式", form.ProductionForm, required: false);
        await FillTextAsync(page, "出品年份", form.ProductionYear.ToString(), required: false);
        await FillTextAsync(page, "制作成本", form.ProductionCost, required: false);
        await FillTextAsync(page, "单集平均时长", form.AverageDurationMinutes.ToString(), required: false);
        await FillTextAsync(page, "制作机构", form.ProductionOrg, required: false);
        await FillPeopleAsync(page, form.CreditName, form.Gender, cancellationToken);
        await SelectAsync(page, "播出平台", form.BroadcastPlatform, required: false);
        await SelectAsync(page, "播出途径", form.BroadcastChannel, required: false);
        await FillTextAsync(page, "播出时间", form.BroadcastDate, required: false);
        if (!form.HasRecordNumber) await FillActorsAsync(page, form.Actors, cancellationToken);
        await SetRadioAsync(page, "是否涉及重大革命和历史或特殊题材", form.SpecialTheme ? "是" : "否", required: false);
        await UploadScopedAsync(page, ["短剧封面", "横屏封面"], data.HorizontalCoverPath, required: true);
        await HandleCropDialogAsync(page, 15, cancellationToken);
        await UploadProofMaterialsAsync(page, data, config, cancellationToken);
        await SetRadioAsync(page, "售卖方式", form.SaleTypeLabel, required: false);
        await SetRadioAsync(page, "售卖类型", form.SaleTypeLabel, required: false);
        await FillTextAsync(page, "免费集数", form.FreeEpisodeCount.ToString(), required: false);
        await FillTextAsync(page, "广告解锁集数", form.UnlockCount.ToString(), required: false);
        await FillTextAsync(page, "全剧价格", form.EpisodePrice, required: false);
        if (form.SmallAmountUnlock) await SetRadioAsync(page, "小额解锁", "是", required: false);
        await SetCheckboxAsync(page, ["付费短剧经营者服务协议", "我已阅读并同意"]);

        progress?.Report($"{config.StoragePlatform.DisplayName()}：第一页已按当前配置填写。制作形式 {form.ProductionForm}，播出 {form.BroadcastDate}，售卖 {form.SaleTypeLabel}。 ");
        if (string.Equals(config.FirstPageAction, "draft", StringComparison.OrdinalIgnoreCase))
        {
            var saved = await ClickVisibleTextAsync(page, ["保存草稿", "存草稿"], 8_000);
            if (!saved) throw new InvalidOperationException("未找到“保存草稿”按钮。");
            progress?.Report($"{config.StoragePlatform.DisplayName()}：第一页草稿已保存。 ");
        }
    }

    private static async Task NavigateToUploadAsync(IPage page, CancellationToken cancellationToken)
    {
        if (page.Url.Contains("content-management/edit", StringComparison.OrdinalIgnoreCase) ||
            await page.GetByText("短剧标题", new PageGetByTextOptions { Exact = false }).CountAsync() > 0)
            return;
        await ClickVisibleTextAsync(page, ["内容"], 5_000);
        await ClickVisibleTextAsync(page, ["原生短剧"], 8_000);
        if (!await ClickVisibleTextAsync(page, ["内容上传"], 10_000))
            throw new InvalidOperationException("未找到快手个人版“内容上传”入口。");
        await page.GetByText("短剧标题", new PageGetByTextOptions { Exact = false }).First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task FillTextAsync(IPage page, string label, string value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) throw new InvalidOperationException($"必填字段“{label}”没有可用值。");
            return;
        }
        var scope = await FindScopeAsync(page, label);
        if (scope is null)
        {
            if (required) throw new InvalidOperationException($"未找到表单字段：{label}");
            return;
        }
        var input = scope.Locator("textarea, input:not([type=file]):not([type=radio]):not([type=checkbox])").First;
        if (await input.CountAsync() == 0)
        {
            if (required) throw new InvalidOperationException($"字段“{label}”内没有可填写控件。");
            return;
        }
        await input.FillAsync(value);
        var actual = await input.InputValueAsync();
        if (required && string.IsNullOrWhiteSpace(actual)) throw new InvalidOperationException($"字段“{label}”填写后仍为空。");
    }

    private static async Task SelectAsync(IPage page, string label, string option, bool required)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            if (required) throw new InvalidOperationException($"字段“{label}”缺少配置值。");
            return;
        }
        var scope = await FindScopeAsync(page, label);
        if (scope is null)
        {
            if (required) throw new InvalidOperationException($"未找到下拉字段：{label}");
            return;
        }
        var trigger = scope.Locator("[role=combobox], .select-trigger, .ks-select, .ant-select, .el-select, input[readonly]").First;
        if (await trigger.CountAsync() == 0)
        {
            if (required) throw new InvalidOperationException($"下拉字段“{label}”没有触发器。");
            return;
        }
        await trigger.ClickAsync();
        var selected = await ClickVisibleTextAsync(page, [option], 5_000);
        if (required && !selected) throw new InvalidOperationException($"下拉字段“{label}”未找到选项“{option}”。");
    }

    private static async Task SetRadioAsync(IPage page, string label, string option, bool required)
    {
        var scope = await FindScopeAsync(page, label);
        if (scope is null)
        {
            if (required) throw new InvalidOperationException($"未找到单选字段：{label}");
            return;
        }
        var radio = scope.GetByText(option, new LocatorGetByTextOptions { Exact = true }).Last;
        if (await radio.CountAsync() == 0)
        {
            if (required) throw new InvalidOperationException($"单选字段“{label}”未找到“{option}”。");
            return;
        }
        await radio.ClickAsync();
    }

    private static async Task UploadScopedAsync(IPage page, IReadOnlyList<string> labels, string path, bool required)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (required) throw new FileNotFoundException($"缺少上传文件：{string.Join('/', labels)}", path);
            return;
        }
        foreach (var label in labels)
        {
            var scope = await FindScopeAsync(page, label);
            if (scope is null) continue;
            var input = scope.Locator("input[type=file]").First;
            if (await input.CountAsync() == 0) continue;
            await input.SetInputFilesAsync(path);
            return;
        }
        if (required) throw new InvalidOperationException($"未找到上传入口：{string.Join('/', labels)}");
    }

    private static async Task UploadProofMaterialsAsync(
        IPage page,
        KuaishouPersonalProjectData data,
        KuaishouPersonalConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(data.CommitmentPdfPath) || !File.Exists(data.CommitmentPdfPath))
            throw new FileNotFoundException($"{config.StoragePlatform.DisplayName()}缺少承诺函 PDF。", data.CommitmentPdfPath);
        await ClickVisibleTextAsync(page, ["切换为上传PDF"], 3_000);
        await UploadScopedAsync(page, ["承诺函", "上传承诺函"], data.CommitmentPdfPath, required: true);
        if (data.ProjectImagePaths.Count < 4)
            throw new InvalidOperationException($"{config.StoragePlatform.DisplayName()}要求 4 张工程图，当前只有 {data.ProjectImagePaths.Count} 张。");
        foreach (var image in data.ProjectImagePaths.Take(4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UploadScopedAsync(page, ["现场拍摄图/短剧工程文件", "短剧工程文件"], image, required: true);
            await page.WaitForTimeoutAsync(300);
        }
    }

    private static async Task FillPeopleAsync(IPage page, string creditName, string gender, CancellationToken cancellationToken)
    {
        foreach (var label in new[] { "导演", "编剧", "制片人" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await FillTextAsync(page, label, creditName, required: label == "导演");
            await SelectAsync(page, label, gender, required: false);
            await SetRadioAsync(page, label, gender, required: false);
        }
    }

    private static async Task<(int Width, int Height)> ProbeVideoSizeAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return (0, 0);
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffprobe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", path })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(8000)) return (0, 0);
            var parts = output.Trim().Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height)
                ? (width, height)
                : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static async Task FillActorsAsync(IPage page, IReadOnlyList<KuaishouPersonalActor> actors, CancellationToken cancellationToken)
    {
        for (var index = 0; index < actors.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index > 0) await ClickVisibleTextAsync(page, ["新增主要演员信息", "新增演员信息", "添加演员"], 2_000);
            var actor = actors[index];
            var cards = page.Locator(".main-actor, .actor-card, [class*=actor]");
            if (await cards.CountAsync() <= index) continue;
            var card = cards.Nth(index);
            var inputs = card.Locator("input");
            if (await inputs.CountAsync() > 0) await inputs.Nth(0).FillAsync(actor.Name);
            if (await inputs.CountAsync() > 1) await inputs.Nth(await inputs.CountAsync() - 1).FillAsync(actor.Role);
            var gender = card.GetByText(actor.Gender, new LocatorGetByTextOptions { Exact = true }).Last;
            if (await gender.CountAsync() > 0) await gender.ClickAsync();
        }
    }

    private static async Task SetCheckboxAsync(IPage page, IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            var scope = await FindScopeAsync(page, label);
            var checkbox = scope?.Locator("input[type=checkbox]").First ?? page.Locator("input[type=checkbox]").Last;
            if (await checkbox.CountAsync() == 0) continue;
            if (!await checkbox.IsCheckedAsync()) await checkbox.CheckAsync();
            return;
        }
    }

    private static async Task HandleCropDialogAsync(IPage page, int shrinkClicks, CancellationToken cancellationToken)
    {
        var dialog = page.Locator("[role=dialog]:visible, .ks-dialog:visible, .ant-modal:visible").Last;
        try { await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 4_000 }); }
        catch { return; }
        var minus = dialog.Locator("button[aria-label*=缩小], button[title*=缩小], button:has-text('-')").First;
        for (var index = 0; index < shrinkClicks && await minus.CountAsync() > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await minus.ClickAsync();
        }
        var confirm = dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "确认", Exact = false }).Last;
        if (await confirm.CountAsync() == 0) confirm = dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "确定", Exact = false }).Last;
        if (await confirm.CountAsync() > 0) await confirm.ClickAsync();
    }

    private static async Task<ILocator?> FindScopeAsync(IPage page, string label)
    {
        var candidates = page.Locator(".ks-form-item, .ant-form-item, .el-form-item, [class*=form-item]")
            .Filter(new LocatorFilterOptions { HasTextString = label });
        var count = Math.Min(await candidates.CountAsync(), 20);
        for (var index = 0; index < count; index++)
        {
            var candidate = candidates.Nth(index);
            if (await candidate.IsVisibleAsync()) return candidate;
        }
        return null;
    }

    private static async Task<bool> ClickVisibleTextAsync(IPage page, IReadOnlyList<string> texts, float timeout)
    {
        foreach (var text in texts)
        {
            var locator = page.GetByText(text, new PageGetByTextOptions { Exact = false });
            var count = Math.Min(await locator.CountAsync(), 20);
            for (var index = 0; index < count; index++)
            {
                var item = locator.Nth(index);
                if (!await item.IsVisibleAsync()) continue;
                try { await item.ClickAsync(new LocatorClickOptions { Timeout = timeout }); return true; }
                catch { /* 尝试下一个候选。 */ }
            }
        }
        return false;
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
