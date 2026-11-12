namespace TikTokPublisher.Core.Services.VideoDedup;

internal static class VideoDedupHardwareOrder
{
    internal readonly record struct Vendors(bool Nvidia, bool Amd, bool Intel);

    public static IReadOnlyList<string> Encoders(bool hevc, Vendors vendors, bool mac)
    {
        var list = new List<string>(4);
        if (vendors.Nvidia)
            Add(hevc ? "hevc_nvenc" : "h264_nvenc");
        if (vendors.Amd)
            Add(hevc ? "hevc_amf" : "h264_amf");
        if (vendors.Intel)
            Add(hevc ? "hevc_qsv" : "h264_qsv");
        Add(hevc ? "hevc_nvenc" : "h264_nvenc");
        Add(hevc ? "hevc_qsv" : "h264_qsv");
        Add(hevc ? "hevc_amf" : "h264_amf");
        if (mac)
            Add(hevc ? "hevc_videotoolbox" : "h264_videotoolbox");
        return list;

        void Add(string encoder)
        {
            if (!list.Contains(encoder, StringComparer.Ordinal))
                list.Add(encoder);
        }
    }

    public static Vendors Detect()
    {
        if (!OperatingSystem.IsWindows())
            return default;

        try
        {
            return ReadWindowsAdapters();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return default;
        }
    }

    private static Vendors ReadWindowsAdapters()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (key is null)
            return default;

        var nvidia = false;
        var amd = false;
        var intel = false;
        foreach (var name in key.GetSubKeyNames())
        {
            if (name.Length != 4 || !name.All(static digit => digit is >= '0' and <= '9'))
                continue;
            using var adapter = key.OpenSubKey(name);
            if (adapter is null)
                continue;
            var text = $"{adapter.GetValue("MatchingDeviceId")} {adapter.GetValue("DriverDesc")}";
            if (ContainsVendor(text, "VEN_10DE", "NVIDIA", "GeForce", "Quadro"))
                nvidia = true;
            else if (ContainsVendor(text, "VEN_1002", "AMD", "Radeon", "ATI"))
                amd = true;
            else if (ContainsVendor(text, "VEN_8086", "Intel", "Arc", "Iris", "UHD"))
                intel = true;
        }

        return new Vendors(nvidia, amd, intel);
    }

    private static bool ContainsVendor(string text, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
