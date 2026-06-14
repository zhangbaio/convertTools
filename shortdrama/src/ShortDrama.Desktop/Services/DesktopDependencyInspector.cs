using ShortDrama.Core.Interfaces;
using ShortDrama.Desktop.Models;

namespace ShortDrama.Desktop.Services;

public sealed class DesktopDependencyInspector
{
    private readonly IExternalProcessRunner _processRunner;

    public DesktopDependencyInspector(IExternalProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public IReadOnlyList<DependencyStatusItem> Inspect()
    {
        return
        [
            InspectBinary("Python", null, OperatingSystem.IsWindows() ? "python" : "python3", GetPythonCandidates()),
            InspectBinary("ffmpeg", null, "ffmpeg", GetBundledCandidates("ffmpeg")),
            InspectBinary("ffprobe", null, "ffprobe", GetBundledCandidates("ffprobe"))
        ];
    }

    public async Task<IReadOnlyList<DependencyStatusItem>> TestAsync(CancellationToken cancellationToken)
    {
        var items = Inspect();
        var tested = new List<DependencyStatusItem>(items.Count);
        foreach (var item in items)
        {
            tested.Add(await TestOneAsync(item, cancellationToken));
        }

        return tested;
    }

    private static DependencyStatusItem InspectBinary(
        string name,
        string? envVarName,
        string binaryName,
        params IEnumerable<string>[] candidateGroups)
    {
        var envPath = !string.IsNullOrWhiteSpace(envVarName) ? Environment.GetEnvironmentVariable(envVarName) : null;
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return NewItem(name, true, envPath, "环境变量", $"命中 {envVarName}");
        }

        foreach (var candidate in candidateGroups.SelectMany(group => group))
        {
            if (File.Exists(candidate))
            {
                return NewItem(name, true, candidate, "内置/固定路径", "已找到可执行文件");
            }
        }

        var pathBinary = TryResolveBinary(binaryName);
        if (pathBinary is not null)
        {
            return NewItem(name, true, pathBinary, "PATH", "已从系统 PATH 解析");
        }

        var hint = envVarName is null
            ? $"未找到 {binaryName}，请安装或放入 PATH。"
            : $"未找到 {binaryName}，可配置 {envVarName} 或将依赖放入 tools/<os-arch>/ 目录。";
        return NewItem(name, false, null, "缺失", hint);
    }

    private async Task<DependencyStatusItem> TestOneAsync(DependencyStatusItem item, CancellationToken cancellationToken)
    {
        if (!item.IsAvailable || string.IsNullOrWhiteSpace(item.Path))
        {
            return item with
            {
                TestStatus = "失败",
                TestMessage = "依赖缺失，无法执行测试。"
            };
        }

        try
        {
            var result = await _processRunner.RunAsync(
                item.Path,
                GetTestArguments(item.Name),
                Path.GetDirectoryName(item.Path),
                cancellationToken);

            var summary = BuildTestSummary(result.StandardOutput, result.StandardError);
            return item with
            {
                TestStatus = result.ExitCode == 0 ? "通过" : "失败",
                TestMessage = summary
            };
        }
        catch (Exception ex)
        {
            return item with
            {
                TestStatus = "失败",
                TestMessage = ex.Message
            };
        }
    }

    private static IReadOnlyList<string> GetTestArguments(string name)
    {
        return name switch
        {
            "Python" => ["--version"],
            "ffmpeg" => ["-version"],
            "ffprobe" => ["-version"],
            _ => ["--version"]
        };
    }

    private static string BuildTestSummary(string stdout, string stderr)
    {
        var candidate = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Concat(stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

        return string.IsNullOrWhiteSpace(candidate) ? "命令已执行。" : candidate;
    }

    private static DependencyStatusItem NewItem(string name, bool isAvailable, string? path, string source, string hint)
    {
        return new DependencyStatusItem(
            name,
            isAvailable,
            path,
            source,
            hint,
            TestStatus: "未测试",
            TestMessage: string.Empty);
    }

    private static IEnumerable<string> GetBundledCandidates(string binaryName)
    {
        var rid = GetOsArchKey();
        foreach (var root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, "tools", rid, binaryName, ExecutableName(binaryName));
            yield return Path.Combine(root, "tools", rid, "ffmpeg", ExecutableName(binaryName));
            yield return Path.Combine(root, "tools", rid, "ffmpeg", "bin", ExecutableName(binaryName));
        }
    }

    private static IEnumerable<string> GetPythonCandidates()
    {
        foreach (var root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, "tools", GetOsArchKey(), "python", ExecutableName("python"));
            yield return Path.Combine(root, "tools", GetOsArchKey(), "python", ExecutableName("python3"));
        }
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            var current = Path.GetFullPath(start);
            while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
            {
                yield return current;
                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }
    }

    private static string GetOsArchKey()
    {
        var os = OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsWindows() ? "windows"
            : "linux";

        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            _ => "unknown"
        };

        return $"{os}-{arch}";
    }

    private static string ExecutableName(string name)
    {
        return OperatingSystem.IsWindows() ? $"{name}.exe" : name;
    }

    private static string? TryResolveBinary(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", string.Empty }
            : new[] { string.Empty };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(dir, name + ext);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }
}
