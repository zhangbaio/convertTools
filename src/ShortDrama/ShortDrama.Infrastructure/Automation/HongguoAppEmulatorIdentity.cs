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
    internal readonly record struct DeviceProfile(string Model, string Brand, string OsApi, string OsVersion)
    {
        internal static DeviceProfile Installed { get; } = new("sdk_gphone64_arm64", "google", "33", "13");
    }

    private static readonly object Gate = new();
    private static (string DeviceId, string InstallId)? Cached;
    private static DeviceProfile? CachedProfile;

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

    internal static DeviceProfile CurrentProfile()
    {
        lock (Gate)
        {
            if (CachedProfile is { } saved && IsProfile(saved))
                return saved;
        }

        try
        {
            var adb = AdbPath();
            var serial = adb is null ? null : EmulatorSerial(adb);
            if (adb is not null && serial is not null)
            {
                var text = Run(adb, ["-s", serial, "shell", "getprop"], 4000);
                if (TryParseProfile(text, out var profile))
                {
                    lock (Gate)
                        CachedProfile = profile;
                    return profile;
                }
            }
        }
        catch
        {
            // 读不到机型时用这台模拟器已经验证过的默认值。
        }

        return DeviceProfile.Installed;
    }

    internal static bool TryParseProfile(string? text, out DeviceProfile profile)
    {
        profile = DeviceProfile.Installed;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var open = line.IndexOf('[');
            var close = line.IndexOf(']');
            var second = close < 0 ? -1 : line.IndexOf('[', close + 1);
            var end = second < 0 ? -1 : line.IndexOf(']', second + 1);
            if (open < 0 || close <= open || second < 0 || end <= second)
                continue;
            props[line[(open + 1)..close]] = line[(second + 1)..end].Trim();
        }

        if (!props.TryGetValue("ro.product.model", out var model) ||
            !props.TryGetValue("ro.product.brand", out var brand) ||
            !props.TryGetValue("ro.build.version.sdk", out var api) ||
            !props.TryGetValue("ro.build.version.release", out var release) ||
            !IsToken(model) || !IsToken(brand) || !IsToken(api) || !IsToken(release))
            return false;
        profile = new DeviceProfile(model, brand, api, release);
        return true;
    }

    private static bool IsProfile(DeviceProfile profile) =>
        IsToken(profile.Model) && IsToken(profile.Brand) && IsToken(profile.OsApi) && IsToken(profile.OsVersion);

    private static bool IsToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
            return false;
        foreach (var ch in value)
        {
            if (ch is '&' or '=' or '?' or '#' or '\r' or '\n')
                return false;
        }

        return true;
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
