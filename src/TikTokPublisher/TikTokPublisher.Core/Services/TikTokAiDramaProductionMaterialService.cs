using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokAiDramaProductionMaterialService
{
    public const string OutputDirectoryName = "AI漫剧制作素材";
    public const string CharacterDirectoryName = "01_角色设定";
    public const string SceneDirectoryName = "02_场景设定";
    public const string StoryboardDirectoryName = "03_分镜设计";
    public const string ManifestDirectoryName = "04_提示词与清单";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    public static bool CanGenerate(string workflowProjectDirectory)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        return FindImagesInNamedDirectories(workflow, "抽帧原图", recursiveFiles: true).Take(12).Count() >= 12 &&
               (FindImagesInNamedDirectories(workflow, "AI 生成过程截图", recursiveFiles: false).Any() ||
                FindImagesInNamedDirectories(workflow, TikTokProjectImageService.OutputDirectoryName, recursiveFiles: false).Any());
    }

    internal static bool NeedsSourceMaterialRefresh(string workflowProjectDirectory)
    {
        var workflow = Path.GetFullPath(workflowProjectDirectory);
        var frameCount = FindImagesInNamedDirectories(workflow, "抽帧原图", recursiveFiles: true)
            .Take(12)
            .Count();
        var hasStoryboard = FindImagesInNamedDirectories(
                workflow,
                TikTokAiGenerationScreenshotService.OutputDirectoryName,
                recursiveFiles: false)
            .Concat(FindImagesInNamedDirectories(
                workflow,
                TikTokProjectImageService.OutputDirectoryName,
                recursiveFiles: false))
            .Any();
        return frameCount < 12 || !hasStoryboard;
    }

    public static bool HasCurrentOutput(string workflowProjectDirectory)
    {
        var root = Path.Combine(
            TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflowProjectDirectory),
            OutputDirectoryName);
        return HasEnoughFiles(Path.Combine(root, CharacterDirectoryName), 6) &&
               HasEnoughFiles(Path.Combine(root, SceneDirectoryName), 6) &&
               HasEnoughFiles(Path.Combine(root, StoryboardDirectoryName), 4);
    }

    public static async Task GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var title = string.IsNullOrWhiteSpace(item.NewTitle) ? item.Title : item.NewTitle;
        var root = Path.Combine(
            TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(context.WorkflowProjectDir),
            OutputDirectoryName);
        var characterDir = Path.Combine(root, CharacterDirectoryName);
        var sceneDir = Path.Combine(root, SceneDirectoryName);
        var storyboardDir = Path.Combine(root, StoryboardDirectoryName);
        var manifestDir = Path.Combine(root, ManifestDirectoryName);

        if (!forceRerun && HasEnoughFiles(characterDir, 6) && HasEnoughFiles(sceneDir, 6) &&
            HasEnoughFiles(storyboardDir, 4))
        {
            log?.Invoke("AI 漫剧制作素材已存在，跳过生成。");
            return;
        }

        if (NeedsSourceMaterialRefresh(context.WorkflowProjectDir))
        {
            log?.Invoke(
                "AI 漫剧制作素材：真实抽帧或分镜工作台不足，正在从原始视频自动补充前置素材。");
            TikTokAiGenerationScreenshotService.Generate(
                context.WorkflowProjectDir,
                title,
                settings,
                log,
                ct);
        }

        ResilientFileSystem.DeleteDirectory(root);
        foreach (var dir in new[] { characterDir, sceneDir, storyboardDir, manifestDir })
            ResilientFileSystem.EnsureDirectory(dir);

        var sourceFrames = FindImagesInNamedDirectories(context.WorkflowProjectDir, "抽帧原图", recursiveFiles: true)
            .Take(32)
            .ToArray();
        if (sourceFrames.Length < 12)
            throw new InvalidOperationException(
                $"生成 AI 漫剧制作素材失败：自动补抽帧后只有 {sourceFrames.Length} 张真实画面参考图，" +
                "请确认项目原始视频仍然存在且 FFmpeg 可用。");

        var selected = SelectEvenly(sourceFrames, 16).ToArray();
        for (var index = 0; index < selected.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var source = selected[index];
            using var image = await Image.LoadAsync(source, ct).ConfigureAwait(false);
            if (index % 2 == 0)
            {
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(768, 768),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center,
                }));
                await image.SaveAsJpegAsync(
                    Path.Combine(characterDir, $"角色设定_{index / 2 + 1:D2}.jpg"), ct).ConfigureAwait(false);
            }
            else
            {
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(960, 540),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center,
                }));
                await image.SaveAsJpegAsync(
                    Path.Combine(sceneDir, $"场景设定_{index / 2 + 1:D2}.jpg"), ct).ConfigureAwait(false);
            }
        }

        // Only use top-level workbench/project screenshots. Extracted frames are deliberately
        // excluded so the storyboard evidence cannot duplicate the character/scene evidence.
        var storyboards = FindImagesInNamedDirectories(context.WorkflowProjectDir, "AI 生成过程截图", recursiveFiles: false)
            .Concat(FindImagesInNamedDirectories(
                context.WorkflowProjectDir, TikTokProjectImageService.OutputDirectoryName, recursiveFiles: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        if (storyboards.Length == 0)
            throw new InvalidOperationException("生成 AI 漫剧制作素材失败：没有真实分镜工作台或工程图素材。");

        foreach (var (source, index) in storyboards.Select((path, index) => (path, index)))
            File.Copy(source, Path.Combine(storyboardDir, $"分镜设计_{index + 1:D2}{Path.GetExtension(source)}"), true);

        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "制作素材说明.txt"),
            $"项目：{title}\n" +
            $"角色设定：{Directory.EnumerateFiles(characterDir).Count()} 张\n" +
            $"场景设定：{Directory.EnumerateFiles(sceneDir).Count()} 张\n" +
            $"分镜设计：{storyboards.Length} 张\n" +
            "说明：角色与场景素材由项目真实画面分类整理；分镜素材来自真实工作台或工程图。",
            ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(manifestDir, "素材来源清单.json"),
            JsonSerializer.Serialize(new
            {
                title,
                sourceFrames = selected.Select(Path.GetFileName),
                storyboards = storyboards.Select(Path.GetFileName),
            }, new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);

        log?.Invoke($"AI 漫剧制作素材生成完成：角色、场景、分镜与来源清单 -> {root}");
    }

    private static bool HasEnoughFiles(string directory, int minimum) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory).Count() >= minimum;

    private static IEnumerable<string> FindImagesInNamedDirectories(
        string root,
        string directoryName,
        bool recursiveFiles) =>
        Directory.EnumerateDirectories(root, directoryName, SearchOption.AllDirectories)
            .SelectMany(dir => Directory.EnumerateFiles(
                dir,
                "*",
                recursiveFiles ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)));

    private static IEnumerable<string> SelectEvenly(IReadOnlyList<string> files, int maximum)
    {
        if (files.Count <= maximum) return files;
        return Enumerable.Range(0, maximum)
            .Select(index => files[(int)Math.Round(index * (files.Count - 1d) / (maximum - 1d))]);
    }

}
