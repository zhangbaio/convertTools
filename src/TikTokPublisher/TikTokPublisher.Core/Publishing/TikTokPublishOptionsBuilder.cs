using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Publishing;

public static class TikTokPublishOptionsBuilder
{
    public static TikTokPublishOptions FromAccount(
        TikTokAccountProfile? account,
        string? workflowProjectDir = null,
        Action<string>? log = null)
    {
        if (account is null)
        {
            return new TikTokPublishOptions
            {
                TargetAudienceMode = "ai_recommend",
                GenreCount = 3,
            };
        }

        var options = TikTokPublishOptions.FromAccount(account);
        options.CopyrightMaterialTypes = TikTokPublishConstants.ValidateCopyrightMaterialTypes(
            options.CopyrightMaterialTypes);
        options.TargetAudienceMode = NormalizeTargetAudienceMode(account.TiktokTargetAudienceMode);
        options.PaidEnabled = TikTokPaidRatioService.DecidePaidForUpload(account, workflowProjectDir, log);
        options.CopyrightMaterialFilePath = string.Empty;
        options.CopyrightMaterialFilePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TikTokPublishConstants.RequiresGeneratedProofMaterial(options.CopyrightMaterialTypes) &&
            !string.IsNullOrWhiteSpace(workflowProjectDir))
        {
            var proofMaterial = TikTokProofMaterialService.GetPdfPath(workflowProjectDir);
            options.CopyrightMaterialFilePath = proofMaterial;
            options.CopyrightMaterialFilePaths = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TikTokPublishConstants.ProductionAgreementMaterialType] = proofMaterial,
            };

            if (File.Exists(proofMaterial))
            {
                TikTokProofMaterialPdfRenderService.ValidatePdf(proofMaterial);
                log?.Invoke($"TikTok 版权材料使用项目生成文件：{proofMaterial}");
            }
            else
            {
                // 上传清单等准备阶段也会构建参数；此处仅绑定规范路径，实际上传前会强制生成并校验。
                log?.Invoke($"TikTok 合作协议等待生成项目证明材料：{proofMaterial}");
            }
        }
        return options;
    }

    private static string NormalizeTargetAudienceMode(string? mode)
    {
        var normalized = (mode ?? "ai_recommend").Trim();
        return normalized is "female" or "male" or "ai_recommend" ? normalized : "ai_recommend";
    }
}
