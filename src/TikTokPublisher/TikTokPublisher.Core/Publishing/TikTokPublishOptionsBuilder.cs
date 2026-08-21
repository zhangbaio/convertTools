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

        TikTokPublishConstants.ValidatePublishConfiguration(account);
        var options = TikTokPublishOptions.FromAccount(account);
        options.CopyrightMaterialTypes = TikTokPublishConstants.ValidateCopyrightMaterialTypes(
            options.CopyrightMaterialTypes);
        options.TargetAudienceMode = NormalizeTargetAudienceMode(account.TiktokTargetAudienceMode);
        options.PaidEnabled = TikTokPaidRatioService.DecidePaidForUpload(account, workflowProjectDir, log);
        options.CopyrightMaterialFilePath = string.Empty;
        options.CopyrightMaterialFilePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(workflowProjectDir))
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            if (TikTokPublishConstants.RequiresGeneratedProofMaterial(options.CopyrightMaterialTypes))
            {
                var proofMaterial = TikTokProofMaterialService.GetPdfPath(workflowProjectDir);
                options.CopyrightMaterialFilePath = proofMaterial;
                paths[TikTokPublishConstants.ProductionAgreementMaterialType] = proofMaterial;

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

            if (options.CopyrightMaterialTypes.Contains(
                    TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
                    StringComparer.Ordinal))
            {
                var timestampCertificate = Path.Combine(
                    workflowProjectDir,
                    TikTokTimestampCertificateService.OutputFileName);
                paths[TikTokPublishConstants.FilingOrDistributionLicenseMaterialType] = timestampCertificate;
                log?.Invoke(
                    File.Exists(timestampCertificate)
                        ? $"TikTok 备案/发行许可时间戳已就绪：{timestampCertificate}"
                        : $"TikTok 备案/发行许可时间戳等待生成：{timestampCertificate}");
            }

            if (options.CopyrightMaterialTypes.Contains(
                    TikTokPublishConstants.SourceFileInformationMaterialType,
                    StringComparer.Ordinal))
            {
                var sourceInfoDir = TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflowProjectDir);
                paths[TikTokPublishConstants.SourceFileInformationMaterialType] = sourceInfoDir;
                var fileCount = TikTokSourceFileInfoUploadPackageService.ListFiles(
                    workflowProjectDir,
                    options.UploadSourceInfoRoleSceneScreenshot).Count;
                var expectedFileCount = TikTokSourceFileInfoUploadPackageService.RequiredFileCount +
                                        (options.UploadSourceInfoRoleSceneScreenshot ? 1 : 0);
                log?.Invoke(
                    fileCount == expectedFileCount
                        ? $"TikTok 原始文件信息上传包已就绪：{fileCount} 个文件 → {sourceInfoDir}"
                        : $"TikTok 原始文件信息上传包等待生成：{sourceInfoDir}");
            }

            if (options.CopyrightMaterialTypes.Contains(
                    TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                    StringComparer.Ordinal))
            {
                var aiDir = TikTokAiGenerationScreenshotService.GetOutputDirectory(workflowProjectDir);
                paths[TikTokPublishConstants.AiGenerationScreenshotsMaterialType] = aiDir;
                var aiCount = TikTokAiGenerationScreenshotService.ListGeneratedImages(workflowProjectDir).Count;
                log?.Invoke(
                    aiCount >= TikTokAiGenerationScreenshotService.RequiredImageCount
                        ? $"TikTok AI 生成过程截图已就绪：{aiCount} 张 → {aiDir}"
                        : $"TikTok AI 生成过程截图等待生成：{aiDir}");

                if (options.UploadAiScriptOutlineWithScreenshots)
                {
                    options.AiScriptOutlineFilePath = Path.Combine(
                        workflowProjectDir,
                        TikTokAiScriptOutlineService.OutputFileName);
                    log?.Invoke(
                        File.Exists(options.AiScriptOutlineFilePath)
                            ? $"TikTok AI 剧本大纲已就绪：{options.AiScriptOutlineFilePath}"
                            : $"TikTok AI 剧本大纲等待生成：{options.AiScriptOutlineFilePath}");
                }
            }

            if (options.CopyrightMaterialTypes.Contains(
                    TikTokPublishConstants.EditingProjectFilesMaterialType,
                    StringComparer.Ordinal))
            {
                var editingDir = TikTokProjectImageService.GetOutputDirectory(workflowProjectDir);
                paths[TikTokPublishConstants.EditingProjectFilesMaterialType] = editingDir;
                var editingCount = TikTokProjectImageService.CountProjectImages(workflowProjectDir);
                log?.Invoke(
                    editingCount >= TikTokProjectImageService.MinUploadImageCount
                        ? $"TikTok 剪辑工程文件（工程图）已就绪：{editingCount} 张 → {editingDir}"
                        : $"TikTok 剪辑工程文件（工程图）等待生成：{editingDir}");
            }

            options.CopyrightMaterialFilePaths = paths;
        }

        return options;
    }

    private static string NormalizeTargetAudienceMode(string? mode)
    {
        var normalized = (mode ?? "ai_recommend").Trim();
        return normalized is "female" or "male" or "ai_recommend" ? normalized : "ai_recommend";
    }
}
