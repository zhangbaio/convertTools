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

    public static string GenerateDeviceId() => HongguoHighCrypto.GenerateDeviceId();

    public static HongguoHighDevice DetectDevice()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new HongguoHighException("高码率版设备身份仅支持 Windows");
        }

        return DetectDeviceWindows();
    }

    public static string TryReadDeviceId()
    {
        try
        {
            using var device = DetectDevice();
            return device.DeviceId;
        }
        catch
        {
            return "";
        }
    }

    public static bool IsReady()
    {
        try
        {
            using var device = DetectDevice();
            LoadStartupMasters(device);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string CacheStartupMasters(string encB64, string signB64, string? deviceId = null)
    {
        var enc = Compact(encB64);
        var sign = Compact(signB64);
        if (HongguoHighCrypto.FromBase64Url(enc).Length != 48 ||
            HongguoHighCrypto.FromBase64Url(sign).Length != 48)
        {
            throw new HongguoHighException("启动 master 必须是 48 字节值的 base64url");
        }

        var payload = new JsonObject
        {
            ["device_id"] = deviceId ?? "",
            ["enc"] = enc,
            ["sign"] = sign
        };
        Directory.CreateDirectory(CacheDirectory);
        File.WriteAllText(MastersCachePath, Protect(payload.ToJsonString()), Encoding.UTF8);
        return MastersCachePath;
    }

    public static void ClearStartupMasters()
    {
        try
        {
            if (File.Exists(MastersCachePath))
            {
                File.Delete(MastersCachePath);
            }
        }
        catch (Exception ex)
        {
            throw new HongguoHighException($"清除启动密钥缓存失败：{ex.Message}", inner: ex);
        }
    }

    public static (string Enc, string Sign) LoadStartupMastersRaw()
    {
        try
        {
            var record = ReadMastersRecord();
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

    public static (string Enc, string Sign) LoadStartupMasters(HongguoHighDevice device)
    {
        var envEnc = Compact(Environment.GetEnvironmentVariable("HGHIGH_STARTUP_ENC"));
        var envSign = Compact(Environment.GetEnvironmentVariable("HGHIGH_STARTUP_SIGN"));
        if (!string.IsNullOrWhiteSpace(envEnc) && !string.IsNullOrWhiteSpace(envSign))
        {
            return (envEnc, envSign);
        }

        var record = ReadMastersRecord();
        if (record is not null)
        {
            var enc = Compact(record["enc"]?.GetValue<string>());
            var sign = Compact(record["sign"]?.GetValue<string>());
            var cachedDevice = (record["device_id"]?.GetValue<string>() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(enc) &&
                !string.IsNullOrWhiteSpace(sign) &&
                (string.IsNullOrWhiteSpace(cachedDevice) || cachedDevice == device.DeviceId))
            {
                return (enc, sign);
            }
        }

        throw new HongguoHighException(
            "未找到本设备的启动密钥。请在「系统设置 → 登录设置」选择官方高码率客户端后点击「提取启动密钥」。");
    }

    public static string ResolveDeviceProof(HongguoHighDevice device)
    {
        var env = (Environment.GetEnvironmentVariable("HGHIGH_DEVICE_PROOF") ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var record = ReadMastersRecord();
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

        return ComputeHardwareDeviceProof(device);
    }

    public static string? FindOfficialClientExe(string? configuredPath = null)
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

        var folder = Path.Combine(localApp, "HongguoHighDownloader");
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.EnumerateFiles(folder, "HG*.exe", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string ComputeHardwareDeviceProof(HongguoHighDevice device)
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
            $"app_id={HongguoHighCrypto.AppId}\n" +
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
    private static HongguoHighDevice DetectDeviceWindows()
    {
        string deviceRaw = "";
        string keyRaw = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HongguoHighCrypto.RegistryKey);
            if (key is null)
            {
                throw new HongguoHighException(@"未找到高码率版注册表 HKCU\Software\HongGuoHighDownloader");
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
            throw new HongguoHighException(@"未找到高码率版注册表 HKCU\Software\HongGuoHighDownloader", inner: ex);
        }

        if (string.IsNullOrWhiteSpace(keyRaw))
        {
            var keyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HongguoHighDownloader",
                "device.key");
            if (File.Exists(keyPath))
            {
                keyRaw = File.ReadAllText(keyPath);
            }
        }

        if (string.IsNullOrWhiteSpace(deviceRaw) || string.IsNullOrWhiteSpace(keyRaw))
        {
            throw new HongguoHighException("高码率版 DeviceId / DeviceKey 缺失，请先运行并登录官方 HG 短剧下载器高码率版");
        }

        var deviceId = Encoding.UTF8.GetString(Unprotect(deviceRaw)).Trim();
        var keyText = Encoding.UTF8.GetString(Unprotect(keyRaw));
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new HongguoHighException("高码率版 DeviceId 为空");
        }

        return new HongguoHighDevice(deviceId, HongguoHighCrypto.ParseDeviceKeyText(keyText));
    }

    private static JsonObject? ReadMastersRecord()
    {
        if (!File.Exists(MastersCachePath))
        {
            return null;
        }

        var json = Encoding.UTF8.GetString(Unprotect(File.ReadAllText(MastersCachePath)));
        return JsonNode.Parse(json)?.AsObject();
    }

    private static string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            HongguoHighCrypto.DpapiEntropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static byte[] Unprotect(string raw)
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
            return ProtectedData.Unprotect(blob, HongguoHighCrypto.DpapiEntropy, DataProtectionScope.CurrentUser);
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
