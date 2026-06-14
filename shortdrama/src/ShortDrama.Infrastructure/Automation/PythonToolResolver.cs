using ShortDrama.Core.Interfaces;

namespace ShortDrama.Infrastructure.Automation;

public sealed class PythonToolResolver
{
    private readonly IExternalProcessRunner _processRunner;

    public PythonToolResolver(IExternalProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<PythonCommand> ResolvePythonCommandAsync(CancellationToken cancellationToken)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                new PythonCommand("py", ["-3"]),
                new PythonCommand("python", []),
                new PythonCommand("python3", [])
            }
            : new[]
            {
                new PythonCommand("python3", []),
                new PythonCommand("python", [])
            };

        foreach (var candidate in candidates)
        {
            try
            {
                var result = await _processRunner.RunAsync(
                    candidate.FileName,
                    [.. candidate.PrefixArguments, "--version"],
                    workingDirectory: null,
                    cancellationToken);
                if (result.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore failed probes and continue.
            }
        }

        throw new InvalidOperationException("未找到可用的 Python 解释器，请安装 python3 或 python。");
    }

    public string ResolveRepositoryToolDirectory(string toolDirectoryName)
    {
        foreach (var root in EnumerateSearchRoots())
        {
            var candidate = Path.Combine(root, toolDirectoryName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"未找到工具目录: {toolDirectoryName}");
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
}

public sealed record PythonCommand(
    string FileName,
    IReadOnlyList<string> PrefixArguments);
