using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Automation.Weixin.Pages;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Automation.Weixin;

public sealed class WeixinPublishOriginalityService
{
    private static readonly string[] StickerExtensions = [".png", ".webp"];
    private static readonly string[] StickerCorners = ["tl", "tr", "bl", "br"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public async Task<IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem>> ApplyAsync(
        string projectDir,
        IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> selectedItems,
        WeixinVideoPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.PublishOriginalityEnabled || selectedItems.Count == 0)
        {
            return selectedItems;
        }

        var cacheRoot = Path.Combine(projectDir, ".publish-originality");
        Directory.CreateDirectory(cacheRoot);
        var token = BuildStagingToken(selectedItems);
        var stagingDir = Path.Combine(cacheRoot, token);
        Directory.CreateDirectory(stagingDir);

        var results = new List<WeixinMaterialPublishPage.PublishVideoItem>(selectedItems.Count);
        foreach (var item in selectedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(item.VideoPath);
            if (!File.Exists(sourcePath))
            {
                results.Add(item);
                continue;
            }

            if (IsInsideDirectory(sourcePath, cacheRoot))
            {
                results.Add(item);
                continue;
            }

            var existing = options.PublishOriginalityReuseAcrossRuns
                ? ResolveExistingProcessedVideo(cacheRoot, item.EpisodeIndex, sourcePath)
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                progress?.Report($"原创度处理：复用 {Path.GetFileName(existing)}");
                results.Add(item with { VideoPath = existing });
                continue;
            }

            var targetPath = Path.Combine(stagingDir, $"{item.EpisodeIndex:D4}-{SanitizeFileName(Path.GetFileName(sourcePath))}");
            progress?.Report($"原创度处理：{Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)}");
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                CopySidecars(sourcePath, targetPath);
                await ApplyOriginalityPassAsync(targetPath, options, progress, cancellationToken);
                WriteMetadata(targetPath, sourcePath, token);
                results.Add(item with { VideoPath = targetPath });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress?.Report($"原创度处理失败，保留原片：{ex.Message}");
                TryDelete(targetPath);
                results.Add(item);
            }
        }

        return results;
    }

    private static async Task ApplyOriginalityPassAsync(
        string videoPath,
        WeixinVideoPublishOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var filters = BuildVideoFilters(options, Path.GetFileName(videoPath));
        var atempo = options.PublishOriginalitySpeed
            ? BuildSpeedTempo(Path.GetFileName(videoPath))
            : null;
        var sticker = PickSticker(options, Path.GetFileName(videoPath));

        if (filters.Count == 0 && atempo is null && string.IsNullOrWhiteSpace(sticker.Path))
        {
            return;
        }

        var tempPath = Path.Combine(
            Path.GetDirectoryName(videoPath) ?? ".",
            Path.GetFileNameWithoutExtension(videoPath) + ".orig-tmp" + Path.GetExtension(videoPath));
        TryDelete(tempPath);

        var args = new List<string>
        {
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            videoPath
        };

        if (!string.IsNullOrWhiteSpace(sticker.Path))
        {
            args.Add("-i");
            args.Add(sticker.Path);
            var videoChain = filters.Count > 0 ? string.Join(",", filters) : "null";
            args.Add("-filter_complex");
            args.Add(
                $"[0:v]{videoChain}[base];[1:v]scale={sticker.Width}:-1,format=rgba,colorchannelmixer=aa={sticker.Opacity.ToString("0.##", CultureInfo.InvariantCulture)}[stk];[base][stk]overlay={OverlayPosition(sticker.Corner, sticker.Margin)}[v]");
            args.AddRange(["-map", "[v]", "-map", "0:a?"]);
        }
        else if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(",", filters));
        }

        if (atempo is double tempo)
        {
            args.Add("-af");
            args.Add($"atempo={tempo.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        args.AddRange(
        [
            "-map_metadata", "-1",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "20",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "128k",
            "-movflags", "+faststart",
            tempPath
        ]);

        await RunProcessAsync(options.FfmpegPath, args, cancellationToken);
        if (!File.Exists(tempPath) || new FileInfo(tempPath).Length <= 0)
        {
            throw new InvalidOperationException("ffmpeg 未生成有效输出文件。");
        }

        File.Delete(videoPath);
        File.Move(tempPath, videoPath);
        progress?.Report($"原创度处理完成：{Path.GetFileName(videoPath)}");
    }

    private static List<string> BuildVideoFilters(WeixinVideoPublishOptions options, string seed)
    {
        var rng = SeedRng(seed);
        var filters = new List<string>();

        if (options.PublishOriginalityZoom)
        {
            var zoom = 1.02d + rng.NextDouble() * 0.04d;
            var z = zoom.ToString("0.####", CultureInfo.InvariantCulture);
            filters.Add($"crop=trunc(iw/{z}/2)*2:trunc(ih/{z}/2)*2,scale=trunc(iw*{z}/2)*2:trunc(ih*{z}/2)*2");
        }

        if (options.PublishOriginalityColor)
        {
            var brightness = ((rng.NextDouble() - 0.5d) * 0.06d).ToString("0.###", CultureInfo.InvariantCulture);
            var contrast = (0.97d + rng.NextDouble() * 0.06d).ToString("0.###", CultureInfo.InvariantCulture);
            var saturation = (0.95d + rng.NextDouble() * 0.10d).ToString("0.###", CultureInfo.InvariantCulture);
            var gamma = (0.97d + rng.NextDouble() * 0.06d).ToString("0.###", CultureInfo.InvariantCulture);
            filters.Add($"eq=brightness={brightness}:contrast={contrast}:saturation={saturation}:gamma={gamma}");
        }

        if (options.PublishOriginalitySpeed)
        {
            var tempo = BuildSpeedTempo(seed)!.Value;
            filters.Add($"setpts={(1.0d / tempo).ToString("0.#####", CultureInfo.InvariantCulture)}*PTS");
        }

        if (options.PublishOriginalityFade)
        {
            filters.Add("fade=t=in:st=0:d=0.4");
        }

        return filters;
    }

    private static double? BuildSpeedTempo(string seed)
    {
        var rng = SeedRng(seed + "|speed");
        return 0.96d + rng.NextDouble() * 0.08d;
    }

    private static StickerSelection PickSticker(WeixinVideoPublishOptions options, string seed)
    {
        if (string.IsNullOrWhiteSpace(options.PublishOriginalityStickerDir) ||
            !Directory.Exists(options.PublishOriginalityStickerDir))
        {
            return StickerSelection.Empty;
        }

        var stickers = Directory.EnumerateFiles(options.PublishOriginalityStickerDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => StickerExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (stickers.Length == 0)
        {
            return StickerSelection.Empty;
        }

        var rng = SeedRng(seed + "|sticker");
        return new StickerSelection(
            Path: stickers[rng.Next(stickers.Length)],
            Width: 96 + rng.Next(80),
            Opacity: 0.55d + rng.NextDouble() * 0.3d,
            Corner: StickerCorners[rng.Next(StickerCorners.Length)],
            Margin: 20 + rng.Next(28));
    }

    private static string OverlayPosition(string corner, int margin)
    {
        return corner switch
        {
            "tl" => $"{margin}:{margin}",
            "tr" => $"main_w-overlay_w-{margin}:{margin}",
            "bl" => $"{margin}:main_h-overlay_h-{margin}",
            _ => $"main_w-overlay_w-{margin}:main_h-overlay_h-{margin}"
        };
    }

    private static string ResolveExistingProcessedVideo(string cacheRoot, int episodeIndex, string sourcePath)
    {
        var fileName = $"{episodeIndex:D4}-{SanitizeFileName(Path.GetFileName(sourcePath))}";
        foreach (var candidate in Directory.EnumerateFiles(cacheRoot, fileName, SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            var metaPath = candidate + ".meta.json";
            if (!File.Exists(metaPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
                if (SourceSignatureMatches(document.RootElement, sourcePath))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static bool SourceSignatureMatches(JsonElement root, string sourcePath)
    {
        if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var info = new FileInfo(sourcePath);
        return info.Exists &&
               TryGetString(source, "source_path", out var recordedPath) &&
               string.Equals(Path.GetFullPath(recordedPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) &&
               TryGetInt64(source, "size", out var recordedSize) &&
               recordedSize == info.Length &&
               TryGetInt64(source, "mtime_ticks", out var recordedTicks) &&
               recordedTicks == info.LastWriteTimeUtc.Ticks;
    }

    private static void WriteMetadata(string outputPath, string sourcePath, string token)
    {
        var sourceInfo = new FileInfo(sourcePath);
        var payload = new
        {
            source = new
            {
                source_path = Path.GetFullPath(sourcePath),
                size = sourceInfo.Length,
                mtime_ticks = sourceInfo.LastWriteTimeUtc.Ticks
            },
            staging_token = token,
            created_at = DateTimeOffset.Now.ToString("O")
        };
        File.WriteAllText(outputPath + ".meta.json", JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
    }

    private static async Task RunProcessAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "ffmpeg" : fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        _ = await stdoutTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"ffmpeg 退出码 {process.ExitCode}" : stderr.Trim());
        }
    }

    private static string BuildStagingToken(IReadOnlyList<WeixinMaterialPublishPage.PublishVideoItem> items)
    {
        var text = string.Join(
            "|",
            items
                .Select(item => Path.GetFullPath(item.VideoPath))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        return "h" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }

    private static Random SeedRng(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed ?? string.Empty));
        return new Random(BitConverter.ToInt32(hash, 0));
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !relative.StartsWith("..", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "video.mp4" : sanitized;
    }

    private static void CopySidecars(string sourcePath, string targetPath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath) ?? ".";
        var targetDirectory = Path.GetDirectoryName(targetPath) ?? ".";
        var sourceStem = Path.GetFileNameWithoutExtension(sourcePath);
        var targetStem = Path.GetFileNameWithoutExtension(targetPath);
        foreach (var suffix in new[] { ".publish.json", ".inputs.json", ".srt", ".vtt", ".txt", ".cover.jpg", ".cover.jpeg", ".cover.png" })
        {
            var sourceSidecar = Path.Combine(sourceDirectory, sourceStem + suffix);
            if (!File.Exists(sourceSidecar))
            {
                continue;
            }

            try
            {
                File.Copy(sourceSidecar, Path.Combine(targetDirectory, targetStem + suffix), overwrite: true);
            }
            catch
            {
            }
        }
    }

    private static bool TryGetString(JsonElement element, string key, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt64(JsonElement element, string key, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(key, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record StickerSelection(
        string Path,
        int Width,
        double Opacity,
        string Corner,
        int Margin)
    {
        public static StickerSelection Empty { get; } = new(string.Empty, 0, 1, "br", 24);
    }
}
