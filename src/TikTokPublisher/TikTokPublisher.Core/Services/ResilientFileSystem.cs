namespace TikTokPublisher.Core.Services;

internal static class ResilientFileSystem
{
    private const int MaxAttempts = 8;

    internal static void EnsureDirectory(string path) =>
        Retry(
            () => Directory.CreateDirectory(path),
            $"无法创建目录：{path}");

    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        Retry(
            () =>
            {
                if (!Directory.Exists(path)) return;
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
            },
            $"无法删除目录：{path}");
    }

    internal static bool TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try { DeleteDirectory(path); return true; }
        catch { return false; }
    }

    internal static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        Retry(
            () =>
            {
                if (!File.Exists(path)) return;
                ClearReadOnlyAttribute(path);
                File.Delete(path);
            },
            $"无法删除文件：{path}");
    }

    internal static bool TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try { DeleteFile(path); return true; }
        catch { return false; }
    }

    internal static void MoveDirectory(string source, string destination)
    {
        Retry(
            () => Directory.Move(source, destination),
            $"无法移动目录：{source} -> {destination}");
    }

    internal static void MoveFile(string source, string destination, bool overwrite = true)
    {
        Retry(
            () =>
            {
                if (overwrite && File.Exists(destination)) ClearReadOnlyAttribute(destination);
                File.Move(source, destination, overwrite);
            },
            $"无法移动文件：{source} -> {destination}");
    }

    internal static void CopyFile(string source, string destination, bool overwrite = true)
    {
        Retry(
            () =>
            {
                if (overwrite && File.Exists(destination)) ClearReadOnlyAttribute(destination);
                File.Copy(source, destination, overwrite);
            },
            $"无法复制文件：{source} -> {destination}");
    }

    internal static void DeleteEntry(string path)
    {
        if (Directory.Exists(path)) DeleteDirectory(path);
        else if (File.Exists(path)) DeleteFile(path);
    }

    private static void Retry(Action action, string message)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt + 1 < MaxAttempts)
                    Thread.Sleep(80 * (attempt + 1));
            }
        }
        throw new IOException(message, lastError);
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                         .Prepend(path))
                ClearReadOnlyAttribute(entry);
        }
        catch { }
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch { }
    }
}
