using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ShortDrama.Infrastructure;
using DiagProcess = System.Diagnostics.Process;

namespace ShortDrama.Infrastructure.Automation;

public sealed record HongguoHighProvisionResult(
    string CachePath,
    string DeviceId,
    string EncMaster,
    string SignMaster);

public static class HongguoHighMasterProvisioner
{
    public const int DefaultWaitSeconds = 30;

    public static async Task<HongguoHighProvisionResult> ExtractAsync(
        string? configuredExePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var exePath = HongguoHighDeviceStore.FindOfficialClientExe(configuredExePath);
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            throw new HongguoHighException(
                "未找到官方高码率客户端 exe。请在「高码率客户端 exe」选择 HG短剧下载器高码率版 2.1.6，" +
                @"默认目录通常为 %LOCALAPPDATA%\HongguoHighDownloader。");
        }

        var python = ResolvePython();
        var script = ResolveProvisionScript();
        if (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(script))
        {
            throw new HongguoHighException(
                "未找到内置 Frida 运行时。安装包会附带 Python + frida；" +
                @"开发环境请把 embeddable Python 和 frida 放到 packaging\dependencies\tools\win-x64\python。");
        }

        var closed = CloseOfficialClient(exePath);
        if (closed > 0)
        {
            progress?.Report($"已关闭 {closed} 个正在运行的官方高码率客户端，随后重新启动以提取密钥。");
            await Task.Delay(1200, cancellationToken);
        }
        else
        {
            progress?.Report("正在启动官方高码率客户端并提取 Enc/Sign Master…");
        }
        var outputPath = Path.Combine(Path.GetTempPath(), $"hghigh-masters-{Guid.NewGuid():N}.json");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(python) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("-B");
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("--exe");
            startInfo.ArgumentList.Add(exePath);
            startInfo.ArgumentList.Add("--output");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add("--wait");
            startInfo.ArgumentList.Add(DefaultWaitSeconds.ToString());

            using var process = new DiagProcess { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    stdout.AppendLine(args.Data);
                    progress?.Report(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    stderr.AppendLine(args.Data);
                    progress?.Report(args.Data);
                }
            };

            if (!process.Start())
            {
                throw new HongguoHighException("无法启动内置 Frida 运行时。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                TryKill(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                var detail = stderr.ToString().Trim();
                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = stdout.ToString().Trim();
                }

                throw new HongguoHighException(
                    string.IsNullOrWhiteSpace(detail)
                        ? "提取启动密钥失败。请完全退出官方客户端后重试。"
                        : detail);
            }

            if (!File.Exists(outputPath))
            {
                throw new HongguoHighException("Frida 提取完成但未写出启动密钥。请完全退出官方客户端后重试。");
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, cancellationToken));
            var root = document.RootElement;
            var enc = root.TryGetProperty("enc", out var encNode) ? encNode.GetString() ?? "" : "";
            var sign = root.TryGetProperty("sign", out var signNode) ? signNode.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(enc) || string.IsNullOrWhiteSpace(sign))
            {
                throw new HongguoHighException("提取结果缺少 enc/sign master。请完全退出官方客户端后重试。");
            }

            var deviceId = HongguoHighDeviceStore.TryReadDeviceId();
            var cachePath = HongguoHighDeviceStore.CacheStartupMasters(enc, sign, deviceId);
            CloseOfficialClient(exePath);
            progress?.Report("已提取 Enc Master 和 Sign Master，可点「保存密钥」再次写入本机缓存。");
            return new HongguoHighProvisionResult(cachePath, deviceId, enc, sign);
        }
        finally
        {
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch
            {
                // Ignore temp cleanup failures.
            }
        }
    }

    public static string? ResolvePython()
    {
        var bundled = BundledToolResolver.TryResolvePython();
        if (!string.IsNullOrWhiteSpace(bundled) && File.Exists(bundled))
        {
            return bundled;
        }

        foreach (var name in new[] { "python", "python3", "py" })
        {
            var resolved = BundledToolResolver.TryResolveBinary(name);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    public static string? ResolveProvisionScript()
    {
        foreach (var root in BundledToolResolver.EnumerateSearchRoots())
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "tools", "hongguo-high", "provision_startup_masters.py"),
                         Path.Combine(root, "Tools", "hongguo-high", "provision_startup_masters.py")
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static bool IsOfficialClientPath(string? processPath, string? configuredExePath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(processPath);
        }
        catch
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(configuredExePath))
        {
            try
            {
                if (string.Equals(full, Path.GetFullPath(configuredExePath), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore invalid configured paths and still match the default install folder.
            }
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HongguoHighDownloader");
        try
        {
            folder = Path.GetFullPath(folder);
        }
        catch
        {
            return false;
        }

        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static int CloseOfficialClient(string? configuredExePath)
    {
        var exePath = HongguoHighDeviceStore.FindOfficialClientExe(configuredExePath);
        var closed = 0;
        foreach (var process in DiagProcess.GetProcesses())
        {
            try
            {
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Accessing MainModule can fail for other-bitness or protected processes.
                }

                var name = process.ProcessName ?? "";
                var looksOfficial = IsOfficialClientPath(path, exePath) ||
                                    name.Contains("HongguoHigh", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("HongGuoHigh", StringComparison.OrdinalIgnoreCase);
                if (!looksOfficial)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                closed++;
            }
            catch
            {
                // Ignore processes we cannot inspect or terminate.
            }
            finally
            {
                process.Dispose();
            }
        }

        return closed;
    }

    private static void TryKill(DiagProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore process cleanup failures.
        }
    }
}
