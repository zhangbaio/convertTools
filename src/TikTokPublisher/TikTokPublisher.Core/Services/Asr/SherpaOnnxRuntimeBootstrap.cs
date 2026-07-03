using System.Runtime.InteropServices;

namespace TikTokPublisher.Core.Services.Asr;

/// <summary>Windows 上预加载 sherpa-onnx 自带的 onnxruntime.dll，避免命中 System32 旧版。</summary>
internal static class SherpaOnnxRuntimeBootstrap
{
    private static int _preloaded;

    public static void PreloadBundledOnnxRuntime()
    {
        if (Interlocked.Exchange(ref _preloaded, 1) == 1)
            return;
        if (!OperatingSystem.IsWindows())
            return;

        foreach (var dll in EnumerateOnnxRuntimeCandidates())
        {
            try
            {
                if (!File.Exists(dll))
                    continue;
                NativeLibrary.Load(dll);
                return;
            }
            catch
            {
                // 尝试下一个候选路径
            }
        }
    }

    private static IEnumerable<string> EnumerateOnnxRuntimeCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseDir in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "",
                 })
        {
            if (string.IsNullOrWhiteSpace(baseDir))
                continue;
            foreach (var relative in new[]
                     {
                         Path.Combine("runtimes", "win-x64", "native", "onnxruntime.dll"),
                         Path.Combine("sherpa-onnx", "lib", "onnxruntime.dll"),
                         Path.Combine("lib", "onnxruntime.dll"),
                     })
            {
                var full = Path.GetFullPath(Path.Combine(baseDir, relative));
                if (seen.Add(full))
                    yield return full;
            }
        }
    }
}
