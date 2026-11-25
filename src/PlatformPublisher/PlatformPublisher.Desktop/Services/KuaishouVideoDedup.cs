using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatformPublisher.Common.Models;
using PlatformPublisher.Common.Services;
using PlatformPublisher.Kuaishou.Publishing;
using PlatformPublisher.Persistence;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace PlatformPublisher.Desktop.Services;

internal static class KuaishouVideoDedup
{
    internal const string Step = "kuaishou-video-dedup";
    internal const string Label = "视频去重";
    private const string LibraryKey = "kuaishou.video-dedup.templates";
    private static readonly PlatformDatabase Database = new(PlatformPublisherPaths.MainDatabasePath);
    private static readonly JsonSettingStore Settings = new(Database);
    private static readonly AccountJsonSettingStore Accounts = new(Database);
    private const string EnabledKey = "kuaishou.video-dedup.enabled";

    internal static bool IsEnabled(string accountId) =>
        Accounts.TryLoad<bool>(accountId, EnabledKey, out var enabled) && enabled;

    internal static void SetEnabled(string accountId, bool enabled) => Accounts.Save(accountId, EnabledKey, enabled);

    internal static IReadOnlyDictionary<string, VideoDedupPreset> Templates()
    {
        var templates = Settings.Load<Dictionary<string, VideoDedupPreset>>(LibraryKey, () => []);
        templates["default"] = VideoDedupPreset.CreateDefault();
        return templates;
    }

    internal static VideoDedupPreset ReadTemplate(string id) =>
        Templates().GetValueOrDefault(id ?? "")?.NormalizedCopy()
        ?? throw new InvalidOperationException("请在快手发布配置为当前账号选择并保存视频去重模板。");

    internal static string Import(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("去重模板必须是 JSON 对象。");
        var preset = document.RootElement.TryGetProperty("steps", out _)
            ? VideoDedupPresetImporter.FromProcessorJson(json)
            : JsonSerializer.Deserialize<VideoDedupPreset>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        preset.Normalize();
        Validate(preset);
        var id = Guid.NewGuid().ToString("N");
        Settings.UpdateObject(LibraryKey, library => library[id] = JsonSerializer.SerializeToNode(preset));
        return id;
    }

    internal static void Validate(VideoDedupPreset preset)
    {
        if (preset.HasDurationLimit)
            throw new InvalidOperationException("快手去重需保留原集数和集序，请选择未设置成片时长限制的模板。");
        VideoDedupEffectFilters.Validate(preset);
    }

    internal static async Task<PublishVideoBatch> RunAsync(
        KuaishouPersonalProjectData data, string accountId, string accountName, VideoDedupPreset preset,
        bool force, Action<string> log, Func<string, CancellationToken, Task<bool>> canPlay, CancellationToken ct,
        Action<int, int>? progress = null)
    {
        Validate(preset);
        if (data.VideoPaths.Count == 0)
            throw new InvalidOperationException("没有可去重的视频，请先下载或导入剧集。");

        var presetVersion = Hash(preset.Name + "\n" + preset.Fingerprint());
        var episodes = new List<(string Source, string FileName, string Key, long Size, long Modified)>();
        for (var index = 0; index < data.VideoPaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(data.VideoPaths[index]);
            var size = file.Length;
            var modified = file.LastWriteTimeUtc.Ticks;
            var fileName = $"第{index + 1:D4}集{file.Extension.ToLowerInvariant()}";
            await using var input = File.OpenRead(file.FullName);
            var contentHash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
            episodes.Add((file.FullName, fileName, Hash($"{presetVersion}\n{fileName}\n{contentHash}"), size, modified));
        }
        var accountKey = Hash(accountId.ToUpperInvariant());
        var identity = Hash(accountKey + "\n" + string.Join('\n', episodes.Select(episode => episode.Key)));
        var revision = identity[..32];
        var accountDirectory = Path.Combine(data.WorkflowDirectory, KuaishouSettlementEpisodeVideoService.DedupDirectoryName, accountKey[..16]);
        var directory = Path.Combine(accountDirectory, revision);
        var cache = Path.Combine(accountDirectory, ".episodes");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(directory);
        var paths = episodes.Select(episode => Path.Combine(directory, episode.FileName)).ToArray();
        var ready = Path.Combine(directory, ".inputs-ready");
        if (!File.Exists(ready))
        {
            for (var index = 0; index < paths.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var temporary = paths[index] + ".tmp";
                await using (var input = File.OpenRead(episodes[index].Source))
                await using (var output = File.Create(temporary))
                    await input.CopyToAsync(output, ct);
                File.Move(temporary, paths[index], overwrite: true);
            }
            CheckSources();
            await WriteMarkerAsync(ready, identity, ct);
        }

        var reusable = episodes.Select(episode => ReadReusableEpisode(accountDirectory, cache, episode.Key, episode.FileName, revision)).ToArray();
        await VideoDedupService.RunStagedSeriesAsync(directory, accountName, paths.Length, preset, force, log, ct, progress, reusable);
        for (var index = 0; index < paths.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var path = paths[index];
            if (!File.Exists(path) || new FileInfo(path).Length == 0 || !await canPlay(path, ct))
            {
                if (File.Exists(path))
                    File.Move(path, path + ".invalid", overwrite: true);
                throw new InvalidOperationException($"第 {index + 1} 集去重成片缺失或无法播放。");
            }
        }
        CheckSources();
        await WriteMarkerAsync(Path.Combine(directory, ".validated.json"),
            JsonSerializer.Serialize(episodes.Select(episode => episode.FileName).ToArray()), ct);
        for (var index = 0; index < episodes.Count; index++)
            await WriteMarkerAsync(Path.Combine(cache, episodes[index].Key), $"{revision}|{new FileInfo(paths[index]).Length}", ct);
        log($"已校验 {paths.Length} 集去重视频。");
        return new PublishVideoBatch(identity, paths);

        void CheckSources()
        {
            foreach (var episode in episodes)
            {
                var source = new FileInfo(episode.Source);
                if (!source.Exists || source.Length != episode.Size || source.LastWriteTimeUtc.Ticks != episode.Modified)
                    throw new InvalidOperationException($"处理期间原片发生变化：{Path.GetFileName(episode.Source)}。请重新执行去重。");
            }
        }
    }

    internal static IReadOnlyList<string> ReadValidatedVideos(string workflow, string accountId, string identity)
    {
        if (identity.Length != 64 || !identity.All(Uri.IsHexDigit))
            return [];
        var accountKey = Hash(accountId.ToUpperInvariant());
        var directory = Path.Combine(workflow, KuaishouSettlementEpisodeVideoService.DedupDirectoryName, accountKey[..16], identity[..32]);
        var manifest = Path.Combine(directory, ".validated.json");
        if (!File.Exists(manifest))
            return [];
        var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [];
        var paths = new List<string>();
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name ||
                !name.StartsWith($"第{index + 1:D4}集.", StringComparison.Ordinal))
                return [];
            paths.Add(Path.Combine(directory, name));
        }
        return paths;
    }

    private static string? ReadReusableEpisode(string root, string cache, string key, string fileName, string current)
    {
        var marker = Path.Combine(cache, key);
        if (!File.Exists(marker))
            return null;
        var fields = File.ReadAllText(marker).Split('|');
        if (fields.Length != 2 || fields[0].Length != 32 || !fields[0].All(Uri.IsHexDigit) ||
            fields[0] == current || !long.TryParse(fields[1], out var length) || length <= 0)
            return null;
        var path = new FileInfo(Path.Combine(root, fields[0], fileName));
        return path.Exists && path.Length == length ? path.FullName : null;
    }

    private static async Task WriteMarkerAsync(string path, string value, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, value, ct);
        File.Move(temporary, path, overwrite: true);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
