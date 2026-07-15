using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ShortDrama.Infrastructure.Automation;

public sealed class HongguoMemoryReaderService
{
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const int ChunkSize = 1024 * 1024;
    private const int ChunkOverlap = 1024;
    private const long MaxReadableRegionBytes = 64L * 1024 * 1024;

    private static readonly Regex DeviceIdPattern = new(
        @"HG[0-9A-Fa-f]{16}(?![0-9A-Fa-f])",
        RegexOptions.Compiled);

    private static readonly Regex InstallIdPattern = new(
        @"(?<![A-Za-z0-9_])install_id=(?<value>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TtreqPattern = new(
        @"(?<![A-Za-z0-9_])ttreq=(?<value>1\$[0-9a-f]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OdinTtPattern = new(
        @"(?<![A-Za-z0-9_])odin_tt=(?<value>[0-9a-f]{64,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<HongguoRuntimeReadResult> ReadRuntimeAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => ReadRuntime(cancellationToken), cancellationToken);
    }

    public HongguoRuntimeReadResult ReadRuntime(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new HongguoRuntimeReadResult(null, null, "not_windows");
        }

        using var process = FindHongguoProcess();
        if (process is null)
        {
            return new HongguoRuntimeReadResult(null, null, "process_not_found");
        }

        string? cookie = null;
        string? deviceId = null;
        var exePath = TryGetProcessPath(process);
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            try
            {
                var exeBytes = File.ReadAllBytes(exePath);
                cookie = ExtractFanqieCookie(exeBytes);
                deviceId = ExtractDeviceId(exeBytes);
            }
            catch
            {
                // Some install paths are locked or require elevation; memory scan is the fallback.
            }
        }

        if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(deviceId))
        {
            ReadProcessMemory(process, cancellationToken, chunk =>
            {
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    cookie = ExtractFanqieCookie(chunk);
                }

                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    deviceId = ExtractDeviceId(chunk);
                }

                return string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(deviceId);
            });
        }

        var reason = (string.IsNullOrWhiteSpace(cookie), string.IsNullOrWhiteSpace(deviceId)) switch
        {
            (false, false) => "ok",
            (true, false) => "fanqie_cookie_not_found",
            (false, true) => "device_id_not_found",
            _ => "runtime_values_not_found"
        };

        return new HongguoRuntimeReadResult(cookie, deviceId, reason);
    }

    public static string? ExtractDeviceId(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        return DeviceIdPattern.Match(text) is { Success: true } match
            ? match.Value.ToUpperInvariant()
            : null;
    }

    public static string? ExtractFanqieCookie(byte[] bytes)
    {
        return NormalizeFanqieCookie(Encoding.Latin1.GetString(bytes));
    }

    /// <summary>
    /// Extracts the three fields required by the Fanqie search endpoint and discards
    /// Aardio string metadata/control bytes that can trail <c>odin_tt</c> in the executable.
    /// This also repairs already persisted cookies produced by older builds.
    /// </summary>
    public static string? NormalizeFanqieCookie(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var candidate in EnumerateCookieCandidates(value))
        {
            var installId = InstallIdPattern.Match(candidate);
            var ttreq = TtreqPattern.Match(candidate);
            var odinTt = OdinTtPattern.Match(candidate);
            if (installId.Success &&
                ttreq.Success &&
                odinTt.Success)
            {
                return $"install_id={installId.Groups["value"].Value}; " +
                       $"ttreq={ttreq.Groups["value"].Value}; " +
                       $"odin_tt={odinTt.Groups["value"].Value}";
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCookieCandidates(string value)
    {
        var start = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && !IsCookieCandidateBoundary(value[index]))
            {
                continue;
            }

            if (index > start)
            {
                yield return value[start..index];
            }

            start = index + 1;
        }
    }

    private static bool IsCookieCandidateBoundary(char value) =>
        char.IsControl(value) || value is '"' or '\'' or '<' or '>' or '\\';

    private static System.Diagnostics.Process? FindHongguoProcess()
    {
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            if (LooksLikeHongguoProcess(process))
            {
                return process;
            }

            process.Dispose();
        }

        return null;
    }

    private static bool LooksLikeHongguoProcess(System.Diagnostics.Process process)
    {
        try
        {
            var name = (process.ProcessName ?? string.Empty).ToLowerInvariant();
            if (IsHongguoName(name))
            {
                return true;
            }

            var title = (process.MainWindowTitle ?? string.Empty).ToLowerInvariant();
            if (IsHongguoName(title))
            {
                return true;
            }

            var path = TryGetProcessPath(process)?.ToLowerInvariant() ?? string.Empty;
            return IsHongguoName(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHongguoName(string value)
    {
        return value.Contains("hongguo", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("hgdownload", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("hglocal", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("红果", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetProcessPath(System.Diagnostics.Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static void ReadProcessMemory(
        System.Diagnostics.Process process,
        CancellationToken cancellationToken,
        Func<byte[], bool> inspectChunk)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var address = IntPtr.Zero;
            var mbiSize = (UIntPtr)Marshal.SizeOf<MemoryBasicInformation>();
            while (!cancellationToken.IsCancellationRequested &&
                   VirtualQueryEx(handle, address, out var info, mbiSize) != UIntPtr.Zero)
            {
                var regionSize = checked((long)info.RegionSize.ToUInt64());
                if (regionSize > 0 &&
                    regionSize <= MaxReadableRegionBytes &&
                    info.State == MemCommit &&
                    IsReadableProtection(info.Protect))
                {
                    if (!ReadRegion(handle, info.BaseAddress, regionSize, inspectChunk))
                    {
                        break;
                    }
                }

                var next = info.BaseAddress.ToInt64() + regionSize;
                if (next <= address.ToInt64())
                {
                    break;
                }

                address = new IntPtr(next);
            }
        }
        catch
        {
            // Best-effort runtime extraction; callers surface a friendly missing-value reason.
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool ReadRegion(
        IntPtr processHandle,
        IntPtr baseAddress,
        long regionSize,
        Func<byte[], bool> inspectChunk)
    {
        var carry = Array.Empty<byte>();
        for (var offset = 0L; offset < regionSize; offset += ChunkSize)
        {
            var length = (int)Math.Min(ChunkSize, regionSize - offset);
            var buffer = new byte[length];
            if (!ReadProcessMemory(processHandle, IntPtr.Add(baseAddress, checked((int)offset)), buffer, length, out var bytesRead) ||
                bytesRead <= 0)
            {
                continue;
            }

            var chunk = CombineTail(carry, buffer, bytesRead);
            if (!inspectChunk(chunk))
            {
                return false;
            }

            carry = chunk.Length <= ChunkOverlap
                ? chunk
                : chunk[^ChunkOverlap..];
        }

        return true;
    }

    private static byte[] CombineTail(byte[] carry, byte[] buffer, int bytesRead)
    {
        if (carry.Length == 0 && bytesRead == buffer.Length)
        {
            return buffer;
        }

        var combined = new byte[carry.Length + bytesRead];
        Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
        Buffer.BlockCopy(buffer, 0, combined, carry.Length, bytesRead);
        return combined;
    }

    private static bool IsReadableProtection(uint protection)
    {
        if ((protection & PageGuard) != 0 || (protection & PageNoAccess) != 0)
        {
            return false;
        }

        return protection is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        out MemoryBasicInformation lpBuffer,
        UIntPtr dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesRead);
}

public sealed record HongguoRuntimeReadResult(string? FanqieCookie, string? DeviceId, string Reason)
{
    public bool HasAnyValue => !string.IsNullOrWhiteSpace(FanqieCookie) || !string.IsNullOrWhiteSpace(DeviceId);
}
