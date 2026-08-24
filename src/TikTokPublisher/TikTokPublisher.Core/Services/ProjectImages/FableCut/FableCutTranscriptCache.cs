using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.Asr;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

internal static class FableCutTranscriptCache
{
    private const int CacheSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<IReadOnlyList<TranscriptSegment>> LoadOrRecognizeAsync(
        string projectDirectory,
        string videoPath,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        var fullVideoPath = Path.GetFullPath(videoPath);
        var cacheDirectory = Path.Combine(Path.GetFullPath(projectDirectory), ".fablecut_asr");
        var digest = ComputeVideoFingerprint(fullVideoPath, settings)[..16];
        var cachePath = Path.Combine(cacheDirectory, $"{Path.GetFileNameWithoutExtension(fullVideoPath)}.{digest}.json");
        var cached = await TryLoadAsync(cachePath, ct).ConfigureAwait(false);
        if (cached is { Count: > 0 })
        {
            log?.Invoke($"FableCut/ASR缓存：{Path.GetFileName(videoPath)}（{cached.Count} 段）");
            return cached;
        }

        var segments = await LocalParaformerAsrClient
            .RecognizeVideoTranscriptAsync(fullVideoPath, settings, log, ct)
            .ConfigureAwait(false);
        if (segments.Count == 0)
            throw new InvalidOperationException($"本地 ASR 未识别到有效对白：{Path.GetFileName(videoPath)}");

        Directory.CreateDirectory(cacheDirectory);
        var payload = new TranscriptCacheDocument(CacheSchemaVersion, segments.ToArray());
        var tempPath = cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, ct)
                .ConfigureAwait(false);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }

        return segments;
    }

    public static string ComputeVideoFingerprint(string videoPath, ClientSettings settings)
    {
        var info = new FileInfo(Path.GetFullPath(videoPath));
        var payload = JsonSerializer.Serialize(new
        {
            schema = CacheSchemaVersion,
            path = info.FullName,
            size = info.Exists ? info.Length : 0,
            mtime_ticks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            asr = ComputeSettingsFingerprint(settings),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string ComputeSettingsFingerprint(ClientSettings settings)
    {
        var resolved = SherpaOnnxModelResolver.TryResolve(settings);
        var payload = JsonSerializer.Serialize(new
        {
            schema = CacheSchemaVersion,
            language = (settings.TiktokAsrLanguage ?? "zh-CN").Trim(),
            configured_model_dir = (settings.TiktokAsrLocalModelDir ?? "").Trim(),
            configured_vad_path = (settings.TiktokAsrLocalVadPath ?? "").Trim(),
            model = FileIdentity(resolved?.ModelPath),
            tokens = FileIdentity(resolved?.TokensPath),
            vad = FileIdentity(resolved?.VadPath),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static object? FileIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            return new
            {
                path = info.FullName,
                size = info.Exists ? info.Length : 0,
                mtime_ticks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
            };
        }
        catch
        {
            return new { path, size = 0L, mtime_ticks = 0L };
        }
    }

    private static async Task<IReadOnlyList<TranscriptSegment>?> TryLoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<TranscriptCacheDocument>(json, JsonOptions);
            if (document is null || document.SchemaVersion != CacheSchemaVersion || document.Segments.Length == 0)
                return null;
            return document.Segments
                .Where(segment => segment.EndSeconds > segment.StartSeconds && !string.IsNullOrWhiteSpace(segment.Text))
                .OrderBy(segment => segment.StartSeconds)
                .ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private sealed record TranscriptCacheDocument(int SchemaVersion, TranscriptSegment[] Segments);
}
