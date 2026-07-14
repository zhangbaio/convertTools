using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    internal static async Task ConfigureCopyrightProofAsync(
        IPage page,
        TikTokPublishOptions options,
        Action<string>? log,
        CancellationToken ct)
    {
        await SelectCopyrightRadioAsync(page, "是否原始权利人", options.IsOriginalRightsHolder ? "是" : "否", ct);
        await SelectCopyrightRadioAsync(
            page,
            "内容原创类型",
            string.Equals(options.ContentOriginalityType, "adapted", StringComparison.OrdinalIgnoreCase) ? "改编" : "原创",
            ct);

        var materialKeys = options.CopyrightMaterialTypes
            .Where(TikTokPublishConstants.CopyrightMaterialLabels.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (materialKeys.Count == 0)
            throw new InvalidOperationException("TikTok 上传材料类型未配置，请至少选择 1 个核心材料，或至少 2 个辅助材料。");

        var combo = await FindComboboxByFieldLabelAsync(page, ["上传材料类型"])
            ?? throw new InvalidOperationException("未找到 TikTok「上传材料类型」下拉框。");
        await combo.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await OpenComboboxAsync(page, combo, ct);

        foreach (var label in TikTokPublishConstants.CopyrightMaterialLabels.Values)
        {
            ct.ThrowIfCancellationRequested();
            var option = page.Locator("[role='tooltip'], [role='dialog'], .semi-portal")
                .Filter(new() { HasText = "核心材料" })
                .Locator("label, .semi-checkbox")
                .Filter(new() { HasText = label })
                .First;
            if (await option.CountAsync() == 0)
                option = page.Locator("label, .semi-checkbox").Filter(new() { HasText = label }).Last;
            if (await option.CountAsync() == 0) continue;

            var shouldSelect = materialKeys.Any(key =>
                string.Equals(TikTokPublishConstants.CopyrightMaterialLabels[key], label, StringComparison.Ordinal));
            var input = option.Locator("input[type='checkbox']").First;
            var isSelected = await input.CountAsync() > 0 && await input.IsCheckedAsync();
            if (isSelected != shouldSelect)
            {
                await ClickWithFallbackAsync(option, ct);
                await page.WaitForTimeoutAsync(150);
            }
        }
        await ClosePopupIfOpenAsync(page);

        var filePath = options.CopyrightMaterialFilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("TikTok 版权材料测试文件不存在，请在账号发布配置中设置材料文件路径。", filePath);

        foreach (var key in materialKeys)
        {
            var label = TikTokPublishConstants.CopyrightMaterialLabels[key];
            var field = page.Locator("div").Filter(new() { HasText = label + "*" })
                .Filter(new() { Has = page.Locator("input[type='file']") })
                .Last;
            if (await field.CountAsync() == 0)
                throw new InvalidOperationException($"已选择版权材料「{label}」，但未找到对应文件选择器。");
            var input = field.Locator("input[type='file']").First;
            await input.SetInputFilesAsync(Path.GetFullPath(filePath), new() { Timeout = 30000 });
            Log(log, $"TikTok 版权材料已上传：{label}（{Path.GetFileName(filePath)}）");
        }
    }

    private static async Task SelectCopyrightRadioAsync(IPage page, string fieldLabel, string optionLabel, CancellationToken ct)
    {
        var selected = await page.EvaluateAsync<bool>(
            """
            ({ fieldLabel, optionLabel }) => {
              const normalize = value => (value || '').replace(/\s+/g, '').replace(/\*/g, '');
              const headings = Array.from(document.querySelectorAll('body *'))
                .filter(node => normalize(node.textContent) === normalize(fieldLabel));
              for (const heading of headings) {
                let root = heading;
                for (let i = 0; i < 5 && root; i++, root = root.parentElement) {
                  const radios = Array.from(root.querySelectorAll('input[type=radio]'));
                  if (!radios.length) continue;
                  const target = radios.find(radio => {
                    const label = radio.closest('label');
                    return normalize(label?.textContent) === normalize(optionLabel);
                  });
                  if (!target) continue;
                  if (!target.checked) (target.closest('label') || target).click();
                  return true;
                }
              }
              return false;
            }
            """,
            new { fieldLabel, optionLabel });
        ct.ThrowIfCancellationRequested();
        if (!selected)
            throw new InvalidOperationException($"未找到 TikTok「{fieldLabel}」的「{optionLabel}」选项。");
        await page.WaitForTimeoutAsync(150);
    }
}
