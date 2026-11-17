using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ShortDrama.Infrastructure.Automation;

/// <summary>
/// DramaBox v1.5.9 的上游请求走 libcurl-impersonate 2.2.3，画像是 safari184，协议是 HTTP/1.1。
/// .NET 和 Python urllib 的 TLS 指纹会被 sapi 直接断开。
/// </summary>
internal static class DramaBoxLibcurl
{
    private const int CurlGlobalAll = 3;
    private const int CurlOptUrl = 10002;
    private const int CurlOptErrorBuffer = 10010;
    private const int CurlOptPostFields = 10015;
    private const int CurlOptHttpHeader = 10023;
    private const int CurlOptCustomRequest = 10036;
    private const int CurlOptTimeout = 13;
    private const int CurlOptPostFieldSize = 60;
    private const int CurlOptAcceptEncoding = 10102;
    private const int CurlOptDnsServers = 10211;
    private const int CurlOptHttpVersion = 84;
    private const int CurlHttpVersion11 = 2;
    private const int CurlInfoResponseCode = 2097154;

    private static readonly object Gate = new();
    private static IntPtr _library;
    private static string? _loadedPath;
    private static bool _initialized;
    private static GlobalInitDelegate? _globalInit;
    private static EasyInitDelegate? _easyInit;
    private static EasyCleanupDelegate? _easyCleanup;
    private static EasyImpersonateDelegate? _easyImpersonate;
    private static EasyPerformDelegate? _easyPerform;
    private static EasyStrErrorDelegate? _easyStrError;
    private static EasyGetInfoDelegate? _easyGetInfo;
    private static EasySetoptDelegate? _setopt;
    private static SlistAppendDelegate? _slistAppend;
    private static SlistFreeDelegate? _slistFree;
    private static readonly WriteCallback WriteThunk = WriteBody;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GlobalInitDelegate(int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr EasyInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void EasyCleanupDelegate(IntPtr easy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EasyImpersonateDelegate(IntPtr easy, IntPtr target, int defaultHeaders);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EasyPerformDelegate(IntPtr easy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr EasyStrErrorDelegate(int code);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EasyGetInfoDelegate(IntPtr easy, int info, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EasySetoptDelegate(IntPtr easy, int option, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SlistAppendDelegate(IntPtr list, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SlistFreeDelegate(IntPtr list);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint WriteCallback(IntPtr data, nuint size, nuint nmemb, IntPtr userdata);

    public static string? Find()
    {
        foreach (var directory in CandidateDirectories())
        {
            var path = Path.Combine(directory, "libcurl-impersonate.dll");
            if (File.Exists(path))
                return path;
        }

        var configured = Environment.GetEnvironmentVariable("DRAMABOX_CURL");
        if (!string.IsNullOrWhiteSpace(configured) &&
            File.Exists(configured) &&
            configured.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return configured;

        return null;
    }

    public static DramaBoxApiService.HttpText SendText(
        string libraryPath,
        HttpMethod method,
        string url,
        byte[]? body,
        IReadOnlyDictionary<string, string> headers,
        int timeoutSeconds)
    {
        EnsureLoaded(libraryPath);
        var buffer = new MemoryStream();
        var status = Perform(method.Method, url, body, headers, timeoutSeconds, buffer);
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return new DramaBoxApiService.HttpText(status, text);
    }

    public static void Download(
        string libraryPath,
        string url,
        string targetPath,
        IReadOnlyDictionary<string, string> headers,
        int timeoutSeconds)
    {
        EnsureLoaded(libraryPath);
        using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        Perform("GET", url, null, headers, timeoutSeconds, file);
    }

    private static int Perform(
        string method,
        string url,
        byte[]? body,
        IReadOnlyDictionary<string, string> headers,
        int timeoutSeconds,
        Stream output)
    {
        var easy = _easyInit!();
        if (easy == IntPtr.Zero)
            throw new InvalidOperationException("DramaBox 接口被 CDN 拒绝：libcurl 初始化失败。");

        var headerList = IntPtr.Zero;
        var urlNative = IntPtr.Zero;
        var methodNative = IntPtr.Zero;
        var profileNative = IntPtr.Zero;
        var dnsNative = IntPtr.Zero;
        var encodingNative = IntPtr.Zero;
        var errorBuffer = Marshal.AllocHGlobal(256);
        var statusBuffer = Marshal.AllocHGlobal(4);
        var outputHandle = GCHandle.Alloc(output);
        GCHandle bodyHandle = default;
        try
        {
            for (var i = 0; i < 256; i++)
                Marshal.WriteByte(errorBuffer, i, 0);
            Marshal.WriteInt32(statusBuffer, 0);
            _setopt!(easy, CurlOptErrorBuffer, errorBuffer);
            profileNative = Marshal.StringToCoTaskMemUTF8("safari184");
            var impersonate = _easyImpersonate!(easy, profileNative, 1);
            if (impersonate != 0)
                throw new InvalidOperationException("DramaBox 接口被 CDN 拒绝：" + ErrorText(impersonate, errorBuffer));

            urlNative = Marshal.StringToCoTaskMemUTF8(url);
            _setopt(easy, CurlOptUrl, urlNative);
            var dnsServers = DirectDnsServers();
            if (!string.IsNullOrWhiteSpace(dnsServers))
            {
                dnsNative = Marshal.StringToCoTaskMemUTF8(dnsServers);
                _setopt(easy, CurlOptDnsServers, dnsNative);
            }
            encodingNative = Marshal.StringToCoTaskMemUTF8("");
            _setopt(easy, CurlOptAcceptEncoding, encodingNative);
            _setopt(easy, 20011, Marshal.GetFunctionPointerForDelegate(WriteThunk));
            _setopt(easy, 10001, GCHandle.ToIntPtr(outputHandle));
            _setopt(easy, CurlOptHttpVersion, new IntPtr(CurlHttpVersion11));
            _setopt(easy, CurlOptTimeout, new IntPtr(timeoutSeconds));
            methodNative = Marshal.StringToCoTaskMemUTF8(method);
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                _setopt(easy, CurlOptCustomRequest, methodNative);
            if (body is not null)
            {
                var pinnedBody = body.Length == 0 ? new byte[1] : body;
                bodyHandle = GCHandle.Alloc(pinnedBody, GCHandleType.Pinned);
                _setopt(easy, CurlOptPostFields, bodyHandle.AddrOfPinnedObject());
                _setopt(easy, CurlOptPostFieldSize, new IntPtr(body.Length));
            }

            foreach (var header in headers)
            {
                var line = Marshal.StringToCoTaskMemUTF8($"{header.Key}: {header.Value}");
                try
                {
                    headerList = _slistAppend!(headerList, line);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(line);
                }
            }

            if (headerList != IntPtr.Zero)
                _setopt(easy, CurlOptHttpHeader, headerList);

            var code = _easyPerform!(easy);
            if (code != 0)
                throw new InvalidOperationException("DramaBox 接口被 CDN 拒绝：" + ErrorText(code, errorBuffer));
            _easyGetInfo!(easy, CurlInfoResponseCode, statusBuffer);
            return Marshal.ReadInt32(statusBuffer);
        }
        finally
        {
            if (headerList != IntPtr.Zero)
                _slistFree!(headerList);
            if (bodyHandle.IsAllocated)
                bodyHandle.Free();
            outputHandle.Free();
            if (urlNative != IntPtr.Zero)
                Marshal.FreeCoTaskMem(urlNative);
            if (methodNative != IntPtr.Zero)
                Marshal.FreeCoTaskMem(methodNative);
            if (profileNative != IntPtr.Zero)
                Marshal.FreeCoTaskMem(profileNative);
            if (dnsNative != IntPtr.Zero)
                Marshal.FreeCoTaskMem(dnsNative);
            if (encodingNative != IntPtr.Zero)
                Marshal.FreeCoTaskMem(encodingNative);
            Marshal.FreeHGlobal(errorBuffer);
            Marshal.FreeHGlobal(statusBuffer);
            _easyCleanup!(easy);
        }
    }

    private static string? DirectDnsServers()
    {
        var servers = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            foreach (var address in nic.GetIPProperties().DnsAddresses)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork || IsFakeIp(address))
                    continue;
                var text = address.ToString();
                if (!servers.Contains(text, StringComparer.Ordinal))
                    servers.Add(text);
            }
        }

        return servers.Count == 0 ? null : string.Join(",", servers);
    }

    private static bool IsFakeIp(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    private static string ErrorText(int code, IntPtr errorBuffer)
    {
        var detail = Marshal.PtrToStringUTF8(errorBuffer);
        if (!string.IsNullOrWhiteSpace(detail))
            return detail.Trim();
        var fallback = _easyStrError!(code);
        return fallback == IntPtr.Zero ? code.ToString() : Marshal.PtrToStringUTF8(fallback) ?? code.ToString();
    }

    private static nuint WriteBody(IntPtr data, nuint size, nuint nmemb, IntPtr userdata)
    {
        var count = checked((int)(size * nmemb));
        if (count <= 0)
            return 0;
        var stream = (Stream)GCHandle.FromIntPtr(userdata).Target!;
        var buffer = new byte[count];
        Marshal.Copy(data, buffer, 0, count);
        stream.Write(buffer, 0, count);
        return (nuint)count;
    }

    private static void EnsureLoaded(string libraryPath)
    {
        lock (Gate)
        {
            if (_initialized && string.Equals(_loadedPath, libraryPath, StringComparison.OrdinalIgnoreCase))
                return;
            if (_library == IntPtr.Zero)
                _library = NativeLibrary.Load(libraryPath);
            _globalInit = Get<GlobalInitDelegate>("curl_global_init");
            _easyInit = Get<EasyInitDelegate>("curl_easy_init");
            _easyCleanup = Get<EasyCleanupDelegate>("curl_easy_cleanup");
            _easyImpersonate = Get<EasyImpersonateDelegate>("curl_easy_impersonate");
            _easyPerform = Get<EasyPerformDelegate>("curl_easy_perform");
            _easyStrError = Get<EasyStrErrorDelegate>("curl_easy_strerror");
            _easyGetInfo = Get<EasyGetInfoDelegate>("curl_easy_getinfo");
            _setopt = Get<EasySetoptDelegate>("curl_easy_setopt");
            _slistAppend = Get<SlistAppendDelegate>("curl_slist_append");
            _slistFree = Get<SlistFreeDelegate>("curl_slist_free_all");
            var init = _globalInit(CurlGlobalAll);
            if (init != 0)
                throw new InvalidOperationException("DramaBox 网络库初始化失败。");
            _loadedPath = libraryPath;
            _initialized = true;
        }
    }

    private static T Get<T>(string name) where T : Delegate
    {
        var export = NativeLibrary.GetExport(_library, name);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var configured = Environment.GetEnvironmentVariable("DRAMABOX_CURL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Directory.Exists(configured))
                yield return configured;
            var parent = Path.GetDirectoryName(configured);
            if (!string.IsNullOrWhiteSpace(parent))
                yield return parent;
        }

        yield return AppContext.BaseDirectory;
        yield return Path.Combine(AppContext.BaseDirectory, "tools");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "DramaBoxDownloader", "runtime", "curl-2.2.3");
    }
}
