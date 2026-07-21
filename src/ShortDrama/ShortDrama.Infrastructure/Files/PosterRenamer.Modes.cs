using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;

namespace ShortDrama.Infrastructure.Files;

public sealed partial class PosterRenamer
{
    private const string DefaultPosterTitleErasePrompt = """
这是海报标题区域去字任务，只处理遮罩区域。
请移除遮罩区域内所有标题文字、汉字、描边、阴影、发光和文字装饰。
用周围背景自然补全遮罩区域，保持原海报风格、光影、纹理和构图连续。
不要生成任何新文字、符号、logo、水印、印章或标记。
遮罩区域外的所有人物、背景、颜色、清晰度和构图必须保持不变。
""";

    private const string DefaultPosterAllTextErasePrompt = """
这是海报全图文字清理任务，不是重新设计或重绘海报。
请删除图片中所有可读文字和文字残影，包括主标题、副标题、人物或角色姓名、演员名、作者、改编或来源说明、版权或出品信息、宣传语、季数、字幕、水印、Logo文字、角标，以及任何中文、英文、拼音、字母和数字文字。
用每处文字周围的背景、纹理和光影自然补全被删除区域，不能留下描边、底纹、模糊字或残影。
不得生成任何新文字、符号、Logo、水印、印章或标记。
人物、脸部、身体、服装、发型、动作、道具、背景、构图、尺寸、比例、颜色、光影和清晰度必须保持不变。
最终输出必须是一张完全无文字的干净海报底图。
""";

    private const string DefaultPosterTitleVerifyAiRetryPrompt = """
    输入图是当前海报标题区域的局部图。删除其中全部旧文字、错字、重复字和文字残影，然后只写一次以下目标标题：{title}
    严格保持目标标题中给出的换行，可排成一至三行；保持阅读顺序清楚、间距均匀、位置居中。
    使用标准、清晰、易识别的简体中文印刷粗体，不要引号、行号、副标题、拼音、英文、logo、水印或其他文字。
    标题必须醒目并占据标题区域的主要空间，禁止缩成角落小字；横排汉字高度至少为局部图高度的 18%，竖排汉字宽度至少为局部图宽度的 22%。
    局部图中的背景、光影、色彩和构图保持不变，输出相同的标题区域局部图。
    标题必须逐字准确，不得漏字、错字、换字、增字或重复。
    """;

    private readonly PosterTitleVerificationService _titleVerifier = new(new HttpClient { Timeout = TimeSpan.FromMinutes(2) });

    private static void Log(PosterRenameRequest request, string message) => request.Log?.Invoke(message);

    private static string NormalizePosterMode(string? value)
    {
        var mode = (value ?? "original").Trim().ToLowerInvariant();
        return mode switch
        {
            "ai" => "poster_ai_edit",
            _ => mode,
        };
    }

    private async Task TryGeneratePosterWithVerificationAsync(
        string configFile,
        string inputPath,
        string outputPath,
        string title,
        PosterLayout layout,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        await TryGeneratePosterWithAiAsync(configFile, inputPath, outputPath, title, layout, cancellationToken);

        var config = KeyValueConfigReader.Read(configFile);
        if (!IsPosterTitleVerifyEnabled(config))
            return;

        var verificationLayout = layout;
        try
        {
            verificationLayout = await DetectPosterLayoutAsync(
                configFile,
                outputPath,
                title,
                cancellationToken).ConfigureAwait(false);
            Log(request, "已基于AI首图重新识别实际标题位置。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AI首图标题位置未重新定位，改用生成前布局继续复核。 output={Output}", outputPath);
            Log(request, "AI首图标题位置暂未重新定位，改用生成前布局继续复核。");
        }

        var verifyMode = PosterTitleVerifyModeHelper.Normalize(config.GetValueOrDefault("PosterTitleVerifyMode"));
        var verifyResult = await VerifyTitleWithFullImageConfirmationAsync(
            config,
            outputPath,
            title,
            verificationLayout,
            request,
            cancellationToken).ConfigureAwait(false);
        if (verifyResult.Ok && verificationLayout.FontScale < 0.06f)
        {
            verifyResult = PosterTitleVerifyResult.Fail(
                $"AI标题字号过小：检测比例 {verificationLayout.FontScale:P1}，最低要求 6.0%");
            Log(request, $"AI海报标题文字正确，但字号过小（{verificationLayout.FontScale:P1}），改用PIL固定模板绘制。");
        }

        if (verifyResult.Ok)
        {
            if (!string.IsNullOrWhiteSpace(verifyResult.DetectedTitle))
                Log(request, $"AI 海报标题校验通过：{verifyResult.DetectedTitle}");
            else
                Log(request, $"AI 海报标题校验通过：{title}");
            return;
        }

        if (verifyResult.IsInconclusive)
        {
            if (PosterTitleVerifyModeHelper.ShouldRepaintInconclusive(verifyMode))
            {
                Log(request, $"AI标题校验无法确认，按兜底模式进入AI去字+PIL确定性重绘：{verifyResult.Reason}");
                await FallbackRepaintVerifiedTitleAsync(
                    config,
                    outputPath,
                    title,
                    verificationLayout,
                    verifyResult,
                    request,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (verifyMode == "blocking")
                throw new InvalidOperationException($"AI 海报标题校验无法确认：{verifyResult.Reason}");

            Log(request, "AI标题校验暂未得到确定结果，已保留首张AI海报并跳过自动改字。");
            return;
        }

        if (verifyMode == "fallback_repaint")
        {
            Log(request, $"AI标题校验未通过，改用AI去字+PIL固定模板绘制：{verifyResult.Reason}");
            await FallbackRepaintVerifiedTitleAsync(
                config,
                outputPath,
                title,
                verificationLayout,
                verifyResult,
                request,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await TryCleanupDetectedResidualTextAsync(
                config,
                outputPath,
                title,
                verificationLayout,
                verifyResult,
                verifyMode,
                request,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await TryRepairTitleWithAiRetriesAsync(
                config,
                outputPath,
                title,
                verificationLayout,
                verifyResult,
                request,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var reason = string.IsNullOrWhiteSpace(verifyResult.Reason)
            ? "标题校验未通过"
            : verifyResult.Reason;
        if (!string.IsNullOrWhiteSpace(verifyResult.DetectedTitle))
            reason = $"{reason}（识别标题：{verifyResult.DetectedTitle}）";

        if (verifyMode == "warn")
        {
            Log(request, $"AI 海报标题校验失败：{reason}，已按配置跳过阻断。");
            return;
        }

        if (verifyMode == "blocking")
            throw new InvalidOperationException($"AI 海报标题校验失败：{reason}");

        if (verifyMode == "image2_regenerate")
        {
            try
            {
                await Image2RegenerateVerifiedTitleAsync(
                    config, outputPath, title, verificationLayout, verifyResult, request, cancellationToken);
                var secondVerify = await VerifyTitleWithFullImageConfirmationAsync(
                    config,
                    outputPath,
                    title,
                    verificationLayout,
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (secondVerify.Ok)
                {
                    Log(request, "AI标题已通过 Image2 自动修复并通过校验");
                    return;
                }

                verifyResult = secondVerify;
                Log(request, $"Image2 重生成后二次校验未通过，改用AI去字+PIL重绘兜底：{secondVerify.Reason}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log(request, $"Image2 重生成失败，改用AI去字+PIL重绘兜底：{ex.Message}");
            }
        }

        await FallbackRepaintVerifiedTitleAsync(
            config, outputPath, title, verificationLayout, verifyResult, request, cancellationToken);
    }

    private async Task GenerateErasePilTitlePosterAsync(
        PosterRenameRequest request,
        string inputPath,
        string outputPath,
        string title,
        string configFile,
        CancellationToken cancellationToken)
    {
        Log(request, "使用 AI去字+PIL重绘 模式生成海报…");
        var renderInputPath = await PrepareRenderableInputAsync(inputPath, cancellationToken);
        try
        {
            var layout = await DetectPosterLayoutAsync(configFile, renderInputPath, title, cancellationToken);
            await WriteGeneratedPosterCandidateAsync(renderInputPath, await File.ReadAllBytesAsync(renderInputPath, cancellationToken), outputPath, cancellationToken);
            var config = KeyValueConfigReader.Read(configFile);
            var verifyEnabled = IsPosterTitleVerifyEnabled(config);
            var verifyMode = PosterTitleVerifyModeHelper.Normalize(config.GetValueOrDefault("PosterTitleVerifyMode"));
            var finalVerify = await FallbackRepaintVerifiedTitleAsync(
                config,
                outputPath,
                title,
                layout,
                PosterTitleVerifyResult.Fail("封面生成模式：AI消除文字+PIL绘制新标题"),
                request,
                cancellationToken,
                forceFullTextCleanup: true,
                verifyEnabled: verifyEnabled,
                throwOnResidual: verifyMode != "warn");
            if (verifyEnabled && !finalVerify.Ok && verifyMode == "blocking")
                throw new InvalidOperationException($"海报文字清理后标题校验仍未通过：{finalVerify.Reason}");
            if (verifyEnabled && !finalVerify.Ok && verifyMode == "warn")
                Log(request, $"海报文字清理后复核仍有差异，已按“仅警告”配置保留结果：{finalVerify.Reason}");
            Log(request, $"已生成AI去字+PIL绘制标题封面：{Path.GetFileName(outputPath)}");
        }
        finally
        {
            if (!string.Equals(renderInputPath, inputPath, StringComparison.Ordinal) && File.Exists(renderInputPath))
                File.Delete(renderInputPath);
        }
    }

    private async Task GenerateCoverFromPosterAsync(
        PosterRenameRequest request,
        string inputPath,
        string outputPath,
        string title,
        string configFile,
        CancellationToken cancellationToken)
    {
        var renderInputPath = await PrepareRenderableInputAsync(inputPath, cancellationToken);
        try
        {
            using var posterImage = await Image.LoadAsync<Rgba32>(renderInputPath, cancellationToken);
            var posterW = posterImage.Width;
            var posterH = posterImage.Height;
            Log(request, $"原始封面：{Path.GetFileName(inputPath)} ({posterW}x{posterH})");

            var config = KeyValueConfigReader.Read(configFile);
            var promptTemplate = GetOptional(config, "FrameCoverPrompt") ?? DefaultPosterGenerationPrompt;
            var prompt = MergePromptWithGuardrails(RenderPromptTemplate(
                promptTemplate,
                CreatePosterPromptVariables(title, title, null, null)),
                title);
            var apiSize = PosterCoverFrameSizeHelper.ResolveFrameApiSize(posterW, posterH, config);
            Log(request, $"AI 生成中：请求尺寸 {apiSize}");

            var resultBytes = await AiEditFrameAsync(posterImage, prompt, config, apiSize, request, cancellationToken);
            await ResizeAndSaveAsync(resultBytes, posterW, posterH, outputPath, cancellationToken);
            Log(request, $"已生成封面：{Path.GetFileName(outputPath)} ({posterW}x{posterH})");

            if (!IsPosterTitleVerifyEnabled(config))
                return;

            var layout = await DetectPosterLayoutAsync(configFile, outputPath, title, cancellationToken);
            var verifyResult = await VerifyTitleWithFullImageConfirmationAsync(
                config,
                outputPath,
                title,
                layout,
                request,
                cancellationToken).ConfigureAwait(false);
            if (verifyResult.Ok)
            {
                Log(request, $"AI 封面标题校验通过：{verifyResult.DetectedTitle ?? title}");
                return;
            }

            var verifyMode = PosterTitleVerifyModeHelper.Normalize(config.GetValueOrDefault("PosterTitleVerifyMode"));
            if (verifyResult.IsInconclusive)
            {
                if (PosterTitleVerifyModeHelper.ShouldRepaintInconclusive(verifyMode))
                {
                    Log(request, $"AI封面标题校验无法确认，按兜底模式进入AI去字+PIL确定性重绘：{verifyResult.Reason}");
                    await FallbackRepaintVerifiedTitleAsync(
                        config,
                        outputPath,
                        title,
                        layout,
                        verifyResult,
                        request,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (verifyMode == "blocking")
                    throw new InvalidOperationException($"AI 封面标题校验无法确认：{verifyResult.Reason}");

                Log(request, "AI封面标题校验暂未得到确定结果，已保留首张AI封面并跳过自动改字。");
                return;
            }

            if (await TryCleanupDetectedResidualTextAsync(
                    config,
                    outputPath,
                    title,
                    layout,
                    verifyResult,
                    verifyMode,
                    request,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (await TryRepairTitleWithAiRetriesAsync(
                    config,
                    outputPath,
                    title,
                    layout,
                    verifyResult,
                    request,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var reason = verifyResult.Reason;
            if (!string.IsNullOrWhiteSpace(verifyResult.DetectedTitle))
                reason = $"{reason}（识别标题：{verifyResult.DetectedTitle}）";
            if (verifyMode == "warn")
            {
                Log(request, $"AI 封面标题校验失败：{reason}，已按配置跳过阻断。");
                return;
            }

            if (verifyMode == "blocking")
                throw new InvalidOperationException($"AI 封面标题校验失败：{reason}");

            await FallbackRepaintVerifiedTitleAsync(
                config, outputPath, title, layout, verifyResult, request, cancellationToken);
        }
        finally
        {
            if (!string.Equals(renderInputPath, inputPath, StringComparison.Ordinal) && File.Exists(renderInputPath))
                File.Delete(renderInputPath);
        }
    }

    private async Task<bool> TryCleanupDetectedResidualTextAsync(
        IReadOnlyDictionary<string, string> config,
        string outputPath,
        string title,
        PosterLayout layout,
        PosterTitleVerifyResult verifyResult,
        string verifyMode,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        if (!verifyResult.HasResidualText)
            return false;

        Log(request, "检测到目标剧名外的文字，优先执行全图去字并重绘目标剧名。");
        try
        {
            var cleanupVerify = await FallbackRepaintVerifiedTitleAsync(
                config,
                outputPath,
                title,
                layout,
                verifyResult,
                request,
                cancellationToken,
                forceFullTextCleanup: true).ConfigureAwait(false);
            if (!cleanupVerify.Ok && verifyMode == "blocking")
                throw new InvalidOperationException($"海报文字清理后标题校验仍未通过：{cleanupVerify.Reason}");
            if (!cleanupVerify.Ok && verifyMode == "warn")
                Log(request, $"海报文字已自动清理，但标题复核仍有差异，已按“仅警告”配置保留结果：{cleanupVerify.Reason}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && verifyMode == "warn")
        {
            Log(request, $"海报其他文字自动清理后仍未通过复核，已按“仅警告”配置保留结果：{ex.Message}");
        }

        return true;
    }

    private async Task<PosterTitleVerifyResult> FallbackRepaintVerifiedTitleAsync(
        IReadOnlyDictionary<string, string> config,
        string outputPath,
        string title,
        PosterLayout layout,
        PosterTitleVerifyResult initialVerify,
        PosterRenameRequest request,
        CancellationToken cancellationToken,
        bool forceFullTextCleanup = false,
        bool verifyEnabled = true,
        bool throwOnResidual = true)
    {
        Log(request, "标题全图复核确认需要修复，开始AI去字+PIL确定性重绘。");
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var candidateSnapshotKind = forceFullTextCleanup ? "source_before_cleanup" : "failed_ai_candidate";
        var candidateSnapshotPath = Path.Combine(
            outputDirectory,
            forceFullTextCleanup ? "海报处理_去字前原图.png" : "海报处理_AI失败.png");
        var obsoleteDiagnosticPath = Path.Combine(
            outputDirectory,
            forceFullTextCleanup ? "海报处理_AI失败.png" : "海报处理_去字前原图.png");
        if (File.Exists(obsoleteDiagnosticPath))
            File.Delete(obsoleteDiagnosticPath);
        var titleMaskPath = Path.Combine(
            outputDirectory,
            "海报处理_标题遮罩.png");
        var erasedPath = Path.Combine(
            outputDirectory,
            "海报处理_已清除标题.png");
        var verifyDebugPath = Path.Combine(
            outputDirectory,
            "海报处理_标题校验.json");

        try
        {
            using (var candidateSnapshot = await Image.LoadAsync<Rgba32>(outputPath, cancellationToken).ConfigureAwait(false))
                await candidateSnapshot.SaveAsPngAsync(candidateSnapshotPath, cancellationToken).ConfigureAwait(false);
            Log(request, forceFullTextCleanup
                ? $"已保留去字前原图用于诊断：{Path.GetFileName(candidateSnapshotPath)}"
                : $"已保留AI失败候选用于诊断：{Path.GetFileName(candidateSnapshotPath)}");

            var maskBytes = await CreateTitleMaskAsync(outputPath, layout, cancellationToken).ConfigureAwait(false);
            if (maskBytes is not null)
            {
                await File.WriteAllBytesAsync(titleMaskPath, maskBytes, cancellationToken).ConfigureAwait(false);
                Log(request, $"已保留标题区域遮罩：{Path.GetFileName(titleMaskPath)}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(request, $"保留标题校验失败候选时出错，继续执行兜底：{ex.Message}");
        }

        await SavePosterTitleVerifyDebugAsync(
            verifyDebugPath,
            title,
            candidateSnapshotPath,
            candidateSnapshotKind,
            titleMaskPath,
            erasedPath,
            layout,
            initialVerify,
            finalVerify: null,
            request,
            cancellationToken).ConfigureAwait(false);

        var eraseAllText = forceFullTextCleanup || initialVerify.HasResidualText;
        var erasePrompt = eraseAllText
            ? DefaultPosterAllTextErasePrompt
            : GetOptional(config, "PosterTitleErasePrompt") ?? DefaultPosterTitleErasePrompt;
        var erasedBytes = await GenerateTitleErasedPosterBytesAsync(
            config, outputPath, layout, erasePrompt, eraseAllText, cancellationToken);
        await WriteGeneratedPosterCandidateAsync(outputPath, erasedBytes, erasedPath, cancellationToken);
        Log(request, $"已生成AI去字底图：{Path.GetFileName(erasedPath)}");

        PosterTitleProgrammaticRenderer.Render(
            erasedPath,
            outputPath,
            title,
            ToTitleLayout(layout));
        Log(request, $"已使用PIL重绘标准标题：{Path.GetFileName(outputPath)}");

        if (!verifyEnabled)
        {
            Log(request, "海报标题校验已关闭，跳过生成后的视觉复核。");
            return new PosterTitleVerifyResult(true, title, "海报标题校验已关闭");
        }

        var finalVerify = await VerifyTitleWithFullImageConfirmationAsync(
            config,
            outputPath,
            title,
            layout,
            request,
            cancellationToken).ConfigureAwait(false);

        if (finalVerify.HasResidualText)
        {
            Log(request, "首次去字后仍检测到其他文字，执行最后一次全图去字重试。");
            var retryErasedBytes = await GenerateTitleErasedPosterBytesAsync(
                config,
                outputPath,
                layout,
                DefaultPosterAllTextErasePrompt,
                eraseAllText: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await WriteGeneratedPosterCandidateAsync(
                outputPath,
                retryErasedBytes,
                erasedPath,
                cancellationToken).ConfigureAwait(false);
            PosterTitleProgrammaticRenderer.Render(
                erasedPath,
                outputPath,
                title,
                ToTitleLayout(layout));
            finalVerify = await VerifyTitleWithFullImageConfirmationAsync(
                config,
                outputPath,
                title,
                layout,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        await SavePosterTitleVerifyDebugAsync(
            verifyDebugPath,
            title,
            candidateSnapshotPath,
            candidateSnapshotKind,
            titleMaskPath,
            erasedPath,
            layout,
            initialVerify,
            finalVerify,
            request,
            cancellationToken).ConfigureAwait(false);
        if (finalVerify.Ok)
            Log(request, "AI标题已自动重绘并通过校验");
        else if (finalVerify.HasResidualText && throwOnResidual)
        {
            throw new InvalidOperationException(
                $"海报全图去字重试后仍存在目标剧名外的文字：{(string.IsNullOrWhiteSpace(finalVerify.ResidualText) ? finalVerify.Reason : finalVerify.ResidualText)}");
        }
        else
            Log(request, $"AI海报标题已用PIL确定性重绘完成；复核仍有差异，已保留确定性结果：{finalVerify.Reason}");

        return finalVerify;
    }

    private static async Task SavePosterTitleVerifyDebugAsync(
        string debugPath,
        string title,
        string candidateSnapshotPath,
        string candidateSnapshotKind,
        string titleMaskPath,
        string erasedPath,
        PosterLayout layout,
        PosterTitleVerifyResult initialVerify,
        PosterTitleVerifyResult? finalVerify,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new
            {
                targetTitle = title,
                candidateSnapshotPath,
                candidateSnapshotKind,
                titleMaskPath,
                erasedPath,
                layout = new
                {
                    layout.X,
                    layout.Y,
                    layout.Width,
                    layout.Height,
                    layout.FontScale,
                    layout.Align,
                },
                initialVerify = new
                {
                    initialVerify.Ok,
                    initialVerify.IsInconclusive,
                    initialVerify.DetectedTitle,
                    initialVerify.HasResidualText,
                    initialVerify.ResidualText,
                    initialVerify.Reason,
                },
                finalVerify = finalVerify is { } final
                    ? new
                    {
                    final.Ok,
                    final.IsInconclusive,
                    final.DetectedTitle,
                    final.HasResidualText,
                    final.ResidualText,
                    final.Reason,
                    }
                    : null,
            };
            await File.WriteAllTextAsync(
                debugPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
            Log(request, $"标题校验诊断已保存：{Path.GetFileName(debugPath)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(request, $"保存标题校验诊断时出错，继续执行兜底：{ex.Message}");
        }
    }

    private async Task<PosterTitleVerifyResult> VerifyTitleWithFullImageConfirmationAsync(
        IReadOnlyDictionary<string, string> config,
        string imagePath,
        string title,
        PosterLayout detectedLayout,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        var croppedVerify = await _titleVerifier.VerifyAsync(
            config,
            imagePath,
            title,
            ToTitleLayout(detectedLayout),
            cancellationToken).ConfigureAwait(false);
        if (croppedVerify.Ok)
        {
            Log(request, "标题区域校验通过，继续检查全图是否残留人物名、作者说明等其他文字。");
        }
        else
        {
            Log(request, croppedVerify.IsInconclusive
                ? "标题区域校验暂未得到确定结果，改用全图复核。"
                : "标题区域校验发现文字差异，开始全图复核。");
        }

        var fullImageLayout = detectedLayout with
        {
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
        };
        var fullImageVerify = await _titleVerifier.VerifyAsync(
            config,
            imagePath,
            title,
            ToTitleLayout(fullImageLayout),
            cancellationToken).ConfigureAwait(false);
        var verifyResult = MergeResidualTextEvidence(croppedVerify, fullImageVerify);
        if (verifyResult.Ok)
        {
            Log(request, croppedVerify.Ok
                ? "海报全图文字复核通过，成品只保留目标新剧名。"
                : $"标题区域裁剪位置不准确，但全图复核通过：{(string.IsNullOrWhiteSpace(verifyResult.DetectedTitle) ? title : verifyResult.DetectedTitle)}");
        }
        else
        {
            Log(request, verifyResult.HasResidualText
                ? $"海报检测到目标剧名外的残留文字：{(string.IsNullOrWhiteSpace(verifyResult.ResidualText) ? verifyResult.Reason : verifyResult.ResidualText)}"
                : verifyResult.IsInconclusive
                    ? "标题全图复核暂未得到确定结果。"
                    : "标题全图复核确认文字需要修复。");
        }

        return verifyResult;
    }

    internal static PosterTitleVerifyResult MergeResidualTextEvidence(
        PosterTitleVerifyResult croppedVerify,
        PosterTitleVerifyResult fullImageVerify)
    {
        if (!croppedVerify.HasResidualText && !fullImageVerify.HasResidualText)
            return fullImageVerify;

        var residualText = string.Join(
            '；',
            new[] { croppedVerify.ResidualText, fullImageVerify.ResidualText }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal));
        var reason = string.Join(
            '；',
            new[] { croppedVerify.Reason, fullImageVerify.Reason }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal));
        var detectedTitle = string.IsNullOrWhiteSpace(fullImageVerify.DetectedTitle)
            ? croppedVerify.DetectedTitle
            : fullImageVerify.DetectedTitle;

        return new PosterTitleVerifyResult(
            false,
            detectedTitle,
            string.IsNullOrWhiteSpace(reason) ? "检测到目标标题之外的残留文字" : reason,
            IsInconclusive: false,
            HasResidualText: true,
            ResidualText: residualText);
    }

    private async Task<bool> TryRepairTitleWithAiRetriesAsync(
        IReadOnlyDictionary<string, string> config,
        string outputPath,
        string title,
        PosterLayout layout,
        PosterTitleVerifyResult initialVerify,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        if (initialVerify.HasResidualText)
        {
            Log(request, "检测到标题区域外的其他文字，跳过局部标题重试，直接进入全图去字处理。");
            return false;
        }

        var retryCount = PosterTitleAiRetryPolicy.ResolveRetryCount(config);
        if (retryCount <= 0)
            return false;

        if (!PosterTitleAiRetryPolicy.ShouldRetry(initialVerify))
        {
            Log(request, $"标题校验失败属于接口或配置异常，跳过AI改字重试：{initialVerify.Reason}");
            return false;
        }

        var previousVerify = initialVerify;
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var outputStem = Path.GetFileNameWithoutExtension(outputPath);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pendingPath = Path.Combine(outputDirectory, $"{outputStem}.ai_retry_{attempt}.pending.png");
            var failedPath = Path.Combine(outputDirectory, $"{outputStem}.ai_retry_{attempt}_failed.png");
            TryDeleteFile(pendingPath);
            TryDeleteFile(failedPath);

            try
            {
                Log(
                    request,
                    $"AI标题安全修复第 {attempt}/{retryCount} 次：目标逐字 {PosterTitleAiRetryPolicy.BuildTitleCharacterSequence(title)}；" +
                    $"上次识别“{previousVerify.DetectedTitle}”，原因：{previousVerify.Reason}");

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(120));
                var retryBytes = await GenerateTitleRepairPosterBytesAsync(
                    config,
                    outputPath,
                    title,
                    layout,
                    attemptCts.Token).ConfigureAwait(false);
                await WriteGeneratedPosterCandidateAsync(
                    outputPath,
                    retryBytes,
                    pendingPath,
                    attemptCts.Token).ConfigureAwait(false);

                var retryVerify = await VerifyTitleWithFullImageConfirmationAsync(
                    config,
                    pendingPath,
                    title,
                    layout,
                    request,
                    cancellationToken).ConfigureAwait(false);
                if (retryVerify.Ok)
                {
                    File.Copy(pendingPath, outputPath, overwrite: true);
                    TryDeleteFile(pendingPath);
                    Log(
                        request,
                        $"AI标题安全修复第 {attempt} 次通过校验：{(string.IsNullOrWhiteSpace(retryVerify.DetectedTitle) ? title : retryVerify.DetectedTitle)}");
                    return true;
                }

                File.Move(pendingPath, failedPath, overwrite: true);
                Log(
                    request,
                    $"AI标题安全修复第 {attempt} 次未通过，候选已留档：{Path.GetFileName(failedPath)}；{retryVerify.Reason}");
                previousVerify = retryVerify;
                if (!PosterTitleAiRetryPolicy.ShouldRetry(retryVerify))
                {
                    Log(request, "重试候选的校验遇到接口或配置异常，停止继续生成，进入既定校验失败处理模式。");
                    break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(pendingPath);
                Log(request, $"AI标题安全修复第 {attempt} 次请求超时，正式海报未被覆盖。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDeleteFile(pendingPath);
                Log(request, $"AI标题安全修复第 {attempt} 次执行失败，正式海报未被覆盖：{ex.Message}");
            }
        }

        Log(request, $"AI标题安全修复未通过，正式海报保持首次成功出图，继续执行标题校验失败处理模式。");
        return false;
    }

    private async Task<byte[]> GenerateTitleRepairPosterBytesAsync(
        IReadOnlyDictionary<string, string> config,
        string inputPath,
        string title,
        PosterLayout layout,
        CancellationToken cancellationToken)
    {
        var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
        var endpoint = GetOptional(config, "ImageEditEndpoint") ?? GetRequired(config, "ImageModelEndpoint");
        var configuredApiPath = GetOptional(config, "ImageEditPath") ?? GetDefaultImageEditPath(endpoint);
        var apiPath = IsOpenAiImageProvider(provider) ||
                      configuredApiPath.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase)
            ? configuredApiPath
            : "/images/generations";
        var requestUrl = BuildApiUrl(endpoint, apiPath);
        var modelId = GetOptional(config, "ImageEditModelId") ?? GetRequired(config, "ImageModelId");
        var apiKey = GetOptional(config, "ImageEditApiKey") ?? GetRequired(config, "ImageModelApiKey");
        var promptTitle = PosterTitleAiRetryPolicy.FormatTitleForPrompt(title);
        var promptVariables = CreatePosterPromptVariables(promptTitle, title, null, null);
        var promptTemplate = GetOptional(config, "PosterTitleVerifyAiRetryPrompt")
            ?? DefaultPosterTitleVerifyAiRetryPrompt;
        var prompt = RenderPromptTemplate(promptTemplate, promptVariables).Trim();

        using var source = await Image.LoadAsync<Rgba32>(inputPath, cancellationToken).ConfigureAwait(false);
        var cropRectangle = PosterTitleAiRetryPolicy.ComputeCropRectangle(
            source.Width,
            source.Height,
            layout.X,
            layout.Y,
            layout.Width,
            layout.Height);
        using var crop = source.Clone(ctx => ctx.Crop(cropRectangle));
        var cropPath = Path.Combine(Path.GetTempPath(), $"poster-title-roi-{Guid.NewGuid():N}.png");
        try
        {
            await crop.SaveAsPngAsync(cropPath, cancellationToken).ConfigureAwait(false);
            byte[] repairedCropBytes;
            if (IsOpenAiImageProvider(provider))
            {
                repairedCropBytes = await GeneratePosterWithEditFormNoMaskAsync(
                    requestUrl,
                    modelId,
                    apiKey,
                    cropPath,
                    "image/png",
                    prompt,
                    provider,
                    "high",
                    "auto",
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var cropApiSize = PosterCoverFrameSizeHelper.ComputeApiSize(cropRectangle.Width, cropRectangle.Height);
                repairedCropBytes = await GeneratePosterWithGenerationsJsonAsync(
                    requestUrl,
                    modelId,
                    apiKey,
                    cropPath,
                    "image/png",
                    prompt,
                    provider,
                    NormalizeImageQuality(GetOptional(config, "ImageQuality")),
                    cropApiSize,
                    cancellationToken).ConfigureAwait(false);
            }

            await using var repairedBuffer = new MemoryStream(repairedCropBytes);
            using var repairedCrop = await Image.LoadAsync<Rgba32>(repairedBuffer, cancellationToken).ConfigureAwait(false);
            if (repairedCrop.Width != cropRectangle.Width || repairedCrop.Height != cropRectangle.Height)
                ResizeToCanvasPreservingAspect(repairedCrop, cropRectangle.Width, cropRectangle.Height);

            ApplyFeatheredTitlePatch(source, repairedCrop, cropRectangle);
            await using var outputBuffer = new MemoryStream();
            await source.SaveAsPngAsync(outputBuffer, cancellationToken).ConfigureAwait(false);
            return outputBuffer.ToArray();
        }
        finally
        {
            TryDeleteFile(cropPath);
        }
    }

    private static void ApplyFeatheredTitlePatch(
        Image<Rgba32> source,
        Image<Rgba32> patch,
        Rectangle target)
    {
        var feather = Math.Clamp(Math.Min(patch.Width, patch.Height) / 18, 4, 24);
        patch.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var edgeDistance = Math.Min(Math.Min(x, row.Length - 1 - x), Math.Min(y, accessor.Height - 1 - y));
                    if (edgeDistance >= feather)
                        continue;

                    var t = Math.Clamp((edgeDistance + 1f) / (feather + 1f), 0f, 1f);
                    var smoothAlpha = t * t * (3f - 2f * t);
                    var pixel = row[x];
                    pixel.A = (byte)Math.Clamp((int)Math.Round(pixel.A * smoothAlpha), 0, 255);
                    row[x] = pixel;
                }
            }
        });
        source.Mutate(ctx => ctx.DrawImage(patch, new Point(target.X, target.Y), 1f));
    }

    private async Task Image2RegenerateVerifiedTitleAsync(
        IReadOnlyDictionary<string, string> config,
        string outputPath,
        string title,
        PosterLayout layout,
        PosterTitleVerifyResult initialVerify,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        Log(request, "AI海报标题校验未通过，开始用 Image2 重生成标题");
        var regeneratedBytes = await GenerateImage2TitleRegeneratedPosterBytesAsync(config, outputPath, title, cancellationToken);
        await WriteGeneratedPosterCandidateAsync(outputPath, regeneratedBytes, outputPath, cancellationToken);
    }

    private async Task<byte[]> GenerateTitleErasedPosterBytesAsync(
        IReadOnlyDictionary<string, string> config,
        string inputPath,
        PosterLayout layout,
        string prompt,
        bool eraseAllText,
        CancellationToken cancellationToken)
    {
        var endpoint = GetOptional(config, "ImageEditEndpoint") ?? GetRequired(config, "ImageModelEndpoint");
        var apiPath = GetOptional(config, "ImageEditPath") ?? GetDefaultImageEditPath(endpoint);
        var requestUrl = BuildApiUrl(endpoint, apiPath);
        var modelId = GetOptional(config, "ImageEditModelId") ?? GetRequired(config, "ImageModelId");
        var apiKey = GetOptional(config, "ImageEditApiKey") ?? GetRequired(config, "ImageModelApiKey");
        var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
        var imageQuality = NormalizeImageQuality(GetOptional(config, "ImageQuality"));
        var sourceInfo = await Image.IdentifyAsync(inputPath, cancellationToken)
            ?? throw new InvalidOperationException($"无法读取待去字海报尺寸: {inputPath}");
        var imageSize = provider == "doubao"
            ? PosterCoverFrameSizeHelper.ResolveFrameApiSize(sourceInfo.Width, sourceInfo.Height, config)
            : NormalizeImageSize(GetOptional(config, "ImageSize"), provider);
        var mediaType = GuessMediaType(Path.GetExtension(inputPath).TrimStart('.'));

        if (eraseAllText && !apiPath.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
        {
            return await GeneratePosterWithEditFormNoMaskAsync(
                requestUrl,
                modelId,
                apiKey,
                inputPath,
                mediaType,
                prompt.Trim(),
                provider,
                imageQuality,
                imageSize,
                cancellationToken).ConfigureAwait(false);
        }

        return await GeneratePosterWithAiPromptAsync(
            requestUrl,
            apiPath,
            modelId,
            apiKey,
            inputPath,
            mediaType,
            layout,
            prompt.Trim(),
            provider,
            imageQuality,
            imageSize,
            cancellationToken);
    }

    private async Task<byte[]> GenerateImage2TitleRegeneratedPosterBytesAsync(
        IReadOnlyDictionary<string, string> config,
        string inputPath,
        string title,
        CancellationToken cancellationToken)
    {
        var image2Config = new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase)
        {
            ["ImageProvider"] = "ofox_image2",
        };
        var endpoint = (GetOptional(image2Config, "OfoxImage2Endpoint") ?? "https://api.ofox.ai/v1").TrimEnd('/');
        var apiPath = "/images/edits";
        var requestUrl = BuildApiUrl(endpoint, apiPath);
        var modelId = GetOptional(image2Config, "OfoxImage2ModelId") ?? "openai/gpt-image-2";
        var apiKey = GetOptional(image2Config, "OfoxImage2ApiKey") ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Image2 重生成需要配置 OfoxImage2ApiKey");

        var prompt = BuildImage2TitleRegenerationPrompt(title);
        var quality = GetOptional(image2Config, "OfoxImage2Quality")
            ?? GetOptional(image2Config, "ImageQuality")
            ?? "medium";
        var size = GetOptional(image2Config, "OfoxImage2Size") ?? "auto";
        var mediaType = GuessMediaType(Path.GetExtension(inputPath).TrimStart('.'));

        return await GeneratePosterWithEditFormNoMaskAsync(
            requestUrl,
            modelId,
            apiKey,
            inputPath,
            mediaType,
            prompt,
            "ofox_image2",
            quality,
            size,
            cancellationToken);
    }

    private async Task<byte[]> AiEditFrameAsync(
        Image<Rgba32> frameImage,
        string prompt,
        IReadOnlyDictionary<string, string> config,
        string apiSize,
        PosterRenameRequest request,
        CancellationToken cancellationToken)
    {
        var tempFrame = Path.Combine(Path.GetTempPath(), $"frame-cover-{Guid.NewGuid():N}.png");
        try
        {
            await frameImage.SaveAsPngAsync(tempFrame, cancellationToken);
            var provider = NormalizeImageProvider(GetOptional(config, "ImageProvider"));
            var endpoint = (GetOptional(config, "ImageEditEndpoint") ?? GetRequired(config, "ImageModelEndpoint")).TrimEnd('/');
            var apiPath = GetOptional(config, "ImageEditPath") ?? GetDefaultImageEditPath(endpoint);
            var requestUrl = BuildApiUrl(endpoint, apiPath);
            var modelId = GetOptional(config, "ImageEditModelId") ?? GetRequired(config, "ImageModelId");
            var apiKey = GetOptional(config, "ImageEditApiKey") ?? GetRequired(config, "ImageModelApiKey");
            Log(request, $"AI 封面模型：{provider} / {modelId}");

            if (apiPath.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
            {
                return await GeneratePosterWithGenerationsJsonAsync(
                    requestUrl,
                    modelId,
                    apiKey,
                    tempFrame,
                    "image/png",
                    prompt,
                    provider,
                    NormalizeImageQuality(GetOptional(config, "ImageQuality")),
                    NormalizeImageSize(apiSize, provider),
                    cancellationToken);
            }

            var editSize = apiSize;
            if (IsOpenAiImageProvider(provider))
            {
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "auto", "1024x1024", "1536x1024", "1024x1536",
                };
                if (!allowed.Contains(editSize))
                {
                    Log(request, $"Ofox/Image2 编辑接口按文档改用 size=auto（原请求尺寸 {editSize}）");
                    editSize = "auto";
                }
            }

            return await GeneratePosterWithEditFormNoMaskAsync(
                requestUrl,
                modelId,
                apiKey,
                tempFrame,
                "image/png",
                prompt,
                provider,
                NormalizeImageQuality(GetOptional(config, "ImageQuality")),
                editSize,
                cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempFrame);
        }
    }

    private async Task<byte[]> GeneratePosterWithEditFormNoMaskAsync(
        string requestUrl,
        string modelId,
        string apiKey,
        string inputPath,
        string mediaType,
        string prompt,
        string provider,
        string imageQuality,
        string imageSize,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(modelId), "model");
        form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
        if (!IsOpenAiImageProvider(provider))
            form.Add(new StringContent("b64_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(imageSize))
            form.Add(new StringContent(imageSize), "size");
        if (IsOpenAiImageProvider(provider) && !string.IsNullOrWhiteSpace(imageQuality))
            form.Add(new StringContent(imageQuality), "quality");

        using var imageStream = File.OpenRead(inputPath);
        var imageContent = new StreamContent(imageStream);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(imageContent, "image", Path.GetFileName(inputPath));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = form;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
        using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
            ThrowPosterApiFailure("AI 海报图片编辑", requestUrl, response, responseText);

        return await ReadImageResponseBytesAsync(responseText, cancellationToken)
            ?? throw new InvalidOperationException("AI 海报图片编辑成功返回，但响应中没有可解析的图片数据。");
    }

    private static async Task WriteGeneratedPosterCandidateAsync(
        string inputPath,
        byte[] imageBytes,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempOut = Path.Combine(Path.GetTempPath(), $"poster-output-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempOut, imageBytes, cancellationToken);
        try
        {
            var sourceInfo = await Image.IdentifyAsync(inputPath, cancellationToken)
                ?? throw new InvalidOperationException($"无法读取原海报尺寸: {inputPath}");
            using var generated = await Image.LoadAsync<Rgba32>(tempOut, cancellationToken);
            if (generated.Width != sourceInfo.Width || generated.Height != sourceInfo.Height)
                ResizeToCanvasPreservingAspect(generated, sourceInfo.Width, sourceInfo.Height);
            await generated.SaveAsync(outputPath, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempOut);
        }
    }

    private static async Task ResizeAndSaveAsync(
        byte[] imageBytes,
        int targetW,
        int targetH,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"cover-resize-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempPath, imageBytes, cancellationToken);
        try
        {
            using var img = await Image.LoadAsync<Rgba32>(tempPath, cancellationToken);
            if (img.Width == targetW && img.Height == targetH)
                await img.SaveAsync(outputPath, cancellationToken);
            else
            {
                ResizeToCanvasPreservingAspect(img, targetW, targetH);
                await img.SaveAsync(outputPath, cancellationToken);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static string BuildImage2TitleRegenerationPrompt(string title)
    {
        var safeTitle = (title ?? "").Trim();
        return "请编辑这张短剧海报，清除全部旧文字并修复标题。"
               + "保持人物、背景、构图、比例、色调和光影基本不变，不要重画主体人物，不要改变图片尺寸比例。"
               + "删除图片中全部现有文字，包括错误或残缺标题、人物名、演员名、作者、改编来源、宣传语、副标题、季数、字幕、水印、Logo文字和角标。"
               + $"重新添加准确的简体中文标题：{safeTitle}。"
               + "标题成品只能包含剧名本身，不要添加 []、《》、“”、拼音、英文、数字、书名号、引号或任何包裹符号。"
               + "必须逐字一致，不能漏字、改字、使用繁体字、异体字、形近字、乱码或艺术变形到难以识别的字。"
               + "标题要清晰、醒目、有短剧海报质感，使用常见、标准、易识别的简体中文海报粗标题。"
               + "最终画面中只能出现一次完整目标标题，不得保留任何其他中文、英文、拼音、数字、旧标题残影、底层文字或重复标题。";
    }

    private static PosterTitleLayout ToTitleLayout(PosterLayout layout) =>
        PosterTitleProgrammaticRenderer.ToTitleLayout(
            layout.X,
            layout.Y,
            layout.Width,
            layout.Height,
            layout.FontScale,
            layout.TextColor,
            layout.BackgroundColor,
            layout.BackgroundOpacity,
            layout.Align);

    private static bool IsPosterTitleVerifyEnabled(IReadOnlyDictionary<string, string> config)
    {
        if (!config.TryGetValue("PosterTitleVerifyEnabled", out var value) || string.IsNullOrWhiteSpace(value))
            return true;
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}

internal static class PosterTitleAiRetryPolicy
{
    internal const int DefaultRetryCount = 1;
    internal const int MaxRetryCount = 3;

    internal static int ResolveRetryCount(IReadOnlyDictionary<string, string> config)
    {
        if (!config.TryGetValue("PosterTitleVerifyAiRetryCount", out var value) ||
            !int.TryParse(value, out var parsed))
        {
            return DefaultRetryCount;
        }

        return Math.Clamp(parsed, 0, MaxRetryCount);
    }

    internal static bool ShouldRetry(PosterTitleVerifyResult result)
    {
        if (result.Ok || result.IsInconclusive)
            return false;
        if (!string.IsNullOrWhiteSpace(result.DetectedTitle))
            return true;

        var reason = result.Reason ?? string.Empty;
        string[] nonRetryableMarkers =
        [
            "接口失败",
            "配置缺少",
            "未返回内容",
            "未返回合法 JSON",
            "校验未返回",
            "HTTP ",
            "timeout",
            "timed out",
            "unauthorized",
            "forbidden",
            "too many requests",
        ];
        return !nonRetryableMarkers.Any(marker =>
            reason.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static string BuildTitleCharacterSequence(string title) =>
        string.Join(" / ", (title ?? string.Empty).Trim().EnumerateRunes().Select(rune => rune.ToString()));

    internal static string FormatTitleForPrompt(string title)
    {
        var characters = (title ?? string.Empty).Trim().EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        if (characters.Length <= 7)
            return string.Concat(characters);

        var lineCount = characters.Length <= 14 ? 2 : 3;
        var baseLength = characters.Length / lineCount;
        var extra = characters.Length % lineCount;
        var lines = new List<string>(lineCount);
        var offset = 0;
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            var length = baseLength + (lineIndex < extra ? 1 : 0);
            lines.Add(string.Concat(characters.Skip(offset).Take(length)));
            offset += length;
        }

        return string.Join('\n', lines);
    }

    internal static Rectangle ComputeCropRectangle(
        int imageWidth,
        int imageHeight,
        float normalizedX,
        float normalizedY,
        float normalizedWidth,
        float normalizedHeight)
    {
        imageWidth = Math.Max(1, imageWidth);
        imageHeight = Math.Max(1, imageHeight);
        var x = Math.Clamp((int)Math.Floor(imageWidth * normalizedX), 0, imageWidth - 1);
        var y = Math.Clamp((int)Math.Floor(imageHeight * normalizedY), 0, imageHeight - 1);
        var width = Math.Clamp((int)Math.Ceiling(imageWidth * normalizedWidth), 1, imageWidth - x);
        var height = Math.Clamp((int)Math.Ceiling(imageHeight * normalizedHeight), 1, imageHeight - y);
        var padX = Math.Max(24, (int)Math.Ceiling(width * 0.14));
        var padY = Math.Max(24, (int)Math.Ceiling(height * 0.55));
        var left = Math.Max(0, x - padX);
        var top = Math.Max(0, y - padY);
        var right = Math.Min(imageWidth, x + width + padX);
        var bottom = Math.Min(imageHeight, y + height + padY);
        return new Rectangle(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }
}
