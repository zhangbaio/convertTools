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

        var enterprise = config.StoragePlatform == PublishPlatform.KuaishouEnterpriseRevenue;
        await NavigateToUploadAsync(page, cancellationToken);
        await FillTextAsync(page, "短剧标题", data.Title, required: true);
        await FillTextAsync(page, "短剧简介", data.Intro, required: true);
        if (enterprise)
            await SetRadioAsync(page, "内容类型", form.ContentTypeLabel, required: false);
        else
            await SelectAsync(page, "内容类型", form.ContentTypeLabel, required: false);
        if (!enterprise || form.ContentTypeLabel == "漫剧")
            await SelectAsync(page, "漫剧制作方式", form.ComicMethodLabel, required: false);
        if (enterprise)
            await SelectOptionalDropdownAsync(page, "请选择男女频道", ReadAutoFillLabel(data.WorkflowDirectory, "audience_gender_label"));
        await SelectLabeledOptionsAsync(page, ReadAutoFillLabels(data.WorkflowDirectory, "tag_label_list"), "请选择分类", "短剧分类", required: true);
        await SelectLabeledOptionsAsync(page, ReadAutoFillLabels(data.WorkflowDirectory, "plot_label_list"), "请选择短剧情节", "短剧情节", required: false);
        await SelectAsync(page, "作者声明", form.AuthorDeclarationLabel, required: true);
        await FillTextAsync(page, "短标题", data.ShortTitle, required: false);
        await FillTextAsync(page, "标签", string.Join(' ', data.Tags), required: false);
        await SelectAsync(page, "版权证明类型", form.CopyrightProofTypeLabel, required: false);
        await FillTextAsync(page, "短剧标题", data.Title, required: true);
        await SetRadioAsync(page, "是否完结", form.Finished ? "是" : "否", required: true);
        await SetRadioAsync(page, "是否全场景展示", form.FullSceneDisplay ? "是" : "否", required: false);
        await SetRadioAsync(page, "是否有备案号", form.HasRecordNumber ? "是" : "否", required: true);
        await page.WaitForTimeoutAsync(600);
        await SetRadioAsync(page, "制作形式", form.ProductionForm, required: false);
        if (!await SelectYearAsync(page, form.ProductionYear))
            await FillTextAsync(page, "出品年份", form.ProductionYear.ToString(), required: false);
        await FillTextAsync(page, "制作成本", form.ProductionCost, required: false);
        await FillTextAsync(page, "单集平均时长", form.AverageDurationMinutes.ToString(), required: false);
        var productionOrg = enterprise
            ? FirstNonEmpty(config.CommitmentRecipientCompanyName, config.ProductionOrganization, form.ProductionOrg)
            : form.ProductionOrg;
        await FillTextAsync(page, "制作机构", productionOrg, required: false);
        if (enterprise)
            await FillEnterprisePeopleAsync(page, data.WorkflowDirectory, cancellationToken);
        else
            await FillPeopleAsync(page, form.CreditName, form.Gender, cancellationToken);
        await SelectAsync(page, "播出平台", form.BroadcastPlatform, required: false);
        await SelectAsync(page, "播出途径", form.BroadcastChannel, required: false);
        await FillTextAsync(page, "播出时间", form.BroadcastDate, required: false);
        if (!form.HasRecordNumber)
            await FillActorsAsync(page, KuaishouPersonalFormResolver.ResolveUploadActors(data.WorkflowDirectory, config), cancellationToken);
        await SetRadioAsync(page, "是否涉及重大革命和历史或特殊题材", "否", required: false);
        await UploadScopedAsync(page, ["短剧封面", "横屏封面"], data.HorizontalCoverPath, required: true);
        progress?.Report($"{config.StoragePlatform.DisplayName()}：短剧封面已上传，正在确认图片裁剪。 ");
        await HandleCropDialogAsync(page, 15, cancellationToken);
        progress?.Report($"{config.StoragePlatform.DisplayName()}：短剧封面裁剪已确认。 ");
        await UploadProofMaterialsAsync(page, data, config, cancellationToken);
        await SetRadioAsync(page, "售卖方式", form.SaleTypeLabel, required: false);
        await SetRadioAsync(page, "售卖类型", form.SaleTypeLabel, required: false);
        await FillTextAsync(page, "免费集数", form.FreeEpisodeCount.ToString(), required: false);
        await FillTextAsync(page, "广告解锁集数", form.UnlockCount.ToString(), required: false);
        await FillTextAsync(page, "全剧价格", form.EpisodePrice, required: false);
        if (form.SmallAmountUnlock) await SetRadioAsync(page, "小额解锁", "是", required: false);
        progress?.Report($"{config.StoragePlatform.DisplayName()}：正在批量设置单集标题和封面。 ");
        await new KuaishouPersonalEpisodeUploadService().ConfigureEpisodeInfoAsync(page, data, config, cancellationToken);
        await SetCheckboxAsync(page, ["付费短剧经营者服务协议", "我已阅读并同意"]);
        await EnsureSingleProofMaterialAsync(page, "承诺函+现场拍摄图/短剧工程文件");

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
        if (page.Url.Contains("content-management/edit", StringComparison.OrdinalIgnoreCase))
            return;

        var button = await WaitForContentUploadButtonAsync(page, cancellationToken);
        if (button is null)
            throw new InvalidOperationException("未找到快手个人版“内容上传”入口。");
        await button.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
        await page.GetByText("短剧标题", new PageGetByTextOptions { Exact = false }).First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<ILocator?> WaitForContentUploadButtonAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames)
            {
                if (!frame.Url.Contains("/content/content-management", StringComparison.OrdinalIgnoreCase) ||
                    frame.Url.Contains("/content-management/edit", StringComparison.OrdinalIgnoreCase))
                    continue;
                var button = frame.Locator("button.ant-btn-primary")
                    .Filter(new LocatorFilterOptions { HasText = "内容上传" })
                    .First;
                try
                {
                    if (await button.CountAsync() > 0 && await button.IsVisibleAsync())
                        return button;
                }
                catch
                {
                    // iframe 还在切换时继续等。
                }
            }

            await page.WaitForTimeoutAsync(400);
        }

        return null;
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
        await trigger.ScrollIntoViewIfNeededAsync();
        await trigger.ClickAsync();
        await page.WaitForTimeoutAsync(300);
        var selected = await ClickDropdownOptionAsync(page, option);
        if (!selected) selected = await ClickVisibleTextAsync(page, [option], 3_000);
        try { await page.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { /* 下拉已经关上。 */ }
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
        await CheckProofMaterialAsync(page, "承诺函+现场拍摄图/短剧工程文件");
        await page.WaitForTimeoutAsync(400);
        if (!await MarkSectionFileInputAsync(page, "承诺函", "commitment", switchToPdf: true))
            throw new InvalidOperationException("未找到承诺函上传入口。");
        await page.Locator("input[type=file][data-kuaishou-upload=commitment]").First.SetInputFilesAsync(data.CommitmentPdfPath);
        if (data.ProjectImagePaths.Count < 4)
            throw new InvalidOperationException($"{config.StoragePlatform.DisplayName()}要求 4 张工程图，当前只有 {data.ProjectImagePaths.Count} 张。");
        foreach (var image in data.ProjectImagePaths.Take(4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await MarkSectionFileInputAsync(page, "现场拍摄图", "project-image", switchToPdf: false))
                throw new InvalidOperationException("未找到现场拍摄图/短剧工程文件上传入口。");
            await page.Locator("input[type=file][data-kuaishou-upload=project-image]").First.SetInputFilesAsync(image);
            await KuaishouCropDialog.ConfirmAsync(page, 8, TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static async Task CheckProofMaterialAsync(IPage page, string option)
    {
        var scope = await FindScopeAsync(page, "版权证明材料");
        if (scope is null) throw new InvalidOperationException("未找到下拉字段：版权证明材料");
        await scope.EvaluateAsync("element => element.setAttribute('data-kuaishou-proof-scope', '1')");
        var trigger = scope.Locator(".select-trigger, [role=combobox], .ks-select").First;
        if (await trigger.CountAsync() == 0) throw new InvalidOperationException("版权证明材料没有下拉触发器。");
        await trigger.ScrollIntoViewIfNeededAsync();
        await trigger.ClickAsync();
        await page.WaitForTimeoutAsync(400);

        var target = string.Concat(option.Where(character => !char.IsWhiteSpace(character)));
        var matched = false;
        var lastState = "";
        for (var attempt = 0; attempt < 3 && !matched; attempt++)
        {
            var rows = await ReadProofChoicesAsync(page);
            lastState = DescribeProofChoices(rows);
            if (rows.Count == 0)
            {
                await trigger.ClickAsync();
                await page.WaitForTimeoutAsync(400);
                continue;
            }

            foreach (var row in rows.Where(row => row.Text != target && row.Checked))
                await ClickProofChoiceAsync(page, row.Index);
            var wanted = rows.FirstOrDefault(row => row.Text == target);
            if (wanted is not null && !wanted.Checked)
                await ClickProofChoiceAsync(page, wanted.Index);
            await page.WaitForTimeoutAsync(300);

            rows = await ReadProofChoicesAsync(page);
            lastState = DescribeProofChoices(rows);
            matched = rows.Any(row => row.Text == target && row.Checked)
                && rows.Where(row => row.Checked).All(row => row.Text == target);
        }

        await RemoveExtraProofTagsAsync(page, target);
        try { await page.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { /* 下拉已经关上。 */ }
        await page.WaitForTimeoutAsync(200);
        var tags = await ReadProofTagsAsync(page);
        if (tags.Count > 0)
            matched = tags.All(tag => tag == target) && tags.Contains(target);
        if (!matched)
            throw new InvalidOperationException($"版权证明材料没有只选中“{option}”。下拉：{lastState}；标签：{string.Join("、", tags)}");
    }

    private static async Task<List<ProofChoice>> ReadProofChoicesAsync(IPage page)
    {
        var raw = await page.EvaluateAsync<string>(
            """
            () => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
              };
              const poppers = Array.from(document.querySelectorAll('.ks-select-dropdown, .ks-popper, .ks-select__popper, [role="listbox"]')).filter(visible);
              const popper = poppers.find(element => {
                const text = compact(element.innerText);
                return text.includes('现场拍摄图') || text.includes('可信时间戳') || text.includes('作品登记证书');
              });
              if (!popper) return '';
              document.querySelectorAll('[data-kuaishou-proof]').forEach(element => element.removeAttribute('data-kuaishou-proof'));
              return Array.from(popper.querySelectorAll('li'))
                .filter(element => visible(element) && !element.querySelector('li'))
                .map(element => ({ element, text: compact(element.innerText || '') }))
                .filter(item => item.text.length > 0 && item.text.length <= 40)
                .map((item, index) => {
                  item.element.setAttribute('data-kuaishou-proof', String(index));
                  const input = item.element.querySelector('input[type=checkbox]');
                  const checked = !!((input && input.checked)
                    || item.element.classList.contains('is-selected')
                    || item.element.classList.contains('selected')
                    || item.element.getAttribute('aria-selected') === 'true'
                    || item.element.querySelector('.is-checked, .is-selected, [aria-checked="true"], [aria-selected="true"]'));
                  return `${index}\t${checked ? 1 : 0}\t${item.text}`;
                })
                .join('\n');
            }
            """);
        return ParseProofChoices(raw);
    }

    private static async Task ClickProofChoiceAsync(IPage page, int index)
    {
        var row = page.Locator($"[data-kuaishou-proof='{index}']");
        var checkbox = row.Locator(".ks-checkbox__inner, .ks-checkbox__input").First;
        if (await checkbox.CountAsync() > 0)
        {
            await checkbox.ClickAsync(new LocatorClickOptions { Timeout = 2_000, Force = true });
            return;
        }

        await row.ClickAsync(new LocatorClickOptions { Timeout = 2_000 });
    }

    private static async Task RemoveExtraProofTagsAsync(IPage page, string target)
    {
        var raw = await page.EvaluateAsync<string>(
            """
            (target) => {
              const scope = document.querySelector('[data-kuaishou-proof-scope="1"]');
              if (!scope) return '';
              const compact = value => String(value || '').replace(/\s+/g, '').replace(/[×✕✖]+$/g, '');
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
              };
              document.querySelectorAll('[data-kuaishou-proof-tag]').forEach(element => element.removeAttribute('data-kuaishou-proof-tag'));
              const lines = [];
              let index = 0;
              for (const tag of scope.querySelectorAll('.ks-tag')) {
                if (!visible(tag)) continue;
                const text = compact(tag.innerText || '');
                if (!text || text === target || text.length > 40) continue;
                const close = tag.querySelector('.ks-tag__close, [class*="close"]');
                if (!close || !visible(close)) continue;
                close.setAttribute('data-kuaishou-proof-tag', String(index));
                lines.push(String(index));
                index += 1;
              }
              return lines.join('\n');
            }
            """,
            target);
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            await page.Locator($"[data-kuaishou-proof-tag='{line}']").ClickAsync(new LocatorClickOptions { Timeout = 2_000, Force = true });
            await page.WaitForTimeoutAsync(200);
        }
    }

    private static async Task<List<string>> ReadProofTagsAsync(IPage page)
    {
        var raw = await page.EvaluateAsync<string>(
            """
            () => {
              const scope = document.querySelector('[data-kuaishou-proof-scope="1"]');
              if (!scope) return '';
              const compact = value => String(value || '').replace(/\s+/g, '').replace(/[×✕✖]+$/g, '');
              return Array.from(scope.querySelectorAll('.ks-tag'))
                .map(tag => compact(tag.innerText || ''))
                .filter(text => text.length > 0 && text.length <= 40)
                .join('\n');
            }
            """);
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static List<ProofChoice> ParseProofChoices(string? raw)
    {
        var rows = new List<ProofChoice>();
        if (string.IsNullOrWhiteSpace(raw)) return rows;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !int.TryParse(parts[0], out var index)) continue;
            rows.Add(new ProofChoice(index, parts[2], parts[1] == "1"));
        }

        return rows;
    }

    private static string DescribeProofChoices(IReadOnlyList<ProofChoice> rows) =>
        rows.Count == 0 ? "（下拉未打开）" : string.Join("、", rows.Select(row => $"{row.Text}={(row.Checked ? "勾选" : "未选")}"));

    private sealed record ProofChoice(int Index, string Text, bool Checked);

    private static async Task<bool> MarkSectionFileInputAsync(IPage page, string section, string marker, bool switchToPdf)
    {
        if (switchToPdf)
        {
            await page.EvaluateAsync<bool>(
                """
                (section) => {
                  const compact = value => String(value || '').replace(/\s+/g, '');
                  const visible = element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
                  };
                  const toggles = Array.from(document.querySelectorAll('a, span, button, div'))
                    .filter(element => visible(element) && compact(element.innerText) === '切换为上传PDF');
                  const toggle = toggles.find(element => {
                    const row = compact(element.parentElement?.innerText || '');
                    return row.includes(compact(section)) && !row.includes('现场拍摄图');
                  });
                  if (!toggle) return false;
                  toggle.click();
                  return true;
                }
                """,
                section);
            await page.WaitForTimeoutAsync(500);
        }

        return await page.EvaluateAsync<bool>(
            """
            ({ section, marker }) => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none' && style.visibility !== 'hidden';
              };
              document.querySelectorAll(`input[type=file][data-kuaishou-upload="${marker}"]`)
                .forEach(input => input.removeAttribute('data-kuaishou-upload'));
              const headings = Array.from(document.querySelectorAll('div, span, label, p'))
                .filter(element => visible(element) && compact(element.innerText).includes(compact(section))
                  && compact(element.innerText).length < 40);
              for (const heading of headings) {
                let node = heading;
                for (let depth = 0; node && depth < 8; depth += 1, node = node.parentElement) {
                  const input = Array.from(node.querySelectorAll('input[type=file]'))
                    .find(item => !item.getAttribute('data-kuaishou-upload'));
                  if (!input) continue;
                  const text = compact(node.innerText || '');
                  if (section === '承诺函' && text.includes('现场拍摄图') && !text.includes('上传承诺函') && !text.includes('承诺函'))
                    continue;
                  input.setAttribute('data-kuaishou-upload', marker);
                  return true;
                }
              }
              return false;
            }
            """,
            new { section, marker });
    }

    private static async Task FillPeopleAsync(IPage page, string creditName, string gender, CancellationToken cancellationToken)
    {
        foreach (var label in new[] { "导演", "编剧", "制片人" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await FillTextAsync(page, label + "姓名", creditName, required: false);
            await FillTextAsync(page, label, creditName, required: label == "导演");
            await SelectAsync(page, label + "性别", gender, required: false);
            await SetRadioAsync(page, label + "性别", gender, required: false);
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
        var people = actors.Where(actor => !string.IsNullOrWhiteSpace(actor.Name)).Take(2).ToArray();
        for (var index = 0; index < people.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = index + 1;
            if (!await ActorCardReadyAsync(page, number))
            {
                if (!await ClickVisibleTextAsync(page, ["新增主要演员信息", "新增演员信息", "添加演员"], 3_000))
                    throw new InvalidOperationException($"页面没有第 {number} 位演员，也没有“新增主要演员信息”。");
                await page.WaitForTimeoutAsync(400);
            }

            if (!await ActorCardReadyAsync(page, number))
                throw new InvalidOperationException($"新增后仍没有第 {number} 位演员卡片。");
            await FillActorCardAsync(page, number, people[index]);
        }
    }

    private static Task<bool> ActorCardReadyAsync(IPage page, int number) =>
        page.EvaluateAsync<bool>(
            """
            (number) => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const title = `演员${number}`;
              return Array.from(document.querySelectorAll('.main-actor, .actor-card, section, div'))
                .some(card => {
                  const text = compact(card.innerText);
                  return text.includes(title) && text.includes('演员姓名') && text.includes('演员角色')
                    && card.querySelector('input');
                });
            }
            """,
            number);

    private static async Task FillActorCardAsync(IPage page, int number, KuaishouPersonalActor actor)
    {
        var filled = await page.EvaluateAsync<bool>(
            """
            ({ number, name, role }) => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const title = `演员${number}`;
              const cards = Array.from(document.querySelectorAll('.main-actor, .actor-card, section, div'))
                .filter(card => {
                  const text = compact(card.innerText);
                  return text.includes(title) && text.includes('演员姓名') && text.includes('演员角色')
                    && card.querySelector('input');
                })
                .sort((left, right) => {
                  const leftBox = left.getBoundingClientRect();
                  const rightBox = right.getBoundingClientRect();
                  return leftBox.width * leftBox.height - rightBox.width * rightBox.height;
                });
              const card = cards[0];
              if (!card) return false;
              const setValue = (input, value) => {
                const proto = input instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                const descriptor = Object.getOwnPropertyDescriptor(proto, 'value');
                if (descriptor?.set) descriptor.set.call(input, value);
                else input.value = value;
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
              };
              const field = label => {
                const items = Array.from(card.querySelectorAll('.ks-form-item, .ant-form-item, .el-form-item'));
                return items
                  .filter(element => compact(element.querySelector('label, .ks-form-item__label, .ant-form-item-label')?.innerText || '').startsWith(compact(label)))
                  .sort((left, right) => {
                    const leftBox = left.getBoundingClientRect();
                    const rightBox = right.getBoundingClientRect();
                    return leftBox.width * leftBox.height - rightBox.width * rightBox.height;
                  })[0] || null;
              };
              const textInput = item => item?.querySelector('input:not([readonly]):not([type=hidden]):not([type=file]):not([type=checkbox]):not([type=radio]), textarea');
              const nameInput = textInput(field('演员姓名'));
              const roleInput = textInput(field('演员角色'));
              if (!nameInput || !roleInput) return false;
              setValue(nameInput, name);
              setValue(roleInput, role);
              const genderItem = field('演员性别');
              const trigger = genderItem?.querySelector('.select-trigger, [role=combobox], .ks-select');
              document.querySelectorAll('[data-kuaishou-actor-gender]').forEach(element => element.removeAttribute('data-kuaishou-actor-gender'));
              trigger?.setAttribute('data-kuaishou-actor-gender', String(number));
              return true;
            }
            """,
            new { number, name = actor.Name, role = actor.Role });
        if (!filled) throw new InvalidOperationException($"第 {number} 位演员的姓名或角色没有填上。");

        var gender = page.Locator($"[data-kuaishou-actor-gender='{number}']").First;
        if (await gender.CountAsync() == 0) throw new InvalidOperationException($"第 {number} 位演员没有性别下拉。");
        await gender.ScrollIntoViewIfNeededAsync();
        await gender.ClickAsync();
        await page.WaitForTimeoutAsync(300);
        if (!await ClickDropdownOptionAsync(page, actor.Gender))
            throw new InvalidOperationException($"第 {number} 位演员性别未找到“{actor.Gender}”。");
        try { await page.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { /* 下拉已经关上。 */ }
    }

    private static IReadOnlyList<string> ReadAutoFillLabels(string workflowDirectory, string property) =>
        ReadAutoFillArray(workflowDirectory, property).Take(3).ToArray();

    private static string ReadAutoFillLabel(string workflowDirectory, string property)
    {
        var path = Path.Combine(workflowDirectory, "kuaishou-auto-fill.json");
        if (!File.Exists(path)) return string.Empty;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<string> ReadAutoFillArray(string workflowDirectory, string property)
    {
        var path = Path.Combine(workflowDirectory, "kuaishou-auto-fill.json");
        if (!File.Exists(path)) return [];
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(property, out var labels) ||
                labels.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];
            return labels.EnumerateArray()
                .Select(item => item.GetString()?.Trim() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToArray();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static async Task SelectOptionalDropdownAsync(IPage page, string placeholder, string option)
    {
        if (string.IsNullOrWhiteSpace(option)) return;
        if (!await MarkSelectTriggerAsync(page, placeholder, placeholder, "data-kuaishou-optional")) return;
        var trigger = page.Locator("[data-kuaishou-optional='1']").First;
        await trigger.ScrollIntoViewIfNeededAsync();
        await trigger.ClickAsync();
        await page.WaitForTimeoutAsync(400);
        await ClickDropdownOptionOnceAsync(page, option);
        try { await page.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { /* 下拉已经关上。 */ }
    }

    private static async Task SelectLabeledOptionsAsync(
        IPage page,
        IReadOnlyList<string> labels,
        string placeholder,
        string fieldLabel,
        bool required)
    {
        var targets = labels.Where(label => !string.IsNullOrWhiteSpace(label)).Take(3).ToArray();
        if (targets.Length == 0)
        {
            if (required) throw new InvalidOperationException($"项目没有可用的{fieldLabel}。");
            return;
        }
        if (!await MarkSelectTriggerAsync(page, placeholder, fieldLabel, "data-kuaishou-category"))
        {
            if (required) throw new InvalidOperationException($"未找到{fieldLabel}。");
            return;
        }
        var trigger = page.Locator("[data-kuaishou-category='1']").First;
        await trigger.ScrollIntoViewIfNeededAsync();
        await trigger.ClickAsync();
        await page.WaitForTimeoutAsync(500);
        foreach (var label in targets)
        {
            if (!await DropdownPopupVisibleAsync(page))
            {
                await trigger.ClickAsync();
                await page.WaitForTimeoutAsync(300);
            }
            if (!await ClickDropdownOptionOnceAsync(page, label))
            {
                if (required) throw new InvalidOperationException($"{fieldLabel}未找到“{label}”。");
            }
        }

        try { await page.Keyboard.PressAsync("Escape"); } catch (PlaywrightException) { /* 分类下拉已经关上。 */ }
    }

    private static async Task<bool> MarkSelectTriggerAsync(IPage page, string placeholder, string fieldLabel, string attribute)
    {
        return await page.EvaluateAsync<bool>(
            """
            ({ placeholder, fieldLabel, attribute }) => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              document.querySelectorAll('[' + attribute + ']').forEach(element => element.removeAttribute(attribute));
              const byPlaceholder = document.querySelector(`input[placeholder="${placeholder}"]`);
              const items = Array.from(document.querySelectorAll('.ks-form-item, .ant-form-item, .el-form-item'));
              const item = items
                .filter(element => compact(element.querySelector('label, .ks-form-item__label, .ant-form-item-label')?.innerText || '').startsWith(fieldLabel))
                .sort((left, right) => {
                  const leftBox = left.getBoundingClientRect();
                  const rightBox = right.getBoundingClientRect();
                  return leftBox.width * leftBox.height - rightBox.width * rightBox.height;
                })[0];
              const trigger = byPlaceholder?.closest('.ks-select')?.querySelector('.select-trigger')
                || item?.querySelector('.select-trigger')
                || item?.querySelector('[role="combobox"], .ks-select')
                || byPlaceholder?.closest('.select-trigger, .ks-select')
                || byPlaceholder;
              if (!trigger) return false;
              trigger.setAttribute(attribute, '1');
              return true;
            }
            """,
            new { placeholder, fieldLabel, attribute });
    }

    private static async Task<bool> SelectYearAsync(IPage page, int year)
    {
        var trigger = page.Locator(".ks-form-item").Filter(new LocatorFilterOptions { HasText = "出品年份" }).Locator("input").First;
        if (await trigger.CountAsync() == 0) return false;
        try
        {
            await trigger.ScrollIntoViewIfNeededAsync();
            await trigger.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
        }
        catch (PlaywrightException)
        {
            return false;
        }
        await page.WaitForTimeoutAsync(400);
        var target = year.ToString();
        var cells = page.Locator(".ks-year-table a.cell, .ks-picker-panel__content a.cell, .ks-picker-panel td a.cell");
        for (var pass = 0; pass < 8; pass++)
        {
            var count = await cells.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var cell = cells.Nth(index);
                if (!await cell.IsVisibleAsync()) continue;
                var text = (await cell.InnerTextAsync()).Trim();
                if (text != target) continue;
                await cell.ClickAsync();
                await page.WaitForTimeoutAsync(300);
                var value = await trigger.InputValueAsync();
                if (value.Contains(target, StringComparison.Ordinal)) return true;
            }
            if (!await ShiftYearDecadeAsync(page, year)) break;
            await page.WaitForTimeoutAsync(250);
        }
        return false;
    }

    private static Task<bool> ShiftYearDecadeAsync(IPage page, int year) =>
        page.EvaluateAsync<bool>(
            """
            (year) => {
              const visible = element => {
                if (!element) return false;
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0 && style.display !== 'none';
              };
              const header = Array.from(document.querySelectorAll('.ks-date-picker__header-label'))
                .filter(visible)
                .map(element => (element.innerText || '').replace(/\s+/g, ' ').trim())
                .find(Boolean) || '';
              const match = header.match(/(\d{4})\D+(\d{4})/);
              if (!match) return false;
              const start = Number(match[1]);
              const end = Number(match[2]);
              const previous = document.querySelector('.ks-date-picker__prev-btn');
              const next = document.querySelector('.ks-date-picker__next-btn');
              if (year < start && previous && visible(previous)) { previous.click(); return true; }
              if (year > end && next && visible(next)) { next.click(); return true; }
              return false;
            }
            """,
            year);

    private static async Task FillEnterprisePeopleAsync(IPage page, string workflowDirectory, CancellationToken cancellationToken)
    {
        var director = ReadPerson(workflowDirectory, "director_info_list", "男");
        var screenwriter = ReadPerson(workflowDirectory, "screenwriter_info_list", "女");
        var producer = screenwriter;
        await FillNamedPersonAsync(page, "导演", director.Name, director.Gender, required: true, cancellationToken);
        await FillNamedPersonAsync(page, "编剧", screenwriter.Name, screenwriter.Gender, required: false, cancellationToken);
        await FillNamedPersonAsync(page, "制片人", producer.Name, producer.Gender, required: false, cancellationToken);
    }

    private static async Task FillNamedPersonAsync(
        IPage page,
        string role,
        string name,
        string gender,
        bool required,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name)) return;
        await FillTextAsync(page, role + "姓名", name, required: false);
        await FillTextAsync(page, role, name, required: required);
        await SelectAsync(page, role + "性别", gender, required: false);
    }

    private static (string Name, string Gender) ReadPerson(string workflowDirectory, string property, string defaultGender)
    {
        var path = Path.Combine(workflowDirectory, "kuaishou-auto-fill.json");
        if (!File.Exists(path)) return (string.Empty, defaultGender);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(property, out var people) ||
                people.ValueKind != System.Text.Json.JsonValueKind.Array)
                return (string.Empty, defaultGender);
            foreach (var person in people.EnumerateArray())
            {
                if (person.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var name = person.TryGetProperty("name", out var nameValue) ? nameValue.GetString()?.Trim() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var gender = person.TryGetProperty("gender", out var genderValue) ? genderValue.GetString()?.Trim() : string.Empty;
                return (name, string.IsNullOrWhiteSpace(gender) ? defaultGender : gender);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return (string.Empty, defaultGender);
    }

    private static async Task<bool> DropdownPopupVisibleAsync(IPage page) =>
        await page.EvaluateAsync<bool>(
            """
            () => Array.from(document.querySelectorAll('.ks-select__popper,.ks-popper,.ant-select-dropdown,.el-select-dropdown,[role="listbox"]'))
              .some(element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0
                  && style.display !== 'none' && style.visibility !== 'hidden';
              });
            """);

    private static async Task<bool> ClickDropdownOptionOnceAsync(IPage page, string option)
    {
        var point = await page.EvaluateAsync<double[]?>(
            """
            (optionText) => {
              const normalize = value => String(value || '').replace(/\s+/g, ' ').trim();
              const compact = value => normalize(value).replace(/\s+/g, '');
              const target = normalize(optionText);
              const compactTarget = compact(optionText);
              if (!target) return null;
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0
                  && rect.bottom >= 0 && rect.right >= 0
                  && rect.top <= window.innerHeight && rect.left <= window.innerWidth
                  && style.display !== 'none' && style.visibility !== 'hidden';
              };
              const popups = Array.from(document.querySelectorAll(
                '.ks-select__popper,.ks-popper,.ant-select-dropdown,.el-select-dropdown,[role="listbox"]'
              )).filter(visible);
              for (const popup of popups) {
                const options = Array.from(popup.querySelectorAll(
                  'li.ks-select-dropdown__item,.ks-select-dropdown__item,[role="option"],.ant-select-item-option,.el-select-dropdown__item,li'
                )).filter(visible);
                const match = options.find(element => compact(element.innerText || element.textContent || '') === compactTarget);
                if (!match) continue;
                const rect = match.getBoundingClientRect();
                return [rect.left + rect.width / 2, rect.top + rect.height / 2];
              }
              return null;
            }
            """,
            option);
        if (point is not { Length: 2 }) return false;
        await page.Mouse.ClickAsync((float)point[0], (float)point[1]);
        await page.WaitForTimeoutAsync(200);
        return true;
    }

    private static async Task SetCheckboxAsync(IPage page, IReadOnlyList<string> labels)
    {
        var marked = await page.EvaluateAsync<bool>(
            """
            (labels) => {
              const compact = value => String(value || '').replace(/\s+/g, '');
              const wanted = labels.map(compact).filter(Boolean);
              document.querySelectorAll('[data-kuaishou-agreement]').forEach(element => element.removeAttribute('data-kuaishou-agreement'));
              const boxes = Array.from(document.querySelectorAll('input[type=checkbox]'));
              for (const checkbox of boxes) {
                let node = checkbox.parentElement;
                let text = '';
                for (let depth = 0; node && depth < 6; depth += 1, node = node.parentElement) {
                  const piece = compact(node.innerText || '');
                  if (piece.length > 80) break;
                  text = piece;
                  if (wanted.some(label => text.includes(label))) break;
                }
                if (!wanted.some(label => text.includes(label))) continue;
                const target = checkbox.closest('label, .ks-checkbox')?.querySelector('.ks-checkbox__inner, .ks-checkbox__input')
                  || checkbox.closest('label, .ks-checkbox')
                  || checkbox;
                target.setAttribute('data-kuaishou-agreement', checkbox.checked ? 'checked' : 'open');
                return true;
              }
              return false;
            }
            """,
            labels);
        if (!marked) throw new InvalidOperationException("未找到“付费短剧经营者服务协议”勾选框。");
        var box = page.Locator("[data-kuaishou-agreement]").First;
        if (await box.GetAttributeAsync("data-kuaishou-agreement") == "checked") return;
        await box.ClickAsync(new LocatorClickOptions { Timeout = 3_000, Force = true });
    }

    private static async Task EnsureSingleProofMaterialAsync(IPage page, string option)
    {
        var target = string.Concat(option.Where(character => !char.IsWhiteSpace(character)));
        var tags = await page.EvaluateAsync<string>(
            """
            (target) => {
              const compact = value => String(value || '').replace(/\s+/g, '').replace(/[×✕✖]+$/g, '');
              const items = Array.from(document.querySelectorAll('.ks-form-item, .ant-form-item, .el-form-item'));
              const item = items
                .filter(element => compact(element.querySelector('label, .ks-form-item__label, .ant-form-item-label')?.innerText || '').startsWith('版权证明材料'))
                .sort((left, right) => {
                  const leftBox = left.getBoundingClientRect();
                  const rightBox = right.getBoundingClientRect();
                  return leftBox.width * leftBox.height - rightBox.width * rightBox.height;
                })[0];
              if (!item) return '';
              return Array.from(item.querySelectorAll('.ks-tag'))
                .map(tag => compact(tag.innerText || ''))
                .filter(text => text.length > 0 && text.length <= 40 && text !== target)
                .join('\n');
            }
            """,
            target);
        if (!string.IsNullOrWhiteSpace(tags))
            throw new InvalidOperationException($"版权证明材料在后续步骤里又被多选了：{tags.Replace('\n', '、')}");
    }

    private static Task HandleCropDialogAsync(IPage page, int shrinkClicks, CancellationToken cancellationToken) =>
        KuaishouCropDialog.ConfirmAsync(page, shrinkClicks, cancellationToken);

    private static async Task<bool> ClickDropdownOptionAsync(IPage page, string option)
    {
        var point = await page.EvaluateAsync<double[]?>(
            """
            (optionText) => {
              const normalize = value => String(value || '').replace(/\s+/g, ' ').trim();
              const compact = value => normalize(value).replace(/\s+/g, '');
              const target = normalize(optionText);
              const compactTarget = compact(optionText);
              if (!target) return null;
              const visible = element => {
                const rect = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rect.width > 0 && rect.height > 0
                  && rect.bottom >= 0 && rect.right >= 0
                  && rect.top <= window.innerHeight && rect.left <= window.innerWidth
                  && style.display !== 'none' && style.visibility !== 'hidden';
              };
              const popups = Array.from(document.querySelectorAll(
                '.ks-select__popper,.ks-popper,.ant-select-dropdown,.el-select-dropdown,[role="listbox"]'
              )).filter(visible);
              for (const popup of popups) {
                const options = Array.from(popup.querySelectorAll(
                  'li.ks-select-dropdown__item,.ks-select-dropdown__item,[role="option"],.ant-select-item-option,.el-select-dropdown__item,li'
                )).filter(visible);
                const option = options.find(element => compact(element.innerText || element.textContent || '') === compactTarget)
                  || options.find(element => {
                    const text = normalize(element.innerText || element.textContent || '');
                    return text.includes(target) && text.length <= target.length + 12;
                  });
                if (!option) continue;
                option.click();
                const rect = option.getBoundingClientRect();
                return [rect.left + rect.width / 2, rect.top + rect.height / 2];
              }
              return null;
            }
            """,
            option);
        if (point is not { Length: 2 }) return false;
        try
        {
            await page.Mouse.ClickAsync((float)point[0], (float)point[1]);
        }
        catch (PlaywrightException)
        {
            // 选项自身的 click 已经发出。
        }
        await page.WaitForTimeoutAsync(200);
        return true;
    }

    private static async Task<ILocator?> FindScopeAsync(IPage page, string label)
    {
        var candidates = page.Locator(".ks-form-item, .ant-form-item, .el-form-item");
        var count = Math.Min(await candidates.CountAsync(), 80);
        ILocator? best = null;
        var bestArea = double.MaxValue;
        var target = Compact(label);
        for (var index = 0; index < count; index++)
        {
            var candidate = candidates.Nth(index);
            try
            {
                if (!await candidate.IsVisibleAsync()) continue;
                var labelNode = candidate.Locator(".ks-form-item__label, .ant-form-item-label, .el-form-item__label, label").First;
                if (await labelNode.CountAsync() == 0) continue;
                var labelText = Compact(await labelNode.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 800 }));
                if (!labelText.StartsWith(target, StringComparison.Ordinal) || labelText.Length > target.Length + 80)
                    continue;
                var box = await candidate.BoundingBoxAsync();
                if (box is null) continue;
                var area = box.Width * box.Height;
                if (area >= bestArea) continue;
                best = candidate;
                bestArea = area;
            }
            catch (PlaywrightException)
            {
                // 表单正在重绘时跳过这一项。
            }
        }
        return best;
    }

    private static string Compact(string? value) =>
        string.Concat((value ?? string.Empty).Where(character => !char.IsWhiteSpace(character) && character is not '*' and not '＊'));

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
