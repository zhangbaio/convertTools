using System.Text.Json;
using ShortDrama.Core.Models;
using SixLabors.ImageSharp;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

/// <summary>
/// Uses the bundled FableCut editor as a deterministic, local-only renderer for
/// project evidence screenshots. The editor/browser portion is serialized because
/// queue workers may process many projects concurrently.
/// </summary>
internal static class FableCutProjectImageBackend
{
    private const int ScreenshotWidth = 1920;
    private const int ScreenshotHeight = 1080;
    private static readonly SemaphoreSlim BrowserGate = new(1, 1);
    private static readonly HashSet<string> BrowserVideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm" };

    public static async Task<ProjectImageGenerateResult> GenerateAsync(
        string sourceProjectDirectory,
        string outputDirectory,
        IReadOnlyList<string> sourceVideos,
        string projectTitle,
        int count,
        int clipCount,
        string? configuredAssetRoot,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        if (sourceVideos.Count == 0)
            throw new InvalidOperationException("FableCut 工程图需要原始视频，不能仅使用缓存抽帧。");

        var assetRoot = FableCutAssetResolver.Resolve(configuredAssetRoot);
        var assetFingerprint = FableCutAssetResolver.ComputeFingerprint(assetRoot);
        Directory.CreateDirectory(outputDirectory);

        await BrowserGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            log?.Invoke($"FableCut：已加载本地编辑器资源 {Path.GetFileName(assetRoot)}（{assetFingerprint[..12]}）");
            await using var renderer = await FableCutScreenshotRenderer.CreateAsync(log, ct).ConfigureAwait(false);
            var outputs = new List<string>(count);

            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var videoPath = Path.GetFullPath(sourceVideos[index % sourceVideos.Count]);
                if (!BrowserVideoExtensions.Contains(Path.GetExtension(videoPath)))
                {
                    throw new InvalidOperationException(
                        $"FableCut 工程图暂不支持浏览器直解码 {Path.GetExtension(videoPath)}：" +
                        $"{Path.GetFileName(videoPath)}。请先转码为 H.264/AAC MP4。");
                }

                var media = await FableCutMediaProbe.ProbeAsync(videoPath, ct).ConfigureAwait(false);
                var transcript = await FableCutTranscriptCache.LoadOrRecognizeAsync(
                    sourceProjectDirectory,
                    videoPath,
                    settings,
                    log,
                    ct).ConfigureAwait(false);
                var cues = transcript
                    .Select(segment => new FableCutSubtitleCue(
                        segment.StartSeconds * 1000d,
                        segment.EndSeconds * 1000d,
                        segment.Text))
                    .ToArray();

                var projectJson = FableCutProjectBuilder.BuildJson(
                    videoPath,
                    projectTitle,
                    index + 1,
                    media.DurationSeconds,
                    media.Width,
                    media.Height,
                    clipCount,
                    cues);
                using var projectDocument = JsonDocument.Parse(projectJson);
                var mediaJson = projectDocument.RootElement.GetProperty("media").GetRawText();

                log?.Invoke(
                    $"FableCut：渲染标准工程图 {index + 1}/{count}（{Path.GetFileName(videoPath)}，{cues.Length} 段台词）");
                var preferredRatio = 0.22d + index % 4 * 0.17d;
                var previewRatio = await FableCutPreviewSelector
                    .SelectAsync(videoPath, media.DurationSeconds, preferredRatio, ct)
                    .ConfigureAwait(false);
                var outputPath = Path.Combine(outputDirectory, $"工程图_{index + 1}.png");

                await using (var server = FableCutLoopbackServer.Start(
                                 assetRoot,
                                 videoPath,
                                 projectJson,
                                 mediaJson))
                {
                    await renderer.CaptureAsync(server.BaseUrl, outputPath, previewRatio, ct)
                        .ConfigureAwait(false);
                }

                await ValidateScreenshotAsync(outputPath, ct).ConfigureAwait(false);
                outputs.Add(Path.GetFullPath(outputPath));
            }

            return new ProjectImageGenerateResult(outputs.Count, outputs);
        }
        finally
        {
            BrowserGate.Release();
        }
    }

    private static async Task ValidateScreenshotAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"FableCut 工程图生成失败：{Path.GetFileName(path)}");

        var image = await Image.IdentifyAsync(path, ct).ConfigureAwait(false);
        if (image is null || image.Width != ScreenshotWidth || image.Height != ScreenshotHeight)
        {
            throw new InvalidOperationException(
                $"FableCut 工程图尺寸无效：{Path.GetFileName(path)}，" +
                $"期望 {ScreenshotWidth}×{ScreenshotHeight}，实际 {image?.Width ?? 0}×{image?.Height ?? 0}");
        }
    }
}
