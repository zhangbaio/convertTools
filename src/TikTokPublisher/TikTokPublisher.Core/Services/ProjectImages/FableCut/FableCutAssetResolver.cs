using System.Security.Cryptography;
using System.Text;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

internal static class FableCutAssetResolver
{
    private static readonly string[] RequiredFiles =
    [
        "index.html",
        "app.js",
        "style.css",
        "ruler-worker.js",
        "meter-worklet.js",
    ];

    public static string Resolve(string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var explicitRoot = Path.GetFullPath(configuredRoot.Trim());
            Validate(explicitRoot, isExplicit: true);
            return explicitRoot;
        }

        foreach (var candidate in EnumerateDefaultCandidates())
        {
            if (IsValid(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new DirectoryNotFoundException(
            "未找到 FableCut 编辑器资源。请重新安装包含 FableCut 资源的程序，" +
            "或在系统设置 → 工程图中配置 FableCut 目录。");
    }

    public static string ComputeFingerprint(string root)
    {
        Validate(root, isExplicit: false);
        using var sha = SHA256.Create();
        foreach (var name in RequiredFiles)
        {
            var path = Path.Combine(root, name);
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            using var stream = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateDefaultCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            var current = Path.GetFullPath(start);
            while (!string.IsNullOrWhiteSpace(current))
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(current, "fablecut"),
                             Path.Combine(current, "Resources", "FableCut"),
                             Path.Combine(current, "src", "TikTokPublisher", "TikTokPublisher.Core", "Resources", "FableCut"),
                             Path.Combine(current, "FableCut"),
                         })
                {
                    string fullPath;
                    try { fullPath = Path.GetFullPath(candidate); }
                    catch { continue; }
                    if (seen.Add(fullPath))
                        yield return fullPath;
                }

                var parent = Directory.GetParent(current);
                if (parent is null)
                    break;
                current = parent.FullName;
            }
        }
    }

    private static bool IsValid(string root) =>
        Directory.Exists(root) && RequiredFiles.All(name => File.Exists(Path.Combine(root, name)));

    private static void Validate(string root, bool isExplicit)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                isExplicit ? $"配置的 FableCut 目录不存在：{root}" : $"FableCut 目录不存在：{root}");

        var missing = RequiredFiles.Where(name => !File.Exists(Path.Combine(root, name))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"FableCut 目录缺少必要文件：{string.Join("、", missing)}（{root}）");
    }
}
