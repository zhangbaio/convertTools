using ShortDrama.Infrastructure.Automation;

namespace TikTokPublisher.Core.Services;

public static class HongguoDeviceUdidHelper
{
    /// <param name="preferAes">
    /// true：1.4.x，读 HongGuoClient GUID；
    /// false：>=1.5.0，只读 HongGuopy 32hex（GUID 不再用于 1.5.0）。
    /// </param>
    public static bool TryReadFromRegistry(out string udid, out string message, bool preferAes = false)
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
            var value = HongguoDeviceId.TryReadFromRegistry(preferAes);
            if (string.IsNullOrWhiteSpace(value))
            {
                message = "未在注册表中找到 HongGuopy/HongGuoClient\\DeviceUDID。";
                return false;
            }

            udid = ClientSettingsStore.NormalizeUdid(value);
            message = preferAes
                ? "已从注册表读取设备唯一标识（优先 HongGuoClient）。"
                : "已从注册表读取设备唯一标识（优先 HongGuopy）。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"读取设备唯一标识失败：{ex.Message}";
            return false;
        }
    }
}
