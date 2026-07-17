using Microsoft.Win32;

namespace TikTokPublisher.Core.Services;

public static class HongguoDeviceUdidHelper
{
    public static bool TryReadFromRegistry(out string udid, out string message)
    {
        udid = "";
        message = "";

        if (!OperatingSystem.IsWindows())
        {
            message = "当前平台不支持从注册表读取设备唯一标识。";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\HongGuoClient", false);
            var value = key?.GetValue("DeviceUDID")?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                message = "未在注册表中找到设备唯一标识。";
                return false;
            }

            udid = ClientSettingsStore.NormalizeUdid(value);
            message = "已从注册表读取设备唯一标识。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"读取设备唯一标识失败：{ex.Message}";
            return false;
        }
    }
}
