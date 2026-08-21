using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace TikTokPublisher.Core.Drama;

public static class DramaPosterCache
{
    public const int ThumbWidth = 178;
    public const int ThumbHeight = 210;

    private const string UserAgent = "Mozilla/5.0 WeixinChannelTool/1.0";
    private static readonly SemaphoreSlim Gate = new(6, 6);
    private static readonly Lazy<HttpClient> SharedHttp = new(CreateHttpClient);

    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TikTokPublisher",
        "poster-cache");

    public static string GetCachePath(string posterUrl, string? cacheDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(cacheDirectory) ? DefaultCacheDirectory : cacheDirectory;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(posterUrl.Trim()))).ToLowerInvariant();
        return Path.Combine(directory, $"{hash}.jpg");
    }

    public static Task<string?> TryGetLocalPathAsync(string posterUrl, CancellationToken cancellationToken = default) =>
        TryGetLocalPathAsync(posterUrl, SharedHttp.Value, DefaultCacheDirectory, cancellationToken);

    public static async Task<string?> TryGetLocalPathAsync(
        string posterUrl,
        HttpClient http,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        var url = (posterUrl ?? string.Empty).Trim();
        if (!LooksLikeHttpUrl(url))
        {
            return null;
        }

        var path = GetCachePath(url, cacheDirectory);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            Directory.CreateDirectory(cacheDirectory);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var image = await Image.LoadAsync(body, cancellationToken);
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(ThumbWidth, ThumbHeight),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Top,
            }));

            var tempPath = path + ".tmp";
            await image.SaveAsJpegAsync(tempPath, new JpegEncoder { Quality = 80 }, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool LooksLikeHttpUrl(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }
}
