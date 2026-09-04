using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using DiagProcess = System.Diagnostics.Process;

namespace ShortDrama.Infrastructure.Automation;

public static class HongguoHighDeviceStore
{
    public static string? CacheDirectoryOverride { get; set; }

    public static string CacheDirectory =>
        string.IsNullOrWhiteSpace(CacheDirectoryOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HongguoHighClient")
            : CacheDirectoryOverride;

    public static string MastersCachePath => Path.Combine(CacheDirectory, "startup_masters.json");

    public static string GetCacheDirectory(HongguoClientProfile profile)
    {
        _ = profile;
        return CacheDirectory;
    }

    public static string GetMastersCachePath(HongguoClientProfile profile) =>
        Path.Combine(GetCacheDirectory(profile), "startup_masters.json");

    public static string GenerateDeviceId() => HongguoHighCrypto.GenerateDeviceId();

    public static HongguoHighDevice DetectDevice() => DetectDevice(HongguoClientProfile.High);

    public static HongguoHighDevice DetectDevice(HongguoClientProfile profile)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new HongguoHighException($"{profile.DisplayName}设备身份仅支持 Windows");
        }

        return DetectDeviceWindows(profile);
    }

    public static string TryReadDeviceId() => TryReadDeviceId(HongguoClientProfile.High);

    public static string TryReadDeviceId(HongguoClientProfile profile)
    {
        try
        {
            using var device = DetectDevice(profile);
            return device.DeviceId;
        }
        catch
        {
            return "";
        }
    }

    public static bool IsReady() => IsReady(HongguoClientProfile.High);

    public static bool IsReady(HongguoClientProfile profile)
    {
        try
        {
            using var device = DetectDevice(profile);
            LoadStartupMasters(device, profile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string CacheStartupMasters(string encB64, string signB64, string? deviceId = null) =>
        CacheStartupMasters(encB64, signB64, deviceId, HongguoClientProfile.High);

    public static string CacheStartupMasters(
        string encB64,
        string signB64,
        string? deviceId,
        HongguoClientProfile profile)
    {
        _ = deviceId;
        var enc = Compact(encB64);
        var sign = Compact(signB64);
        if (HongguoHighCrypto.FromBase64Url(enc).Length != 48 ||
            HongguoHighCrypto.FromBase64Url(sign).Length != 48)
        {
            throw new HongguoHighException("启动 master 必须是 48 字节值的 base64url");
        }

        var payload = new JsonObject
        {
            // The two official products use the same startup masters. They are not
            // device credentials, so keep one DPAPI-protected cache for both editions.
            ["device_id"] = "",
            ["enc"] = enc,
            ["sign"] = sign
        };
        var directory = GetCacheDirectory(profile);
        var path = GetMastersCachePath(profile);
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, Protect(payload.ToJsonString(), HongguoClientProfile.High), Encoding.UTF8);
        return path;
    }

    public static void ClearStartupMasters() => ClearStartupMasters(HongguoClientProfile.High);

    public static void ClearStartupMasters(HongguoClientProfile profile)
    {
        try
        {
            var path = GetMastersCachePath(profile);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            throw new HongguoHighException($"清除启动密钥缓存失败：{ex.Message}", inner: ex);
        }
    }

    public static (string Enc, string Sign) LoadStartupMastersRaw() =>
        LoadStartupMastersRaw(HongguoClientProfile.High);

    public static (string Enc, string Sign) LoadStartupMastersRaw(HongguoClientProfile profile)
    {
        try
        {
            var record = ReadMastersRecord(profile);
            if (record is null)
            {
                return ("", "");
            }

            return (Compact(record["enc"]?.GetValue<string>()), Compact(record["sign"]?.GetValue<string>()));
        }
        catch
        {
            return ("", "");
        }
    }

    public static (string Enc, string Sign) LoadStartupMasters(HongguoHighDevice device) =>
        LoadStartupMasters(device, HongguoClientProfile.High);

    public static (string Enc, string Sign) LoadStartupMasters(
        HongguoHighDevice device,
        HongguoClientProfile profile)
    {
        var envEnc = Compact(Environment.GetEnvironmentVariable(profile.EnvironmentPrefix + "_STARTUP_ENC"));
        var envSign = Compact(Environment.GetEnvironmentVariable(profile.EnvironmentPrefix + "_STARTUP_SIGN"));
        if (!string.IsNullOrWhiteSpace(envEnc) && !string.IsNullOrWhiteSpace(envSign))
        {
            return (envEnc, envSign);
        }

        var record = ReadMastersRecord(profile);
        if (record is not null)
        {
            var enc = Compact(record["enc"]?.GetValue<string>());
            var sign = Compact(record["sign"]?.GetValue<string>());
            if (!string.IsNullOrWhiteSpace(enc) &&
                !string.IsNullOrWhiteSpace(sign))
            {
                return (enc, sign);
            }
        }

        throw new HongguoHighException(
            $"未找到本设备的启动密钥。请在「系统设置 → 登录设置」选择官方{profile.DisplayName}客户端后点击「提取启动密钥」。");
    }

    public static string ResolveDeviceProof(HongguoHighDevice device) =>
        ResolveDeviceProof(device, HongguoClientProfile.High);

    public static string ResolveDeviceProof(HongguoHighDevice device, HongguoClientProfile profile)
    {
        var env = (Environment.GetEnvironmentVariable(profile.EnvironmentPrefix + "_DEVICE_PROOF") ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var record = ReadMastersRecord(profile);
            var cachedDevice = (record?["device_id"]?.GetValue<string>() ?? "").Trim();
            var cachedProof = (record?["device_proof"]?.GetValue<string>() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(cachedProof) &&
                (string.IsNullOrWhiteSpace(cachedDevice) || cachedDevice == device.DeviceId))
            {
                return cachedProof;
            }
        }
        catch
        {
            // Fall through to hardware proof.
        }

        return ComputeHardwareDeviceProof(device, profile);
    }

    public static string? FindOfficialClientExe(string? configuredPath = null) =>
        FindOfficialClientExe(configuredPath, HongguoClientProfile.High);

    public static string? FindOfficialClientExe(string? configuredPath, HongguoClientProfile profile)
    {
        var configured = (configuredPath ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApp))
        {
            return null;
        }

        var folder = Path.Combine(localApp, profile.LocalDataDirectory);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(folder, "HG*.exe", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string ComputeHardwareDeviceProof(HongguoHighDevice device) =>
        ComputeHardwareDeviceProof(device, HongguoClientProfile.High);

    public static string ComputeHardwareDeviceProof(HongguoHighDevice device, HongguoClientProfile profile)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "";
        }

        string machineGuid;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machineGuid = (key?.GetValue("MachineGuid") as string ?? "").Trim();
        }
        catch
        {
            machineGuid = "";
        }

        var csproductUuid = (CimValue("Win32_ComputerSystemProduct", "UUID") ?? "").ToLowerInvariant();
        var baseboardSerial = CimValue("Win32_BaseBoard", "SerialNumber") ?? "";
        var processorId = (CimValue("Win32_Processor", "ProcessorId") ?? "").ToLowerInvariant();
        var diskSerial = SystemDiskSerial().ToLowerInvariant();
        var volumeSerial = SystemVolumeSerial();
        if (string.IsNullOrWhiteSpace(machineGuid) || string.IsNullOrWhiteSpace(csproductUuid))
        {
            return "";
        }

        var block =
            "device-proof-v2\n" +
            $"app_id={profile.AppId}\n" +
            $"device_id={device.DeviceId}\n" +
            $"machine_guid={machineGuid}\n" +
            $"csproduct_uuid={csproductUuid}\n" +
            $"baseboard_serial={baseboardSerial}\n" +
            $"processor_id={processorId}\n" +
            $"system_disk_serial={diskSerial}\n" +
            $"system_volume_serial={volumeSerial}\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(block))).ToLowerInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static HongguoHighDevice DetectDeviceWindows(HongguoClientProfile profile)
    {
        string deviceRaw = "";
        string keyRaw = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(profile.RegistryKey);
            if (key is null)
            {
                throw new HongguoHighException($@"未找到{profile.DisplayName}注册表 HKCU\{profile.RegistryKey}");
            }

            deviceRaw = key.GetValue("DeviceId") as string ?? "";
            keyRaw = key.GetValue("DeviceKey") as string ?? "";
        }
        catch (HongguoHighException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HongguoHighException($@"未找到{profile.DisplayName}注册表 HKCU\{profile.RegistryKey}", inner: ex);
        }

        if (string.IsNullOrWhiteSpace(keyRaw))
        {
            var keyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                profile.LocalDataDirectory,
                "device.key");
            if (File.Exists(keyPath))
            {
                keyRaw = File.ReadAllText(keyPath);
            }
        }

        if (string.IsNullOrWhiteSpace(deviceRaw) || string.IsNullOrWhiteSpace(keyRaw))
        {
            throw new HongguoHighException($"{profile.DisplayName} DeviceId / DeviceKey 缺失，请先运行并登录官方 HG 短剧下载器{profile.DisplayName}");
        }

        var deviceId = Encoding.UTF8.GetString(Unprotect(deviceRaw, profile)).Trim();
        var keyText = Encoding.UTF8.GetString(Unprotect(keyRaw, profile));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new HongguoHighException($"{profile.DisplayName} DeviceId 为空");
        }

        return new HongguoHighDevice(deviceId, HongguoHighCrypto.ParseDeviceKeyText(keyText));
    }

    private static JsonObject? ReadMastersRecord(HongguoClientProfile profile)
    {
        var path = GetMastersCachePath(profile);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = Encoding.UTF8.GetString(Unprotect(File.ReadAllText(path), HongguoClientProfile.High));
        return JsonNode.Parse(json)?.AsObject();
    }

    private static string Protect(string plaintext, HongguoClientProfile profile)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            profile.DpapiEntropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static byte[] Unprotect(string raw, HongguoClientProfile profile)
    {
        var text = (raw ?? "").Trim();
        if (text.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..].Trim();
        }

        var blob = Convert.FromBase64String(text);
        if (!OperatingSystem.IsWindows())
        {
            return blob;
        }

        try
        {
            return ProtectedData.Unprotect(blob, profile.DpapiEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new HongguoHighException("DPAPI 解密失败", inner: ex);
        }
    }

    private static string Compact(string? value) =>
        string.Concat((value ?? "").Where(ch => !char.IsWhiteSpace(ch)));

    private static string? CimValue(string className, string propertyName)
    {
        return PowerShellValue(
            $"Get-CimInstance {className} | Select-Object -First 1 -ExpandProperty {propertyName}");
    }

    private static string SystemDiskSerial()
    {
        var drive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd(':');
        var value = PowerShellValue(
            $"$n=(Get-Partition -DriveLetter {drive} -ErrorAction SilentlyContinue).DiskNumber; " +
            "if ($null -ne $n) { (Get-CimInstance Win32_DiskDrive -Filter \"Index=$n\" | Select-Object -First 1 -ExpandProperty SerialNumber) }");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = CimValue("Win32_DiskDrive -Filter 'Index=0'", "SerialNumber");
        }

        return (value ?? "").Trim();
    }

    private static string SystemVolumeSerial()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "";
        }

        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";
        if (!GetVolumeInformation(systemDrive, null, 0, out var serial, out _, out _, null, 0))
        {
            return "";
        }

        return serial.ToString("x8");
    }

    private static string? PowerShellValue(string script)
    {
        try
        {
            using var process = new DiagProcess
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(25000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
