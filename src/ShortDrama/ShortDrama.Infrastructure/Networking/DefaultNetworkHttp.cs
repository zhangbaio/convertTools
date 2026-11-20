using System.Net;

namespace ShortDrama.Infrastructure.Networking;

public static class DefaultNetworkHttp
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public static HttpClient CreateClient(TimeSpan timeout) => new(CreateHandler()) { Timeout = timeout };
}
