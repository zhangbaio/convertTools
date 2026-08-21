using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace TikTokPublisher.Core.Services;

public enum ManualRoleVectorMode
{
    Auto,
    ReferencesOnly,
    Paired,
}

public sealed record ManualRoleCharacter(
    int Order,
    string Name,
    string CharacterPath,
    string ReferencePath,
    string ReferenceSha256 = "");

public sealed record ManualRoleVectorConfiguration(
    ManualRoleVectorMode Mode,
    bool Locked,
    IReadOnlyList<ManualRoleCharacter> Characters,
    string Fingerprint);

/// <summary>
/// Owns user-selected role material. Sources are copied below the evidence directory so a queue
/// rerun never depends on a temporary screenshot path and automatic package cleanup cannot erase them.
/// </summary>
public static class ManualRoleVectorMaterialService
{
    public const string DirectoryName = "手动角色素材";
    public const string CharacterDirectoryName = "角色定妆图";
    public const string GeneratedCharacterDirectoryName = "自动角色定妆图";
    public const string ReferenceDirectoryName = "人物参考图";
    public const string ConfigurationFileName = "角色配置.json";
    private const string ConfigurationVersion = "v1";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    public static string GetRoot(string workflowProjectDirectory) =>
        Path.Combine(
            TikTokSourceFileInfoScreenshotService.GetEvidenceDirectory(workflowProjectDirectory),
            DirectoryName);

    public static string GetConfigurationPath(string workflowProjectDirectory) =>
        Path.Combine(GetRoot(workflowProjectDirectory), ConfigurationFileName);

    public static ManualRoleVectorConfiguration Load(string workflowProjectDirectory)
    {
        var root = GetRoot(workflowProjectDirectory);
        var configPath = GetConfigurationPath(workflowProjectDirectory);
        if (!File.Exists(configPath))
            return AutoConfiguration();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var element = document.RootElement;
            var mode = ParseMode(element.TryGetProperty("mode", out var modeValue) ? modeValue.GetString() : null);
            var locked = !element.TryGetProperty("locked", out var lockedValue) || lockedValue.GetBoolean();
            var characters = new List<ManualRoleCharacter>();
            if (element.TryGetProperty("characters", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    var order = entry.TryGetProperty("order", out var orderValue)
                        ? orderValue.GetInt32()
                        : characters.Count + 1;
                    var name = entry.TryGetProperty("name", out var nameValue)
                        ? nameValue.GetString() ?? $"角色{order}"
                        : $"角色{order}";
                    var character = ResolveManagedPath(root,
                        entry.TryGetProperty("characterPath", out var characterValue) ? characterValue.GetString() : null);
                    var reference = ResolveManagedPath(root,
                        entry.TryGetProperty("referencePath", out var referenceValue) ? referenceValue.GetString() : null);
                    var referenceSha256 = entry.TryGetProperty("referenceSha256", out var referenceHashValue)
                        ? referenceHashValue.GetString() ?? string.Empty
                        : string.Empty;
                    characters.Add(new ManualRoleCharacter(order, name, character, reference, referenceSha256));
                }
            }

            var fingerprint = element.TryGetProperty("fingerprint", out var fingerprintValue)
                ? fingerprintValue.GetString() ?? string.Empty
                : string.Empty;
            fingerprint = TryComputeCurrentFingerprint(mode, characters, fingerprint);
            return new ManualRoleVectorConfiguration(
                mode,
                locked,
                characters.OrderBy(character => character.Order).ToArray(),
                fingerprint);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"手动角色配置文件损坏：{configPath}", ex);
        }
    }

    public static ManualRoleVectorConfiguration SavePaired(
        string workflowProjectDirectory,
        IReadOnlyList<ManualRoleCharacter> sources,
        bool locked = true)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < TikTokReferenceSourcePackageService.MinCharacterCount or
            > TikTokReferenceSourcePackageService.MaxCharacterCount)
        {
            throw new InvalidOperationException("手动角色素材必须包含 3–6 个人物。");
        }

        var invalid = sources.FirstOrDefault(source =>
            string.IsNullOrWhiteSpace(source.Name) ||
            !IsReadableImage(source.CharacterPath) ||
            !IsReadableImage(source.ReferencePath));
        if (invalid is not null)
            throw new InvalidOperationException($"角色“{invalid.Name}”缺少有效的定妆图或人物参考图。");

        var root = GetRoot(workflowProjectDirectory);
        var characterDirectory = Path.Combine(root, CharacterDirectoryName);
        var referenceDirectory = Path.Combine(root, ReferenceDirectoryName);
        Directory.CreateDirectory(characterDirectory);
        Directory.CreateDirectory(referenceDirectory);

        var managed = new List<ManualRoleCharacter>();
        foreach (var (source, index) in sources.Select((value, index) => (value, index)))
        {
            var order = index + 1;
            var safeName = SanitizeFileName(source.Name);
            var characterPath = Path.Combine(characterDirectory, $"{order:00}_{safeName}.png");
            var referencePath = Path.Combine(referenceDirectory, $"{order:00}_{safeName}_参考.png");
            SaveNormalizedPng(source.CharacterPath, characterPath, 768, 1024, contain: true);
            SaveNormalizedPng(source.ReferencePath, referencePath, 720, 1280, contain: false);
            managed.Add(new ManualRoleCharacter(order, source.Name.Trim(), characterPath, referencePath));
        }

        DeleteUnlistedImages(characterDirectory, managed.Select(character => character.CharacterPath));
        DeleteUnlistedImages(referenceDirectory, managed.Select(character => character.ReferencePath));
        var fingerprint = ComputeFingerprint(ManualRoleVectorMode.Paired, managed);
        WriteConfiguration(workflowProjectDirectory, ManualRoleVectorMode.Paired, locked, managed, fingerprint);
        return Load(workflowProjectDirectory);
    }

    public static ManualRoleVectorConfiguration SaveReferences(
        string workflowProjectDirectory,
        IReadOnlyList<ManualRoleCharacter> sources,
        bool locked = true)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < TikTokReferenceSourcePackageService.MinCharacterCount or
            > TikTokReferenceSourcePackageService.MaxCharacterCount)
        {
            throw new InvalidOperationException("手动人物参考图必须包含 3–6 个不同人物。");
        }

        var invalid = sources.FirstOrDefault(source =>
            string.IsNullOrWhiteSpace(source.Name) || !IsReadableImage(source.ReferencePath));
        if (invalid is not null)
            throw new InvalidOperationException($"角色“{invalid.Name}”缺少有效的人物参考图。");
        var duplicateReference = sources
            .GroupBy(source => ComputeSha256(source.ReferencePath), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateReference is not null)
            throw new InvalidOperationException("检测到重复的人物参考图；每个角色必须指定不同人物的图片。");

        var root = GetRoot(workflowProjectDirectory);
        var referenceDirectory = Path.Combine(root, ReferenceDirectoryName);
        var generatedDirectory = Path.Combine(root, GeneratedCharacterDirectoryName);
        Directory.CreateDirectory(referenceDirectory);
        Directory.CreateDirectory(generatedDirectory);
        var previous = Load(workflowProjectDirectory);
        var managed = new List<ManualRoleCharacter>();
        foreach (var (source, index) in sources.Select((value, index) => (value, index)))
        {
            var order = index + 1;
            var safeName = SanitizeFileName(source.Name);
            var referencePath = Path.Combine(referenceDirectory, $"{order:00}_{safeName}_参考.png");
            SaveNormalizedPng(source.ReferencePath, referencePath, 720, 1280, contain: false);
            var referenceHash = ComputeSha256(referencePath);
            var generatedPath = Path.Combine(generatedDirectory, $"{order:00}_{safeName}.png");
            var old = previous.Characters.FirstOrDefault(character => character.Order == order);
            if (old is not null &&
                string.Equals(old.ReferenceSha256, referenceHash, StringComparison.OrdinalIgnoreCase) &&
                IsReadableImage(old.CharacterPath) &&
                !string.Equals(old.CharacterPath, generatedPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(old.CharacterPath, generatedPath, overwrite: true);
                var oldState = GetGeneratedStatePath(old.CharacterPath);
                if (File.Exists(oldState)) File.Copy(oldState, GetGeneratedStatePath(generatedPath), overwrite: true);
            }
            if (!IsGeneratedCharacterCurrent(generatedPath, referenceHash))
            {
                TryDelete(generatedPath);
                TryDelete(GetGeneratedStatePath(generatedPath));
            }
            managed.Add(new ManualRoleCharacter(order, source.Name.Trim(), generatedPath, referencePath, referenceHash));
        }

        DeleteUnlistedImages(referenceDirectory, managed.Select(character => character.ReferencePath));
        DeleteUnlistedImages(generatedDirectory, managed.Select(character => character.CharacterPath));
        DeleteUnlistedStateFiles(generatedDirectory, managed.Select(character => GetGeneratedStatePath(character.CharacterPath)));
        var fingerprint = ComputeFingerprint(ManualRoleVectorMode.ReferencesOnly, managed);
        WriteConfiguration(
            workflowProjectDirectory,
            ManualRoleVectorMode.ReferencesOnly,
            locked,
            managed,
            fingerprint);
        return Load(workflowProjectDirectory);
    }

    public static void UseAutomaticMode(string workflowProjectDirectory)
    {
        var root = GetRoot(workflowProjectDirectory);
        Directory.CreateDirectory(root);
        WriteConfiguration(workflowProjectDirectory, ManualRoleVectorMode.Auto, false, [], string.Empty);
    }

    internal static IReadOnlyList<string> MaterializePairedCharacters(string workflowProjectDirectory)
    {
        var configuration = Load(workflowProjectDirectory);
        ValidatePaired(configuration);
        var packageRoot = TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory);
        var characterDirectory = Path.Combine(packageRoot, TikTokReferenceSourcePackageService.CharacterDirectoryName);
        Directory.CreateDirectory(characterDirectory);
        var outputCharacters = new List<string>();
        foreach (var character in configuration.Characters.OrderBy(value => value.Order))
        {
            var output = Path.Combine(characterDirectory,
                $"{character.Order:00}_{SanitizeFileName(character.Name)}.png");
            File.Copy(character.CharacterPath, output, overwrite: true);
            outputCharacters.Add(output);
        }

        DeleteUnlistedImages(characterDirectory, outputCharacters);
        var manifest = new
        {
            version = "v4-manual-character-pairs",
            sourceMode = "manual-paired",
            locked = configuration.Locked,
            configuredCount = configuration.Characters.Count,
            candidateCount = configuration.Characters.Count,
            selectedCount = configuration.Characters.Count,
            fallbackToMinimum = false,
            fallbackReason = string.Empty,
            characterCount = configuration.Characters.Count,
            characters = configuration.Characters.OrderBy(value => value.Order).Select((character, index) => new
            {
                order = index + 1,
                name = character.Name,
                roleType = "人工指定",
                importance = 100 - index,
                file = Path.GetFileName(outputCharacters[index]),
                referencePath = Path.GetFullPath(character.ReferencePath),
                source = "manual",
                isFallback = false,
            }),
        };
        File.WriteAllText(
            Path.Combine(characterDirectory, TikTokReferenceSourcePackageService.CharacterManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        return outputCharacters;
    }

    internal static IReadOnlyList<string> MaterializeReferenceGeneratedCharacters(string workflowProjectDirectory)
    {
        var configuration = Load(workflowProjectDirectory);
        ValidateReferences(configuration, requireGeneratedCharacters: true);
        return MaterializeCharacters(workflowProjectDirectory, configuration, "manual-references");
    }

    internal static string GetGeneratedStatePath(string generatedCharacterPath) =>
        generatedCharacterPath + ".reference.sha256";

    internal static bool IsGeneratedCharacterCurrent(string generatedCharacterPath, string referenceSha256)
    {
        if (!IsReadableImage(generatedCharacterPath) || string.IsNullOrWhiteSpace(referenceSha256)) return false;
        try
        {
            return File.Exists(GetGeneratedStatePath(generatedCharacterPath)) &&
                   string.Equals(
                       File.ReadAllText(GetGeneratedStatePath(generatedCharacterPath)).Trim(),
                       referenceSha256,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void MarkGeneratedCharacterCurrent(string generatedCharacterPath, string referenceSha256) =>
        File.WriteAllText(GetGeneratedStatePath(generatedCharacterPath), referenceSha256, new UTF8Encoding(false));

    internal static void ValidateReferences(
        ManualRoleVectorConfiguration configuration,
        bool requireGeneratedCharacters = false)
    {
        if (configuration.Mode != ManualRoleVectorMode.ReferencesOnly ||
            configuration.Characters.Count is < TikTokReferenceSourcePackageService.MinCharacterCount or
            > TikTokReferenceSourcePackageService.MaxCharacterCount)
        {
            throw new InvalidOperationException("手动人物参考图配置无效，请重新选择 3–6 个不同人物。");
        }
        foreach (var character in configuration.Characters)
        {
            if (!IsReadableImage(character.ReferencePath))
                throw new InvalidOperationException($"角色“{character.Name}”的人物参考图已丢失，请重新选择。");
            if (requireGeneratedCharacters &&
                !IsGeneratedCharacterCurrent(character.CharacterPath, ComputeSha256(character.ReferencePath)))
            {
                throw new InvalidOperationException($"角色“{character.Name}”的自动定妆图尚未生成或已失效。");
            }
        }
    }

    internal static void ValidatePaired(ManualRoleVectorConfiguration configuration)
    {
        if (configuration.Mode != ManualRoleVectorMode.Paired ||
            configuration.Characters.Count is < TikTokReferenceSourcePackageService.MinCharacterCount or
            > TikTokReferenceSourcePackageService.MaxCharacterCount)
        {
            throw new InvalidOperationException("手动角色配对配置无效，请重新选择 3–6 个人物。");
        }

        foreach (var character in configuration.Characters)
        {
            if (!IsReadableImage(character.CharacterPath) || !IsReadableImage(character.ReferencePath))
                throw new InvalidOperationException($"角色“{character.Name}”的人工素材已丢失，请重新选择。");
        }
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ManualRoleVectorConfiguration AutoConfiguration() =>
        new(ManualRoleVectorMode.Auto, false, [], string.Empty);

    private static ManualRoleVectorMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "references" or "manual-references" => ManualRoleVectorMode.ReferencesOnly,
        "paired" or "manual-paired" => ManualRoleVectorMode.Paired,
        _ => ManualRoleVectorMode.Auto,
    };

    private static string ModeName(ManualRoleVectorMode mode) => mode switch
    {
        ManualRoleVectorMode.ReferencesOnly => "manual-references",
        ManualRoleVectorMode.Paired => "manual-paired",
        _ => "auto",
    };

    private static void WriteConfiguration(
        string workflowProjectDirectory,
        ManualRoleVectorMode mode,
        bool locked,
        IReadOnlyList<ManualRoleCharacter> characters,
        string fingerprint)
    {
        var root = GetRoot(workflowProjectDirectory);
        Directory.CreateDirectory(root);
        var payload = new
        {
            version = ConfigurationVersion,
            mode = ModeName(mode),
            locked,
            fingerprint,
            characters = characters.Select(character => new
            {
                order = character.Order,
                name = character.Name,
                characterPath = ToRelativePath(root, character.CharacterPath),
                referencePath = ToRelativePath(root, character.ReferencePath),
                referenceSha256 = character.ReferenceSha256,
            }),
            savedAt = DateTimeOffset.Now,
        };
        File.WriteAllText(
            GetConfigurationPath(workflowProjectDirectory),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static string ComputeFingerprint(
        ManualRoleVectorMode mode,
        IReadOnlyList<ManualRoleCharacter> characters)
    {
        var values = new List<string> { ConfigurationVersion, ModeName(mode) };
        values.AddRange(characters.OrderBy(character => character.Order).SelectMany(character => new[]
        {
            character.Order.ToString(),
            character.Name,
            mode == ManualRoleVectorMode.ReferencesOnly || !File.Exists(character.CharacterPath)
                ? string.Empty
                : ComputeSha256(character.CharacterPath),
            ComputeSha256(character.ReferencePath),
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));
    }

    private static string TryComputeCurrentFingerprint(
        ManualRoleVectorMode mode,
        IReadOnlyList<ManualRoleCharacter> characters,
        string fallback)
    {
        try
        {
            if (mode == ManualRoleVectorMode.Auto) return string.Empty;
            if (mode == ManualRoleVectorMode.ReferencesOnly &&
                characters.Count > 0 && characters.All(character => File.Exists(character.ReferencePath)))
                return ComputeFingerprint(mode, characters);
            if (mode == ManualRoleVectorMode.Paired &&
                characters.Count > 0 && characters.All(character =>
                    File.Exists(character.CharacterPath) && File.Exists(character.ReferencePath)))
                return ComputeFingerprint(mode, characters);
        }
        catch
        {
            // Validation reports the missing or unreadable file with the role name later.
        }
        return fallback;
    }

    private static bool IsReadableImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !ImageExtensions.Contains(Path.GetExtension(path))) return false;
        try { return Image.Identify(path) is not null; }
        catch { return false; }
    }

    private static void SaveNormalizedPng(string source, string destination, int width, int height, bool contain)
    {
        var fullSource = Path.GetFullPath(source);
        var fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        using var image = Image.Load(source);
        image.Mutate(context => context.AutoOrient().Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = contain ? ResizeMode.Pad : ResizeMode.Crop,
            Position = AnchorPositionMode.Center,
            PadColor = Color.White,
        }));
        var temporary = fullDestination + $".{Guid.NewGuid():N}.tmp.png";
        try
        {
            image.Save(temporary, new PngEncoder());
            if (!string.Equals(fullSource, fullDestination, StringComparison.OrdinalIgnoreCase))
                File.Move(temporary, fullDestination, overwrite: true);
            else
            {
                File.Move(temporary, fullDestination + ".replacement", overwrite: true);
                File.Move(fullDestination + ".replacement", fullDestination, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string ResolveManagedPath(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
    }

    private static string? ToRelativePath(string root, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetRelativePath(root, path);

    private static string SanitizeFileName(string value)
    {
        var result = string.Concat((value ?? string.Empty).Trim()
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(result) ? "未命名角色" : result;
    }

    private static void DeleteUnlistedImages(string directory, IEnumerable<string> keepPaths)
    {
        if (!Directory.Exists(directory)) return;
        var keep = keepPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory).Where(path => ImageExtensions.Contains(Path.GetExtension(path))))
        {
            if (!keep.Contains(Path.GetFullPath(path))) File.Delete(path);
        }
    }

    private static IReadOnlyList<string> MaterializeCharacters(
        string workflowProjectDirectory,
        ManualRoleVectorConfiguration configuration,
        string sourceMode)
    {
        var packageRoot = TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory);
        var characterDirectory = Path.Combine(packageRoot, TikTokReferenceSourcePackageService.CharacterDirectoryName);
        Directory.CreateDirectory(characterDirectory);
        var outputCharacters = new List<string>();
        foreach (var character in configuration.Characters.OrderBy(value => value.Order))
        {
            var output = Path.Combine(characterDirectory,
                $"{character.Order:00}_{SanitizeFileName(character.Name)}.png");
            File.Copy(character.CharacterPath, output, overwrite: true);
            outputCharacters.Add(output);
        }
        DeleteUnlistedImages(characterDirectory, outputCharacters);
        WriteCharacterManifest(characterDirectory, configuration, outputCharacters, sourceMode);
        return outputCharacters;
    }

    private static void WriteCharacterManifest(
        string characterDirectory,
        ManualRoleVectorConfiguration configuration,
        IReadOnlyList<string> outputCharacters,
        string sourceMode)
    {
        var payload = new
        {
            version = "v5-manual-character-references",
            sourceMode,
            locked = configuration.Locked,
            configuredCount = configuration.Characters.Count,
            candidateCount = configuration.Characters.Count,
            selectedCount = configuration.Characters.Count,
            fallbackToMinimum = false,
            fallbackReason = string.Empty,
            characterCount = configuration.Characters.Count,
            characters = configuration.Characters.OrderBy(value => value.Order).Select((character, index) => new
            {
                order = index + 1,
                name = character.Name,
                roleType = "人工指定参考人物",
                importance = 100 - index,
                file = Path.GetFileName(outputCharacters[index]),
                referencePath = Path.GetFullPath(character.ReferencePath),
                source = sourceMode,
                isFallback = false,
            }),
        };
        File.WriteAllText(
            Path.Combine(characterDirectory, TikTokReferenceSourcePackageService.CharacterManifestFileName),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static void DeleteUnlistedStateFiles(string directory, IEnumerable<string> keepPaths)
    {
        if (!Directory.Exists(directory)) return;
        var keep = keepPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.reference.sha256"))
            if (!keep.Contains(Path.GetFullPath(path))) File.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
