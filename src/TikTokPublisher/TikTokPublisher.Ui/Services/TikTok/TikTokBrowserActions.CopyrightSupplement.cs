using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private const string EditableCopyrightProofFieldSelector =
        "[x-field-id='copyrightProof.isOriginalRightsHolder'], [x-field-id='copyrightProof.selectedMaterialTypes']";

    private static readonly string[] CopyrightTabLabels = ["版权证明", "版权信息"];

    // config.copyrightProof.materials[].materialType in the series detail API.
    internal static readonly IReadOnlyDictionary<int, string> CopyrightMaterialTypeByApiValue =
        new Dictionary<int, string>
        {
            [2] = TikTokPublishConstants.ProductionAgreementMaterialType,
            [3] = "work_registration_certificate",
            [4] = TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
            [5] = "opening_ending_rights_notice",
            [6] = TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
            [7] = TikTokPublishConstants.EditingProjectFilesMaterialType,
            [8] = TikTokPublishConstants.SourceFileInformationMaterialType,
        };

    /// <summary>
    /// Draft series open the full edit form (copyright section inline); any other status opens
    /// the detail view where copyright lives under a tab and is editable only while TikTok
    /// still accepts supplementary material.
    /// </summary>
    internal static async Task OpenEditableCopyrightProofAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        var located = await WaitUntilAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            if (await page.Locator("#semiTabcopyright, #copyright-proof-section").CountAsync() > 0)
                return true;
            foreach (var label in CopyrightTabLabels)
            {
                if (await page.GetByRole(AriaRole.Tab, new() { Name = label, Exact = true }).CountAsync() > 0)
                    return true;
            }
            return false;
        }, 30000, 300, ct);
        if (!located)
            throw new InvalidOperationException("TikTok 剧集页面未找到「版权信息/版权证明」区域。");

        var tab = page.Locator("#semiTabcopyright").First;
        if (await tab.CountAsync() == 0)
        {
            foreach (var label in CopyrightTabLabels)
            {
                var byRole = page.GetByRole(AriaRole.Tab, new() { Name = label, Exact = true }).First;
                if (await byRole.CountAsync() > 0)
                {
                    tab = byRole;
                    break;
                }
            }
        }

        if (await tab.CountAsync() > 0)
        {
            await tab.ClickAsync(new() { Timeout = 15000 });
        }
        else
        {
            await page.Locator("#copyright-proof-section").First
                .ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        }

        var editable = await WaitUntilAsync(async () =>
        {
            try { return await page.Locator(EditableCopyrightProofFieldSelector).CountAsync() > 0; }
            catch { return false; }
        }, 15000, 300, ct);
        if (!editable)
        {
            var bodyText = NormalizeWhitespace(await SafeInnerTextAsync(page.Locator("body")));
            var reason = bodyText.Contains("版权核验未通过", StringComparison.Ordinal)
                ? "平台已最终驳回版权核验"
                : "版权信息当前为只读（可能正在审核或已审核通过）";
            throw new InvalidOperationException(
                $"TikTok 当前剧集不允许补充版权材料：{reason}，页面没有可编辑的版权证明表单。");
        }

        Log(log, "已进入可编辑的版权证明区域；本任务不会修改剧集信息、视频、商业模式或价格。");
    }

    /// <summary>
    /// Supplement mode (after publish) renders its own 「提交版权证明材料」 button instead of
    /// the regular save/submit footer. Returns false when that button is not on the page.
    /// </summary>
    internal static async Task<bool> TrySubmitStandaloneCopyrightProofAsync(
        IPage page,
        FinalAction finalAction,
        Action<string>? log,
        CancellationToken ct)
    {
        var button = page.Locator("[class*='submitCopyrightProofRow'] button").First;
        if (await button.CountAsync() == 0)
            return false;

        if (finalAction == FinalAction.Draft)
        {
            throw new InvalidOperationException(
                "当前剧集处于版权材料补充状态，平台只提供「提交版权证明材料」，没有保存草稿；" +
                "账号提交动作为「保存草稿」，已停止且未提交。");
        }

        await WaitBeforeSubmitAsync(log, ct).ConfigureAwait(false);
        await DismissFloatingAssistantAsync(page, log);
        await button.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await WaitSubmitEnabledAsync(button, log, ct, timeoutSeconds: 120);
        await button.ClickAsync(new() { Timeout = 15000 });
        await ConfirmSubmitDialogIfPresentAsync(page, log, ct);
        await page.WaitForTimeoutAsync(1500);
        Log(log, "TikTok 已点击「提交版权证明材料」，等待落库复查。");
        return true;
    }

    internal static string ExtractSeriesIdFromUrl(string? url)
    {
        var match = Regex.Match(url ?? string.Empty, @"/(\d{16,20})(?:[/?#]|$)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    internal sealed record SeriesCopyrightMaterials(
        IReadOnlyDictionary<string, int> FileCounts,
        IReadOnlyList<int> UnknownApiTypes);

    internal static SeriesCopyrightMaterials ParseSeriesCopyrightMaterials(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.Number &&
            code.GetInt32() != 0)
        {
            throw new InvalidOperationException($"TikTok 剧集详情接口返回错误码 {code.GetInt32()}。");
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknown = new List<int>();
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("collection", out var collection))
        {
            throw new InvalidOperationException("TikTok 剧集详情接口缺少 collection。");
        }

        if (collection.TryGetProperty("config", out var config) &&
            config.ValueKind == JsonValueKind.Object &&
            config.TryGetProperty("copyrightProof", out var proof) &&
            proof.ValueKind == JsonValueKind.Object &&
            proof.TryGetProperty("materials", out var materials) &&
            materials.ValueKind == JsonValueKind.Array)
        {
            foreach (var material in materials.EnumerateArray())
            {
                if (!material.TryGetProperty("materialType", out var typeElement))
                    continue;
                var apiType = typeElement.ValueKind == JsonValueKind.Number
                    ? typeElement.GetInt32()
                    : int.TryParse(typeElement.GetString(), out var parsed) ? parsed : 0;
                var fileCount = material.TryGetProperty("files", out var files) &&
                                files.ValueKind == JsonValueKind.Array
                    ? files.GetArrayLength()
                    : 0;
                if (!CopyrightMaterialTypeByApiValue.TryGetValue(apiType, out var key))
                {
                    unknown.Add(apiType);
                    continue;
                }
                counts[key] = counts.GetValueOrDefault(key) + fileCount;
            }
        }

        return new SeriesCopyrightMaterials(counts, unknown);
    }

    /// <summary>Returns null when the saved materials match the configuration.</summary>
    internal static string? DescribeCopyrightMaterialMismatch(
        SeriesCopyrightMaterials saved,
        IReadOnlyCollection<string> configured,
        bool requireFullRebuild)
    {
        var problems = new List<string>();
        var missing = configured
            .Where(key => saved.FileCounts.GetValueOrDefault(key) == 0)
            .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key])
            .ToArray();
        if (missing.Length > 0)
            problems.Add($"缺少：{string.Join("、", missing)}");

        if (requireFullRebuild)
        {
            var leftovers = TikTokPublishConstants.AutoManagedCopyrightMaterialTypes
                .Where(key => !configured.Contains(key) && saved.FileCounts.GetValueOrDefault(key) > 0)
                .Select(key => TikTokPublishConstants.CopyrightMaterialLabels[key])
                .ToArray();
            if (leftovers.Length > 0)
                problems.Add($"未配置的材料仍有附件：{string.Join("、", leftovers)}");
        }

        return problems.Count == 0 ? null : string.Join("；", problems);
    }

    internal static async Task<SeriesCopyrightMaterials?> TryReadSeriesCopyrightMaterialsAsync(
        IPage page,
        string seriesId,
        Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
            return null;
        try
        {
            var json = await page.EvaluateAsync<string>(
                """
                async id => {
                  const response = await fetch('/api/content-partner/series/' + id, { credentials: 'include' });
                  if (!response.ok) return JSON.stringify({ code: response.status });
                  return await response.text();
                }
                """,
                seriesId);
            return ParseSeriesCopyrightMaterials(json);
        }
        catch (Exception ex)
        {
            Log(log, $"读取 TikTok 剧集版权材料接口失败，改用页面复查：{ex.Message}");
            return null;
        }
    }
}
