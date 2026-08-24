using Microsoft.Win32;

namespace ShortDrama.Infrastructure.Automation;

public static class MapleleafDeviceStore
{
    private const string RegistryValue = "DeviceUDID";
    private static readonly string[] RegistryPaths =
    [
        @"Software\HongGuoClient",
        @"Software\WOW6432Node\HongGuoClient"
    ];

    public static string GenerateDeviceId() => Guid.NewGuid().ToString();

    public static string TryReadDeviceId()
    {
        if (!OperatingSystem.IsWindows())
            return string.Empty;

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var path in RegistryPaths)
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    var value = key?.GetValue(RegistryValue)?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // Try the next hive/view. HKLM commonly requires more permissions.
                }
            }
        }

        return string.Empty;
    }
}
