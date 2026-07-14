using System.Security.Cryptography;

namespace TikTokPublisher.Core.Services;

public static class TikTokProofMaterialTemplateProvider
{
    public const string LegacyDefaultTemplatePath =
        @"C:\Users\PC\Desktop\zhangbiao\word\权力声明模板.docx";
    public const string BuiltInTemplateFileName = "权力声明模板.docx";

    internal const string EmbeddedResourceName =
        "TikTokPublisher.Core.Resources.ProofMaterialTemplate.docx";

    private static readonly object ReleaseLock = new();

    /// <summary>
    /// Returns a valid custom template when configured; otherwise releases and returns the
    /// managed template embedded in TikTokPublisher.Core.
    /// </summary>
    public static string ResolveTemplatePath(string? configuredPath, string? dataRoot = null)
    {
        var customPath = TryResolveCustomTemplate(configuredPath);
        return customPath ?? EnsureBuiltInTemplate(dataRoot);
    }

    public static string EnsureBuiltInTemplate(string? dataRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(dataRoot)
            ? AppPaths.DataRoot
            : Path.GetFullPath(dataRoot);
        var targetDirectory = Path.Combine(root, "templates", "proof-material");
        var targetPath = Path.Combine(targetDirectory, BuiltInTemplateFileName);

        lock (ReleaseLock)
        {
            var assembly = typeof(TikTokProofMaterialTemplateProvider).Assembly;
            using var resourceStream = assembly.GetManifestResourceStream(EmbeddedResourceName)
                ?? throw new InvalidOperationException(
                    $"程序集缺少内置证明材料模板资源：{EmbeddedResourceName}");
            using var buffer = new MemoryStream();
            resourceStream.CopyTo(buffer);
            var resourceBytes = buffer.ToArray();
            ValidateDocxBytes(resourceBytes);
            var expectedHash = SHA256.HashData(resourceBytes);

            if (HasExpectedContent(targetPath, expectedHash))
                return targetPath;

            Directory.CreateDirectory(targetDirectory);
            var temporaryPath = Path.Combine(
                targetDirectory,
                $".{BuiltInTemplateFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, resourceBytes);
                try
                {
                    File.Move(temporaryPath, targetPath, overwrite: true);
                }
                catch (IOException) when (HasExpectedContent(targetPath, expectedHash))
                {
                    // Another process released the same resource between our hash check and move.
                }

                if (!HasExpectedContent(targetPath, expectedHash))
                    throw new IOException("内置证明材料模板释放后校验失败。");
                return targetPath;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    internal static string? TryResolveCustomTemplate(string? configuredPath)
    {
        var value = Environment.ExpandEnvironmentVariables(configuredPath?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("builtin", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("internal", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("内置", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            if (PathsEqual(fullPath, LegacyDefaultTemplatePath))
            {
                // Old versions persisted this machine-specific path as the default. Treat it
                // as the built-in-template sentinel so upgraded installations are portable.
                return null;
            }

            if (!string.Equals(Path.GetExtension(fullPath), ".docx", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(fullPath) ||
                new FileInfo(fullPath).Length <= 0)
            {
                return null;
            }

            return fullPath;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsLegacyDefaultPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return PathsEqual(Path.GetFullPath(path.Trim()), LegacyDefaultTemplatePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasExpectedContent(string path, ReadOnlySpan<byte> expectedHash)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash);
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateDocxBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
            throw new InvalidDataException("程序集内置证明材料模板不是有效的 DOCX/ZIP 文件。");
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
