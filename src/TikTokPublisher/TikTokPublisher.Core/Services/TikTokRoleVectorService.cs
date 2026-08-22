using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokRoleVectorService
{
    public const string OutputFileName = TikTokReferenceSourcePackageService.CharacterWorkbenchFileName;
    public const string BackupFileName = "角色矢量图_旧版.png";
    public const string StateFileName = ".role-vector-state.json";
    public const string StateVersion = "v9-configurable-minimum-count";

    public static string GetOutputPath(string workflowProjectDirectory) =>
        Path.Combine(TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory), OutputFileName);

    public static string GetStatePath(string workflowProjectDirectory) =>
        Path.Combine(TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory), StateFileName);

    public static bool HasCurrentOutput(string workflowProjectDirectory)
        => HasCurrentOutput(workflowProjectDirectory, configuredCharacterCount: null, minimumCharacterCount: null);

    public static bool HasCurrentOutput(string workflowProjectDirectory, int? configuredCharacterCount)
        => HasCurrentOutput(
            workflowProjectDirectory,
            configuredCharacterCount,
            configuredCharacterCount);

    public static bool HasCurrentOutput(
        string workflowProjectDirectory,
        int? configuredCharacterCount,
        int? minimumCharacterCount)
    {
        var path = GetOutputPath(workflowProjectDirectory);
        if (!File.Exists(path)) return false;
        try
        {
            var info = Image.Identify(path);
            return info is not null &&
                   info.Width == RoleVectorTemplateRenderer.CanvasWidth &&
                   info.Height == RoleVectorTemplateRenderer.CanvasHeight &&
                   ValidateState(
                       workflowProjectDirectory,
                       path,
                       configuredCharacterCount,
                       minimumCharacterCount);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> GenerateAsync(
        QueueProjectItem item,
        ClientSettings settings,
        int configuredCharacterCount,
        bool forceRerun,
        Action<string>? log,
        CancellationToken ct,
        int minimumCharacterCount = TikTokAccountProfile.DefaultRoleVectorMinimumCharacterCount)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        configuredCharacterCount = TikTokReferenceSourcePackageService.NormalizeConfiguredCharacterCount(
            configuredCharacterCount);
        minimumCharacterCount = TikTokReferenceSourcePackageService.NormalizeMinimumCharacterCount(
            minimumCharacterCount,
            configuredCharacterCount);
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        var output = GetOutputPath(context.WorkflowProjectDir);
        var manualConfiguration = ManualRoleVectorMaterialService.Load(context.WorkflowProjectDir);
        if (!forceRerun && HasCurrentOutput(
                context.WorkflowProjectDir,
                configuredCharacterCount,
                minimumCharacterCount))
        {
            TikTokSourceFileInfoUploadPackageService.RefreshRoleDerivedImages(
                context.WorkflowProjectDir, log, ct);
            log?.Invoke($"角色矢量图已存在且尺寸正确，跳过生成：{output}");
            return output;
        }

        var root = TikTokReferenceSourcePackageService.GetRoot(context.WorkflowProjectDir);
        Directory.CreateDirectory(root);
        await TikTokReferenceSourcePackageService.GenerateAsync(
            item,
            settings,
            forceRerun: false,
            log,
            ct,
            configuredCharacterCount,
            recoverMissingRoleReferences: true,
            minimumCharacterCount: minimumCharacterCount).ConfigureAwait(false);
        var characters = TikTokReferenceSourcePackageService.ListCurrentCharacterImages(
            context.WorkflowProjectDir,
            configuredCharacterCount);
        if (characters.Count < minimumCharacterCount)
        {
            characters = await TikTokReferenceSourcePackageService.EnsureCharacterImagesAsync(
                item,
                settings,
                configuredCharacterCount,
                log,
                ct,
                minimumCharacterCount).ConfigureAwait(false);
        }
        else
        {
            log?.Invoke($"角色矢量图：复用刚完成的 {characters.Count} 张角色定妆图和人物配对清单。");
        }
        var sceneSources = await TikTokReferenceSourcePackageService.ResolveSceneSourcesAsync(
            context, root, log, ct).ConfigureAwait(false);
        var usedCharacters = characters.Take(6).ToArray();
        if (usedCharacters.Length < minimumCharacterCount ||
            usedCharacters.Length > TikTokReferenceSourcePackageService.MaxCharacterCount)
        {
            throw new InvalidOperationException(
                $"角色矢量图目标 {configuredCharacterCount} 人、最低 {minimumCharacterCount} 人，" +
                $"当前只有 {usedCharacters.Length} 人。");
        }
        var pairedReferences = TikTokReferenceSourcePackageService.ResolvePairedCharacterReferences(
            context.WorkflowProjectDir,
            usedCharacters);
        var referenceSources = pairedReferences.Count == usedCharacters.Length
            ? pairedReferences
            : sceneSources.Take(usedCharacters.Length).ToArray();
        if (referenceSources.Count == 0)
            throw new InvalidOperationException("生成角色矢量图失败：没有可用的角色参考图、真实场景图或视频抽帧。");
        var templateDescription = usedCharacters.Length switch
        {
            2 => "双人上下居中模板",
            3 => "三人居中模板",
            4 => "左三右一模板",
            5 => "左三右二模板",
            6 => "左三右三模板",
            _ => throw new InvalidOperationException("角色矢量图模板人数无效。"),
        };
        if (usedCharacters.Length < configuredCharacterCount)
        {
            log?.Invoke(
                $"角色矢量图：目标 {configuredCharacterCount} 人未达成，" +
                $"已满足最低 {minimumCharacterCount} 人，按实际 {usedCharacters.Length} 人兜底。");
        }
        log?.Invoke(
            $"角色矢量图：目标 {configuredCharacterCount} 人，最低 {minimumCharacterCount} 人，" +
            $"最终选择 {usedCharacters.Length} 人，" +
            $"使用{templateDescription}；" +
            (pairedReferences.Count == usedCharacters.Length
                ? $"左右已按角色一一配对，服装参考帧 {referenceSources.Count} 张。"
                : $"未找到完整角色配对清单，使用真实参考帧 {referenceSources.Count} 张兜底。"));

        var temporary = Path.Combine(root, $".{Path.GetFileNameWithoutExtension(OutputFileName)}.{Guid.NewGuid():N}.tmp.png");
        try
        {
            RoleVectorTemplateRenderer.Render(temporary, usedCharacters, referenceSources);
            var info = Image.Identify(temporary)
                ?? throw new InvalidDataException("生成的角色矢量图不是有效 PNG。");
            if (info.Width != RoleVectorTemplateRenderer.CanvasWidth ||
                info.Height != RoleVectorTemplateRenderer.CanvasHeight)
            {
                throw new InvalidDataException(
                    $"角色矢量图尺寸必须为 {RoleVectorTemplateRenderer.CanvasWidth}×" +
                    $"{RoleVectorTemplateRenderer.CanvasHeight}，当前为 {info.Width}×{info.Height}。");
            }

            if (File.Exists(output))
                File.Copy(output, Path.Combine(root, BackupFileName), overwrite: true);
            File.Move(temporary, output, overwrite: true);
            WriteState(
                context.WorkflowProjectDir,
                output,
                configuredCharacterCount,
                minimumCharacterCount,
                usedCharacters,
                referenceSources);
            TikTokSourceFileInfoUploadPackageService.RefreshRoleDerivedImages(
                context.WorkflowProjectDir, log, ct);
            log?.Invoke($"角色矢量图生成完成：{info.Width}×{info.Height} → {output}");
            return output;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void WriteState(
        string workflowProjectDirectory,
        string output,
        int configuredCharacterCount,
        int minimumCharacterCount,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> references)
    {
        var root = TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory);
        var characterManifest = TikTokReferenceSourcePackageService.GetCharacterManifestPath(workflowProjectDirectory);
        var layout = RoleVectorTemplateRenderer.ResolveLayout(characters.Count);
        var payload = new
        {
            version = StateVersion,
            sourceMode = ManualRoleVectorMaterialService.Load(workflowProjectDirectory).Mode switch
            {
                ManualRoleVectorMode.ReferencesOnly => "manual-references",
                ManualRoleVectorMode.Paired => "manual-paired",
                _ => "auto",
            },
            manualFingerprint = ManualRoleVectorMaterialService.Load(workflowProjectDirectory).Fingerprint,
            configuredCount = configuredCharacterCount,
            minimumCount = minimumCharacterCount,
            characterCount = characters.Count,
            fallbackUsed = characters.Count < configuredCharacterCount,
            templateResource = layout.ResourceName,
            outputSha256 = ComputeSha256(output),
            characterManifest = Path.GetRelativePath(root, characterManifest),
            characterManifestSha256 = ComputeSha256(characterManifest),
            characters = characters.Select(path => new
            {
                path = Path.GetRelativePath(root, path),
                sha256 = ComputeSha256(path),
            }),
            references = references.Select(path => new
            {
                path = Path.GetFullPath(path),
                sha256 = ComputeSha256(path),
            }),
            generatedAt = DateTimeOffset.Now,
        };
        File.WriteAllText(
            GetStatePath(workflowProjectDirectory),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static bool ValidateState(
        string workflowProjectDirectory,
        string output,
        int? expectedConfiguredCharacterCount,
        int? expectedMinimumCharacterCount)
    {
        var statePath = GetStatePath(workflowProjectDirectory);
        if (!File.Exists(statePath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var rootElement = document.RootElement;
            if (rootElement.GetProperty("version").GetString() != StateVersion) return false;
            var sourceMode = rootElement.TryGetProperty("sourceMode", out var sourceModeValue)
                ? sourceModeValue.GetString() ?? "auto"
                : "auto";
            var manualConfiguration = ManualRoleVectorMaterialService.Load(workflowProjectDirectory);
            if (sourceMode == "manual-paired" &&
                (manualConfiguration.Mode != ManualRoleVectorMode.Paired ||
                 !rootElement.TryGetProperty("manualFingerprint", out var fingerprintValue) ||
                 !string.Equals(
                     fingerprintValue.GetString(),
                     manualConfiguration.Fingerprint,
                     StringComparison.OrdinalIgnoreCase))) return false;
            if (sourceMode == "manual-references" &&
                (manualConfiguration.Mode != ManualRoleVectorMode.ReferencesOnly ||
                 !rootElement.TryGetProperty("manualFingerprint", out var referenceFingerprintValue) ||
                 !string.Equals(
                     referenceFingerprintValue.GetString(),
                     manualConfiguration.Fingerprint,
                     StringComparison.OrdinalIgnoreCase))) return false;
            if (sourceMode == "auto" && manualConfiguration.Mode != ManualRoleVectorMode.Auto) return false;
            var configuredCount = rootElement.GetProperty("configuredCount").GetInt32();
            if (expectedConfiguredCharacterCount.HasValue &&
                configuredCount != TikTokReferenceSourcePackageService.NormalizeConfiguredCharacterCount(
                    expectedConfiguredCharacterCount.Value)) return false;
            var minimumCount = rootElement.TryGetProperty("minimumCount", out var minimumCountValue)
                ? minimumCountValue.GetInt32()
                : configuredCount;
            if (expectedConfiguredCharacterCount.HasValue && expectedMinimumCharacterCount.HasValue &&
                minimumCount != TikTokReferenceSourcePackageService.NormalizeMinimumCharacterCount(
                    expectedMinimumCharacterCount.Value,
                    expectedConfiguredCharacterCount.Value)) return false;
            var characterCount = rootElement.GetProperty("characterCount").GetInt32();
            if (characterCount is < TikTokReferenceSourcePackageService.MinCharacterCount or
                > TikTokReferenceSourcePackageService.MaxCharacterCount) return false;
            if (characterCount < minimumCount || characterCount > configuredCount) return false;
            if (rootElement.GetProperty("templateResource").GetString() !=
                RoleVectorTemplateRenderer.ResolveLayout(characterCount).ResourceName) return false;
            if (!MatchesHash(output, rootElement.GetProperty("outputSha256").GetString())) return false;

            var packageRoot = TikTokReferenceSourcePackageService.GetRoot(workflowProjectDirectory);
            var manifest = ResolveStoredPath(packageRoot, rootElement.GetProperty("characterManifest").GetString());
            if (!MatchesHash(manifest, rootElement.GetProperty("characterManifestSha256").GetString())) return false;
            if (!ValidateFileEntries(packageRoot, rootElement.GetProperty("characters"), characterCount)) return false;
            if (!ValidateFileEntries(packageRoot, rootElement.GetProperty("references"), minimumCount: 1)) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateFileEntries(string packageRoot, JsonElement entries, int minimumCount)
    {
        if (entries.ValueKind != JsonValueKind.Array) return false;
        var count = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var path = ResolveStoredPath(packageRoot, entry.GetProperty("path").GetString());
            if (!MatchesHash(path, entry.GetProperty("sha256").GetString())) return false;
            count++;
        }
        return count >= minimumCount;
    }

    private static string ResolveStoredPath(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
    }

    private static bool MatchesHash(string path, string? expected) =>
        !string.IsNullOrWhiteSpace(expected) && File.Exists(path) &&
        string.Equals(ComputeSha256(path), expected, StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
