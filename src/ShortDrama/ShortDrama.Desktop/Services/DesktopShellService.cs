using System.Diagnostics;

namespace ShortDrama.Desktop.Services;

public sealed class DesktopShellService
{
    public bool TryOpenPath(string path, out string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            message = "路径为空。";
            return false;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            message = $"路径不存在: {path}";
            return false;
        }

        try
        {
            StartOpenProcess(path, reveal: false);
            message = $"已打开: {path}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"打开失败: {ex.Message}";
            return false;
        }
    }

    public bool TryRevealPath(string path, out string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            message = "路径为空。";
            return false;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            message = $"路径不存在: {path}";
            return false;
        }

        try
        {
            StartOpenProcess(path, reveal: true);
            message = $"已定位: {path}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"定位失败: {ex.Message}";
            return false;
        }
    }

    private static void StartOpenProcess(string path, bool reveal)
    {
        ProcessStartInfo startInfo;

        if (OperatingSystem.IsMacOS())
        {
            startInfo = new ProcessStartInfo("open");
            if (reveal && File.Exists(path))
            {
                startInfo.ArgumentList.Add("-R");
            }

            startInfo.ArgumentList.Add(path);
        }
        else if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo("explorer.exe");
            startInfo.Arguments = reveal && File.Exists(path)
                ? $"/select,\"{path}\""
                : $"\"{path}\"";
        }
        else
        {
            var target = reveal && File.Exists(path)
                ? Path.GetDirectoryName(path) ?? path
                : path;
            startInfo = new ProcessStartInfo("xdg-open");
            startInfo.ArgumentList.Add(target);
        }

        startInfo.UseShellExecute = false;
        Process.Start(startInfo);
    }
}
