using System.Runtime.InteropServices;
using TikTokPublisher.Core.Models;

namespace TikTokPublisher.Core.Services.Asr;

public sealed record SherpaOnnxModelPaths(string ModelPath, string TokensPath, string VadPath);

/// <summary>解析本地 Paraformer + silero VAD 模型路径（对齐 Python <c>_resolve_local_model_paths</c>）。</summary>
public static class SherpaOnnxModelResolver
{
    private const string DefaultParaformerDir = "sherpa-onnx-paraformer-zh-2023-09-14";

    public static SherpaOnnxModelPaths? TryResolve(ClientSettings settings)
    {
        var explicitDir = (settings.TiktokSilenceLocalModelDir ?? "").Trim();
        var explicitVad = (settings.TiktokSilenceLocalVadPath ?? "").Trim();
        var dirCandidates = new List<string>();
        if (!string.IsNullOrEmpty(explicitDir))
            dirCandidates.Add(explicitDir);
        foreach (var root in ModelRootDirs())
        {
            dirCandidates.Add(Path.Combine(root, "models", DefaultParaformerDir));
            dirCandidates.Add(Path.Combine(root, "models"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var modelDir in dirCandidates)
        {
            var key = Path.GetFullPath(Environment.ExpandEnvironmentVariables(modelDir));
            if (!seen.Add(key) || !Directory.Exists(key))
                continue;

            var model = Path.Combine(key, "model.int8.onnx");
            if (!File.Exists(model))
                model = Path.Combine(key, "model.onnx");
            var tokens = Path.Combine(key, "tokens.txt");
            if (!File.Exists(model) || !File.Exists(tokens))
                continue;

            var vadCandidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitVad))
                vadCandidates.Add(explicitVad);
            vadCandidates.Add(Path.Combine(key, "silero_vad.onnx"));
            var parent = Directory.GetParent(key)?.FullName;
            if (!string.IsNullOrEmpty(parent))
                vadCandidates.Add(Path.Combine(parent, "silero_vad.onnx"));
            foreach (var root in ModelRootDirs())
                vadCandidates.Add(Path.Combine(root, "models", "silero_vad.onnx"));

            foreach (var vad in vadCandidates)
            {
                if (string.IsNullOrWhiteSpace(vad) || !File.Exists(vad))
                    continue;
                return new SherpaOnnxModelPaths(model, tokens, vad);
            }
        }

        return null;
    }

    public static (bool Ok, string Reason) CheckAvailable(ClientSettings settings)
    {
        return TryResolve(settings) is not null
            ? (true, "")
            : (false, "未找到本地 Paraformer 模型（请在 ASR 配置里设置模型目录，或放到 models/ 下；需 model.int8.onnx、tokens.txt、silero_vad.onnx）。");
    }

    public static string ToAsciiSafePath(string path)
    {
        var text = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!OperatingSystem.IsWindows() || text.All(c => c < 128))
            return text;

        var shortPath = TryGetWindowsShortPath(text);
        if (!string.IsNullOrEmpty(shortPath) && shortPath.All(c => c < 128))
            return shortPath;

        var cacheRoot = AsciiModelCacheRoot();
        if (cacheRoot is null)
            return text;

        var target = Path.Combine(cacheRoot, Path.GetFileName(text));
        try
        {
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(text).Length)
                File.Copy(text, target, overwrite: true);
            return target;
        }
        catch
        {
            return text;
        }
    }

    private static IEnumerable<string> ModelRootDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        foreach (var root in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "",
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            try
            {
                var full = Path.GetFullPath(root);
                if (seen.Add(full))
                    results.Add(full);
            }
            catch
            {
                // ignore invalid roots
            }
        }

        var cursor = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(cursor); depth++)
        {
            try
            {
                var full = Path.GetFullPath(cursor);
                if (Directory.Exists(Path.Combine(full, "models")) && seen.Add(full))
                    results.Add(full);
            }
            catch
            {
                // ignore
            }

            cursor = Directory.GetParent(cursor)?.FullName ?? "";
        }

        return results;
    }

    private static string? AsciiModelCacheRoot()
    {
        foreach (var baseDir in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TikTokShortDramaUploader", "asr-model-cache"),
                     Path.Combine(Path.GetTempPath(), "TikTokShortDramaUploader-asr-model-cache"),
                 })
        {
            try
            {
                if (!baseDir.All(c => c < 128))
                    continue;
                Directory.CreateDirectory(baseDir);
                return baseDir;
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static string? TryGetWindowsShortPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var buffer = new char[4096];
        var length = GetShortPathName(path, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string lpszLongPath, char[] lpszShortPath, int cchBuffer);
}
