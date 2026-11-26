using System.Security.Cryptography;
using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Persistence;
using static PlatformPublisher.Kuaishou.Publishing.KuaishouSettlementEpisodeVideoService;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace PlatformPublisher.Desktop.Services;

internal static class KuaishouVideoDedup
{
    internal const string Step = "kuaishou-video-dedup";
    internal const string DispatchStep = "video_dedup";
    internal const string Label = "视频去重";
    private const string LibraryKey = "kuaishou.video-dedup.templates";
    private static readonly PlatformDatabase Database = new(PlatformPublisherPaths.MainDatabasePath);
    private static readonly JsonSettingStore Settings = new(Database);
    private static readonly AccountJsonSettingStore Accounts = new(Database);
    private const string EnabledKey = "kuaishou.video-dedup.enabled";
    private const string PresetKey = "kuaishou.video-dedup.account-preset";
    private const string ManifestName = ".validated.json";
    private const string BatchPrefix = "批次-";

    internal sealed class FailedException(Exception cause) : InvalidOperationException($"视频去重失败：{cause.Message}", cause)
    {
    }

    private sealed record PublishedManifest(string Identity, string AccountKey, string[] Files, string[] EpisodeKeys);

    internal static bool IsEnabled(string accountId) =>
        Accounts.TryLoad<bool>(accountId, EnabledKey, out var enabled) && enabled;

    internal static void SetEnabled(string accountId, bool enabled) => Accounts.Save(accountId, EnabledKey, enabled);

    internal static VideoDedupPreset LoadPreset(string accountId, string? legacyTemplateId, PlatformDatabase? database = null)
    {
        var accounts = database is null ? Accounts : new AccountJsonSettingStore(database);
        if (accounts.TryLoad<VideoDedupPreset>(accountId, PresetKey, out var saved) && saved is not null)
            return saved.NormalizedCopy();
        var settings = database is null ? Settings : new JsonSettingStore(database);
        if (!string.IsNullOrWhiteSpace(legacyTemplateId) && legacyTemplateId != "default" &&
            settings.TryLoad<Dictionary<string, VideoDedupPreset>>(LibraryKey, out var legacy))
            return legacy?.GetValueOrDefault(legacyTemplateId)?.NormalizedCopy() ?? VideoDedupPreset.CreateDefault();
        return VideoDedupPreset.CreateDefault();
    }

    internal static void SavePreset(string accountId, VideoDedupPreset preset, PlatformDatabase? database = null)
    {
        preset = preset.NormalizedCopy();
        Validate(preset);
        var accounts = database is null ? Accounts : new AccountJsonSettingStore(database);
        accounts.Save(accountId, PresetKey, preset);
    }

    internal static void Validate(VideoDedupPreset preset)
    {
        if (preset.DurationRangeError() is { } durationError)
            throw new InvalidOperationException(durationError);
        VideoDedupEffectFilters.Validate(preset);
    }

    internal static async Task<PublishVideoBatch> RunAsync(
        KuaishouPersonalProjectData data, string accountId, string accountName, VideoDedupPreset preset,
        PublishPlatform platform, bool force, Action<string> log, Func<string, CancellationToken, Task<bool>> canPlay,
        CancellationToken ct)
    {
        var workflow = Path.GetFullPath(data.WorkflowDirectory);
        EnsureOrdinaryDirectory(workflow);
        await using var lease = await AcquireOutputLockAsync(workflow, ct);
        var directory = Path.Combine(workflow, VideosDirectoryName);
        EnsureOrdinaryDirectory(directory);

        if (!force)
        {
            PublishVideoBatch? current = null;
            try
            {
                current = ReadCurrentDedupBatch(workflow, accountId);
            }
            catch (InvalidOperationException ex)
            {
                log(ex.Message);
            }

            var upload = new KuaishouPersonalUploadStateStore().Load(workflow, platform);
            if (current is null && !string.IsNullOrWhiteSpace(upload.MiniSeriesId))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(upload.VideoBatchIdentity)
                    ? "已有快手草稿使用原片。请取消视频去重后续传，或按新分集另建草稿。"
                    : "已有快手草稿绑定的去重成片缺失或未通过校验。请恢复原批次后续传，或按新分集另建草稿。");
            if (current is not null)
            {
                upload.BindVideoBatch(current);
                log($"复用当前账号已校验的 {current.Paths.Count} 集去重成片。");
                return current;
            }
        }

        Validate(preset);
        var originals = KuaishouSettlementEpisodeVideoService.ReadSource(data.SourceDirectory, workflow);
        if (originals.Count == 0)
            originals = data.VideoPaths;
        if (originals.Count == 0)
            throw new InvalidOperationException("没有可去重的原视频，请先下载或导入剧集。");

        var previous = ReadDedupManifest(directory);
        var writingPath = Path.Combine(directory, DedupWritingName);
        var manifestPath = Path.Combine(directory, DedupManifestName);
        var oldFiles = (previous?.Files ?? []).Concat(ReadInterruptedFiles(writingPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new List<FileInfo>(originals.Count);
        for (var index = 0; index < originals.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var source = new FileInfo(originals[index]);
            if ((IsInside(directory, source.FullName) && HasManagedDedupVideos(directory)) ||
                IsInside(Path.Combine(workflow, DedupDirectoryName), source.FullName) ||
                IsInside(Path.Combine(workflow, DedupOutputDirectoryName), source.FullName) ||
                !source.Exists || source.Length == 0 || !await canPlay(source.FullName, ct))
                throw new InvalidOperationException($"第 {index + 1} 集原视频缺失或无法读取：{source.Name}");
            sources.Add(source);
        }
        MoveOriginalsOutOfVideos(workflow, directory, sources, ct);
        var copies = await FindPreparedSourceCopiesAsync(directory, sources, oldFiles, ct);

        var identity = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        string[]? generatedFiles = null;
        try
        {
            var paths = await VideoDedupService.RunDirectSeriesAsync(
                directory, sources.Select(source => source.FullName).ToArray(), accountName, preset, targets =>
                {
                    ct.ThrowIfCancellationRequested();
                    var planned = targets.Select(Path.GetFileName).Select(name => name!).ToArray();
                    foreach (var target in targets)
                    {
                        if (Directory.Exists(target) ||
                            (File.Exists(target) && !oldFiles.Contains(Path.GetFileName(target)) && !copies.Contains(target)))
                            throw new IOException($"videos 中已有不属于旧去重批次的同名文件：{Path.GetFileName(target)}");
                    }

                    foreach (var copy in copies)
                        DeleteOutput(directory, Path.GetFileName(copy));

                    var owned = oldFiles.Concat(planned).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    WriteMarker(writingPath, owned);
                    // 删除旧集的过程中若出错，由记录保留待清理的范围，下一次从原片重试。
                    File.Delete(manifestPath);
                    foreach (var file in oldFiles)
                        DeleteOutput(directory, file);

                    CleanOtherBatches(workflow, Path.Combine(workflow, DedupOutputDirectoryName),
                        Path.Combine(workflow, DedupDirectoryName), log);
                    if (oldFiles.Count > 0)
                        log($"已清除本项目之前的去重成片，使用原视频重新转码。");
                    generatedFiles = planned;
                }, log, ct);

            EnsureDedupEpisodeOrder(paths);
            for (var index = 0; index < paths.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (!await canPlay(paths[index], ct))
                    throw new InvalidOperationException($"第 {index + 1} 集去重成片无法播放。");
            }
            ct.ThrowIfCancellationRequested();
            WriteMarker(manifestPath, new DedupManifest(identity, BatchAccountKey(accountId), generatedFiles!));
            File.Delete(writingPath);
            log($"已校验去重成片，原片 {sources.Count} 集，成片 {paths.Count} 集。目录 {directory}。");
            return new PublishVideoBatch(identity, paths);
        }
        catch
        {
            if (generatedFiles is not null && File.Exists(writingPath))
            {
                File.Delete(manifestPath);
                foreach (var file in generatedFiles)
                {
                    try { DeleteOutput(directory, file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        log($"清理未完成的成片失败：{ex.Message}");
                    }
                }
            }
            throw;
        }
    }

    private static void MoveOriginalsOutOfVideos(string workflow, string directory, List<FileInfo> sources, CancellationToken ct)
    {
        var originals = Path.Combine(workflow, DedupOriginalsDirectoryName);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            if (!IsInside(directory, source.FullName))
                continue;
            ct.ThrowIfCancellationRequested();
            EnsureOrdinaryDirectory(originals);
            var target = Path.Combine(originals, source.Name);
            File.Move(source.FullName, target);
            sources[index] = new FileInfo(target);
        }
    }

    private static async Task<HashSet<string>> FindPreparedSourceCopiesAsync(
        string directory, IReadOnlyList<FileInfo> sources, HashSet<string> oldFiles, CancellationToken ct)
    {
        var copies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared = Directory.EnumerateFiles(directory).Where(path =>
            !oldFiles.Contains(Path.GetFileName(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0).ToArray();
        for (var index = 0; index < sources.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var source = sources[index];
            var copy = KuaishouPersonalEpisodeUploadService.MatchEpisodeFile(prepared, index + 1);
            if (copy is null || new FileInfo(copy).Length != source.Length)
                continue;
            await using var original = source.OpenRead();
            await using var candidate = File.OpenRead(copy);
            var originalHash = await SHA256.HashDataAsync(original, ct);
            var candidateHash = await SHA256.HashDataAsync(candidate, ct);
            if (originalHash.SequenceEqual(candidateHash))
                copies.Add(copy);
        }
        return copies;
    }

    private static bool IsInside(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string[] ReadInterruptedFiles(string marker)
    {
        if (!File.Exists(marker))
            return [];
        var files = JsonSerializer.Deserialize<string[]>(File.ReadAllText(marker));
        if (files is null || files.Any(file => file is null || file.Length < 7 ||
                !file.StartsWith("第", StringComparison.Ordinal) || !file.EndsWith("集.mp4", StringComparison.Ordinal) ||
                !int.TryParse(file.AsSpan(1, file.Length - 6), out var number) || file != DedupEpisodeName(number) ||
                number < 1))
            throw new InvalidOperationException("未完成的去重记录无效，请检查项目的 videos 目录。");
        return files;
    }

    private static void DeleteOutput(string directory, string file)
    {
        var path = Path.Combine(directory, file);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"去重成片路径是文件链接：{path}");
        File.Delete(path);
    }

    internal static IReadOnlyList<string> ReadValidatedVideos(string workflow, string accountId, string identity)
    {
        if (identity.Length != 64 || !identity.All(Uri.IsHexDigit))
            return [];
        if (ReadCurrentDedupBatch(workflow, accountId) is { } current)
            return current.Identity == identity ? current.Paths : [];
        var accountKey = BatchAccountKey(accountId);
        if (FindPublishedBatch(Path.Combine(workflow, KuaishouSettlementEpisodeVideoService.DedupOutputDirectoryName), accountKey, identity) is { } published)
            return published.Paths;
        var directory = Path.Combine(workflow, KuaishouSettlementEpisodeVideoService.DedupDirectoryName, accountKey[..16], identity[..32]);
        return ReadLegacyVideos(directory);
    }

    private static string[] ReadLegacyVideos(string directory)
    {
        var manifest = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifest))
            return [];
        var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [];
        return ResolveManifestPaths(directory, names);
    }

    private static string[] ResolveManifestPaths(string directory, string[] names)
    {
        var paths = new string[names.Length];
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name ||
                !name.StartsWith($"第{index + 1:D4}集.", StringComparison.Ordinal))
                return [];
            paths[index] = Path.Combine(directory, name);
        }
        return paths;
    }

    private static (string Directory, PublishedManifest Manifest, string[] Paths)? FindPublishedBatch(string root, string accountKey, string? identity)
    {
        foreach (var directory in ListPublishedDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var manifest = ReadPublishedManifest(directory);
            if (manifest is null || manifest.AccountKey != accountKey || (identity is not null && manifest.Identity != identity))
                continue;
            var paths = ResolveManifestPaths(directory, manifest.Files);
            if (paths.Length != 0)
                return (directory, manifest, paths);
        }
        return null;
    }

    private static PublishedManifest? ReadPublishedManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path))
            return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<PublishedManifest>(File.ReadAllText(path));
            return manifest is { Identity.Length: 64, AccountKey.Length: 64, Files.Length: > 0, EpisodeKeys: not null } &&
                manifest.Identity.All(Uri.IsHexDigit) && manifest.AccountKey.All(Uri.IsHexDigit) ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ListPublishedDirectories(string root) =>
        Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0
            ? Directory.EnumerateDirectories(root, BatchPrefix + "*").Where(path =>
                Guid.TryParseExact(Path.GetFileName(path)[BatchPrefix.Length..], "N", out _) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            : [];

    private static void CleanOtherBatches(string workflow, string publishedRoot, string stagingRoot, Action<string> log)
    {
        foreach (var directory in ListPublishedDirectories(publishedRoot).ToArray())
        {
            if (ReadPublishedManifest(directory) is not { } manifest)
                continue;
            if (ResolveManifestPaths(directory, manifest.Files).Length == 0)
                continue;
            foreach (var file in manifest.Files)
                DeleteOutput(directory, file);
            File.Delete(Path.Combine(directory, ManifestName));
            if (Directory.GetFileSystemEntries(directory).Length == 0)
                Directory.Delete(directory);
            else
                log($"已清除旧批次视频，保留其他文件：{directory}。");
        }
        if (Directory.Exists(publishedRoot) && (File.GetAttributes(publishedRoot) & FileAttributes.ReparsePoint) == 0 &&
            Directory.GetFileSystemEntries(publishedRoot).Length == 0)
            Directory.Delete(publishedRoot);
        DeleteLegacyStaging(workflow, stagingRoot);
    }

    private static void DeleteLegacyStaging(string workflow, string directory)
    {
        if (!Directory.Exists(directory))
            return;
        if (!IsInside(workflow, directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"旧去重目录无法安全清理：{directory}");
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var parent))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"旧去重目录中包含文件或目录链接：{entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
        Directory.Delete(directory, recursive: true);
    }

    private static void EnsureOrdinaryDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"去重目录不能是目录链接：{directory}");
    }

    private static void WriteMarker<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
