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
        CancellationToken cancellationToken) =>
        await ExtractAsync(configuredExePath, HongguoClientProfile.High, progress, cancellationToken);

    public static async Task<HongguoHighProvisionResult> ExtractAsync(
        string? configuredExePath,
        HongguoClientProfile profile,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var exePath = HongguoHighDeviceStore.FindOfficialClientExe(configuredExePath, profile);
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            throw new HongguoHighException(
                $"未找到官方{profile.DisplayName}客户端 exe。请先选择对应的 HG 短剧下载器，" +
                $@"默认目录通常为 %LOCALAPPDATA%\{profile.LocalDataDirectory}。");
        }

        var python = ResolvePython();
        var script = ResolveProvisionScript();
        if (string.IsNullOrWhiteSpace(python) || string.IsNullOrWhiteSpace(script))
        {
            throw new HongguoHighException(
                "未找到可用的 Frida 16.x。安装包需内置 Frida 16.7.19；" +
                "源码启动可用系统 Python（pip install frida==16.7.19）。");
        }

        var closed = CloseOfficialClient(exePath, profile);
        if (closed > 0)
        {
            progress?.Report($"已关闭 {closed} 个正在运行的官方{profile.DisplayName}客户端，随后重新启动以提取密钥。");
            await Task.Delay(1200, cancellationToken);
        }
        else
        {
            progress?.Report($"正在启动官方{profile.DisplayName}客户端并提取 Enc/Sign Master…");
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
            var logGate = new object();
            var completed = new int[1];
            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data) || Volatile.Read(ref completed[0]) != 0)
                {
                    return;
                }

                lock (logGate)
                {
                    stdout.AppendLine(args.Data);
                }

                progress?.Report(args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data) || Volatile.Read(ref completed[0]) != 0)
                {
                    return;
                }

                lock (logGate)
                {
                    stderr.AppendLine(args.Data);
                }

                progress?.Report(args.Data);
            };

            if (!process.Start())
            {
                throw new HongguoHighException("无法启动内置 Frida 运行时。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(DefaultWaitSeconds + 10);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryReadMastersFile(outputPath, out var enc, out var sign) ||
                        TryParseMastersJson(Snapshot(stdout, logGate), out enc, out sign))
                    {
                        var deviceId = HongguoHighDeviceStore.TryReadDeviceId(profile);
                        var cachePath = HongguoHighDeviceStore.CacheStartupMasters(enc, sign, deviceId, profile);
                        Interlocked.Exchange(ref completed[0], 1);
                        progress?.Report("已提取 Enc Master 和 Sign Master，正在填入表单…");
                        TryKill(process, entireProcessTree: false);
                        return new HongguoHighProvisionResult(cachePath, deviceId, enc, sign);
                    }

                    if (process.HasExited)
                    {
                        if (TryReadMastersFile(outputPath, out enc, out sign) ||
                            TryParseMastersJson(Snapshot(stdout, logGate), out enc, out sign))
                        {
                            var deviceId = HongguoHighDeviceStore.TryReadDeviceId(profile);
                            var cachePath = HongguoHighDeviceStore.CacheStartupMasters(enc, sign, deviceId, profile);
                            Interlocked.Exchange(ref completed[0], 1);
                            return new HongguoHighProvisionResult(cachePath, deviceId, enc, sign);
                        }

                        var detail = Snapshot(stderr, logGate).Trim();
                        if (string.IsNullOrWhiteSpace(detail))
                        {
                            detail = Snapshot(stdout, logGate).Trim();
                        }

                        throw new HongguoHighException(
                            string.IsNullOrWhiteSpace(detail)
                                ? "提取启动密钥失败。请完全退出官方客户端后重试。"
                                : detail);
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        TryKill(process);
                        throw new HongguoHighException("提取启动密钥超时。密钥已抽出时会立即填入表单，无需关闭官方客户端。");
                    }

                    await Task.Delay(150, cancellationToken);
                }
            }
            catch (HongguoHighException)
            {
                TryKill(process);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
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

    public static bool IsUsableFridaVersion(string? version)
    {
        var text = (version ?? "").Trim();
        return text.Length > 0 && !text.StartsWith("17.", StringComparison.Ordinal);
    }

    public static string? ResolvePython()
    {
        foreach (var candidate in EnumeratePythonCandidates())
        {
            if (TryReadFridaVersion(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool TryReadFridaVersion(string pythonExe, out string version)
    {
        version = "";
        if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(pythonExe) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("-B");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import frida; print(frida.__version__)");

            using var process = DiagProcess.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                TryKill(process);
                return false;
            }

            if (process.ExitCode != 0)
            {
                return false;
            }

            var last = stdout.Replace("\r", "", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()
                ?.Trim() ?? "";
            version = last;
            return IsUsableFridaVersion(version);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumeratePythonCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundled = BundledToolResolver.TryResolvePython();
        if (!string.IsNullOrWhiteSpace(bundled) && File.Exists(bundled) && seen.Add(Path.GetFullPath(bundled)))
        {
            yield return bundled;
        }

        foreach (var name in new[] { "python", "python3", "py" })
        {
            var resolved = BundledToolResolver.TryResolveBinary(name);
            if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
            {
                continue;
            }

            var full = Path.GetFullPath(resolved);
            if (seen.Add(full))
            {
                yield return resolved;
            }
        }
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

    public static bool TryReadMastersFile(string path, out string enc, out string sign)
    {
        enc = "";
        sign = "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return TryParseMastersJson(File.ReadAllText(path), out enc, out sign);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryParseMastersJson(string text, out string enc, out string sign)
    {
        enc = "";
        sign = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            const string prefix = "MASTERS_JSON:";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                line = line[prefix.Length..].Trim();
            }

            if (TryReadEncSign(line, out enc, out sign))
            {
                return true;
            }
        }

        return TryReadEncSign(text, out enc, out sign);
    }

    private static bool TryReadEncSign(string json, out string enc, out string sign)
    {
        enc = "";
        sign = "";
        if (string.IsNullOrWhiteSpace(json) || json.IndexOf("enc", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            enc = root.TryGetProperty("enc", out var encNode) ? encNode.GetString()?.Trim() ?? "" : "";
            sign = root.TryGetProperty("sign", out var signNode) ? signNode.GetString()?.Trim() ?? "" : "";
            return !string.IsNullOrWhiteSpace(enc) && !string.IsNullOrWhiteSpace(sign);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Snapshot(StringBuilder builder, object gate)
    {
        lock (gate)
        {
            return builder.ToString();
        }
    }

    public static bool IsOfficialClientPath(string? processPath, string? configuredExePath)
        => IsOfficialClientPath(processPath, configuredExePath, HongguoClientProfile.High);

    public static bool IsOfficialClientPath(
        string? processPath,
        string? configuredExePath,
        HongguoClientProfile profile)
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
            profile.LocalDataDirectory);
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
        => CloseOfficialClient(configuredExePath, HongguoClientProfile.High);

    public static int CloseOfficialClient(string? configuredExePath, HongguoClientProfile profile)
    {
        var exePath = HongguoHighDeviceStore.FindOfficialClientExe(configuredExePath, profile);
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
                var exeName = string.IsNullOrWhiteSpace(exePath)
                    ? ""
                    : Path.GetFileNameWithoutExtension(exePath);
                var looksOfficial = IsOfficialClientPath(path, exePath, profile) ||
                                    (profile.Edition == HongguoClientProfile.HighEdition &&
                                     (name.Contains("HongguoHigh", StringComparison.OrdinalIgnoreCase) ||
                                      name.Contains("HongGuoHigh", StringComparison.OrdinalIgnoreCase))) ||
                                    (profile.Edition == HongguoClientProfile.StandardEdition &&
                                     name.Contains("HongguoDownloader", StringComparison.OrdinalIgnoreCase)) ||
                                    (!string.IsNullOrWhiteSpace(exeName) &&
                                     name.Equals(exeName, StringComparison.OrdinalIgnoreCase));
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

    private static void TryKill(DiagProcess process, bool entireProcessTree = true)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: entireProcessTree);
            }
        }
        catch
        {
            // Ignore process cleanup failures.
        }
    }
}
