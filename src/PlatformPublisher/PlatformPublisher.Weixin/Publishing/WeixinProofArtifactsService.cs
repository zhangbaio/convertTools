using PlatformPublisher.Common.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ShortDrama.Core.Interfaces;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;
using System.Diagnostics;
using System.Security.Cryptography;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinProofArtifactsResult(string WorkflowDirectory, string AiProofPath, string TimestampCertificatePath);

public sealed class WeixinProofArtifactsService
{
    private readonly IWorkService _workService;
    public WeixinProofArtifactsService(IWorkService workService) => _workService = workService;

    public async Task<string> GenerateAiProofAsync(
        PublishJob job,
        ClientSettings settings,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var workflowDir = await ResolveWorkflowDirectoryAsync(job, cancellationToken);
        var outputs = Enumerable.Range(1, 4)
            .Select(index => Path.Combine(workflowDir, $"AI制作证明_{index}.png"))
            .ToArray();
        if (outputs.All(path => File.Exists(path) && new FileInfo(path).Length > 100))
        {
            progress?.Report($"AI 制作证明图片已存在，复用 {outputs.Length} 张。 ");
            return outputs[0];
        }
        var videosDir = Path.Combine(workflowDir, "videos");
        var videos = Directory.Exists(videosDir)
            ? Directory.EnumerateFiles(videosDir, "*.mp4", SearchOption.TopDirectoryOnly)
                .OrderBy(EpisodeNumber).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).Take(4).ToArray()
            : [];
        if (videos.Length < 4)
            throw new InvalidOperationException($"生成 AI 制作证明需要至少 4 集工作视频，当前只有 {videos.Length} 集。");
        var info = ParseInfo(Path.Combine(workflowDir, "短剧信息.txt"));
        var title = First(info.GetValueOrDefault("新剧名"), job.ProjectName);
        for (var index = 0; index < outputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"AI 制作证明：生成 {index + 1}/4，{Path.GetFileName(videos[index])}");
            await GenerateAiProofPageAsync(videos[index], outputs[index], title, index + 1, cancellationToken);
        }
        progress?.Report("AI 制作证明图片完成：4 张本地制作过程记录。 ");
        return outputs[0];
    }

    public async Task<string> GenerateTimestampCertificateAsync(
        PublishJob job,
        ClientSettings settings,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var workflowDir = await ResolveWorkflowDirectoryAsync(job, cancellationToken);
        var info = ParseInfo(Path.Combine(workflowDir, "短剧信息.txt"));
        var item = new QueueProjectItem
        {
            ProjectDir = job.ProjectDirectory,
            DisplayName = job.ProjectName,
            OriginalTitle = First(info.GetValueOrDefault("原剧名"), job.ProjectName),
            NewTitle = First(info.GetValueOrDefault("新剧名"), job.ProjectName),
            AccountProfileName = job.AccountName,
        };
        var path = await TikTokTimestampCertificateService.GenerateAsync(
            item,
            settings,
            account: null,
            force,
            message => progress?.Report(message),
            cancellationToken);
        progress?.Report($"可信时间戳本地模板证书完成：{path}（未调用第三方 TSA 服务）");
        return path;
    }

    private async Task<string> ResolveWorkflowDirectoryAsync(PublishJob job, CancellationToken cancellationToken)
    {
        var config = await _workService.EnsureWeixinUploadConfigAsync(job.ProjectDirectory, null, cancellationToken);
        return Path.GetDirectoryName(config) ?? throw new InvalidOperationException("无法定位工作项目目录。");
    }

    private static Dictionary<string, string> ParseInfo(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOfAny([':', '：']);
            if (index > 0) result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return result;
    }

    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static async Task GenerateAiProofPageAsync(
        string videoPath,
        string outputPath,
        string title,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(videoPath, cancellationToken);
        var tempDir = Path.Combine(Path.GetTempPath(), "yunfan-ai-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var frames = new List<string>();
        try
        {
            foreach (var (ratio, index) in new[] { (0.12, 1), (0.36, 2), (0.62, 3), (0.86, 4) })
            {
                var frame = Path.Combine(tempDir, $"frame-{index}.png");
                await ExtractFrameAsync(videoPath, frame, Math.Max(0.1, probe.DurationSeconds * ratio), cancellationToken);
                frames.Add(frame);
            }
            using var canvas = new Image<Rgba32>(1600, 1846, new Rgba32(7, 14, 24));
            var family = ResolveFontFamily();
            var titleFont = family.CreateFont(32, FontStyle.Bold);
            var headingFont = family.CreateFont(23, FontStyle.Bold);
            var bodyFont = family.CreateFont(19);
            canvas.Mutate(ctx =>
            {
                ctx.Fill(new Rgba32(10, 22, 36), new RectangleF(0, 0, 1600, 116));
                ctx.DrawText("云帆 · AI 内容制作过程记录", titleFont, new Rgba32(92, 180, 255), new PointF(42, 28));
                ctx.DrawText($"项目：{title}  ·  第 {pageIndex} 页", bodyFont, Color.White, new PointF(42, 78));
                DrawInfoPanel(ctx, headingFont, bodyFont, videoPath, probe, pageIndex);
            });
            for (var index = 0; index < frames.Count; index++)
            {
                using var source = await Image.LoadAsync<Rgba32>(frames[index], cancellationToken);
                source.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(700, 500),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center,
                }));
                var x = index % 2 == 0 ? 50 : 850;
                var y = index < 2 ? 190 : 750;
                canvas.Mutate(ctx =>
                {
                    ctx.Draw(new Rgba32(34, 211, 176), 3, new RectangleF(x - 3, y - 3, 706, 506));
                    ctx.DrawImage(source, new Point(x, y), 1f);
                    ctx.Fill(new Rgba32(8, 18, 30, 220), new RectangleF(x, y + 450, 700, 50));
                    ctx.DrawText($"关键帧 {index + 1}  ·  {new[] { 12, 36, 62, 86 }[index]}% 时间点",
                        bodyFont, Color.White, new PointF(x + 14, y + 463));
                });
            }
            canvas.Mutate(ctx =>
            {
                ctx.DrawText("生成说明", headingFont, new Rgba32(92, 180, 255), new PointF(50, 1320));
                ctx.DrawText("本页由本地 C# 流水线从实际工作视频抽帧生成，用于记录视频素材、时间点、媒体参数与文件指纹。",
                    bodyFont, new Rgba32(210, 224, 238), new PointF(50, 1360));
                ctx.DrawText("未调用第三方工作台截图，不包含虚构审核状态或虚构平台数据。",
                    bodyFont, new Rgba32(210, 224, 238), new PointF(50, 1395));
                ctx.DrawText($"SHA-256：{ComputeSha256(videoPath)}", bodyFont, new Rgba32(148, 163, 184), new PointF(50, 1480));
                ctx.DrawText($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}", bodyFont,
                    new Rgba32(148, 163, 184), new PointF(50, 1520));
            });
            await canvas.SaveAsPngAsync(outputPath, cancellationToken);
        }
        finally
        {
            foreach (var frame in frames)
                try { File.Delete(frame); } catch { }
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void DrawInfoPanel(
        IImageProcessingContext context,
        Font headingFont,
        Font bodyFont,
        string videoPath,
        MediaProbe probe,
        int pageIndex)
    {
        context.Fill(new Rgba32(12, 27, 44), new RectangleF(50, 1620, 1500, 170));
        context.DrawText("媒体与流程信息", headingFont, new Rgba32(92, 180, 255), new PointF(72, 1642));
        context.DrawText($"源文件：{Path.GetFileName(videoPath)}", bodyFont, Color.White, new PointF(72, 1684));
        context.DrawText($"分辨率：{probe.Width}×{probe.Height}    时长：{probe.DurationSeconds:0.0} 秒    证明页：{pageIndex}/4",
            bodyFont, new Rgba32(210, 224, 238), new PointF(72, 1720));
    }

    private static FontFamily ResolveFontFamily()
    {
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" })
            if (SystemFonts.TryGet(name, out var family)) return family;
        return SystemFonts.Collection.Families.First();
    }

    private static async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync("ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,duration", "-of", "csv=p=0", path],
            cancellationToken);
        var parts = result.Trim().Split(',');
        return new MediaProbe(
            parts.Length > 0 && int.TryParse(parts[0], out var width) ? width : 0,
            parts.Length > 1 && int.TryParse(parts[1], out var height) ? height : 0,
            parts.Length > 2 && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var duration) ? duration : 0);
    }

    private static Task ExtractFrameAsync(string videoPath, string outputPath, double seconds, CancellationToken cancellationToken) =>
        RunProcessAsync("ffmpeg",
            ["-y", "-hide_banner", "-loglevel", "error", "-ss", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", videoPath, "-frames:v", "1", "-vf", "scale=700:500:force_original_aspect_ratio=increase,crop=700:500", outputPath],
            cancellationToken);

    private static async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"无法启动 {fileName}");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} 执行失败：{error.Trim()}");
        return output;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int EpisodeNumber(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(path), @"第\s*0*(\d+)\s*集");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : int.MaxValue;
    }

    private sealed record MediaProbe(int Width, int Height, double DurationSeconds);
}
