using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using TikTokPublisher.Core.Media;

namespace TikTokPublisher.Core.Drama;

public static class DramaPosterCache
{
    public const int ThumbWidth = 178;
    public const int ThumbHeight = 210;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36 " +
        "WeixinChannelTool/1.0";
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
            request.Headers.TryAddWithoutValidation(
                "Accept",
                "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (body.Length == 0)
                return null;

            var tempPath = path + ".tmp.jpg";
            try
            {
                using var image = Image.Load(body);
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(ThumbWidth, ThumbHeight),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Top,
                }));
                await image.SaveAsJpegAsync(
                    tempPath,
                    new JpegEncoder { Quality = 80 },
                    cancellationToken);
            }
            catch (UnknownImageFormatException)
            {
                if (!await TryConvertUnsupportedImageAsync(body, tempPath, cancellationToken))
                    return null;
            }

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

    private static async Task<bool> TryConvertUnsupportedImageAsync(
        byte[] body,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var sourcePath = outputPath + ".source";
        try
        {
            await File.WriteAllBytesAsync(sourcePath, body, cancellationToken);
            await FfmpegRunner.RunAsync(
                MediaBinaryResolver.ResolveFfmpeg(),
                [
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-i", sourcePath,
                    "-frames:v", "1",
                    "-vf", $"scale={ThumbWidth}:{ThumbHeight}:force_original_aspect_ratio=increase,crop={ThumbWidth}:{ThumbHeight}",
                    "-q:v", "3",
                    outputPath,
                ],
                cancellationToken);
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(sourcePath); }
            catch { }
            if (!File.Exists(outputPath))
            {
                try { File.Delete(outputPath); }
                catch { }
            }
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
