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
        options.TargetAudienceMode = NormalizeTargetAudienceMode(account.TiktokTargetAudienceMode);
        options.PaidEnabled = TikTokPaidRatioService.DecidePaidForUpload(account, workflowProjectDir, log);
        if (!string.IsNullOrWhiteSpace(workflowProjectDir))
        {
            var proofMaterial = TikTokProofMaterialService.GetPdfPath(workflowProjectDir);
            if (File.Exists(proofMaterial))
            {
                try
                {
                    TikTokProofMaterialPdfRenderService.ValidatePdf(proofMaterial);
                    options.CopyrightMaterialFilePath = proofMaterial;
                    log?.Invoke($"TikTok 版权材料使用项目生成文件：{proofMaterial}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // 项目证明材料无效或暂不可读时，保留账号手工配置的材料文件。
                }
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
