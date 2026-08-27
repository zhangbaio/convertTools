using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Media;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Publishing;

namespace TikTokPublisher.Core.Services;

public static class TikTokMaterialValidationService
{
    public sealed class Options
    {
        public int Concurrency { get; init; } = 4;
        public IReadOnlySet<string> EnabledSteps { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public bool AllowMissingUploadVideos { get; init; }

        public static Options FromAccount(
            TikTokAccountProfile? account,
            ClientSettings? settings = null,
            QueueRunOptions? runOptions = null,
            bool allowMissingUploadVideos = false) => new()
        {
            Concurrency = Math.Clamp(settings?.TiktokMaterialValidateConcurrency ?? 4, 1, 16),
            EnabledSteps = (runOptions?.EnabledSteps ?? [])
                .ToHashSet(StringComparer.Ordinal),
            AllowMissingUploadVideos = allowMissingUploadVideos,
        };
    }

    public static async Task ValidateAsync(
        string sourceProjectDir,
        string title,
        string originalTitle,
        Options options,
        Action<string>? log,
        CancellationToken ct,
        TikTokAccountProfile? account = null)
    {
        ct.ThrowIfCancellationRequested();
        var payload = TikTokUploadStagingService.BuildPayload(
            sourceProjectDir, title, originalTitle,
            rebuildStaging: false, repairSmallVideos: false, log, ct);
        if (payload.UploadPaths.Count == 0)
        {
            payload = TikTokUploadStagingService.BuildPayload(
                sourceProjectDir, title, originalTitle,
                rebuildStaging: true, repairSmallVideos: false, log, ct);
        }

        if (payload.UploadPaths.Count == 0 && !options.AllowMissingUploadVideos)
            throw new InvalidOperationException("TikTok 素材校验失败：未找到可校验视频");

        if (payload.UploadPaths.Count == 0)
            log?.Invoke("成片检查：项目已上传且本机源视频已清理，跳过视频文件检查，继续检查上传材料。");

        if (payload.UploadPaths.Count != payload.SourcePaths.Count)
            throw new InvalidOperationException("TikTok 素材校验失败：上传副本数量异常");

        var ffprobe = MediaBinaryResolver.ResolveFfprobe();
        var issues = new List<string>();
        for (var index = 0; index < payload.SourcePaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = payload.SourcePaths[index];
            var uploadPath = payload.UploadPaths[index];
            var episode = index + 1;
            var name = Path.GetFileName(sourcePath);

            long fileSize;
            try { fileSize = new FileInfo(uploadPath).Length; }
            catch (Exception ex)
            {
                issues.Add($"第{episode}集 | {name} | 读大小失败（{ex.Message}）");
                continue;
            }

            if (fileSize < TikTokVideoConstraints.MinSizeBytes &&
                TikTokSmallVideoPaddingService.SupportsPadding(uploadPath))
            {
                try
                {
                    if (await TikTokSmallVideoPaddingService.PadWithoutReencodeAsync(uploadPath, log, ct))
                        fileSize = new FileInfo(uploadPath).Length;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"第{episode}集 | {name} | 小文件自动补齐失败（{ex.Message}）");
                }
            }

            MediaProbe probe;
            try { probe = await MediaProbe.ProbeAsync(ffprobe, uploadPath, ct); }
            catch (Exception ex)
            {
                issues.Add($"第{episode}集 | {name} | 读时长失败（{ex.Message}）");
                continue;
            }

            foreach (var message in ValidateVideoLimits(fileSize, probe.DurationSeconds))
                issues.Add($"第{episode}集 | {name} | {message}");

            if (issues.Any(i => i.StartsWith($"第{episode}集", StringComparison.Ordinal)))
                continue;

            log?.Invoke(
                $"通过：第{episode}集 | {name} | {TikTokSmallVideoPaddingService.FormatSize(fileSize)} | {FormatDuration(probe.DurationSeconds)}");
        }

        if (issues.Count > 0)
        {
            foreach (var message in issues)
                log?.Invoke($"失败：{message}");
            var preview = string.Join("；", issues.Take(5));
            var suffix = issues.Count > 5 ? $"；等 {issues.Count} 个问题" : "";
            throw new InvalidOperationException($"TikTok 素材校验失败：{preview}{suffix}");
        }

        if (payload.UploadPaths.Count > 0)
        {
            log?.Invoke($"通过：共 {payload.SourcePaths.Count} 个视频。");
            TikTokUploadManifestService.Save(sourceProjectDir, account, payload, log);
            SaveValidationState(sourceProjectDir, payload, options);
        }

        ValidateGeneratedUploadMaterials(sourceProjectDir, account, options, log);
    }

    internal static void ValidateGeneratedUploadMaterials(
        string sourceProjectDir,
        TikTokAccountProfile? account,
        Options options,
        Action<string>? log)
    {
        if (account is null) return;
        var materialTypes = TikTokPublishConstants.NormalizeCopyrightMaterialTypes(
            account.TiktokCopyrightMaterialTypes);
        if (materialTypes.Count == 0) return;

        var enabled = options.EnabledSteps;
        var workflow = ProjectWorkspaceService.LoadContext(sourceProjectDir).WorkflowProjectDir;
        var issues = new List<string>();
        var needsProofStep = TikTokPublishConstants.RequiresAutoGeneratedCopyrightMaterial(materialTypes);
        var proofEnabled = enabled.Contains(QueueStepRegistry.GenerateProofMaterial);
        if (needsProofStep && !proofEnabled)
        {
            issues.Add("账号已选择自动生成的证明材料，但本次未启用“生成证明材料”步骤");
        }

        if (proofEnabled && materialTypes.Contains(
                TikTokPublishConstants.ProductionAgreementMaterialType,
                StringComparer.Ordinal))
        {
            try
            {
                TikTokProofMaterialPdfRenderService.ValidatePdf(
                    TikTokProofMaterialService.GetPdfPath(workflow));
            }
            catch (Exception ex)
            {
                issues.Add($"合作协议无效：{ex.Message}");
            }
        }

        if (proofEnabled && materialTypes.Contains(
                TikTokPublishConstants.SourceFileInformationMaterialType,
                StringComparer.Ordinal))
        {
            var selection = TikTokSourceFileInfoPackageSelection.FromEnabledSteps(
                enabled,
                account.TiktokUploadSourceInfoRoleSceneScreenshot);
            try
            {
                TikTokSourceFileInfoUploadPackageService.Validate(
                    workflow,
                    account.TiktokUploadSourceInfoRoleSceneScreenshot,
                    selection);
            }
            catch (Exception ex)
            {
                issues.Add($"原始文件信息上传包无效：{ex.Message}");
            }
        }

        if (proofEnabled && materialTypes.Contains(
                TikTokPublishConstants.AiGenerationScreenshotsMaterialType,
                StringComparer.Ordinal))
        {
            var count = TikTokAiGenerationScreenshotService.ListGeneratedImages(workflow).Count;
            if (count < TikTokAiGenerationScreenshotService.RequiredImageCount)
                issues.Add($"AI 生成过程截图要求 {TikTokAiGenerationScreenshotService.RequiredImageCount} 张，当前 {count} 张");
        }

        if (proofEnabled && materialTypes.Contains(
                TikTokPublishConstants.EditingProjectFilesMaterialType,
                StringComparer.Ordinal))
        {
            var count = TikTokProjectImageService.CountProjectImages(workflow);
            if (count < TikTokProjectImageService.MinUploadImageCount)
                issues.Add($"剪辑工程文件要求至少 {TikTokProjectImageService.MinUploadImageCount} 张工程图，当前 {count} 张");
        }

        if (materialTypes.Contains(
                TikTokPublishConstants.FilingOrDistributionLicenseMaterialType,
                StringComparer.Ordinal))
        {
            if (!enabled.Contains(QueueStepRegistry.GenerateTimestampCertificate))
            {
                issues.Add("账号已选择备案/发行许可材料，但本次未启用“生成时间戳”步骤");
            }
            else if (!TikTokTimestampCertificateService.HasCurrentOutput(new QueueProjectItem
                     {
                         ProjectDir = sourceProjectDir,
                     }))
            {
                issues.Add("备案/发行许可时间戳认证证书缺失或无效");
            }
        }

        if (issues.Count > 0)
        {
            foreach (var issue in issues) log?.Invoke($"成片检查失败：{issue}");
            throw new InvalidOperationException("成片检查失败：" + string.Join("；", issues));
        }

        log?.Invoke("成片检查：账号所选上传材料与本次启用步骤一致，产物完整。");
    }

    public static bool HasCurrentGeneratedUploadMaterials(
        string sourceProjectDir,
        TikTokAccountProfile? account,
        Options options)
    {
        try
        {
            ValidateGeneratedUploadMaterials(sourceProjectDir, account, options, log: null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IEnumerable<string> ValidateVideoLimits(long fileSizeBytes, double durationSeconds)
    {
        if (fileSizeBytes < TikTokVideoConstraints.MinSizeBytes)
            yield return $"文件过小（{TikTokSmallVideoPaddingService.FormatSize(fileSizeBytes)} < {TikTokSmallVideoPaddingService.FormatSize(TikTokVideoConstraints.MinSizeBytes)}）";
        if (fileSizeBytes > TikTokVideoConstraints.MaxSizeBytes)
            yield return $"文件过大（{TikTokSmallVideoPaddingService.FormatSize(fileSizeBytes)} > {TikTokSmallVideoPaddingService.FormatSize(TikTokVideoConstraints.MaxSizeBytes)}）";
        if (durationSeconds < TikTokVideoConstraints.MinDurationSeconds)
            yield return $"时长过短（{FormatDuration(durationSeconds)} < {FormatDuration(TikTokVideoConstraints.MinDurationSeconds)}）";
        if (durationSeconds > TikTokVideoConstraints.MaxDurationSeconds)
            yield return $"时长过长（{FormatDuration(durationSeconds)} > {FormatDuration(TikTokVideoConstraints.MaxDurationSeconds)}）";
    }

    private static string FormatDuration(double seconds)
    {
        var total = Math.Max(0, (int)Math.Round(seconds));
        var minutes = total / 60;
        var rest = total % 60;
        return minutes > 0 ? $"{minutes}分{rest}秒" : $"{rest}秒";
    }

    private static void SaveValidationState(
        string sourceProjectDir,
        TikTokUploadStagingService.StagingResult payload,
        Options options)
    {
        var context = ProjectWorkspaceService.LoadContext(sourceProjectDir);
        var episodes = payload.UploadPaths
            .Select(path => Path.GetFileName(path) ?? "")
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, _ => (object?)true, StringComparer.Ordinal);

        var state = new Dictionary<string, object?>
        {
            ["fingerprint"] = ComputeMaterialFingerprint(payload.UploadPaths),
            ["params"] = ValidationParamsSignature(options),
            ["episodes"] = episodes,
        };
        ProjectStateDocumentStore.SaveDocument(
            context.WorkspaceRoot,
            context.SourceProjectDir,
            "material_validation_state",
            state,
            context.WorkflowProjectDir);
    }

    private static string ValidationParamsSignature(Options options) =>
        $"v2|material-only|{options.Concurrency}";

    internal static string ComputeMaterialFingerprint(IReadOnlyList<string> uploadVideoPaths)
    {
        var entries = new List<object?[]>();
        foreach (var path in uploadVideoPaths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return "";
                var mtimeNs = (long)(info.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks * 100L;
                entries.Add([info.Name, info.Length, mtimeNs]);
            }
            catch
            {
                return "";
            }
        }

        if (entries.Count == 0) return "";
        entries.Sort((a, b) => string.CompareOrdinal(a[0]?.ToString(), b[0]?.ToString()));
        var text = JsonSerializer.Serialize(entries);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public static bool HasCurrentValidationState(string sourceProjectDir)
    {
        try
        {
            var uploadPaths = ProjectVideoResolver.ResolveUploadVideos(
                sourceProjectDir,
                allowStagedFallback: true);
            if (uploadPaths.Count == 0)
                return false;

            var context = ProjectWorkspaceService.LoadContext(sourceProjectDir);
            var state = ProjectStateDocumentStore.LoadDocument(
                context.WorkspaceRoot,
                context.SourceProjectDir,
                "material_validation_state");
            if (!state.TryGetValue("fingerprint", out var fingerprintElement))
                return false;

            var savedFingerprint = fingerprintElement.ValueKind == JsonValueKind.String
                ? fingerprintElement.GetString()
                : "";
            if (string.IsNullOrWhiteSpace(savedFingerprint))
                return false;

            return string.Equals(
                savedFingerprint.Trim(),
                ComputeMaterialFingerprint(uploadPaths),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
