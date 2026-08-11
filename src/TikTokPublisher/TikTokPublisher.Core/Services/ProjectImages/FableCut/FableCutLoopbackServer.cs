using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TikTokPublisher.Core.Services.ProjectImages.FableCut;

internal sealed class FableCutLoopbackServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private readonly string _assetRoot;
    private readonly string _videoPath;
    private readonly byte[] _projectJson;
    private readonly byte[] _mediaJson;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly Task _acceptLoop;
    private int _clientId;

    private FableCutLoopbackServer(
        string assetRoot,
        string videoPath,
        string projectJson,
        string mediaJson)
    {
        _assetRoot = Path.GetFullPath(assetRoot);
        _videoPath = Path.GetFullPath(videoPath);
        _projectJson = Encoding.UTF8.GetBytes(projectJson);
        _mediaJson = Encoding.UTF8.GetBytes(mediaJson);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}/";
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    public string BaseUrl { get; }

    public static FableCutLoopbackServer Start(
        string assetRoot,
        string videoPath,
        string projectJson,
        string mediaJson) =>
        new(assetRoot, videoPath, projectJson, mediaJson);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }

        var clients = _clients.Values.ToArray();
        if (clients.Length > 0)
        {
            try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* best-effort shutdown */ }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            var id = Interlocked.Increment(ref _clientId);
            var task = HandleClientAsync(client, ct);
            _clients[id] = task;
            _ = task.ContinueWith(
                _ => { _clients.TryRemove(id, out var ignored); },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is null)
                    return;
                await DispatchAsync(stream, request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private async Task DispatchAsync(NetworkStream stream, HttpRequest request, CancellationToken ct)
    {
        var method = request.Method;
        var isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
        if (method is not ("GET" or "HEAD" or "PUT"))
        {
            await SendTextAsync(stream, 405, "Method Not Allowed", "method not allowed", isHead, ct);
            return;
        }

        var path = DecodePath(request.Target);
        if (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase))
        {
            if (path != "/api/project")
            {
                await SendTextAsync(stream, 404, "Not Found", "not found", false, ct);
                return;
            }

            await DrainBodyAsync(stream, request.ContentLength, ct).ConfigureAwait(false);
            await SendBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true}"u8.ToArray(), false, null, ct);
            return;
        }

        if (path == "/api/project")
        {
            await SendBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", _projectJson, isHead, null, ct);
            return;
        }

        if (path.StartsWith("/api/media", StringComparison.Ordinal))
        {
            await SendBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", _mediaJson, isHead, null, ct);
            return;
        }

        if (path.StartsWith("/api/library", StringComparison.Ordinal))
        {
            await SendBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", "[]"u8.ToArray(), isHead, null, ct);
            return;
        }

        if (path.StartsWith("/api/export/ffmpeg", StringComparison.Ordinal))
        {
            await SendBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", "{\"available\":false}"u8.ToArray(), isHead, null, ct);
            return;
        }

        if (path == "/media/episode")
        {
            await SendFileAsync(stream, _videoPath, request, isHead, allowRange: true, ct).ConfigureAwait(false);
            return;
        }

        var relative = path.TrimStart('/');
        if (string.IsNullOrWhiteSpace(relative))
            relative = "index.html";
        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(_assetRoot, relative.Replace('/', Path.DirectorySeparatorChar))); }
        catch
        {
            await SendTextAsync(stream, 400, "Bad Request", "bad path", isHead, ct);
            return;
        }

        if (!IsWithinRoot(candidate, _assetRoot))
        {
            await SendTextAsync(stream, 403, "Forbidden", "forbidden", isHead, ct);
            return;
        }

        if (Directory.Exists(candidate))
            candidate = Path.Combine(candidate, "index.html");
        if (!File.Exists(candidate))
        {
            await SendTextAsync(stream, 404, "Not Found", "not found", isHead, ct);
            return;
        }

        await SendFileAsync(stream, candidate, request, isHead, allowRange: false, ct).ConfigureAwait(false);
    }

    private static async Task SendFileAsync(
        NetworkStream stream,
        string path,
        HttpRequest request,
        bool isHead,
        bool allowRange,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        var size = info.Length;
        var rangeHeader = request.Headers.GetValueOrDefault("Range");
        var hasRange = !string.IsNullOrWhiteSpace(rangeHeader);

        // FableCut calls fetch(src).arrayBuffer() once for every synthetic mediaId
        // to build audio waveforms. All of those ids point at the same episode, so
        // allowing a full response here can decode the video roughly sixteen times.
        // Media elements use Sec-Fetch-Dest: video and/or byte ranges; only those may
        // read the real source. The renderer injects lightweight deterministic peaks.
        var isMediaElement =
            string.Equals(request.Headers.GetValueOrDefault("Sec-Fetch-Dest"), "video", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Headers.GetValueOrDefault("X-FableCut-Media-Element"), "1", StringComparison.Ordinal);
        if (allowRange && !hasRange && !isMediaElement)
        {
            await SendTextAsync(stream, 413, "Content Too Large", "full media fetch disabled", isHead, ct);
            return;
        }

        long start = 0, end = Math.Max(0, size - 1);
        var status = 200;
        var reason = "OK";
        if (allowRange && hasRange)
        {
            if (!TryParseRange(rangeHeader!, size, out start, out end))
            {
                await WriteHeadersAsync(
                    stream,
                    416,
                    "Range Not Satisfiable",
                    "text/plain; charset=utf-8",
                    0,
                    new Dictionary<string, string> { ["Content-Range"] = $"bytes */{size}" },
                    ct);
                return;
            }
            status = 206;
            reason = "Partial Content";
        }

        var length = size == 0 ? 0 : end - start + 1;
        var extra = new Dictionary<string, string>();
        if (allowRange)
            extra["Accept-Ranges"] = "bytes";
        if (status == 206)
            extra["Content-Range"] = $"bytes {start}-{end}/{size}";
        await WriteHeadersAsync(stream, status, reason, MimeType(path), length, extra, ct).ConfigureAwait(false);
        if (isHead || length == 0)
            return;

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, useAsync: true);
        file.Seek(start, SeekOrigin.Begin);
        var remaining = length;
        var buffer = new byte[1024 * 1024];
        while (remaining > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read <= 0)
                break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }

    internal static bool TryParseRange(string value, long size, out long start, out long end)
    {
        start = 0;
        end = 0;
        if (size <= 0 || string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;
        var spec = value[6..].Trim();
        if (spec.Contains(','))
            return false;
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;
        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();
        if (left.Length == 0)
        {
            if (!long.TryParse(right, out var suffix) || suffix <= 0)
                return false;
            suffix = Math.Min(suffix, size);
            start = size - suffix;
            end = size - 1;
            return true;
        }
        if (!long.TryParse(left, out start) || start < 0 || start >= size)
            return false;
        if (right.Length == 0)
            end = size - 1;
        else if (!long.TryParse(right, out end) || end < start)
            return false;
        end = Math.Min(end, size - 1);
        return true;
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        var matched = 0;
        while (buffer.Length < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (read == 0)
                return null;
            buffer.WriteByte(one[0]);
            matched = (matched, one[0]) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0,
            };
            if (matched == 4)
                break;
        }
        if (matched != 4)
            return null;

        var headerText = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
            return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var contentLength = headers.TryGetValue("Content-Length", out var rawLength) && long.TryParse(rawLength, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
        return new HttpRequest(requestLine[0].ToUpperInvariant(), requestLine[1], headers, contentLength);
    }

    private static async Task DrainBodyAsync(NetworkStream stream, long length, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (length > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), ct).ConfigureAwait(false);
            if (read <= 0)
                break;
            length -= read;
        }
    }

    private static Task SendTextAsync(NetworkStream stream, int status, string reason, string text, bool isHead, CancellationToken ct) =>
        SendBytesAsync(stream, status, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), isHead, null, ct);

    private static async Task SendBytesAsync(
        NetworkStream stream,
        int status,
        string reason,
        string contentType,
        byte[] body,
        bool isHead,
        IReadOnlyDictionary<string, string>? extra,
        CancellationToken ct)
    {
        await WriteHeadersAsync(stream, status, reason, contentType, body.LongLength, extra, ct).ConfigureAwait(false);
        if (!isHead && body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static async Task WriteHeadersAsync(
        NetworkStream stream,
        int status,
        string reason,
        string contentType,
        long contentLength,
        IReadOnlyDictionary<string, string>? extra,
        CancellationToken ct)
    {
        var builder = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(contentLength).Append("\r\n")
            .Append("Connection: close\r\n");
        if (extra is not null)
            foreach (var (key, value) in extra)
                builder.Append(key).Append(": ").Append(value).Append("\r\n");
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
    }

    private static string DecodePath(string target)
    {
        var query = target.IndexOf('?');
        var raw = query >= 0 ? target[..query] : target;
        try { return Uri.UnescapeDataString(raw); }
        catch { return raw; }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".wav" => "audio/wav",
        _ => "application/octet-stream",
    };

    private sealed record HttpRequest(
        string Method,
        string Target,
        Dictionary<string, string> Headers,
        long ContentLength);
}
