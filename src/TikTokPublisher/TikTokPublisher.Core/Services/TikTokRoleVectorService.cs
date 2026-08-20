using SixLabors.ImageSharp;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokRoleVectorService
{
    public const string OutputFileName = TikTokReferenceSourcePackageService.CharacterWorkbenchFileName;
    public const string BackupFileName = "角色矢量图_旧版.png";

    public static string GetOutputPath(string workflowProjectDirectory) =>
        Path.Combine(TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory), OutputFileName);

    public static bool HasCurrentOutput(string workflowProjectDirectory)
    {
        var path = GetOutputPath(workflowProjectDirectory);
        if (!File.Exists(path)) return false;
        try
        {
            var info = Image.Identify(path);
            return info is not null &&
                   info.Width == RoleVectorTemplateRenderer.CanvasWidth &&
                   info.Height == RoleVectorTemplateRenderer.CanvasHeight;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var output = GetOutputPath(context.WorkflowProjectDir);
        if (!forceRerun && HasCurrentOutput(context.WorkflowProjectDir))
        {
            log?.Invoke($"角色矢量图已存在且尺寸正确，跳过生成：{output}");
            return output;
        }

        var root = TikTokReferenceSourcePackageService.GetRoot(context.WorkflowProjectDir);
        Directory.CreateDirectory(root);
        await TikTokReferenceSourcePackageService.GenerateAsync(
            item,
            settings,
            forceRerun: false,
            log,
            ct).ConfigureAwait(false);
        var characters = await TikTokReferenceSourcePackageService.EnsureCharacterImagesAsync(
            item, settings, log, ct).ConfigureAwait(false);
        var sceneSources = await TikTokReferenceSourcePackageService.ResolveSceneSourcesAsync(
            context, root, log, ct).ConfigureAwait(false);
        if (sceneSources.Count == 0)
            throw new InvalidOperationException("生成角色矢量图失败：没有可用的真实场景图或视频抽帧。");

        var usedCharacters = characters.Take(6).ToArray();
        var templateDescription = usedCharacters.Length switch
        {
            3 => "三人居中模板",
            4 => "左三右一模板",
            _ => "标准角色模板",
        };
        log?.Invoke(
            $"角色矢量图：检测到 {usedCharacters.Length} 个角色，使用{templateDescription}；" +
            $"真实参考帧 {sceneSources.Count} 张。");

        var temporary = Path.Combine(root, $".{Path.GetFileNameWithoutExtension(OutputFileName)}.{Guid.NewGuid():N}.tmp.png");
        try
        {
            RoleVectorTemplateRenderer.Render(temporary, usedCharacters, sceneSources);
            var info = Image.Identify(temporary)
                ?? throw new InvalidDataException("生成的角色矢量图不是有效 PNG。");
            if (info.Width != RoleVectorTemplateRenderer.CanvasWidth ||
                info.Height != RoleVectorTemplateRenderer.CanvasHeight)
            {
                throw new InvalidDataException(
                    $"角色矢量图尺寸必须为 {RoleVectorTemplateRenderer.CanvasWidth}×" +
                    $"{RoleVectorTemplateRenderer.CanvasHeight}，当前为 {info.Width}×{info.Height}。");
            }

            if (File.Exists(output))
                File.Copy(output, Path.Combine(root, BackupFileName), overwrite: true);
            File.Move(temporary, output, overwrite: true);
            log?.Invoke($"角色矢量图生成完成：{info.Width}×{info.Height} → {output}");
            return output;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
