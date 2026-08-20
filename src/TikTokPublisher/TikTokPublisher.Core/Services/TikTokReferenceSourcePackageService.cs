using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using Color = SixLabors.ImageSharp.Color;
using Font = SixLabors.Fonts.Font;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 生成与人工制作项目一致的原始素材包。角色定妆图必须来自已配置的图片模型，
/// 不能以视频抽帧或程序色块替代。
/// </summary>
public static partial class TikTokReferenceSourcePackageService
{
    public const string DirectoryName = "参考格式原始素材包";
    public const string CharacterDirectoryName = "角色";
    public const string CharacterManifestFileName = "角色清单.json";
    public const int MinCharacterCount = 3;
    public const int MaxCharacterCount = 6;
    public const string VideoDirectoryName = "videos";
    public const string MaterialDirectoryName = "素材文件";
    public const string CharacterWorkbenchFileName = "角色矢量图.png";
    public const string SceneDesignFileName1 = "场景设计图1.png";
    public const string SceneDesignFileName2 = "场景设计图2.png";
    public const string StateFileName = ".reference-source-package.json";
    public const string Version = "v5-clear-single-frame-selection";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    public static string GetRoot(string workflowProjectDirectory) =>
        Path.Combine(
            TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflowProjectDirectory),
            DirectoryName);

    public static string GetCharacterManifestPath(string workflowProjectDirectory) =>
        Path.Combine(GetRoot(workflowProjectDirectory), CharacterDirectoryName, CharacterManifestFileName);

    private static string GetStatePath(string workflowProjectDirectory) =>
        Path.Combine(
            TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflowProjectDirectory),
            StateFileName);

    public static bool HasCurrentOutput(string workflowProjectDirectory)
    {
        var root = GetRoot(workflowProjectDirectory);
        var state = GetStatePath(workflowProjectDirectory);
        var characterDir = Path.Combine(root, CharacterDirectoryName);
        return File.Exists(state) &&
               File.Exists(Path.Combine(root, SceneDesignFileName1)) &&
               File.Exists(Path.Combine(root, SceneDesignFileName2)) &&
               Directory.Exists(characterDir) &&
               Directory.EnumerateFiles(characterDir).Count(IsImage) >= 3;
    }

    public static async Task<string> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct,
        int configuredCharacterCount = TikTokAccountProfile.DefaultRoleVectorCharacterCount)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        configuredCharacterCount = NormalizeConfiguredCharacterCount(configuredCharacterCount);
        var root = GetRoot(context.WorkflowProjectDir);
        var title = FirstNonEmpty(item.NewTitle, item.Title, item.OriginalTitle, Path.GetFileName(context.SourceProjectDir));
        var originalTitle = FirstNonEmpty(item.OriginalTitle, item.DisplayName, title);
        var intro = ResolveIntro(item, context);
        var script = ReadProjectScript(context, title, intro);
        var candidates = NormalizeCharacterProfiles(ExtractCharacterProfiles(script, intro), intro);
        var characters = SelectCharacterProfiles(candidates, configuredCharacterCount);
        var episodeCharacterSources = FindEpisodeCharacterSources(context, root)
            .Take(characters.Length)
            .ToArray();
        var sourceFingerprint = ComputeSourceFingerprint(
            title, intro, script, settings, episodeCharacterSources);
        if (!forceRerun && HasCurrentOutput(context.WorkflowProjectDir) &&
            HasMatchingFingerprint(context.WorkflowProjectDir, sourceFingerprint))
        {
            await RefreshDerivedImagesAsync(context.WorkflowProjectDir, log, ct).ConfigureAwait(false);
            log?.Invoke($"参考格式原始素材包已存在，已按当前模板刷新并复用：{root}");
            return root;
        }

        var useEpisodeCharacters = episodeCharacterSources.Length >= MinCharacterCount;
        var existingCharacterDir = Path.Combine(root, CharacterDirectoryName);
        var reusableCharacterPaths = !forceRerun && !useEpisodeCharacters && Directory.Exists(existingCharacterDir)
            ? SelectExistingCharacterImages(existingCharacterDir, log, configuredCharacterCount).ToArray()
            : [];
        var reuseCharacters = reusableCharacterPaths.Length >= MinCharacterCount;
        if (!useEpisodeCharacters && !reuseCharacters) EnsureImageModelConfigured(settings);
        ResetPackageRoot(root, preserveCharactersAndRoleVector: reuseCharacters);
        var characterDir = Path.Combine(root, CharacterDirectoryName);
        var videoDir = Path.Combine(root, VideoDirectoryName);
        var materialDir = Path.Combine(root, MaterialDirectoryName, "001");
        Directory.CreateDirectory(characterDir);
        Directory.CreateDirectory(videoDir);
        Directory.CreateDirectory(materialDir);

        var generatedCharacters = new List<GeneratedCharacter>();
        if (useEpisodeCharacters)
        {
            generatedCharacters.AddRange(await ImportEpisodeCharacterImagesAsync(
                characterDir,
                characters,
                episodeCharacterSources,
                settings,
                log,
                ct).ConfigureAwait(false));
            log?.Invoke(
                $"参考格式素材包：已从剧集真实角色素材生成 {generatedCharacters.Count} 张定妆图，人物形象与成片保持一致。");
        }
        else if (reuseCharacters)
        {
            generatedCharacters.AddRange(reusableCharacterPaths.Select(path => new GeneratedCharacter(
                new CharacterProfile(Path.GetFileNameWithoutExtension(path), "复用已有图片模型角色定妆图"),
                path)));
            log?.Invoke($"参考格式素材包：复用现有角色定妆图 {generatedCharacters.Count} 张，不调用图片模型。");
        }
        else
        {
            log?.Invoke($"参考格式素材包：识别 {characters.Length} 个主要角色，开始调用图片模型生成真人定妆图。");
            foreach (var (character, index) in characters.Select((value, index) => (value, index)))
            {
                ct.ThrowIfCancellationRequested();
                var output = Path.Combine(characterDir, $"{SanitizeFileName(character.Name)}.png");
                log?.Invoke($"角色图片 {index + 1}/{characters.Length}：{character.Name}（图片模型）");
                var bytes = await GenerateImageWithRetryAsync(
                    BuildCharacterPrompt(character), settings, character.Name, ct).ConfigureAwait(false);
                await SaveNormalizedPngAsync(bytes, output, 768, 1024, ct).ConfigureAwait(false);
                generatedCharacters.Add(new GeneratedCharacter(character, output));
            }
        }
        WriteCharacterManifest(characterDir, generatedCharacters, configuredCharacterCount, candidates.Length);

        var videos = ProjectVideoResolver.ResolveSourceVideos(context.SourceProjectDir).ToArray();
        LinkVideos(videos, videoDir, materialDir, title, ct);
        await WriteProjectFilesAsync(
            root, context, title, originalTitle, intro, script, item, videos.Length, ct).ConfigureAwait(false);

        await RefreshDerivedImagesAsync(context.WorkflowProjectDir, log, ct).ConfigureAwait(false);

        var statePath = GetStatePath(context.WorkflowProjectDir);
        await WriteHiddenStateFileAsync(
            statePath,
            JsonSerializer.Serialize(new
            {
                version = Version,
                sourceFingerprint,
                title,
                imageProvider = PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider),
                imageModel = ResolveModelId(settings),
                generatedAt = DateTimeOffset.Now,
                characters = generatedCharacters.Select(x => new
                {
                    x.Profile.Name,
                    x.Profile.Description,
                    file = Path.GetFileName(x.Path),
                    source = x.Source,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);

        log?.Invoke($"参考格式原始素材包生成完成：{root}");
        return root;
    }

    public static async Task RefreshDerivedImagesAsync(
        string workflowProjectDirectory,
        Action<string>? log,
        CancellationToken ct)
    {
        var context = ProjectWorkspaceService.LoadContext(workflowProjectDirectory);
        var root = GetRoot(context.WorkflowProjectDir);
        var sceneSources = await ResolveSceneSourcesAsync(context, root, log, ct).ConfigureAwait(false);
        RenderSceneDesignSheet(
            Path.Combine(root, SceneDesignFileName1),
            Path.GetFileName(context.WorkflowProjectDir).TrimStart('_'),
            "主要场景设计参考",
            sceneSources.Take(4).ToArray());
        RenderSceneDesignSheet(
            Path.Combine(root, SceneDesignFileName2),
            Path.GetFileName(context.WorkflowProjectDir).TrimStart('_'),
            "补充场景与光线参考",
            sceneSources.Skip(4).Take(4).ToArray());
        TrySetHidden(GetStatePath(context.WorkflowProjectDir));
        log?.Invoke($"参考格式素材包：已用 {sceneSources.Count} 张真实场景帧刷新场景设计图。");
    }

    internal static async Task<IReadOnlyList<string>> EnsureCharacterImagesAsync(
        QueueProjectItem item,
        ClientSettings settings,
        int configuredCharacterCount,
        Action<string>? log,
        CancellationToken ct)
    {
        configuredCharacterCount = NormalizeConfiguredCharacterCount(configuredCharacterCount);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var root = GetRoot(context.WorkflowProjectDir);
        var characterDir = Path.Combine(root, CharacterDirectoryName);
        Directory.CreateDirectory(characterDir);

        var title = FirstNonEmpty(item.NewTitle, item.Title, item.OriginalTitle, Path.GetFileName(context.SourceProjectDir));
        var intro = ResolveIntro(item, context);
        var script = ReadProjectScript(context, title, intro);
        var candidates = NormalizeCharacterProfiles(ExtractCharacterProfiles(script, intro), intro);
        var profiles = SelectCharacterProfiles(candidates, configuredCharacterCount);

        var episodeCharacterSources = FindEpisodeCharacterSources(context, root)
            .Take(profiles.Length)
            .ToArray();
        if (episodeCharacterSources.Length >= MinCharacterCount)
        {
            var imported = await ImportEpisodeCharacterImagesAsync(
                characterDir,
                profiles,
                episodeCharacterSources,
                settings,
                log,
                ct).ConfigureAwait(false);
            WriteCharacterManifest(characterDir, imported, configuredCharacterCount, episodeCharacterSources.Length);
            log?.Invoke(
                $"角色矢量图：已使用 {imported.Count} 张剧集真实角色素材，不再重新生成其他演员形象。");
            return imported.Select(character => character.Path).ToArray();
        }

        var existing = SelectExistingCharacterImages(characterDir, log, configuredCharacterCount).ToList();
        if (existing.Count >= MinCharacterCount)
        {
            log?.Invoke($"角色矢量图：复用现有角色定妆图 {existing.Count} 张，不调用图片模型。");
            return existing;
        }

        EnsureImageModelConfigured(settings);

        foreach (var (profile, index) in profiles.Select((value, index) => (value, index)))
        {
            ct.ThrowIfCancellationRequested();
            var output = Path.Combine(characterDir, $"{SanitizeFileName(profile.Name)}.png");
            if (File.Exists(output)) continue;
            log?.Invoke($"角色矢量图：角色图片 {index + 1}/{profiles.Length}，生成 {profile.Name} 定妆图。");
            var bytes = await GenerateImageWithRetryAsync(
                BuildCharacterPrompt(profile), settings, profile.Name, ct).ConfigureAwait(false);
            await SaveNormalizedPngAsync(bytes, output, 768, 1024, ct).ConfigureAwait(false);
        }

        existing = profiles
            .Select(profile => Path.Combine(characterDir, $"{SanitizeFileName(profile.Name)}.png"))
            .Where(File.Exists)
            .Take(MaxCharacterCount)
            .ToList();
        if (existing.Count < MinCharacterCount)
            throw new InvalidOperationException(
                $"生成角色矢量图至少需要 {MinCharacterCount} 张角色定妆图，当前只有 {existing.Count} 张。");
        WriteCharacterManifest(
            characterDir,
            existing.Select((path, index) => new GeneratedCharacter(profiles[index], path)).ToList(),
            configuredCharacterCount,
            candidates.Length);
        return existing;
    }

    internal static async Task<IReadOnlyList<string>> ResolveSceneSourcesAsync(
        ProjectWorkspaceContext context,
        string packageRoot,
        Action<string>? log,
        CancellationToken ct)
    {
        var sceneSources = FindSceneSources(context, packageRoot).Take(8).ToList();
        if (sceneSources.Count >= 4) return sceneSources;

        var videos = ProjectVideoResolver.ResolveSourceVideos(context.SourceProjectDir).ToArray();
        return await ExtractSceneFramesAsync(packageRoot, videos, log, ct).ConfigureAwait(false);
    }

    internal static IReadOnlyList<CharacterProfile> ExtractCharacterProfiles(string script, string intro = "")
    {
        var profiles = new Dictionary<string, CharacterProfile>(StringComparer.Ordinal);
        foreach (var raw in Regex.Split(script ?? string.Empty, "\\r?\\n"))
        {
            var line = raw.Trim().TrimStart('-', '*', '•', '△');
            if (line.Length is < 4 or > 500) continue;
            var match = CharacterDefinitionRegex().Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (IsNonCharacterName(name)) continue;
            profiles.TryAdd(name, new CharacterProfile(name, line));
        }

        if (profiles.Count < 3)
        {
            foreach (var raw in Regex.Split(script ?? string.Empty, "\\r?\\n"))
            {
                var match = DialogueRegex().Match(raw.Trim());
                if (!match.Success) continue;
                var name = match.Groups["name"].Value.Trim();
                if (IsNonCharacterName(name)) continue;
                profiles.TryAdd(name, new CharacterProfile(name, $"主要短剧角色。剧情参考：{intro}"));
                if (profiles.Count >= 6) break;
            }
        }

        return profiles.Values.ToArray();
    }

    internal static CharacterProfile[] NormalizeCharacterProfiles(
        IEnumerable<CharacterProfile> candidates,
        string intro = "")
    {
        var candidateList = candidates.ToList();
        if (candidateList.Count > 0 && candidateList.All(profile => IsGenericCharacterName(profile.Name)))
            candidateList.Clear();
        var indexed = candidateList
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name) && !IsNonCharacterName(profile.Name))
            .Select((profile, index) => new { Profile = profile, Index = index })
            .GroupBy(item => NormalizeCharacterName(item.Profile.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group.First())
            .OrderBy(item => CharacterPriority(item.Profile))
            .ThenBy(item => item.Index)
            .Select(item => item.Profile)
            .Take(MaxCharacterCount)
            .ToList();

        if (indexed.Count < MinCharacterCount)
            indexed = AddFallbackCharacters(indexed, intro).Take(MinCharacterCount).ToList();
        if (indexed.Count is < MinCharacterCount or > MaxCharacterCount)
            throw new InvalidOperationException(
                $"角色采集结果必须为 {MinCharacterCount}–{MaxCharacterCount} 人，当前为 {indexed.Count} 人。");
        return indexed.ToArray();
    }

    internal static int NormalizeConfiguredCharacterCount(int value) =>
        Math.Clamp(
            value > 0 ? value : TikTokAccountProfile.DefaultRoleVectorCharacterCount,
            MinCharacterCount,
            MaxCharacterCount);

    internal static int ResolveSelectedCharacterCount(int candidateCount, int configuredCharacterCount)
    {
        configuredCharacterCount = NormalizeConfiguredCharacterCount(configuredCharacterCount);
        if (candidateCount < MinCharacterCount) return MinCharacterCount;
        return candidateCount >= configuredCharacterCount
            ? configuredCharacterCount
            : MinCharacterCount;
    }

    private static CharacterProfile[] SelectCharacterProfiles(
        IReadOnlyList<CharacterProfile> candidates,
        int configuredCharacterCount)
    {
        var selectedCount = ResolveSelectedCharacterCount(candidates.Count, configuredCharacterCount);
        if (candidates.Count < selectedCount)
            throw new InvalidOperationException(
                $"角色候选不足：配置 {configuredCharacterCount} 人，最低需要 {selectedCount} 人，当前只有 {candidates.Count} 人。");
        return candidates.Take(selectedCount).ToArray();
    }

    private static IReadOnlyList<string> SelectExistingCharacterImages(
        string characterDirectory,
        Action<string>? log,
        int configuredCharacterCount)
    {
        if (!Directory.Exists(characterDirectory)) return [];
        var all = Directory.EnumerateFiles(characterDirectory)
            .Where(IsImage)
            .ToArray();
        configuredCharacterCount = NormalizeConfiguredCharacterCount(configuredCharacterCount);
        var ordered = new List<string>();
        var manifestPath = Path.Combine(characterDirectory, CharacterManifestFileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.TryGetProperty("characters", out var entries) &&
                    entries.ValueKind == JsonValueKind.Array)
                {
                    ordered.AddRange(entries.EnumerateArray()
                        .OrderBy(entry => entry.TryGetProperty("order", out var order) ? order.GetInt32() : int.MaxValue)
                        .Select(entry => entry.TryGetProperty("file", out var file) ? file.GetString() : null)
                        .Where(file => !string.IsNullOrWhiteSpace(file))
                        .Select(file => Path.Combine(characterDirectory, file!))
                        .Where(path => File.Exists(path) && IsImage(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                }
            }
            catch
            {
                // 旧清单损坏时按现有文件重建。
            }
        }

        ordered.AddRange(all
            .OrderBy(path => CharacterFilePriority(Path.GetFileNameWithoutExtension(path)))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Where(path => !ordered.Contains(path, StringComparer.OrdinalIgnoreCase)));
        if (all.Length > MaxCharacterCount)
            log?.Invoke($"角色矢量图：角色目录共 {all.Length} 张图片，按重要度限制为 {MaxCharacterCount} 人。");
        if (ordered.Count < MinCharacterCount) return ordered;

        var selectedCount = ResolveSelectedCharacterCount(ordered.Count, configuredCharacterCount);
        var selectedPaths = ordered.Take(selectedCount).ToArray();
        var fallbackToMinimum = ordered.Count < configuredCharacterCount;
        if (fallbackToMinimum)
        {
            log?.Invoke(
                $"角色矢量图：配置 {configuredCharacterCount} 人，现有有效角色图 {ordered.Count} 张，" +
                $"未达到配置数量，回退到 {MinCharacterCount} 人。");
        }
        WriteCharacterManifest(
            characterDirectory,
            selectedPaths.Select(path => new GeneratedCharacter(
                new CharacterProfile(Path.GetFileNameWithoutExtension(path), "从现有角色目录选择"),
                path)).ToList(),
            configuredCharacterCount,
            ordered.Count);
        return selectedPaths;
    }

    private static void WriteCharacterManifest(
        string characterDirectory,
        IReadOnlyList<GeneratedCharacter> characters,
        int configuredCharacterCount,
        int candidateCount)
    {
        var selected = characters
            .GroupBy(character => Path.GetFullPath(character.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(MaxCharacterCount)
            .ToArray();
        if (selected.Length < MinCharacterCount)
            throw new InvalidOperationException(
                $"角色清单必须包含 {MinCharacterCount}–{MaxCharacterCount} 人，当前为 {selected.Length} 人。");

        var payload = new
        {
            version = "v2-configured-count",
            configuredCount = NormalizeConfiguredCharacterCount(configuredCharacterCount),
            candidateCount,
            selectedCount = selected.Length,
            fallbackToMinimum = candidateCount < NormalizeConfiguredCharacterCount(configuredCharacterCount),
            fallbackReason = candidateCount < NormalizeConfiguredCharacterCount(configuredCharacterCount)
                ? $"有效人物数 {candidateCount} 未达到配置人数 {NormalizeConfiguredCharacterCount(configuredCharacterCount)}，回退至 {MinCharacterCount} 人"
                : string.Empty,
            characterCount = selected.Length,
            characters = selected.Select((character, index) => new
            {
                order = index + 1,
                name = character.Profile.Name,
                roleType = DescribeCharacterRole(character.Profile),
                importance = 100 - CharacterPriority(character.Profile) * 20 - index,
                file = Path.GetFileName(character.Path),
                isFallback = character.Profile.Description.Contains("补充", StringComparison.OrdinalIgnoreCase) ||
                             character.Profile.Description.Contains("根据剧情简介塑造", StringComparison.OrdinalIgnoreCase),
            }),
        };
        File.WriteAllText(
            Path.Combine(characterDirectory, CharacterManifestFileName),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static string NormalizeCharacterName(string name) =>
        string.Concat((name ?? string.Empty).Where(char.IsLetterOrDigit));

    private static int CharacterPriority(CharacterProfile profile)
    {
        var text = profile.Name + " " + profile.Description;
        if (text.Contains("男主", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("女主", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("主角", StringComparison.OrdinalIgnoreCase)) return 0;
        if (text.Contains("核心反派", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("反派", StringComparison.OrdinalIgnoreCase)) return 1;
        if (text.Contains("关键配角", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("主要配角", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("关键角色", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static int CharacterFilePriority(string name) => CharacterPriority(new CharacterProfile(name, name));

    private static string DescribeCharacterRole(CharacterProfile profile) => CharacterPriority(profile) switch
    {
        0 => "主角",
        1 => "反派",
        2 => "关键配角",
        _ => "主要角色",
    };

    internal static IReadOnlyList<string> FindEpisodeCharacterSources(
        ProjectWorkspaceContext context,
        string packageRoot)
    {
        var preferredDirectoryNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [TikTokAiDramaProductionMaterialService.CharacterDirectoryName] = 0,
            ["02_角色素材"] = 1,
            ["角色定妆"] = 2,
            ["角色素材"] = 3,
            ["角色设定"] = 4,
            ["角色"] = 5,
        };

        var roots = new[] { context.WorkflowProjectDir, context.SourceProjectDir }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var extractedFrames = roots
            .SelectMany(root => Directory.EnumerateDirectories(
                root,
                TikTokAiGenerationScreenshotService.RetainedFramesDirectoryName,
                SearchOption.AllDirectories))
            .Where(directory => !directory.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(IsImage)
            .Select(path => new CharacterSourcePath(path, IsExtractedFrame: true));
        var curatedCharacters = roots
            .SelectMany(root => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            .Where(directory => !directory.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            .Select(directory => new
            {
                Directory = directory,
                Rank = preferredDirectoryNames.TryGetValue(Path.GetFileName(directory), out var rank)
                    ? rank
                    : int.MaxValue,
            })
            .Where(item => item.Rank != int.MaxValue)
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Directory, StringComparer.OrdinalIgnoreCase)
            .SelectMany(item => Directory.EnumerateFiles(item.Directory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsImage)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            .Where(path => !string.Equals(
                Path.GetFileName(path), CharacterWorkbenchFileName, StringComparison.OrdinalIgnoreCase))
            .Select(path => new CharacterSourcePath(path, IsExtractedFrame: false));

        return extractedFrames
            .Concat(curatedCharacters)
            .GroupBy(candidate => Path.GetFullPath(candidate.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.IsExtractedFrame).First())
            .Select(AnalyzeCharacterSource)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(CharacterSourceCategory)
            .ThenByDescending(candidate => candidate.QualityScore)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .ToArray();
    }

    private static CharacterSourceCandidate? AnalyzeCharacterSource(CharacterSourcePath source)
    {
        try
        {
            using var image = Image.Load<Rgba32>(source.Path);
            var faceCount = TikTokAiGenerationScreenshotService.CountLikelyFaces(image);
            var visibility = TikTokAiGenerationScreenshotService.ScoreFaceVisibility(image);
            var resolutionBonus = Math.Clamp(Math.Min(image.Width, image.Height) / 720d, 0d, 1d) * 0.25;
            return new CharacterSourceCandidate(
                source.Path,
                source.IsExtractedFrame,
                faceCount,
                visibility + resolutionBonus);
        }
        catch
        {
            return null;
        }
    }

    private static int CharacterSourceCategory(CharacterSourceCandidate candidate) =>
        (candidate.LikelyFaceCount, candidate.IsExtractedFrame) switch
        {
            (1, true) => 0,
            (1, false) => 1,
            (0, true) => 2,
            (0, false) => 3,
            (_, true) => 4,
            _ => 5,
        };

    private static async Task<IReadOnlyList<GeneratedCharacter>> ImportEpisodeCharacterImagesAsync(
        string characterDirectory,
        IReadOnlyList<CharacterProfile> profiles,
        IReadOnlyList<string> sources,
        ClientSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        var count = Math.Min(profiles.Count, sources.Count);
        if (count < MinCharacterCount)
            return [];

        var staged = new List<(
            CharacterProfile Profile,
            string Temporary,
            string Output,
            bool GeneratedWithReference)>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var output = Path.Combine(
                    characterDirectory,
                    $"{SanitizeFileName(profiles[index].Name)}.png");
                var temporary = Path.Combine(characterDirectory, $".episode-character-{Guid.NewGuid():N}.png");
                byte[] bytes;
                var generatedWithReference = false;
                if (IsImageModelConfigured(settings))
                {
                    try
                    {
                        log?.Invoke(
                            $"角色图片 {index + 1}/{count}：已优选单人清晰参考帧 {Path.GetFileName(sources[index])}，" +
                            $"以剧集画面中的 {profiles[index].Name} 为形象参考生成全身定妆照。");
                        bytes = await GenerateReferenceImageWithRetryAsync(
                            BuildReferenceCharacterPrompt(profiles[index]),
                            sources[index],
                            settings,
                            profiles[index].Name,
                            ct).ConfigureAwait(false);
                        generatedWithReference = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log?.Invoke(
                            $"角色“{profiles[index].Name}”参考图生图失败，改用剧集原画面兜底，确保不生成陌生演员：{ex.Message}");
                        bytes = await File.ReadAllBytesAsync(sources[index], ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    log?.Invoke(
                        $"角色“{profiles[index].Name}”未配置图片模型，使用剧集原画面兜底；配置图片模型后可生成同人物全身定妆照。");
                    bytes = await File.ReadAllBytesAsync(sources[index], ct).ConfigureAwait(false);
                }
                await SaveNormalizedPngAsync(bytes, temporary, 768, 1024, ct).ConfigureAwait(false);
                staged.Add((profiles[index], temporary, output, generatedWithReference));
            }

            foreach (var oldImage in Directory.EnumerateFiles(characterDirectory).Where(IsImage)
                         .Where(path => staged.All(item => !string.Equals(
                             item.Temporary, path, StringComparison.OrdinalIgnoreCase))))
                File.Delete(oldImage);

            foreach (var item in staged)
                File.Move(item.Temporary, item.Output, overwrite: true);

            return staged.Select(item => new GeneratedCharacter(
                new CharacterProfile(
                    item.Profile.Name,
                    item.Profile.Description + "（形象取自剧集真实角色画面）"),
                item.Output,
                item.GeneratedWithReference
                    ? "episode-reference-image-model"
                    : "episode-character-source-fallback")).ToArray();
        }
        finally
        {
            foreach (var item in staged)
                try { if (File.Exists(item.Temporary)) File.Delete(item.Temporary); } catch { }
        }
    }

    internal static string BuildCharacterPrompt(CharacterProfile profile) =>
        "Use case: photorealistic-natural\n" +
        "Asset type: 中国短剧角色真人定妆参考图\n" +
        $"Subject: {profile.Name}。{profile.Description}\n" +
        "Style/medium: 真实真人影视剧演员定妆摄影，电影级写实照片，不是插画，不是动漫，不是3D\n" +
        "Composition/framing: 竖版3:4，单人，正面全身或四分之三全身，人物居中，完整头部和手脚\n" +
        "Scene/backdrop: 干净的浅灰色摄影棚无缝背景\n" +
        "Lighting/mood: 柔和专业棚拍光线，自然真实皮肤、头发和服装纹理\n" +
        "Constraints: 虚构中国成年人；严格遵循角色年龄、身份、气质和服装；画面中仅一人；无文字、无Logo、无水印\n" +
        "Avoid: 现实明星或公众人物脸、儿童、卡通、动漫、插画、塑料皮肤、过度磨皮、多余手指、多人、拼贴、字幕";

    internal static string BuildReferenceCharacterPrompt(CharacterProfile profile) =>
        "任务类型：严格参考图人物一致性的真人角色定妆照编辑。\n" +
        $"角色：{profile.Name}。{profile.Description}\n" +
        "参考图是人物身份的唯一依据。必须保留参考图中主要人物完全相同的脸部身份、五官结构、脸型、年龄、肤色、发型和整体气质，" +
        "必须让观众一眼认出是剧集里的同一个人；不得换脸、不得重新选角、不得生成相似但不同的人。\n" +
        "将该人物自然补全为正面全身或四分之三全身单人定妆照，保持剧中人物所属时代、身份和核心服装特征，" +
        "姿态自然，完整显示头部、双手和双脚，人物居中。\n" +
        "竖版3:4，干净浅灰色摄影棚无缝背景，柔和专业棚拍光，真实影视摄影，自然皮肤、头发和服装纹理。\n" +
        "画面仅一人，无文字、无Logo、无水印；不是动漫、插画或3D。最高优先级：人物身份与参考图严格一致。";

    internal static CharacterProfile[] AddFallbackCharacters(
        IReadOnlyList<CharacterProfile> existing,
        string intro)
    {
        var result = existing.ToList();
        foreach (Match match in IntroCharacterListRegex().Matches(intro ?? string.Empty))
        {
            var listedNames = match.Groups["names"].Value
                .Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in listedNames.Where(name => !IsNonCharacterName(name)))
            {
                if (result.Any(item => item.Name == name)) continue;
                result.Add(new CharacterProfile(
                    name,
                    $"剧情简介中明确出现的主要角色。时代、身份、服装与气质必须符合以下剧情：{intro}"));
                if (result.Count >= 6) return result.ToArray();
            }
        }

        var names = new[] { "女主", "男主", "主要配角" };
        foreach (var name in names)
        {
            if (result.Any(item => item.Name == name)) continue;
            result.Add(new CharacterProfile(name, $"现代都市中国短剧主要角色，根据剧情简介塑造：{intro}"));
            if (result.Count >= 3) break;
        }
        return result.ToArray();
    }

    private static async Task<byte[]> GenerateImageWithRetryAsync(
        string prompt,
        ClientSettings settings,
        string roleName,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await GenerateImageAsync(prompt, settings, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException($"角色“{roleName}”图片模型生成失败：{last?.Message}", last);
    }

    private static async Task<byte[]> GenerateReferenceImageWithRetryAsync(
        string prompt,
        string referenceImagePath,
        ClientSettings settings,
        string roleName,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await GenerateReferenceImageAsync(
                    prompt, referenceImagePath, settings, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException($"角色“{roleName}”参考图生图失败：{last?.Message}", last);
    }

    private static async Task<byte[]> GenerateReferenceImageAsync(
        string prompt,
        string referenceImagePath,
        ClientSettings settings,
        CancellationToken ct)
    {
        var provider = PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider);
        var endpoint = provider == "ofox_image2"
            ? FirstNonEmpty(settings.OfoxImage2Endpoint, ClientSettingsDefaults.OfoxImage2Endpoint)
            : FirstNonEmpty(settings.ImageModelEndpoint, ClientSettingsDefaults.ImageModelEndpoint);
        var model = ResolveModelId(settings);
        var apiKey = provider == "ofox_image2" ? settings.OfoxImage2ApiKey : settings.ImageModelApiKey;
        var mediaType = ResolveImageMediaType(referenceImagePath);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoint.TrimEnd('/') + (provider == "ofox_image2" ? "/images/edits" : "/images/generations"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (provider == "ofox_image2")
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
            form.Add(new StringContent(NormalizeOfoxPortraitSize(settings.OfoxImage2Size)), "size");
            form.Add(new StringContent(FirstNonEmpty(settings.OfoxImage2Quality, "medium")), "quality");
            var imageContent = new ByteArrayContent(await File.ReadAllBytesAsync(referenceImagePath, ct)
                .ConfigureAwait(false));
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            form.Add(imageContent, "image", Path.GetFileName(referenceImagePath));
            request.Content = form;
        }
        else
        {
            var imageBytes = await File.ReadAllBytesAsync(referenceImagePath, ct).ConfigureAwait(false);
            var payload = BuildDoubaoReferenceImagePayload(
                model,
                prompt,
                $"data:{mediaType};base64,{Convert.ToBase64String(imageBytes)}",
                PosterImageConfigHelper.DoubaoImageSizeForRatio(settings.DoubaoImageResolution, "3:4"));
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"角色参考图生图失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}；" +
                $"请确认当前图片模型支持图生图。响应：{Truncate(body, 1200)}");
        return await ReadGeneratedImageBytesAsync(body, ct).ConfigureAwait(false);
    }

    internal static Dictionary<string, object?> BuildDoubaoReferenceImagePayload(
        string model,
        string prompt,
        string referenceDataUri,
        string size) => new()
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["image"] = new[] { referenceDataUri },
            ["size"] = size,
            ["response_format"] = "b64_json",
            ["watermark"] = false,
            ["sequential_image_generation"] = "disabled",
        };

    private static async Task<byte[]> GenerateImageAsync(
        string prompt,
        ClientSettings settings,
        CancellationToken ct)
    {
        var provider = PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider);
        var endpoint = provider == "ofox_image2"
            ? FirstNonEmpty(settings.OfoxImage2Endpoint, ClientSettingsDefaults.OfoxImage2Endpoint)
            : FirstNonEmpty(settings.ImageModelEndpoint, ClientSettingsDefaults.ImageModelEndpoint);
        var model = ResolveModelId(settings);
        var apiKey = provider == "ofox_image2" ? settings.OfoxImage2ApiKey : settings.ImageModelApiKey;
        var url = endpoint.TrimEnd('/') + "/images/generations";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["size"] = provider == "ofox_image2"
                ? NormalizeOfoxPortraitSize(settings.OfoxImage2Size)
                : PosterImageConfigHelper.DoubaoImageSizeForRatio(settings.DoubaoImageResolution, "3:4"),
        };
        if (provider == "ofox_image2")
            payload["quality"] = FirstNonEmpty(settings.OfoxImage2Quality, "medium");
        else
        {
            payload["response_format"] = "b64_json";
            payload["watermark"] = false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"角色真人定妆图生成失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}；" +
                $"请检查系统设置中的图片模型、Endpoint 和 API Key。响应：{Truncate(body, 1200)}");

        return await ReadGeneratedImageBytesAsync(body, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadGeneratedImageBytesAsync(string body, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new InvalidOperationException("图片模型成功返回，但响应中没有 data 图片数据。");
        var first = data[0];
        if (first.TryGetProperty("b64_json", out var b64) && !string.IsNullOrWhiteSpace(b64.GetString()))
            return Convert.FromBase64String(b64.GetString()!);
        if (first.TryGetProperty("url", out var imageUrl) && Uri.TryCreate(imageUrl.GetString(), UriKind.Absolute, out var uri))
            return await Http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        throw new InvalidOperationException("图片模型响应中没有可解析的 b64_json 或 url。");
    }

    private static async Task SaveNormalizedPngAsync(
        byte[] bytes,
        string output,
        int width,
        int height,
        CancellationToken ct)
    {
        using var image = Image.Load<Rgba32>(bytes);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Crop,
            Position = AnchorPositionMode.Center,
        }));
        await image.SaveAsPngAsync(output, ct).ConfigureAwait(false);
    }

    private static void DrawNode(Image<Rgba32> canvas, string path, int x, int y, int width, int height)
    {
        try
        {
            using var image = Image.Load<Rgba32>(path);
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));
            canvas.Mutate(ctx =>
            {
                ctx.Fill(Color.ParseHex("24272c"), new RectangleF(x - 5, y - 5, width + 10, height + 10));
                ctx.DrawImage(image, new Point(x, y), 1f);
            });
        }
        catch
        {
            canvas.Mutate(ctx => ctx.Fill(Color.ParseHex("24272c"), new RectangleF(x, y, width, height)));
        }
    }

    private static void RenderSceneDesignSheet(
        string output,
        string title,
        string heading,
        IReadOnlyList<string> sources)
    {
        using var canvas = new Image<Rgba32>(2435, 1254, Color.White);
        var family = ResolveFont();
        var titleFont = family.CreateFont(25, FontStyle.Bold);
        var bodyFont = family.CreateFont(18);
        canvas.Mutate(ctx =>
        {
            ctx.DrawText(title, titleFont, Color.ParseHex("16181c"), new PointF(45, 30));
            ctx.DrawText(heading, bodyFont, Color.ParseHex("343941"), new PointF(45, 72));
        });
        var usable = sources.Where(File.Exists).Take(4).ToArray();
        if (usable.Length == 0)
        {
            canvas.Mutate(ctx => ctx.DrawText(
                "当前项目没有可用的真实场景参考图；角色图片仍由图片模型生成。",
                bodyFont, Color.ParseHex("69717d"), new PointF(70, 180)));
            canvas.SaveAsPng(output);
            return;
        }

        for (var index = 0; index < usable.Length; index++)
            DrawNode(canvas, usable[index], 45, 125 + index * 260, 500, 230);
        DrawNode(canvas, usable[0], 670, 125, 1710, 960);
        canvas.Mutate(ctx => ctx.DrawText(
            "场景参考来自项目真实画面，用于角色、光线与空间一致性设计。",
            bodyFont, Color.ParseHex("343941"), new PointF(670, 1120)));
        canvas.SaveAsPng(output);
    }

    private static async Task WriteProjectFilesAsync(
        string root,
        ProjectWorkspaceContext context,
        string title,
        string originalTitle,
        string intro,
        string script,
        QueueProjectItem item,
        int videoCount,
        CancellationToken ct)
    {
        var infoDir = Path.Combine(root, SanitizeFileName(title));
        Directory.CreateDirectory(infoDir);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "短剧信息.txt"),
            $"原剧名：{originalTitle}\n新剧名：{title}\n集数：{Math.Max(item.EpisodeCount, videoCount)}\n简介：{intro}\n",
            new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "简介.txt"), intro + Environment.NewLine,
            new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(infoDir, "详细简介.txt"),
            $"剧名：{title}\n\n作者：制作方\n\n类型：{item.GenreCategory}\n\n集数：{Math.Max(item.EpisodeCount, videoCount)}\n\n简介：{intro}\n\n发布时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n",
            new UTF8Encoding(false), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(root, $"{SanitizeFileName(title)} 剧本.txt"), script,
            new UTF8Encoding(false), ct).ConfigureAwait(false);

        var metadata = ReadMetadataObject(context);
        metadata["projectKey"] = title;
        metadata["sourceName"] = originalTitle;
        metadata["displayName"] = title;
        metadata["title"] = title;
        metadata["originalTitle"] = originalTitle;
        metadata["intro"] = intro;
        metadata["episodeCount"] = Math.Max(item.EpisodeCount, videoCount);
        metadata["workflowProjectDir"] = context.WorkflowProjectDir;
        metadata["sourceProjectDir"] = context.SourceProjectDir;
        await File.WriteAllTextAsync(Path.Combine(infoDir, "shortdrama-project.json"),
            metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false), ct).ConfigureAwait(false);

        var poster = FindPoster(context);
        if (poster is not null)
        {
            using var image = Image.Load<Rgba32>(poster);
            await image.SaveAsJpegAsync(Path.Combine(infoDir, "海报图片.jpg"), ct).ConfigureAwait(false);
            await image.SaveAsJpegAsync(Path.Combine(infoDir, $"{SanitizeFileName(title)}.jpg"), ct).ConfigureAwait(false);
        }
    }

    private static void LinkVideos(
        IReadOnlyList<string> videos,
        string videoDir,
        string materialDir,
        string title,
        CancellationToken ct)
    {
        for (var index = 0; index < videos.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(videos[index]);
            LinkOrCopy(videos[index], Path.Combine(videoDir, $"{SanitizeFileName(title)}-第{index + 1}集{extension}"));
            if (index < 40)
                LinkOrCopy(videos[index], Path.Combine(materialDir, $"001-{index + 1}{extension}"));
        }
    }

    private static void LinkOrCopy(string source, string target)
    {
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            if (!OperatingSystem.IsWindows() || !CreateHardLink(target, source, IntPtr.Zero))
                throw new IOException($"无法创建硬链接：{source}");
        }
        catch
        {
            if (new FileInfo(source).Length <= 64L * 1024 * 1024)
                File.Copy(source, target);
            else
                File.WriteAllText(target + ".索引.txt", source, new UTF8Encoding(false));
        }
    }

    private static string ReadProjectScript(ProjectWorkspaceContext context, string title, string intro)
    {
        var roots = new[] { context.WorkflowProjectDir, context.SourceProjectDir };
        var textFile = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path).Contains("剧本", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
        if (textFile is not null)
            return File.ReadAllText(textFile);

        var docx = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.docx", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path).Contains("剧本", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (docx is not null)
        {
            using var document = WordprocessingDocument.Open(docx, false);
            var paragraphs = document.MainDocumentPart?.Document.Body?
                .Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
                .Select(p => p.InnerText.Trim())
                .Where(text => text.Length > 0) ?? [];
            return string.Join(Environment.NewLine, paragraphs);
        }

        return $"{title}\n人物设定\n女主：现代都市短剧女主角。\n男主：现代都市短剧男主角。\n主要配角：推动剧情发展的成年配角。\n\n剧情简介：{intro}";
    }

    private static IEnumerable<string> FindSceneSources(ProjectWorkspaceContext context, string packageRoot)
    {
        return new[] { context.WorkflowProjectDir, context.SourceProjectDir }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(IsImage)
            .Where(path => !path.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase))
            .Where(path => path.Contains("场景", StringComparison.OrdinalIgnoreCase) ||
                           path.Contains("首帧", StringComparison.OrdinalIgnoreCase) ||
                           path.Contains("抽帧原图", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<string>> ExtractSceneFramesAsync(
        string root,
        IReadOnlyList<string> videos,
        Action<string>? log,
        CancellationToken ct)
    {
        var outputDir = Path.Combine(root, MaterialDirectoryName, "场景参考");
        Directory.CreateDirectory(outputDir);
        foreach (var old in Directory.EnumerateFiles(outputDir, "场景参考_*.jpg"))
            try { File.Delete(old); } catch { }
        if (videos.Count == 0) return [];

        var selected = videos.Count <= 8
            ? videos
            : Enumerable.Range(0, 8)
                .Select(index => videos[(int)Math.Round(index * (videos.Count - 1d) / 7d)])
                .ToArray();
        var ffmpeg = FfmpegLocator.ResolveFfmpeg();
        var outputs = new List<string>(selected.Count);
        foreach (var (video, index) in selected.Select((path, index) => (path, index)))
        {
            ct.ThrowIfCancellationRequested();
            var output = Path.Combine(outputDir, $"场景参考_{index + 1:D2}.jpg");
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y", "-ss", "00:00:01.500",
                         "-i", video, "-frames:v", "1", "-vf", "scale=1280:-2", "-q:v", "2", output,
                     })
                process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 1024)
                outputs.Add(output);
            else
                log?.Invoke($"场景抽帧失败：{Path.GetFileName(video)}；{Truncate(stderr, 240)}");
        }
        return outputs;
    }

    private static JsonObject ReadMetadataObject(ProjectWorkspaceContext context)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(context.WorkflowProjectDir, "shortdrama-project.json"),
                     Path.Combine(context.SourceProjectDir, "shortdrama-project.json"),
                 })
        {
            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject value)
                    return value;
            }
            catch { }
        }
        return new JsonObject();
    }

    private static string ResolveIntro(QueueProjectItem item, ProjectWorkspaceContext context)
    {
        if (!string.IsNullOrWhiteSpace(item.Description)) return item.Description.Trim();
        var metadata = ReadMetadataObject(context);
        return metadata["intro"]?.GetValue<string>()?.Trim() ?? "";
    }

    private static string? FindPoster(ProjectWorkspaceContext context) =>
        new[] { context.WorkflowProjectDir, context.SourceProjectDir }
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            .FirstOrDefault(path => IsImage(path) &&
                                    (Path.GetFileName(path).Contains("海报", StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetFileName(path).Contains("poster", StringComparison.OrdinalIgnoreCase)));

    private static void EnsureImageModelConfigured(ClientSettings settings)
    {
        if (!IsImageModelConfigured(settings))
            throw new InvalidOperationException(
                "生成参考格式原始文件信息需要图片模型生成真人角色图；请先在系统设置中完整配置豆包或 Ofox Image2。不会使用视频抽帧冒充角色模型图。");
    }

    private static bool IsImageModelConfigured(ClientSettings settings)
    {
        var provider = PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider);
        return provider == "ofox_image2"
            ? !string.IsNullOrWhiteSpace(settings.OfoxImage2Endpoint) &&
              !string.IsNullOrWhiteSpace(settings.OfoxImage2ApiKey) &&
              !string.IsNullOrWhiteSpace(settings.OfoxImage2ModelId)
            : !string.IsNullOrWhiteSpace(settings.ImageModelEndpoint) &&
              !string.IsNullOrWhiteSpace(settings.ImageModelApiKey) &&
              !string.IsNullOrWhiteSpace(settings.ImageModelId);
    }

    private static string ResolveImageMediaType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };

    private static string ResolveModelId(ClientSettings settings) =>
        PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider) == "ofox_image2"
            ? FirstNonEmpty(settings.OfoxImage2ModelId, ClientSettingsDefaults.OfoxImage2ModelId)
            : settings.ImageModelId.Trim();

    private static string NormalizeOfoxPortraitSize(string? value)
    {
        var normalized = (value ?? "auto").Trim().ToLowerInvariant();
        return normalized is "1024x1536" or "auto" ? normalized : "1024x1536";
    }

    private static string ComputeSourceFingerprint(
        string title,
        string intro,
        string script,
        ClientSettings settings,
        IReadOnlyList<string> episodeCharacterSources)
    {
        var characterSourceFingerprint = string.Join('|', episodeCharacterSources.Select(path =>
        {
            using var stream = File.OpenRead(path);
            return $"{Path.GetFullPath(path)}:{Convert.ToHexString(SHA256.HashData(stream))}";
        }));
        var value = string.Join('\n',
            Version,
            title,
            intro,
            script,
            PosterImageConfigHelper.NormalizeImageProvider(settings.ImageProvider),
            ResolveModelId(settings),
            characterSourceFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool HasMatchingFingerprint(string workflowProjectDirectory, string expected)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(GetStatePath(workflowProjectDirectory)));
            return document.RootElement.TryGetProperty("sourceFingerprint", out var value) &&
                   string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static FontFamily ResolveFont()
    {
        foreach (var name in new[] { "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "Arial" })
            if (SystemFonts.TryGet(name, out var family)) return family;
        return SystemFonts.Collection.Families.First();
    }

    private static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    private static bool IsNonCharacterName(string value) =>
        value.Length is < 2 or > 12 ||
        value is "人物" or "场景" or "时间" or "地点" or "旁白" or "音效" or "音乐" or "BGM" or "OS" ||
        value.Contains("简介", StringComparison.Ordinal) || value.Contains("类型", StringComparison.Ordinal);

    private static bool IsGenericCharacterName(string value) =>
        value is "女主" or "男主" or "主要配角" or "配角" or "主角";

    private static string SanitizeFileName(string value) =>
        string.Concat(FirstNonEmpty(value, "未命名").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { }
    }

    private static void ResetPackageRoot(string root, bool preserveCharactersAndRoleVector)
    {
        if (!preserveCharactersAndRoleVector)
        {
            TryDeleteDirectory(root);
            return;
        }

        if (!Directory.Exists(root)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, CharacterDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, CharacterWorkbenchFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, TikTokRoleVectorService.BackupFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            catch (Exception ex)
            {
                throw new IOException($"无法清理旧的参考格式素材：{entry}", ex);
            }
        }
    }

    private static void TrySetHidden(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch { }
    }

    internal static async Task WriteHiddenStateFileAsync(
        string path,
        string content,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                content,
                new UTF8Encoding(false),
                ct).ConfigureAwait(false);
            if (File.Exists(path) && OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(path);
                attributes &= ~(FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System);
                File.SetAttributes(path, attributes);
            }
            File.Move(temporary, path, overwrite: true);
            TrySetHidden(path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    [GeneratedRegex(@"^(?<name>[^\s（(：:]{2,12})\s*[（(][^）)]{0,80}[）)]\s*[：:]?.+$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterDefinitionRegex();

    [GeneratedRegex(@"^(?<name>[^\s：:]{2,12})\s*[：:]\s*.+$", RegexOptions.CultureInvariant)]
    private static partial Regex DialogueRegex();

    [GeneratedRegex(@"(?:(?<=成员)|(?<=包括)|(?<=人物)|(?<=角色)|(?<=，)|(?<=。)|^)(?<names>[\p{IsCJKUnifiedIdeographs}]{2,6}(?:、[\p{IsCJKUnifiedIdeographs}]{2,6}){1,5})(?:三人|四人|五人|等人|一行人)", RegexOptions.CultureInvariant)]
    private static partial Regex IntroCharacterListRegex();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    internal sealed record CharacterProfile(string Name, string Description);
    private sealed record CharacterSourcePath(string Path, bool IsExtractedFrame);
    private sealed record CharacterSourceCandidate(
        string Path,
        bool IsExtractedFrame,
        int LikelyFaceCount,
        double QualityScore);
    private sealed record GeneratedCharacter(
        CharacterProfile Profile,
        string Path,
        string Source = "image-model");
}
