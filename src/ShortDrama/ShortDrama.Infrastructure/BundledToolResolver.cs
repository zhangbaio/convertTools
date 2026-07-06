namespace ShortDrama.Infrastructure;

public static class BundledToolResolver
{
    public static string? TryResolveBinary(string name)
    {
        foreach (var candidate in EnumerateBinaryCandidates(name))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? TryResolvePython()
    {
        foreach (var root in EnumerateSearchRoots())
        {
            foreach (var runtimeFolder in EnumerateRuntimeFolders())
            {
                foreach (var candidate in new[]
                         {
                             Path.Combine(root, "tools", runtimeFolder, "python", ExecutableName("python")),
                             Path.Combine(root, "tools", runtimeFolder, "python", ExecutableName("python3")),
                         })
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "tools", "python", ExecutableName("python")),
                         Path.Combine(root, "tools", "python", ExecutableName("python3")),
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start))
                continue;

            var current = Path.GetFullPath(start);
            while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
            {
                yield return current;
                var parent = Directory.GetParent(current);
                if (parent is null)
                    break;

                current = parent.FullName;
            }
        }
    }

    private static IEnumerable<string> EnumerateBinaryCandidates(string name)
    {
        var fileName = ExecutableName(name);
        foreach (var dir in EnumeratePathDirectories())
            yield return Path.Combine(dir, fileName);

        foreach (var root in EnumerateSearchRoots())
        {
            yield return Path.Combine(root, fileName);
            foreach (var runtimeFolder in EnumerateRuntimeFolders())
            {
                yield return Path.Combine(root, "tools", runtimeFolder, name, fileName);
                yield return Path.Combine(root, "tools", runtimeFolder, name, "bin", fileName);

                if (name is "ffmpeg" or "ffprobe")
                {
                    yield return Path.Combine(root, "tools", runtimeFolder, "ffmpeg", fileName);
                    yield return Path.Combine(root, "tools", runtimeFolder, "ffmpeg", "bin", fileName);
                }
            }

            yield return Path.Combine(root, "tools", name, fileName);
            yield return Path.Combine(root, "tools", name, "bin", fileName);
        }
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            yield break;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                yield return trimmed;
        }
    }

    private static string ExecutableName(string name) =>
        OperatingSystem.IsWindows() && !Path.HasExtension(name) ? $"{name}.exe" : name;

    private static IEnumerable<string> EnumerateRuntimeFolders()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (OperatingSystem.IsWindows())
        {
            yield return $"win-{arch}";
            yield return $"windows-{arch}";
            yield break;
        }

        var os = OperatingSystem.IsMacOS() ? "osx" : "linux";
        yield return $"{os}-{arch}";
    }
}
