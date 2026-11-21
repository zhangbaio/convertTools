using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChannelsPublisher.Clip;

internal static partial class ClipShotCuts
{
    public static async Task<int[]> ReadAsync(string path, int durationMs, string outputRoot, string ffmpeg,
        bool cache, Action<string>? log, CancellationToken ct)
    {
        var cachePath = cache ? GetCachePath(path, outputRoot, ffmpeg) : null;
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(cachePath, ct));
                if (cached is not null && cached.All(cut => cut > 0 && cut < durationMs))
                    return cached.Distinct().Order().ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                log?.Invoke($"{Path.GetFileName(path)} 镜头缓存无法读取，将重新检测。");
            }
        }

        var (code, output, _) = await Ffmpeg.RunCaptureAsync(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-i", path, "-an", "-sn",
                "-vf", "scale=320:-2,select='gt(scene,0.3)',metadata=mode=print:file=-", "-f", "null", "-"], ct);
        if (code != 0)
        {
            log?.Invoke($"{Path.GetFileName(path)} 镜头检测失败。");
            return [];
        }

        var cuts = Timestamp().Matches(output)
            .Select(match => double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? Math.Round(seconds * 1000) : 0)
            .Where(milliseconds => milliseconds > 0 && milliseconds < durationMs)
            .Select(milliseconds => (int)milliseconds).Distinct().Order().ToArray();
        if (cachePath is not null)
            await SaveAsync(cachePath, cuts, log, ct);
        return cuts;
    }

    private static string GetCachePath(string path, string outputRoot, string ffmpeg)
    {
        var source = new FileInfo(path);
        var key = $"ffmpeg-scene-v1|{ffmpeg}|{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(outputRoot, "分析", $"镜头-{hash}.json");
    }

    private static async Task SaveAsync(string path, int[] cuts, Action<string>? log, CancellationToken ct)
    {
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(cuts), ct);
            File.Move(pending, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"无法保存镜头缓存。{ex.Message}");
        }
        finally
        {
            try { File.Delete(pending); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex(@"\bpts_time:([^\s]+)")]
    private static partial Regex Timestamp();
}
