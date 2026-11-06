using Microsoft.Extensions.Logging.Abstractions;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Files;
using ShortDrama.Infrastructure.Networking;
using ShortDrama.Infrastructure.Parsing;
using ShortDrama.Infrastructure.Process;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System.Text.Json;

namespace PlatformPublisher.Kuaishou.Publishing;

public sealed record KuaishouPosterSettings(
    string PosterMode,
    bool EraseMode,
    string VerticalPrompt,
    string VerticalSafeRetryPrompt,
    string HorizontalPrompt,
    string HorizontalSafeRetryPrompt,
    string ImageProvider,
    string ImageModelId,
    string ImageModelApiKey,
    string ImageModelEndpoint,
    string ImageEditPath,
    string ImageSize,
    string ImageQuality,
    string HorizontalRequestSize,
    string ChatEndpoint,
    string ChatApiKey,
    string ChatModel,
    bool TitleVerifyEnabled,
    string TitleVerifyMode,
    int TitleVerifyAiRetryCount,
    string LayoutDetectPrompt);

/// <summary>
/// 用现有海报步骤生成快手竖屏海报，再把成品或无文字底图扩成 1600×1000 横屏封面。
/// </summary>
public sealed class KuaishouPosterService
{
    public const int HorizontalWidth = 1600;
    public const int HorizontalHeight = 1000;
    public const string VerticalPosterFileName = "快手竖屏海报.jpg";
    public const string HorizontalCoverFileName = "快手横屏封面.jpg";
    public const string ErasedPosterFileName = "海报处理_已清除标题.png";

    public async Task PrepareAsync(
        string projectDirectory,
        KuaishouPosterSettings settings,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ImageModelEndpoint) ||
            string.IsNullOrWhiteSpace(settings.ImageModelApiKey) ||
            string.IsNullOrWhiteSpace(settings.ImageModelId))
            throw new InvalidOperationException("请先在系统设置的 AI图片 中填写模型 Endpoint、API Key 和模型 ID。");
        if (string.IsNullOrWhiteSpace(settings.ChatEndpoint) ||
            string.IsNullOrWhiteSpace(settings.ChatApiKey) ||
            string.IsNullOrWhiteSpace(settings.ChatModel))
            throw new InvalidOperationException("生成海报需要 AI文本 中的接口地址、API Key 和模型名称，用于定位标题。");

        var directories = KuaishouArtifactColumnStatusReader.ResolveDirectories(projectDirectory);
        Directory.CreateDirectory(directories.WorkflowDirectory);
        var source = FindSourcePoster(directories.SourceDirectory, directories.WorkflowDirectory);
        var infoPath = Path.Combine(directories.WorkflowDirectory, "短剧信息.txt");
        if (!File.Exists(infoPath))
        {
            var sourceInfo = Path.Combine(directories.SourceDirectory, "短剧信息.txt");
            if (File.Exists(sourceInfo))
                File.Copy(sourceInfo, infoPath, overwrite: false);
        }

        var portraitPng = Path.Combine(directories.WorkflowDirectory, "海报图片.png");
        var verticalJpeg = Path.Combine(directories.WorkflowDirectory, VerticalPosterFileName);
        var horizontalJpeg = Path.Combine(directories.WorkflowDirectory, HorizontalCoverFileName);
        var erasedPng = Path.Combine(directories.WorkflowDirectory, ErasedPosterFileName);
        var configPath = WriteConfig(directories.WorkflowDirectory, settings);
        var input = source;
        string? sourceCopy = null;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(portraitPng), StringComparison.OrdinalIgnoreCase))
        {
            sourceCopy = Path.Combine(directories.WorkflowDirectory, $".poster-source-{Guid.NewGuid():N}{Path.GetExtension(source)}");
            File.Copy(source, sourceCopy, overwrite: true);
            input = sourceCopy;
        }

        try
        {
            progress?.Report(settings.EraseMode
                ? "开始清除竖屏海报文字，并用固定字体写入新剧名。"
                : "开始重新设计竖屏海报。");
            var http = PreferredNetworkHttp.CreateClient(TimeSpan.FromMinutes(5));
            var renamer = new PosterRenamer(
                new TxtProjectInfoParser(),
                new ExternalProcessRunner(),
                http,
                NullLogger<PosterRenamer>.Instance);
            await renamer.RenameAsync(
                new PosterRenameRequest(
                    directories.WorkflowDirectory,
                    InputFilePath: input,
                    OutputFilePath: portraitPng,
                    ConfigFile: configPath,
                    Overwrite: true,
                    Log: message => progress?.Report(message)),
                cancellationToken);
            await SaveJpegCopyAsync(portraitPng, verticalJpeg, cancellationToken);
            progress?.Report($"已生成{VerticalPosterFileName}。");

            var landscapeInput = settings.EraseMode ? erasedPng : portraitPng;
            if (settings.EraseMode && !File.Exists(erasedPng))
                throw new InvalidOperationException("去字模式没有生成无文字底图，不能据此扩展横屏封面。");
            var title = ReadTitle(directories.WorkflowDirectory);

            try
            {
                progress?.Report(settings.EraseMode
                    ? "开始把无文字底图扩展为 16:10 横屏封面。"
                    : "开始把竖屏海报扩展为 16:10 横屏封面。");
                await renamer.GenerateSizedCoverAsync(
                    configPath,
                    landscapeInput,
                    horizontalJpeg,
                    title,
                    ApplyTitle(settings.HorizontalPrompt, title, requireTitle: !settings.EraseMode),
                    ApplyTitle(settings.HorizontalSafeRetryPrompt, title, requireTitle: !settings.EraseMode),
                    HorizontalWidth,
                    HorizontalHeight,
                    settings.HorizontalRequestSize,
                    drawTitleWithProgram: settings.EraseMode,
                    verifyTitle: !settings.EraseMode && settings.TitleVerifyEnabled,
                    message => progress?.Report(message),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report($"快手横屏封面 AI 生成失败，改为居中裁成 {HorizontalWidth}×{HorizontalHeight}：{ex.Message}");
                await CropLandscapeAsync(landscapeInput, horizontalJpeg, cancellationToken);
                if (settings.EraseMode)
                {
                    PosterRenamer.DrawLowerTitle(horizontalJpeg, horizontalJpeg, title);
                    progress?.Report("已在裁剪后的横屏封面上写入固定标题。");
                }
            }
        }
        finally
        {
            TryDelete(configPath);
            if (sourceCopy is not null)
                TryDelete(sourceCopy);
        }
    }

    public static string HorizontalRequestSize(string? provider)
    {
        var normalized = (provider ?? "").Trim().ToLowerInvariant();
        return normalized is "ofox_image2" or "openai_image2" ? "1536x1024" : "2816x1584";
    }

    public static string FindSourcePoster(string sourceDirectory, string workflowDirectory)
    {
        foreach (var directory in DistinctDirectories(sourceDirectory, workflowDirectory))
        {
            foreach (var name in new[] { "海报图片.png", "海报图片.jpg", "海报图片.jpeg", "海报图片.webp" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                    return path;
            }
        }

        foreach (var directory in DistinctDirectories(sourceDirectory, workflowDirectory))
        {
            var candidate = Directory.EnumerateFiles(directory)
                .Where(IsUsableSourceImage)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (candidate is not null)
                return candidate;
        }

        throw new FileNotFoundException("未找到可用于生成快手封面的海报图片。");
    }

    public static async Task CropLandscapeAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync(sourcePath, cancellationToken);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(HorizontalWidth, HorizontalHeight),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center,
        }));
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await image.SaveAsJpegAsync(targetPath, new JpegEncoder { Quality = 92 }, cancellationToken);
    }

    private static async Task SaveJpegCopyAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync(sourcePath, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await image.SaveAsJpegAsync(targetPath, new JpegEncoder { Quality = 92 }, cancellationToken);
    }

    private static string WriteConfig(string workflowDirectory, KuaishouPosterSettings settings)
    {
        var path = Path.Combine(workflowDirectory, $".kuaishou-poster-{Guid.NewGuid():N}.json");
        var payload = new Dictionary<string, object?>
        {
            ["PosterMode"] = settings.EraseMode ? "erase_fixed_lower_title" : "original",
            ["ImageProvider"] = settings.ImageProvider,
            ["ImageModelId"] = settings.ImageModelId,
            ["ImageModelApiKey"] = settings.ImageModelApiKey,
            ["ImageModelEndpoint"] = settings.ImageModelEndpoint,
            ["ImageEditModelId"] = settings.ImageModelId,
            ["ImageEditApiKey"] = settings.ImageModelApiKey,
            ["ImageEditEndpoint"] = settings.ImageModelEndpoint,
            ["ImageEditPath"] = settings.ImageEditPath,
            ["ImageSize"] = settings.ImageSize,
            ["ImageQuality"] = settings.ImageQuality,
            ["ChatModelEndpoint"] = settings.ChatEndpoint,
            ["ChatModelApiKey"] = settings.ChatApiKey,
            ["ChatModelId"] = settings.ChatModel,
            ["PosterGenerationPrompt"] = settings.VerticalPrompt,
            ["PosterInpaintPrompt"] = settings.VerticalPrompt,
            ["PosterGenerationSafeRetryPrompt"] = settings.VerticalSafeRetryPrompt,
            ["PosterInpaintSafeRetryPrompt"] = settings.VerticalSafeRetryPrompt,
            ["PosterAllTextErasePrompt"] = settings.VerticalPrompt.Replace("{title}", "", StringComparison.Ordinal),
            ["PosterLayoutDetectPrompt"] = settings.LayoutDetectPrompt,
            ["PosterLayoutRequestMode"] = "standard",
            ["PosterTitleVerifyEnabled"] = settings.TitleVerifyEnabled,
            ["PosterTitleVerifyMode"] = settings.TitleVerifyMode,
            ["PosterTitleVerifyAiRetryCount"] = settings.TitleVerifyAiRetryCount,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return path;
    }

    private static string ApplyTitle(string prompt, string title, bool requireTitle)
    {
        var text = (prompt ?? "").Replace("{title}", title, StringComparison.Ordinal);
        if (requireTitle && !text.Contains(title, StringComparison.Ordinal))
            text = $"{text.TrimEnd()}\n只写一次海报标题：{title}";
        return text;
    }

    private static string ReadTitle(string workflowDirectory)
    {
        var path = Path.Combine(workflowDirectory, "短剧信息.txt");
        if (!File.Exists(path))
            throw new InvalidOperationException("未找到短剧信息.txt，无法确定横屏封面标题。");
        string? fallback = null;
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOfAny([':', '：']);
            if (index <= 0)
                continue;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (value.Length == 0)
                continue;
            if (key is "新剧名" or "新剧名称")
                return value;
            if (fallback is null && key is "原剧名" or "剧名")
                fallback = value;
        }

        return fallback ?? throw new InvalidOperationException("短剧信息里没有新剧名，无法生成横屏封面。");
    }

    private static IEnumerable<string> DistinctDirectories(string sourceDirectory, string workflowDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[] { sourceDirectory, workflowDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;
            if (seen.Add(Path.GetFullPath(directory)))
                yield return directory;
        }
    }

    private static bool IsUsableSourceImage(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp"))
            return false;
        var name = Path.GetFileNameWithoutExtension(path);
        return name is not ("快手横屏封面" or "快手竖屏海报" or "海报处理_已清除标题")
               && !name.StartsWith("工程图_", StringComparison.Ordinal)
               && !name.StartsWith("成本报表", StringComparison.Ordinal)
               && !name.StartsWith(".poster-source-", StringComparison.Ordinal)
               && !name.StartsWith(".kuaishou-poster-", StringComparison.Ordinal);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
