using System.Net;
using System.Net.Sockets;

namespace ShortDrama.Infrastructure.Networking;

public enum NetworkAddressPreference
{
    System,
    Ipv4,
    Ipv6,
}

public static class PreferredNetworkHttp
{
    public const string SystemValue = "system";
    public const string Ipv4Value = "ipv4";
    public const string Ipv6Value = "ipv6";

    private static readonly TimeSpan PerAddressConnectTimeout = TimeSpan.FromSeconds(10);
    private static int _preference;

    public static NetworkAddressPreference Preference
    {
        get => (NetworkAddressPreference)Volatile.Read(ref _preference);
        set => Volatile.Write(ref _preference, (int)value);
    }

    public static NetworkAddressPreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Ipv4Value => NetworkAddressPreference.Ipv4,
        Ipv6Value => NetworkAddressPreference.Ipv6,
        _ => NetworkAddressPreference.System,
    };

    public static string ToValue(NetworkAddressPreference preference) => preference switch
    {
        NetworkAddressPreference.Ipv4 => Ipv4Value,
        NetworkAddressPreference.Ipv6 => Ipv6Value,
        _ => SystemValue,
    };

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectAsync,
    };

    public static HttpClient CreateClient(TimeSpan timeout) => new(CreateHandler()) { Timeout = timeout };

    internal static IPAddress[] OrderAddresses(IEnumerable<IPAddress> addresses, NetworkAddressPreference preference)
    {
        var preferred = preference == NetworkAddressPreference.Ipv6
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;
        return addresses
            .OrderBy(address => address.AddressFamily == preferred ? 0 : 1)
            .ToArray();
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var preference = Preference;
        if (preference == NetworkAddressPreference.System)
        {
            var systemSocket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await systemSocket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(systemSocket, ownsSocket: true);
            }
            catch
            {
                systemSocket.Dispose();
                throw;
            }
        }

        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : OrderAddresses(
                await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false),
                preference);

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(PerAddressConnectTimeout);
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), attemptCts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"无法连接 {endpoint.Host}:{endpoint.Port}", lastError);
    }
}
