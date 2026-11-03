using System.Xml.Linq;
using DiagProcess = System.Diagnostics.Process;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// 本机模拟器里红果已经注册的 device_id 和 install_id。
/// 播放接口只接受这对真实身份，本地随机号和新注册号都会拿到空包。
/// </summary>
internal static class HongguoAppEmulatorIdentity
{
    private const string PrefsPath = "/data/data/com.phoenix.read/shared_prefs/applog_stats.xml";
    private static readonly object Gate = new();
    private static (string DeviceId, string InstallId)? Cached;

    internal static bool TryRead(out string deviceId, out string installId)
    {
        lock (Gate)
        {
            if (Cached is { } saved && IsPair(saved.DeviceId, saved.InstallId))
            {
                deviceId = saved.DeviceId;
                installId = saved.InstallId;
                return true;
            }

            try
            {
                var adb = AdbPath();
                var serial = adb is null ? null : EmulatorSerial(adb);
                if (adb is not null && serial is not null)
                {
                    var xml = Run(adb, ["-s", serial, "exec-out", "cat", PrefsPath], 4000);
                    if (TryParse(xml, out deviceId, out installId))
                    {
                        Cached = (deviceId, installId);
                        return true;
                    }
                }
            }
            catch
            {
                // 模拟器不在时改用已经保存的身份。
            }
        }

        deviceId = "";
        installId = "";
        return false;
    }

    internal static bool TryParse(string? xml, out string deviceId, out string installId)
    {
        deviceId = "";
        installId = "";
        if (string.IsNullOrWhiteSpace(xml))
            return false;
        try
        {
            var root = XDocument.Parse(xml);
            foreach (var node in root.Descendants("string"))
            {
                var name = (string?)node.Attribute("name") ?? "";
                var text = (node.Value ?? "").Trim();
                if (name == "device_id")
                    deviceId = text;
                else if (name == "install_id")
                    installId = text;
            }
        }
        catch
        {
            return false;
        }

        return IsPair(deviceId, installId);
    }

    private static bool IsPair(string deviceId, string installId) =>
        HongguoGuestPlayback.IsSavedIdentity(deviceId, installId);

    private static string? AdbPath()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Android", "sdk"),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var adb = Path.Combine(root, "platform-tools", OperatingSystem.IsWindows() ? "adb.exe" : "adb");
            if (File.Exists(adb))
                return adb;
        }

        return null;
    }

    private static string? EmulatorSerial(string adb)
    {
        var output = Run(adb, ["devices"], 3000);
        string? first = null;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[1], "device", StringComparison.Ordinal))
                continue;
            if (!parts[0].StartsWith("emulator-", StringComparison.Ordinal))
                continue;
            first ??= parts[0];
            if (string.Equals(parts[0], "emulator-5554", StringComparison.Ordinal))
                return parts[0];
        }

        return first;
    }

    private static string Run(string file, string[] args, int timeoutMs)
    {
        var start = new System.Diagnostics.ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = DiagProcess.Start(start);
        if (process is null)
            return "";
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // 超时后的进程清理失败不影响下一次读取。
            }

            return "";
        }

        _ = stderr.GetAwaiter().GetResult();
        return stdout.GetAwaiter().GetResult();
    }
}
