using System.Diagnostics;
using System.Reflection;
using System.Text;
using SixLabors.ImageSharp;

namespace TikTokPublisher.Core.Services;

internal static class WindowsExplorerScreenshotService
{
    private const string ScriptResourceName =
        "TikTokPublisher.Core.Resources.CaptureExplorerWindow.ps1";

    internal sealed record CaptureRequest(string Directory, string OutputPath, bool LargeIcons);

    public static bool TryCaptureAll(
        IReadOnlyList<CaptureRequest> requests,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || requests.Count == 0)
        {
            return false;
        }

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"tiktok-explorer-capture-{Guid.NewGuid():N}.ps1");
        try
        {
            WriteScript(scriptPath);
            for (var index = 0; index < requests.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = requests[index];
                if (!Directory.Exists(request.Directory))
                {
                    throw new DirectoryNotFoundException(
                        $"资源管理器截图目录不存在：{request.Directory}");
                }

                log?.Invoke(
                    $"原始文件信息/资源管理器截图 {index + 1}/{requests.Count}：" +
                    $"{request.Directory}。");
                CaptureOne(scriptPath, request, cancellationToken);
                using var image = Image.Load(request.OutputPath);
                if (image.Width < 800 || image.Height < 500)
                {
                    throw new InvalidDataException(
                        $"资源管理器截图尺寸异常：{image.Width}×{image.Height}");
                }
            }

            return requests.All(request => File.Exists(request.OutputPath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"原始文件信息/资源管理器截图失败，将使用兼容渲染图：{ex.Message}");
            foreach (var request in requests)
            {
                TryDelete(request.OutputPath);
            }
            return false;
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    private static void CaptureOne(
        string scriptPath,
        CaptureRequest request,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass",
                     "-File", scriptPath,
                     "-TargetPath", Path.GetFullPath(request.Directory),
                     "-OutputPath", Path.GetFullPath(request.OutputPath),
                     "-View", request.LargeIcons ? "LargeIcons" : "Details",
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        while (!process.WaitForExit(250))
        {
            if (!cancellationToken.IsCancellationRequested) continue;
            try { process.Kill(entireProcessTree: true); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
        }

        var stderr = process.StandardError.ReadToEnd().Trim();
        if (process.ExitCode != 0 || !File.Exists(request.OutputPath))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? $"资源管理器截图进程退出码：{process.ExitCode}"
                    : stderr);
        }
    }

    private static void WriteScript(string destination)
    {
        using var source = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ScriptResourceName)
            ?? throw new InvalidOperationException(
                $"未找到资源管理器截图脚本资源：{ScriptResourceName}");
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        File.WriteAllText(destination, reader.ReadToEnd(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
